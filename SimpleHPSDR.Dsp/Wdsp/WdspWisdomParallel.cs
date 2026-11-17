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
//
// Planning can be stopped and picked up again: each worker saves its wisdom
// after every plan, and the next start plans only what the saved parts don't
// hold. Workers end when the engine that started them is gone.
//
// A package may also carry a ready-made wisdom file beside the libraries
// (wdspWisdom01.seed, made by the Native libraries workflow with the same
// FFTW). On a Raspberry Pi, where planning takes an hour or more, that file
// is used instead of planning (TryUseSeed).

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

    /// <summary>"1": use the packaged wisdom file on any machine that has one;
    /// "0": never; unset: on a Raspberry Pi only.</summary>
    public const string SeedEnvironmentVariable = "STATION_ENGINE_WISDOM_SEED";

    /// <summary>The packaged wisdom file, beside the WDSP and FFTW libraries.</summary>
    public const string SeedFileName = "wdspWisdom01.seed";

    // Set by the engine for its workers: the engine's process id. A worker
    // whose parent is no longer that process stops.
    private const string ParentEnvironmentVariable = "STATION_ENGINE_WISDOM_PARENT";

    private const string PartPrefix = ".wisdom-part-";

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

    /// <summary>Largest first, each to the least-loaded worker (LPT).
    /// <paramref name="costOf"/> gives each plan's relative time by its code.</summary>
    internal static List<List<Plan>> Assign(IReadOnlyList<Plan> plans, IReadOnlyDictionary<string, double> costOf, int workers)
    {
        var buckets = Enumerable.Range(0, workers).Select(_ => new List<Plan>()).ToList();
        var load = new double[workers];
        foreach (var plan in plans.OrderByDescending(p => costOf[p.Code]))
        {
            int w = Array.IndexOf(load, load.Min());
            buckets[w].Add(plan);
            load[w] += costOf[plan.Code];
        }
        return buckets.Where(b => b.Count > 0).ToList();
    }

    /// <summary>Wisdom parts in <paramref name="dir"/>, from this run or one that was stopped.</summary>
    private static List<string> PartFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, PartPrefix + "*")
                .Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Removes what a planning run left in <paramref name="dir"/>. For
    /// the caller once the wisdom file is in place.</summary>
    public static void DeleteParts(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, PartPrefix + "*"))
                try { File.Delete(f); } catch (Exception) { }
        }
        catch (Exception) { }
    }

    /// <summary>Whether the packaged wisdom file is to be used here: on a
    /// Raspberry Pi (64-bit Linux), or as <see cref="SeedEnvironmentVariable"/> says.</summary>
    internal static bool SeedWanted()
    {
        switch (Environment.GetEnvironmentVariable(SeedEnvironmentVariable))
        {
            case "1": return true;
            case "0": return false;
        }
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return false;
        try
        {
            return File.ReadAllText("/proc/device-tree/model").Contains("Raspberry Pi", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Puts the packaged wisdom file <paramref name="seedPath"/> in place as
    /// <paramref name="wisdomPath"/>, when this machine is to use one and the
    /// file holds every plan WDSP asks for with this FFTW library. False (and
    /// no file) otherwise; the caller then plans as usual.
    /// </summary>
    public static bool TryUseSeed(string seedPath, string fftwPath, string wisdomPath, ILogger log)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(seedPath) || !File.Exists(fftwPath) || !SeedWanted())
            return false;
        try
        {
            using var fftw = Fftw.Load(fftwPath);
            int total = AllPlans().Count;
            int covered = fftw.ImportWisdom(seedPath) == 0 ? 0 : Covered(fftw).Count;
            if (covered != total)
            {
                // Made with another FFTW build, or incomplete: nothing of it stays.
                fftw.ForgetWisdom();
                log.LogWarning("wdsp.wisdom packaged file covers {Covered} of {Total} plans; not used", covered, total);
                return false;
            }
            string temp = wisdomPath + ".tmp";
            File.Copy(seedPath, temp, overwrite: true);
            File.Move(temp, wisdomPath, overwrite: true);
            log.LogInformation("wdsp.wisdom taken from the packaged file ({Total} plans); no planning needed", total);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "wdsp.wisdom packaged file could not be used");
            try { File.Delete(wisdomPath + ".tmp"); } catch (Exception) { }
            return false;
        }
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
        string dir = Path.GetDirectoryName(wisdomPath)!;
        var processes = new List<Process>();
        var clock = Stopwatch.StartNew();
        try
        {
            using var fftw = Fftw.Load(fftwPath);

            // What a run that was stopped already planned. Its parts go into
            // this process's FFTW (where the merge at the end needs them too);
            // FFTW itself then says which plans they hold.
            var earlier = PartFiles(dir);
            foreach (var part in earlier) fftw.ImportWisdom(part);
            var have = earlier.Count > 0 ? Covered(fftw) : [];
            var todo = plans.Where(p => !have.Contains(p.Code)).ToList();
            double doneCost = have.Sum(code => costOf[code]);
            int donePlans = have.Count;
            var shares = Assign(todo, costOf, Math.Min(WorkerCount(), Math.Max(1, todo.Count)));
            var gate = new object();
            status($"Planning FFTs in parallel: {100 * doneCost / total:0}% ({donePlans} of {plans.Count} plans, {shares.Count} at once)");
            log.LogInformation("wdsp.wisdom parallel build workers={Workers} plans={Plans} alreadyPlanned={Have}",
                shares.Count, todo.Count, have.Count);

            var parts = new List<string>();
            for (int w = 0; w < shares.Count; w++)
            {
                string part = Path.Combine(dir, $"{PartPrefix}{Environment.ProcessId}-{w}");
                parts.Add(part);
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.Environment[ParentEnvironmentVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
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

            status("Planning FFTs in parallel: merging");
            foreach (var part in parts)
                if (fftw.ImportWisdom(part) == 0) throw new InvalidOperationException($"could not read {part}");
            int covered = Covered(fftw).Count;
            if (covered != plans.Count)
                throw new InvalidOperationException($"the parts hold {covered} of {plans.Count} plans");
            string temp = wisdomPath + ".tmp";
            if (fftw.ExportWisdom(temp) == 0) throw new InvalidOperationException("could not write the merged wisdom");
            File.Move(temp, wisdomPath, overwrite: true);
            DeleteParts(dir);
            log.LogInformation("wdsp.wisdom parallel build done in {Seconds:0} s with {Workers} workers", clock.Elapsed.TotalSeconds, shares.Count);
            return true;
        }
        catch (Exception ex)
        {
            // The parts stay: the next start plans only what they don't hold.
            log.LogWarning(ex, "wdsp.wisdom parallel build failed; WDSP will plan one at a time");
            foreach (var p in processes)
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
            try { File.Delete(wisdomPath + ".tmp"); } catch (Exception) { }
            return false;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>The worker: plan the given share and write its wisdom. Returns the exit code.</summary>
    public static int RunWorker(string[] args)
    {
        // args: --plan-fftw-wisdom <fftw library> <output file> <cf:64,cb:65,...>
        if (args.Length != 4) { Console.Error.WriteLine("usage: --plan-fftw-wisdom <fftw> <out> <plans>"); return 2; }
        StopWithParent();
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
                    // Saved after every plan (all of this worker's so far), so
                    // a run that is stopped keeps what it finished.
                    string temp = args[2] + ".tmp";
                    if (fftw.ExportWisdom(temp) == 0) return 3;
                    File.Move(temp, args[2], overwrite: true);
                    Console.Out.WriteLine("done " + p.Code);
                    Console.Out.Flush();
                }
            }
            finally
            {
                Fftw.Free(input);
                Fftw.Free(output);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();

    // A worker left behind by an engine that was stopped would plan on for
    // many minutes, on a core of its own, beside the workers of the next
    // start. So a worker watches for its parent going away and ends itself;
    // what it finished is already saved.
    private static void StopWithParent()
    {
        if (OperatingSystem.IsWindows()
            || !int.TryParse(Environment.GetEnvironmentVariable(ParentEnvironmentVariable), NumberStyles.None,
                CultureInfo.InvariantCulture, out int parent))
            return;
        new Thread(() =>
        {
            try
            {
                while (GetParentProcessId() == parent) Thread.Sleep(1000);
            }
            catch (Exception) { return; }
            Environment.Exit(4);
        }) { IsBackground = true, Name = "wisdom-parent-watch" }.Start();
    }

    /// <summary>The codes of wisdom.c's plans that the wisdom now in
    /// <paramref name="fftw"/> holds (planned with FFTW_WISDOM_ONLY, so a gap
    /// gives no plan).</summary>
    private static HashSet<string> Covered(Fftw fftw)
    {
        nuint bytes = (nuint)((MaxSize + 1) * 2 * sizeof(double));
        IntPtr input = Fftw.Malloc0(bytes), output = Fftw.Malloc0(bytes);
        try
        {
            var covered = new HashSet<string>();
            foreach (var p in AllPlans())
            {
                IntPtr plan = fftw.Make(p, input, output, FftwPatient | FftwWisdomOnly);
                if (plan != IntPtr.Zero) { covered.Add(p.Code); fftw.Destroy(plan); }
            }
            return covered;
        }
        finally
        {
            Fftw.Free(input);
            Fftw.Free(output);
        }
    }

    /// <summary>For checking: how many of wisdom.c's plans <paramref name="wisdomFile"/> covers.</summary>
    internal static (int Covered, int Total) Coverage(string fftwPath, string wisdomFile)
    {
        using var fftw = Fftw.Load(fftwPath);
        if (fftw.ImportWisdom(wisdomFile) == 0) return (0, AllPlans().Count);
        return (Covered(fftw).Count, AllPlans().Count);
    }

    /// <summary>The few FFTW calls needed, bound by name from the bundled library.</summary>
    internal sealed class Fftw : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PlanDft1d(int n, IntPtr input, IntPtr output, int sign, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PlanReal1d(int n, IntPtr input, IntPtr output, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PlanAction(IntPtr plan);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NoArguments();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WisdomFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        private readonly IntPtr _lib;
        private readonly PlanDft1d _dft;
        private readonly PlanReal1d _r2c, _c2r;
        private readonly PlanAction _execute, _destroy;
        private readonly Action _forget;
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
            var forget = Get<NoArguments>("fftw_forget_wisdom");
            _forget = () => forget();
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
        public void ForgetWisdom() => _forget();

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
