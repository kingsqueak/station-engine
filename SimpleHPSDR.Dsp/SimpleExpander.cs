// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleExpander: a downward expander for the TX mic block.
//
// The alternative to WDSP's DEXP (a two-level gate). Signal chain:
//
//   detector  peak envelope of |mic x sidechain gain| (fast rise, 20 ms fall),
//             in dB below full scale. The sidechain gain is the mic gain the
//             TX chain applies later, so the threshold is on the mic meter's
//             scale and does not move when the mic gain does.
//   curve     below the threshold each dB becomes Ratio dB, through a 6 dB
//             soft knee, limited to RangeDb of cut.
//   ballistic the cut (in dB) follows the curve: towards less cut with the
//             attack time, towards more cut with the release time after the
//             hold time has run out.
//
// No look-ahead and no delay line: the output is the input times a smooth gain.
// A settings change swaps the coefficients and keeps the detector and gain
// state, so adjusting it while talking does not drop the audio.
//
// Threads: Process runs on the audio thread and owns all running state.
// Configure and ReadMeter may be called from any thread; they exchange
// immutable or single-word values only. No locks, no allocation in Process.

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp;

public sealed class SimpleExpander
{
    public const double LevelFloorDb = -120.0;
    private const double KneeDb = 6.0;
    private const double DetectorRiseMs = 0.3;
    private const double DetectorFallMs = 20.0;
    private const long MeterArmMs = 2000;
    private const long MeterStaleMs = 500;

    private sealed record Coefficients(
        SimpleExpanderConfig Config, int RateHz,
        double DetRise, double DetFall, double Attack, double Release, int HoldSamples);

    private volatile SimpleExpanderConfig _config = SimpleExpanderConfig.Default;
    private Coefficients? _coef;      // audio thread only
    private double _env;              // linear detector envelope
    private double _cutDb;            // cut being applied, >= 0
    private int _holdLeft;            // samples

    private long _meterArmedUntilMs = long.MinValue;
    private long _lastBlockMs = long.MinValue / 2;
    private double _meterLevelDb = LevelFloorDb;
    private double _meterCutDb;

    public SimpleExpanderConfig Config => _config;

    public void Configure(SimpleExpanderConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        _config = cfg.Clamped();
    }

    /// <summary>True when Process has work to do: enabled, or the meter was
    /// polled in the last two seconds.</summary>
    public bool Wanted =>
        _config.Enabled || Environment.TickCount64 <= Volatile.Read(ref _meterArmedUntilMs);

    /// <summary>Forget the detector and gain state (a bypassed stretch, a new
    /// transmission after a pause in processing).</summary>
    public void Reset()
    {
        _env = 0.0;
        _cutDb = 0.0;
        _holdLeft = 0;
    }

    /// <summary>The static curve: dB of cut for a detector level.</summary>
    public static double CutForLevel(double levelDb, double thresholdDb, double ratio, double rangeDb)
    {
        double slope = ratio - 1.0;
        double under = thresholdDb - levelDb;       // > 0 below the threshold
        double cut;
        if (under <= -KneeDb / 2) cut = 0.0;
        else if (under >= KneeDb / 2) cut = slope * under;
        else
        {
            double t = under + KneeDb / 2;          // 0..KneeDb across the knee
            cut = slope * t * t / (2 * KneeDb);
        }
        return Math.Min(cut, rangeDb);
    }

    /// <summary>Run one mic block. With the expander enabled the block is
    /// changed in place; otherwise (meter only) it is read and left alone.</summary>
    public void Process(Span<float> mic, int rateHz, double sidechainGain)
    {
        var cfg = _config;
        var c = _coef;
        if (c is null || !ReferenceEquals(c.Config, cfg) || c.RateHz != rateHz)
        {
            double rate = Math.Max(1, rateHz);
            static double Coef(double ms, double rate) => Math.Exp(-1.0 / (ms / 1000.0 * rate));
            c = new Coefficients(cfg, rateHz,
                Coef(DetectorRiseMs, rate), Coef(DetectorFallMs, rate),
                Coef(cfg.AttackMs, rate), Coef(cfg.ReleaseMs, rate),
                (int)(cfg.HoldMs / 1000.0 * rate));
            _coef = c;
        }
        if (!double.IsFinite(sidechainGain) || sidechainGain <= 0.0) sidechainGain = 1.0;

        double env = _env, cutDb = _cutDb, blockPeak = 0.0;
        int holdLeft = _holdLeft;
        bool apply = cfg.Enabled;
        double thr = cfg.ThresholdDb, ratio = cfg.Ratio, range = cfg.RangeDb;

        for (int i = 0; i < mic.Length; i++)
        {
            double x = mic[i];
            double a = Math.Abs(x) * sidechainGain;
            if (!double.IsFinite(a)) a = 0.0;
            env = a > env ? a + c.DetRise * (env - a) : a + c.DetFall * (env - a);
            if (env > blockPeak) blockPeak = env;

            double levelDb = env > 1e-6 ? 20.0 * Math.Log10(env) : LevelFloorDb;
            double target = CutForLevel(levelDb, thr, ratio, range);
            if (target < cutDb)
            {
                cutDb = target + c.Attack * (cutDb - target);
                holdLeft = c.HoldSamples;
            }
            else if (holdLeft > 0) holdLeft--;
            else cutDb = target + c.Release * (cutDb - target);

            if (apply) mic[i] = (float)(x * Math.Pow(10.0, -cutDb / 20.0));
        }

        if (env < 1e-9) env = 0.0;                  // keep denormals out
        _env = env; _cutDb = cutDb; _holdLeft = holdLeft;
        Volatile.Write(ref _meterLevelDb,
            blockPeak > 1e-6 ? 20.0 * Math.Log10(blockPeak) : LevelFloorDb);
        Volatile.Write(ref _meterCutDb, cutDb);
        Volatile.Write(ref _lastBlockMs, Environment.TickCount64);
    }

    /// <summary>Latest reading. Calling it keeps the detector running for two
    /// seconds while the expander is off.</summary>
    public SimpleExpanderMeterDto ReadMeter()
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _meterArmedUntilMs, now + MeterArmMs);
        if (now - Volatile.Read(ref _lastBlockMs) > MeterStaleMs)
            return SimpleExpanderMeterDto.Inactive;
        return new SimpleExpanderMeterDto(
            Volatile.Read(ref _meterLevelDb), Volatile.Read(ref _meterCutDb), true);
    }
}
