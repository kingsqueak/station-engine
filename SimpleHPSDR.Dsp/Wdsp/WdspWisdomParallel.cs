// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR: build WDSP's FFTW wisdom on several cores.
//
// WDSP's WDSPwisdom (native/wdsp/wisdom.c) plans 65 FFTs one after another
// with FFTW_PATIENT, which leaves all but one core idle for many minutes.
// FFTW's planner can't run on two threads at once, but separate processes can
// each plan a share and FFTW merges their wisdom files. So the engine starts
// copies of itself in a hidden worker mode, each plans its share through the
// bundled FFTW library directly (the same plans, flags, sizes and malloc'd
// out-of-place buffers as wisdom.c), and the parent merges the parts into
// WDSP's wisdom file. WDSPwisdom then finds a complete file and only loads it.
//
// Not on Windows, where FFTW is linked into wdsp.dll and not reachable on its
// own; any failure falls back to WDSP's own one-at-a-time planning.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SimpleHPSDR.Dsp.Wdsp;

public static class WdspWisdomParallel
{
    /// <summary>First argument of a worker: StationEngine --plan-fftw-wisdom &lt;fftw&gt; &lt;out&gt; &lt;plans&gt;.</summary>
    public const string WorkerArgument = "--plan-fftw-wisdom";

    /// <summary>Set to "1" to plan one at a time, as WDSP does.</summary>
    public const string SequentialEnvironmentVariable = "STATION_ENGINE_WISDOM_SEQUENTIAL";

    // Mirrors wisdom.c: MAX_WISDOM_SIZE 262144, sizes 64 upward by doubling.
    private const int MinSize = 64, MaxSize = 262_144;
    private const uint FftwPatient = 1u << 5, FftwWisdomOnly = 1u << 21;
    private const int FftwForward = -1, FftwBackward = 1;

    internal enum Kind { ComplexForward, ComplexBackward, RealForward, RealInverse }

    internal readonly record struct Plan(Kind Kind, int Size)
    {
        public string Code => Kind switch
        {
            Kind.ComplexForward => "cf",
            Kind.ComplexBackward => "cb",
            Kind.RealForward => "rf",
            _ => "ri",
        } + ":" + Size.ToString(CultureInfo.InvariantCulture);

        public static Plan Parse(string code)
        {
            var parts = code.Split(':');
            var kind = parts[0] switch
            {
                "cf" => Kind.ComplexForward,
                "cb" => Kind.ComplexBackward,
                "rf" => Kind.RealForward,
                "ri" => Kind.RealInverse,
                _ => throw new FormatException($"unknown plan kind '{parts[0]}'"),
            };
            return new Plan(kind, int.Parse(parts[1], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>wisdom.c's 65 plans, in its order: for each size forward,
    /// backward and backward at size+1, then for each size real forward and inverse.</summary>
    internal static IReadOnlyList<Plan> AllPlans()
    {
        var list = new List<Plan>();
        for (int n = MinSize; n <= MaxSize; n *= 2)
        {
            list.Add(new Plan(Kind.ComplexForward, n));
            list.Add(new Plan(Kind.ComplexBackward, n));
            list.Add(new Plan(Kind.ComplexBackward, n + 1));
        }
        for (int n = MinSize; n <= MaxSize; n *= 2)
        {
            list.Add(new Plan(Kind.RealForward, n));
            list.Add(new Plan(Kind.RealInverse, n));
        }
        return list;
    }

    // Relative planning time per plan in wisdom.c order, from a timed sequential
    // run on an M2 Max (474 s): (plan index where a stretch starts, seconds).
    private static readonly (int Step, double Seconds)[] MeasuredRun =
    [
        (0, 0), (17, 21), (27, 59), (32, 135), (33, 146), (34, 166), (35, 192), (36, 237),
        (37, 282), (38, 327), (39, 328), (57, 358), (59, 368), (61, 388), (63, 418), (64, 433),
        (65, 474),
    ];

    internal static double Cost(int index)
    {
        static double At(double step)
        {
            for (int i = 1; i < MeasuredRun.Length; i++)
                if (step <= MeasuredRun[i].Step)
                {
                    var (s0, t0) = MeasuredRun[i - 1];
                    var (s1, t1) = MeasuredRun[i];
                    return t0 + (t1 - t0) * (step - s0) / (s1 - s0);
                }
            return MeasuredRun[^1].Seconds;
        }
        return Math.Max(0.05, At(index + 1) - At(index));
    }

    /// <summary>How many workers: the performance cores less one (Apple), else
    /// the cores less one; at least 2, at most 8.</summary>
    internal static int WorkerCount()
    {
        int cores = Environment.ProcessorCount;
        if (OperatingSystem.IsMacOS() && SysctlInt("hw.perflevel0.physicalcpu") is int perf and > 0)
            cores = perf;
        return Math.Clamp(cores - 1, 2, 8);
    }

    private static int? SysctlInt(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl", ["-n", name])
                { RedirectStandardOutput = true, UseShellExecute = false });
            if (p is null) return null;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return int.TryParse(text.Trim(), out int v) ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Largest first, each to the least-loaded worker (LPT).</summary>
    internal static List<List<Plan>> Assign(IReadOnlyList<Plan> plans, int workers)
    {
        var buckets = Enumerable.Range(0, workers).Select(_ => new List<Plan>()).ToList();
        var load = new double[workers];
        foreach (var i in Enumerable.Range(0, plans.Count).OrderByDescending(Cost))
        {
            int w = Array.IndexOf(load, load.Min());
            buckets[w].Add(plans[i]);
            load[w] += Cost(i);
        }
        return buckets.Where(b => b.Count > 0).ToList();
    }

    /// <summary>
    /// Build <paramref name="wisdomPath"/> with worker processes. False (and no
    /// file) when it couldn't; the caller then lets WDSP plan as usual.
    /// <paramref name="status"/> gets lines like "Planning FFTs in parallel: 42%
    /// (31 of 65 plans, 7 at once)".
    /// </summary>
    public static bool TryBuild(string wisdomPath, string fftwPath, ILogger log, Action<string> status)
    {
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(SequentialEnvironmentVariable) == "1")
            return false;
        if (!File.Exists(fftwPath) || Environment.ProcessPath is not { } exe)
            return false;

        var plans = AllPlans();
        double total = Enumerable.Range(0, plans.Count).Sum(Cost);
        var costOf = Enumerable.Range(0, plans.Count).ToDictionary(i => plans[i].Code, Cost);
        int workers = WorkerCount();
        var shares = Assign(plans, workers);
        string dir = Path.GetDirectoryName(wisdomPath)!;
        string partPrefix = Path.Combine(dir, $".wisdom-part-{Environment.ProcessId}-");
        var parts = new List<string>();
        var processes = new List<Process>();
        var clock = Stopwatch.StartNew();
        double doneCost = 0;
        int donePlans = 0;
        var gate = new object();
        status($"Planning FFTs in parallel: 0% (0 of {plans.Count} plans, {shares.Count} at once)");
        log.LogInformation("wdsp.wisdom parallel build workers={Workers} plans={Plans}", shares.Count, plans.Count);
        try
        {
            for (int w = 0; w < shares.Count; w++)
            {
                string part = partPrefix + w;
                parts.Add(part);
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                // Run as `dotnet StationEngine.dll` in development: pass the assembly too.
                if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    && Environment.GetCommandLineArgs() is { Length: > 0 } argv)
                    psi.ArgumentList.Add(argv[0]);
                psi.ArgumentList.Add(WorkerArgument);
                psi.ArgumentList.Add(fftwPath);
                psi.ArgumentList.Add(part);
                psi.ArgumentList.Add(string.Join(",", shares[w].Select(p => p.Code)));
                var proc = Process.Start(psi) ?? throw new InvalidOperationException("worker did not start");
                processes.Add(proc);
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not { } line || !line.StartsWith("done ", StringComparison.Ordinal)) return;
                    lock (gate)
                    {
                        donePlans++;
                        doneCost += costOf.GetValueOrDefault(line[5..], 0);
                        status($"Planning FFTs in parallel: {100 * doneCost / total:0}% ({donePlans} of {plans.Count} plans, {shares.Count} at once)");
                    }
                };
                proc.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } l) log.LogWarning("wdsp.wisdom worker: {Line}", l); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            foreach (var p in processes)
            {
                p.WaitForExit();
                if (p.ExitCode != 0) throw new InvalidOperationException($"worker exited with {p.ExitCode}");
            }
            if (donePlans != plans.Count)
                throw new InvalidOperationException($"workers planned {donePlans} of {plans.Count}");

            status("Planning FFTs in parallel: merging");
            using var fftw = Fftw.Load(fftwPath);
            foreach (var part in parts)
                if (fftw.ImportWisdom(part) == 0) throw new InvalidOperationException($"could not read {part}");
            string temp = wisdomPath + ".tmp";
            if (fftw.ExportWisdom(temp) == 0) throw new InvalidOperationException("could not write the merged wisdom");
            File.Move(temp, wisdomPath, overwrite: true);
            log.LogInformation("wdsp.wisdom parallel build done in {Seconds:0} s with {Workers} workers", clock.Elapsed.TotalSeconds, shares.Count);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "wdsp.wisdom parallel build failed; WDSP will plan one at a time");
            foreach (var p in processes)
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            try { File.Delete(wisdomPath + ".tmp"); } catch (Exception) { }
            return false;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
            foreach (var part in parts) try { File.Delete(part); } catch (Exception) { }
        }
    }

    /// <summary>The worker: plan the given share and write its wisdom. Returns the exit code.</summary>
    public static int RunWorker(string[] args)
    {
        // args: --plan-fftw-wisdom <fftw library> <output file> <cf:64,cb:65,...>
        if (args.Length != 4) { Console.Error.WriteLine("usage: --plan-fftw-wisdom <fftw> <out> <plans>"); return 2; }
        try
        {
            using var fftw = Fftw.Load(args[1]);
            var plans = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Plan.Parse).ToList();
            // As wisdom.c: two separate buffers of (MAX_WISDOM_SIZE + 1) complex
            // values, from malloc like WDSP's malloc0 on macOS and Linux, so the
            // alignment (part of each plan's key) matches what WDSP will look up.
            nuint bytes = (nuint)((MaxSize + 1) * 2 * sizeof(double));
            IntPtr input = Fftw.Malloc0(bytes), output = Fftw.Malloc0(bytes);
            try
            {
                foreach (var p in plans)
                {
                    IntPtr plan = fftw.Make(p, input, output, FftwPatient);
                    if (plan == IntPtr.Zero) throw new InvalidOperationException($"FFTW could not plan {p.Code}");
                    fftw.Execute(plan);
                    fftw.Destroy(plan);
                    Console.Out.WriteLine("done " + p.Code);
                    Console.Out.Flush();
                }
            }
            finally
            {
                Fftw.Free(input);
                Fftw.Free(output);
            }
            return fftw.ExportWisdom(args[2]) != 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>For checking: how many of wisdom.c's plans <paramref name="wisdomFile"/>
    /// covers (planned with FFTW_WISDOM_ONLY, so a gap gives no plan).</summary>
    internal static (int Covered, int Total) Coverage(string fftwPath, string wisdomFile)
    {
        using var fftw = Fftw.Load(fftwPath);
        if (fftw.ImportWisdom(wisdomFile) == 0) return (0, AllPlans().Count);
        nuint bytes = (nuint)((MaxSize + 1) * 2 * sizeof(double));
        IntPtr input = Fftw.Malloc0(bytes), output = Fftw.Malloc0(bytes);
        try
        {
            int covered = 0;
            foreach (var p in AllPlans())
            {
                IntPtr plan = fftw.Make(p, input, output, FftwPatient | FftwWisdomOnly);
                if (plan != IntPtr.Zero) { covered++; fftw.Destroy(plan); }
            }
            return (covered, AllPlans().Count);
        }
        finally
        {
            Fftw.Free(input);
            Fftw.Free(output);
        }
    }

    /// <summary>The few FFTW calls needed, bound by name from the bundled library.</summary>
    internal sealed class Fftw : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PlanDft1d(int n, IntPtr input, IntPtr output, int sign, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PlanReal1d(int n, IntPtr input, IntPtr output, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PlanAction(IntPtr plan);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WisdomFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        private readonly IntPtr _lib;
        private readonly PlanDft1d _dft;
        private readonly PlanReal1d _r2c, _c2r;
        private readonly PlanAction _execute, _destroy;
        private readonly WisdomFile _import, _export;

        private Fftw(IntPtr lib)
        {
            _lib = lib;
            T Get<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
            _dft = Get<PlanDft1d>("fftw_plan_dft_1d");
            _r2c = Get<PlanReal1d>("fftw_plan_dft_r2c_1d");
            _c2r = Get<PlanReal1d>("fftw_plan_dft_c2r_1d");
            _execute = Get<PlanAction>("fftw_execute");
            _destroy = Get<PlanAction>("fftw_destroy_plan");
            _import = Get<WisdomFile>("fftw_import_wisdom_from_filename");
            _export = Get<WisdomFile>("fftw_export_wisdom_to_filename");
        }

        public static Fftw Load(string path) => new(NativeLibrary.Load(path));

        public IntPtr Make(Plan p, IntPtr input, IntPtr output, uint flags) => p.Kind switch
        {
            Kind.ComplexForward => _dft(p.Size, input, output, FftwForward, flags),
            Kind.ComplexBackward => _dft(p.Size, input, output, FftwBackward, flags),
            Kind.RealForward => _r2c(p.Size, input, output, flags),
            _ => _c2r(p.Size, input, output, flags),
        };

        public void Execute(IntPtr plan) => _execute(plan);
        public void Destroy(IntPtr plan) => _destroy(plan);
        public int ImportWisdom(string path) => _import(path);
        public int ExportWisdom(string path) => _export(path);

        // WDSP's malloc0 on macOS/Linux: malloc, then zero.
        public static unsafe IntPtr Malloc0(nuint bytes)
        {
            void* p = NativeMemory.Alloc(bytes);
            NativeMemory.Clear(p, bytes);
            return (IntPtr)p;
        }

        public static unsafe void Free(IntPtr p) => NativeMemory.Free((void*)p);

        public void Dispose() { }   // FFTW stays loaded: WDSP uses the same library
    }
}
