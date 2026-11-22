// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// SimpleStudio: a chain of mic effects the operator arranges, run on the TX
// mic block ahead of everything else (DEXP, SimpleExpander, WDSP's TXA).
// The effects and their parameter table are ported from Patch Prime
// (kingsqueak/patch-prime, engine/src/effects.cpp, commit 2608d58).
//
// This file is the one description of the effects both sides use: the engine
// validates and runs a chain from it, the app builds its controls from it.

namespace SimpleHPSDR.Contracts;

public enum StudioParamKind
{
    Linear,
    /// <summary>Shown on a log scale (a frequency, a time).</summary>
    Log,
    /// <summary>A whole number indexing <see cref="StudioParamInfo.Options"/>.</summary>
    Choice,
    /// <summary>0 or 1.</summary>
    Toggle,
}

public sealed record StudioParamInfo(
    string Name,
    string Label,
    string Unit,
    double Min,
    double Max,
    double Default,
    StudioParamKind Kind = StudioParamKind.Linear,
    IReadOnlyList<string>? Options = null)
{
    /// <summary>A value made valid: finite, in range, whole for a choice or toggle.</summary>
    public double Valid(double value)
    {
        if (!double.IsFinite(value)) value = Default;
        value = Math.Clamp(value, Min, Max);
        if (Kind is StudioParamKind.Choice or StudioParamKind.Toggle) value = Math.Round(value, MidpointRounding.AwayFromZero);
        return value;
    }
}

public sealed record StudioEffectInfo(string Type, string Label, IReadOnlyList<StudioParamInfo> Params)
{
    /// <summary>Position of a parameter in <see cref="Params"/>, or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Params.Count; i++)
            if (Params[i].Name == name) return i;
        return -1;
    }

    /// <summary>Every parameter's default, in <see cref="Params"/> order.</summary>
    public double[] Defaults()
    {
        var values = new double[Params.Count];
        for (int i = 0; i < values.Length; i++) values[i] = Params[i].Default;
        return values;
    }

    /// <summary>Every parameter's value in <see cref="Params"/> order: the
    /// given ones made valid, the default for the rest.</summary>
    public double[] Values(IReadOnlyDictionary<string, double>? given)
    {
        var values = Defaults();
        if (given is null) return values;
        for (int i = 0; i < values.Length; i++)
            if (given.TryGetValue(Params[i].Name, out double v)) values[i] = Params[i].Valid(v);
        return values;
    }
}

public static class StudioCatalogue
{
    /// <summary>Effects one chain may hold.</summary>
    public const int MaxEffects = 8;

    public const string Eq = "eq";
    public const string Compressor = "compressor";
    public const string DeEsser = "deesser";
    public const string Multiband = "multiband";
    public const string Gate = "gate";
    public const string Expander = "expander";
    public const string Leveler = "leveler";
    public const string Limiter = "limiter";
    public const string Reverb = "reverb";
    public const string Gain = "gain";

    public const int EqBands = 6;
    public const int MultibandBands = 4;
    public static readonly IReadOnlyList<string> EqBandTypes =
        ["Bell", "Low shelf", "High shelf", "Low cut", "High cut"];

    /// <summary>Every effect, in the order a menu lists them.</summary>
    public static IReadOnlyList<StudioEffectInfo> Effects { get; } = Build();

    public static StudioEffectInfo? Find(string? type)
    {
        foreach (var e in Effects)
            if (e.Type == type) return e;
        return null;
    }

    private static StudioParamInfo P(string name, string label, string unit, double min, double max,
        double initial, StudioParamKind kind = StudioParamKind.Linear, IReadOnlyList<string>? options = null) =>
        new(name, label, unit, min, max, initial, kind, options);

    private static List<StudioEffectInfo> Build()
    {
        const StudioParamKind Log = StudioParamKind.Log, Toggle = StudioParamKind.Toggle;

        // The EQ's starting bands: low shelf 80 Hz, bells at 250 Hz, 1, 3 and
        // 6 kHz, high shelf 12 kHz; all flat.
        (int Type, double Freq, double Q)[] eqBands =
            [(1, 80, 0.707), (0, 250, 1), (0, 1000, 1), (0, 3000, 1), (0, 6000, 1), (2, 12000, 0.707)];
        var eq = new List<StudioParamInfo>();
        for (int b = 0; b < EqBands; b++)
        {
            string p = $"b{b + 1}", l = $"Band {b + 1} ";
            eq.Add(P(p + "On", l + "on", "", 0, 1, 1, Toggle));
            eq.Add(P(p + "Type", l + "type", "", 0, EqBandTypes.Count - 1, eqBands[b].Type,
                StudioParamKind.Choice, EqBandTypes));
            eq.Add(P(p + "Freq", l + "frequency", "Hz", 20, 20000, eqBands[b].Freq, Log));
            eq.Add(P(p + "Gain", l + "gain", "dB", -24, 24, 0));
            eq.Add(P(p + "Q", l + "Q", "", 0.1, 10, eqBands[b].Q, Log));
        }

        double[] crossovers = [120, 1000, 6000];
        var multiband = new List<StudioParamInfo>();
        for (int x = 0; x < crossovers.Length; x++)
            multiband.Add(P($"x{x + 1}", $"Crossover {x + 1}", "Hz", 30, 16000, crossovers[x], Log));
        for (int b = 1; b <= MultibandBands; b++)
        {
            string p = $"b{b}", l = $"Band {b} ";
            multiband.Add(P(p + "On", l + "on", "", 0, 1, 1, Toggle));
            multiband.Add(P(p + "Solo", l + "solo", "", 0, 1, 0, Toggle));
            multiband.Add(P(p + "Threshold", l + "threshold", "dB", -60, 0, -20));
            multiband.Add(P(p + "Ratio", l + "ratio", ":1", 1, 20, 2, Log));
            multiband.Add(P(p + "Attack", l + "attack", "ms", 0.1, 200, 10, Log));
            multiband.Add(P(p + "Release", l + "release", "ms", 10, 3000, 150, Log));
            multiband.Add(P(p + "Makeup", l + "makeup", "dB", -12, 24, 0));
        }

        return
        [
            new(Eq, "Parametric EQ", eq),
            // The full controls start where the one knob stands at 40 %.
            new(Compressor, "Compressor",
            [
                P("amount", "Amount", "%", 0, 100, 40),
                P("advanced", "Full controls", "", 0, 1, 0, Toggle),
                P("threshold", "Threshold", "dB", -60, 0, -20),
                P("ratio", "Ratio", ":1", 1, 20, 3, Log),
                P("attack", "Attack", "ms", 0.1, 200, 5, Log),
                P("release", "Release", "ms", 10, 3000, 120, Log),
                P("makeup", "Makeup", "dB", -12, 24, 20.0 / 3.0),
            ]),
            new(DeEsser, "De-esser",
            [
                P("frequency", "Frequency", "Hz", 2000, 12000, 6000, Log),
                P("threshold", "Threshold", "dB", -60, 0, -30),
                P("range", "Range", "dB", 0, 24, 10),
                P("listen", "Listen", "", 0, 1, 0, Toggle),
            ]),
            new(Multiband, "Multiband compressor", multiband),
            new(Gate, "Noise gate",
            [
                P("threshold", "Threshold", "dB", -80, 0, -50),
                P("range", "Range", "dB", 0, 80, 40),
                P("attack", "Attack", "ms", 0.1, 50, 1, Log),
                P("hold", "Hold", "ms", 0, 1000, 100),
                P("release", "Release", "ms", 5, 2000, 150, Log),
            ]),
            new(Expander, "Expander",
            [
                P("threshold", "Threshold", "dB", -80, 0, -45),
                P("ratio", "Ratio", "1:", 1, 8, 2, Log),
                P("range", "Range", "dB", 0, 60, 20),
                P("attack", "Attack", "ms", 0.1, 50, 2, Log),
                P("release", "Release", "ms", 10, 2000, 150, Log),
            ]),
            new(Leveler, "Broadcast leveler",
            [
                P("target", "Target", "LUFS", -36, -6, -18),
                P("maxBoost", "Max boost", "dB", 0, 35, 12),
                P("maxCut", "Max cut", "dB", 0, 24, 12),
                P("speed", "Speed", "s", 0.5, 30, 5, Log),
                P("freeze", "Freeze below", "LUFS", -70, -20, -50),
            ]),
            new(Limiter, "Safety limiter",
            [
                P("ceiling", "Ceiling", "dB", -24, 0, -1),
                P("release", "Release", "ms", 5, 2000, 100, Log),
                P("truePeak", "True peak", "", 0, 1, 0, Toggle),
            ]),
            // Patch Prime's "width" is left out: the mic is one channel.
            new(Reverb, "Reverb",
            [
                P("size", "Size", "%", 0, 100, 50),
                P("decay", "Decay", "s", 0.2, 10, 2, Log),
                P("predelay", "Pre-delay", "ms", 0, 200, 20),
                P("damping", "Damping", "%", 0, 100, 50),
                P("mix", "Mix (wet)", "%", 0, 100, 25),
            ]),
            new(Gain, "Gain", [P("gainDb", "Gain", "dB", -24, 24, 0)]),
        ];
    }
}

/// <summary>One effect in the chain. <c>Id</c> names this instance (any short
/// text, unique in the chain): the engine keeps an effect's running state when
/// the chain is rearranged by matching ids. <c>Params</c> holds values by the
/// names in <see cref="StudioCatalogue"/>; missing ones take their default.</summary>
public sealed record StudioEffectConfig(
    string Id,
    string Type,
    bool Enabled,
    IReadOnlyDictionary<string, double>? Params);

/// <summary>The SimpleStudio chain: effects in the order they run.
/// <c>Enabled</c> off bypasses the whole chain (it costs nothing then).</summary>
public sealed record SimpleStudioConfig(bool Enabled, IReadOnlyList<StudioEffectConfig>? Effects)
{
    public static SimpleStudioConfig Default { get; } = new(false, []);

    /// <summary>A valid copy: unknown effect types dropped, at most
    /// <see cref="StudioCatalogue.MaxEffects"/> effects, every parameter present
    /// and in range, unknown parameters dropped, ids unique and not empty.</summary>
    public SimpleStudioConfig Normalized()
    {
        var effects = new List<StudioEffectConfig>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Effects ?? [])
        {
            if (effects.Count >= StudioCatalogue.MaxEffects) break;
            if (e is null || StudioCatalogue.Find(e.Type) is not { } info) continue;
            var values = info.Values(e.Params);
            var named = new Dictionary<string, double>(values.Length, StringComparer.Ordinal);
            for (int i = 0; i < values.Length; i++) named[info.Params[i].Name] = values[i];
            string id = (e.Id ?? "").Trim();
            if (id.Length > 40) id = id[..40];
            if (id.Length == 0 || !ids.Add(id))
            {
                int n = 1;
                do id = $"{info.Type}-{n++}"; while (!ids.Add(id));
            }
            effects.Add(new StudioEffectConfig(id, info.Type, e.Enabled, named));
        }
        return new SimpleStudioConfig(Enabled, effects);
    }
}

public sealed record SimpleStudioSetRequest(SimpleStudioConfig Config);

/// <summary>One effect's live readings. Levels are dB below full scale
/// (floored at -120): the peak since the last read and the RMS over about
/// 300 ms, going in and coming out. <c>Values</c> are the effect's own meters:
/// gate, expander, de-esser, limiter: [cut dB]; compressor: [gain reduction dB,
/// threshold in use dB]; multiband: four gain reductions (dB), then four
/// detector levels (dB above -80); leveler: [gain dB (+ = boost), loudness
/// LUFS]; the others none. Reductions are the largest since the last read.</summary>
public sealed record StudioEffectMeterDto(
    string Id,
    double InPeakDb,
    double InRmsDb,
    double OutPeakDb,
    double OutRmsDb,
    IReadOnlyList<double> Values);

/// <summary>Reading for <c>GET /api/tx/simple-studio/meters</c>. <c>Active</c>
/// is false when no TX block went through the chain recently (not keyed or
/// monitoring, a digital mode, or SimpleStudio off). Polling switches the
/// level measurement on for about 2 s; nothing is measured otherwise.</summary>
public sealed record SimpleStudioMetersDto(bool Active, IReadOnlyList<StudioEffectMeterDto> Effects)
{
    public static SimpleStudioMetersDto Inactive { get; } = new(false, []);
}
