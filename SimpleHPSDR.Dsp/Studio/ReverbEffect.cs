// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/reverb.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>An algorithmic reverb: the input goes through a pre-delay and four
/// diffusing allpasses into an 8-line feedback delay network with a
/// Householder matrix. Each line is damped (high frequencies die first, like a
/// real room) and turned down per pass so the tail falls by 60 dB over the
/// decay time. Two lines' lengths move slowly, which keeps the tail from
/// ringing. The output is the mid of the network's left and right taps.
///
/// Parameters: "size" (%, the room: scales every line), "decay" (s, RT60),
/// "predelay" (ms), "damping" (%), "mix" (% wet). As an insert, a little mix;
/// on a reverb bus, mix 100 %. Size and pre-delay glide when changed, so
/// moving them doesn't click.
///
/// Bypassed, it stops taking in sound but rings out over the dry signal (what
/// is already in the pre-delay still goes in); once the tail has died away it
/// sleeps and costs nothing. Switched back on, a tail still ringing carries on.</summary>
internal sealed class ReverbEffect : StudioEffect
{
    public const int LineCount = 8;
    public const double MaxPredelayMs = 200.0;

    // Parameter positions, as in StudioCatalogue.
    private const int Size = 0;
    private const int Decay = 1;
    private const int Predelay = 2;
    private const int Damping = 3;
    private const int Mix = 4;

    private const int BlockFrames = 32;
    // Bypassed, the tail counts as gone below this (-100 dBFS) ...
    private const float QuietLevel = 1e-5f;
    // ... for this long: more than the longest pre-delay, so a tail that
    // hasn't started yet isn't taken for silence.
    private const double QuietSeconds = 0.5;
    // Matches StudioChain.CrossfadeSeconds: the on/off ramp takes as long as
    // the chain's bypass crossfade does for the other effects.
    private const double CrossfadeSeconds = 0.010;

    // Line lengths in ms at size factor 1: spread out and not multiples of each
    // other, so their echoes don't pile up on the same moments.
    private static readonly double[] LineMs = [29.7, 37.1, 41.1, 43.7, 53.3, 59.9, 67.1, 79.3];
    // Diffuser lengths in samples at 44.1 kHz (Schroeder/Freeverb style).
    private static readonly double[] DiffuserSamples = [142.0, 107.0, 379.0, 277.0];
    private const float Diffusion = 0.7f;
    private const double MinScale = 0.3; // size 0 %
    private const double MaxScale = 1.7; // size 100 %
    // How each line feeds left and right: different signs decorrelate them.
    private static readonly float[] ToLeft = [1, -1, 1, -1, 1, -1, 1, -1];
    private static readonly float[] ToRight = [1, 1, -1, -1, 1, 1, -1, -1];
    private const float OutputGain = 0.35f;

    /// <summary>A delay line read at a fractional position (linear interpolation).</summary>
    private sealed class Line
    {
        public float[] Buffer = [];
        public int Write;

        public void Push(float x)
        {
            Buffer[Write] = x;
            Write = (Write + 1) % Buffer.Length;
        }

        /// <summary>Samples back, at least 1.</summary>
        public float Read(double delay)
        {
            int size = Buffer.Length;
            delay = Math.Clamp(delay, 1.0, size - 1);
            int whole = (int)delay;
            float fraction = (float)(delay - whole);
            int newer = (Write + size - whole) % size;
            int older = (newer + size - 1) % size;
            return (Buffer[newer] * (1f - fraction)) + (Buffer[older] * fraction);
        }

        public void Clear()
        {
            Array.Clear(Buffer);
            Write = 0;
        }
    }

    /// <summary>A Schroeder allpass for diffusion.</summary>
    private sealed class Allpass
    {
        public float[] Buffer = [];
        public int At;

        public float Step(float x)
        {
            float delayed = Buffer[At];
            float y = delayed - (Diffusion * x);
            Buffer[At] = x + (Diffusion * y);
            At = (At + 1) % Buffer.Length;
            return y;
        }

        public void Clear()
        {
            Array.Clear(Buffer);
            At = 0;
        }
    }

    // Audio thread only (sized in Prepare).
    private double _rate = 48000.0;
    private readonly Line _pre = new();
    private readonly Allpass[] _diffusers = [new(), new(), new(), new()];
    private readonly Line[] _lines = [new(), new(), new(), new(), new(), new(), new(), new()];
    private readonly double[] _base = new double[LineCount];   // line lengths at size 1, in samples
    private readonly float[] _lowpass = new float[LineCount];  // damping filter state
    private readonly double[] _gain = new double[LineCount];   // per-pass gain for the decay
    private double _scale = 1.0;       // size factor, gliding per sample
    private double _preSamples;        // gliding per sample
    private double _scaleTarget = 1.0;
    private double _preTarget;
    private double _sampleGlide = 1.0; // per sample, ~100 ms
    private float _mixNow = 0.25f;     // smoothed, so changes don't step
    private float _smooth = 1f;        // per sample, ~10 ms
    private float _onNow = 1f;         // 1 = on, 0 = bypassed, ramping
    private float _onStep = 1f;        // per sample, over the chain's crossfade time
    private int _quietFrames;          // bypassed: how long the tail has been silent
    private int _sleepFrames;          // after that many, stop
    private bool _asleep;              // bypassed and the tail has gone
    private float _damp = 0.5f;        // one-pole coefficient
    private double _phase;             // modulation of line 0
    private double _phase2;            // of line 4, a bit slower
    private double _phaseStep;
    private double _modDepth;          // samples
    private float _denormal = 1e-18f;

    public ReverbEffect() : base(StudioCatalogue.Reverb) { }

    public override bool RingsOut => true;

    private static double ScaleFor(double sizePercent) =>
        MinScale + ((MaxScale - MinScale) * sizePercent / 100.0);

    public override void Prepare(double sampleRate)
    {
        var p = Params;
        _rate = sampleRate;
        _modDepth = 0.00015 * sampleRate; // about 7 samples at 48 kHz
        _phaseStep = 2.0 * Math.PI * 0.5 / sampleRate;
        for (int i = 0; i < LineCount; i++)
        {
            _base[i] = LineMs[i] * 0.001 * sampleRate;
            // The modulation adds up to twice its depth.
            _lines[i].Buffer = new float[(int)Math.Ceiling((_base[i] * MaxScale) + (2.0 * _modDepth) + 4.0)];
        }
        for (int i = 0; i < _diffusers.Length; i++)
            _diffusers[i].Buffer = new float[(int)Math.Max(1.0, Math.Round(DiffuserSamples[i] * sampleRate / 44100.0))];
        _pre.Buffer = new float[(int)Math.Ceiling(MaxPredelayMs * 0.001 * sampleRate) + 4];
        _scale = _scaleTarget = ScaleFor(p[Size]);
        _preSamples = _preTarget = p[Predelay] * 0.001 * sampleRate;
        _sampleGlide = 1.0 - Math.Exp(-1.0 / (0.1 * sampleRate));
        _smooth = (float)(1.0 - Math.Exp(-1.0 / (0.01 * sampleRate)));
        _mixNow = (float)(p[Mix] / 100.0);
        bool on = Enabled;
        _onNow = on ? 1f : 0f;
        _onStep = (float)(1.0 / Math.Max(1.0, CrossfadeSeconds * sampleRate));
        _quietFrames = 0;
        _sleepFrames = (int)(QuietSeconds * sampleRate);
        _asleep = !on;
        Clear();
        UpdateBlock(p, _scale);
    }

    private void Clear()
    {
        foreach (var line in _lines) line.Clear();
        foreach (var allpass in _diffusers) allpass.Clear();
        _pre.Clear();
        Array.Clear(_lowpass);
    }

    /// <summary>Only for a chain coming back from idle (bypass doesn't call it
    /// for an effect that rings out): the old tail is gone.</summary>
    public override void Resume()
    {
        Clear();
        _quietFrames = 0;
    }

    private void UpdateBlock(double[] p, double scale)
    {
        // Size and pre-delay glide sample by sample (in Process), so the read
        // positions never jump.
        _scaleTarget = ScaleFor(p[Size]);
        _preTarget = p[Predelay] * 0.001 * _rate;
        // Each pass through a line loses its share of 60 dB per decay time.
        double decay = p[Decay];
        for (int i = 0; i < LineCount; i++)
        {
            double length = _base[i] * scale;
            _gain[i] = Math.Pow(10.0, -3.0 * length / (decay * _rate));
        }
        // Damping: the lines' low-pass from 20 kHz (0 %) down to 1.5 kHz (100 %).
        double damping = p[Damping] / 100.0;
        double cutoff = Math.Min(20000.0 * Math.Pow(1500.0 / 20000.0, damping), 0.45 * _rate);
        _damp = (float)(1.0 - Math.Exp(-2.0 * Math.PI * cutoff / _rate));
    }

    public override void Process(Span<float> block)
    {
        bool on = Enabled;
        if (_asleep)
        {
            if (!on) return; // the dry signal, untouched
            _asleep = false;
            _quietFrames = 0;
        }
        var p = Params;
        float onTarget = on ? 1f : 0f;
        float peak = 0f;
        float mixTarget = (float)(p[Mix] / 100.0);
        int frames = block.Length;
        Span<float> output = stackalloc float[LineCount];
        Span<float> fed = stackalloc float[LineCount];

        Line[] lines = _lines;
        Allpass[] diffusers = _diffusers;
        float[] lowpass = _lowpass;
        double[] gains = _gain;
        double[] baseLength = _base;
        double scale = _scale;
        double preSamples = _preSamples;
        float mixNow = _mixNow;
        float onNow = _onNow;
        float onStep = _onStep;
        float smooth = _smooth;
        double sampleGlide = _sampleGlide;
        double phase = _phase;
        double phase2 = _phase2;
        double phaseStep = _phaseStep;
        double modDepth = _modDepth;
        float denormal = _denormal;
        const double twoPi = 2.0 * Math.PI;

        for (int start = 0; start < frames; start += BlockFrames)
        {
            UpdateBlock(p, scale);
            double scaleTarget = _scaleTarget;
            double preTarget = _preTarget;
            float damp = _damp;
            int end = Math.Min(frames, start + BlockFrames);
            for (int i = start; i < end; i++)
            {
                float input = block[i];
                onNow = onTarget > onNow ? Math.Min(onTarget, onNow + onStep)
                                         : Math.Max(onTarget, onNow - onStep);
                _pre.Push(input * onNow);
                scale += (scaleTarget - scale) * sampleGlide;
                preSamples += (preTarget - preSamples) * sampleGlide;
                mixNow += (mixTarget - mixNow) * smooth;
                // (The tiny alternating offset keeps the diffusers out of denormals.)
                float x = _pre.Read(Math.Max(1.0, preSamples)) + denormal;
                for (int d = 0; d < diffusers.Length; d++)
                    x = diffusers[d].Step(x);

                // Read the lines (two of them slowly wandering), damp, decay, mix.
                phase += phaseStep;
                if (phase > twoPi) phase -= twoPi;
                phase2 += phaseStep * 0.7;
                if (phase2 > twoPi) phase2 -= twoPi;
                float sum = 0f;
                for (int l = 0; l < LineCount; l++)
                {
                    double length = baseLength[l] * scale;
                    if (l == 0)
                        length += modDepth * (1.0 + Math.Sin(phase));
                    else if (l == 4)
                        length += modDepth * (1.0 + Math.Cos(phase2));
                    output[l] = lines[l].Read(length);
                    lowpass[l] += damp * (output[l] - lowpass[l]);
                    fed[l] = (float)gains[l] * lowpass[l];
                    sum += fed[l];
                }
                // Householder feedback: lossless, every line feeds every other.
                float spread = sum * (2f / LineCount);
                float left = 0f;
                float right = 0f;
                for (int l = 0; l < LineCount; l++)
                {
                    lines[l].Push(fed[l] - spread + x + denormal);
                    left += output[l] * ToLeft[l];
                    right += output[l] * ToRight[l];
                }
                denormal = -denormal; // keeps the lines out of denormals without a DC offset
                left *= OutputGain;
                right *= OutputGain;
                // One channel: the mid of the two sides (the width setting has
                // nothing to do here, so there is none).
                float wet = (left + right) * 0.5f;
                peak = Math.Max(peak, Math.Abs(wet));

                // On: dry and wet by the mix. Bypassed: all the dry, and the tail.
                block[i] = (input * (1f - (mixNow * onNow))) + (wet * mixNow);
            }
        }
        _scale = scale;
        _preSamples = preSamples;
        _mixNow = mixNow;
        _onNow = onNow;
        _phase = phase;
        _phase2 = phase2;
        _denormal = denormal;

        if (on || onNow > 0f || peak >= QuietLevel)
        {
            _quietFrames = 0;
        }
        else
        {
            _quietFrames += frames;
            if (_quietFrames >= _sleepFrames)
            {
                Clear();
                _asleep = true;
            }
        }
        // Damping filters out of denormals once the tail is gone.
        for (int l = 0; l < LineCount; l++)
        {
            if (Math.Abs(lowpass[l]) < 1e-20f) lowpass[l] = 0f;
        }
    }
}
