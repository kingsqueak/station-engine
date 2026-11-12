using System.Numerics;
using System.Runtime.InteropServices;

namespace SimpleHPSDR.Dsp;

/// <summary>
/// SimpleHPSDR: a high-resolution spectrum of a few kHz around one receiver's
/// dial, for the app's filter close-up. The receiver's IQ (display
/// orientation) is mixed so the dial sits at 0 Hz, low-pass filtered and
/// decimated to 16 kHz in one or two FIR stages, and kept in a ring. The FFT
/// runs only when a spectrum is asked for, so the cost while nobody looks is
/// the mixing and filtering, and the engine stops feeding it a few seconds
/// after the last request. Bins run low to high, 0 Hz (the dial) in the middle.
/// </summary>
public sealed class CloseUpAnalyzer
{
    public const int OutputRateHz = 16_000;
    public const int MaxFftSize = 16_384;
    private const int Stage1RateHz = 96_000;

    private readonly int _inRate;
    private readonly Fir? _stage1;
    private readonly Fir _stage2;
    private readonly object _ringLock = new();
    private readonly float[] _ringI = new float[MaxFftSize], _ringQ = new float[MaxFftSize];
    private int _ringPos;
    private long _written;
    private long _lastFeedTicks;
    private double _phase;
    private float[] _outI = new float[256], _outQ = new float[256];
    private static readonly Dictionary<int, (double[] Window, double Gain, double[] Cos, double[] Sin)> Plans = new();

    private CloseUpAnalyzer(int inRate, int d1, int d2)
    {
        _inRate = inRate;
        int rate1 = inRate / d1;
        // Stage 1 only has to keep what stage 2 will pass (to 10 kHz) clear of aliases.
        if (d1 > 1) _stage1 = new Fir(Design(inRate, 10_000, rate1 - 10_000), d1);
        // Stage 2: flat to 6 kHz, down by 10 kHz so nothing folds into the shown +-6 kHz.
        _stage2 = new Fir(Design(rate1, 6_000, OutputRateHz - 6_000), d2);
    }

    /// <summary>Null when the receiver's rate doesn't divide down to 16 kHz.</summary>
    public static CloseUpAnalyzer? TryCreate(int inRateHz)
    {
        if (inRateHz <= 0 || inRateHz % OutputRateHz != 0) return null;
        int d1 = inRateHz > Stage1RateHz && inRateHz % Stage1RateHz == 0 ? inRateHz / Stage1RateHz : 1;
        int d2 = inRateHz / d1 / OutputRateHz;
        return new CloseUpAnalyzer(inRateHz, d1, d2);
    }

    public int InputRateHz => _inRate;

    /// <summary>One block of interleaved IQ; the dial is <paramref name="dialOffsetHz"/>
    /// from the centre of this IQ.</summary>
    public void Feed(ReadOnlySpan<double> iq, int dialOffsetHz)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool gap = _lastFeedTicks != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastFeedTicks, now).TotalSeconds > 0.5;
        _lastFeedTicks = now;
        if (gap) { _stage1?.Reset(); _stage2.Reset(); lock (_ringLock) _written = 0; }

        int n = iq.Length / 2;
        if (_outI.Length < n) { _outI = new float[n]; _outQ = new float[n]; }
        int outs = 0;
        // Mix by -offset with a rotator, renormalised every block.
        double step = -2 * Math.PI * dialOffsetHz / _inRate;
        double c = Math.Cos(_phase), s = Math.Sin(_phase);
        double sc = Math.Cos(step), ss = Math.Sin(step);
        for (int k = 0; k < n; k++)
        {
            double i = iq[2 * k], q = iq[2 * k + 1];
            float mi = (float)(i * c - q * s), mq = (float)(i * s + q * c);
            double nc = c * sc - s * ss;
            s = c * ss + s * sc;
            c = nc;
            if (_stage1 is { } f1)
            {
                if (!f1.Push(mi, mq, out mi, out mq)) continue;
            }
            if (_stage2.Push(mi, mq, out var oi, out var oq))
            {
                _outI[outs] = oi;
                _outQ[outs] = oq;
                outs++;
            }
        }
        _phase = Math.IEEERemainder(_phase + step * n, 2 * Math.PI);

        if (outs == 0) return;
        lock (_ringLock)
        {
            for (int k = 0; k < outs; k++)
            {
                _ringI[_ringPos] = _outI[k];
                _ringQ[_ringPos] = _outQ[k];
                if (++_ringPos == MaxFftSize) _ringPos = 0;
            }
            _written += outs;
        }
    }

    /// <summary>The latest <paramref name="fftSize"/>-point spectrum in dB
    /// (about dBFS), low to high, the dial at bin fftSize/2. False until
    /// enough samples have come in.</summary>
    public bool TryGetSpectrum(int fftSize, Span<float> dbOut)
    {
        if (fftSize < 64 || fftSize > MaxFftSize || (fftSize & (fftSize - 1)) != 0 || dbOut.Length < fftSize)
            return false;
        var re = new double[fftSize];
        var im = new double[fftSize];
        lock (_ringLock)
        {
            if (_written < fftSize) return false;
            int start = (_ringPos - fftSize + MaxFftSize) % MaxFftSize;
            for (int k = 0; k < fftSize; k++)
            {
                int j = (start + k) % MaxFftSize;
                re[k] = _ringI[j];
                im[k] = _ringQ[j];
            }
        }
        var (window, gain, cos, sin) = Plan(fftSize);
        for (int k = 0; k < fftSize; k++) { re[k] *= window[k]; im[k] *= window[k]; }
        Fft(re, im, cos, sin);
        int half = fftSize / 2;
        for (int k = 0; k < fftSize; k++)
        {
            int b = (k + half) & (fftSize - 1);   // fftshift: most negative first
            double p = (re[b] * re[b] + im[b] * im[b]) * gain;
            dbOut[k] = (float)(10 * Math.Log10(p + 1e-30));
        }
        return true;
    }

    private static (double[] Window, double Gain, double[] Cos, double[] Sin) Plan(int n)
    {
        lock (Plans)
        {
            if (Plans.TryGetValue(n, out var p)) return p;
            // 4-term Blackman-Harris: -92 dB sidelobes, so weak signals beside strong ones stay visible.
            var w = new double[n];
            double sum = 0;
            for (int k = 0; k < n; k++)
            {
                double x = 2 * Math.PI * k / n;
                w[k] = 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x);
                sum += w[k];
            }
            var cos = new double[n / 2];
            var sin = new double[n / 2];
            for (int k = 0; k < n / 2; k++)
            {
                cos[k] = Math.Cos(2 * Math.PI * k / n);
                sin[k] = -Math.Sin(2 * Math.PI * k / n);
            }
            p = (w, 1.0 / (sum * sum), cos, sin);
            Plans[n] = p;
            return p;
        }
    }

    // In-place iterative radix-2 forward FFT.
    private static void Fft(double[] re, double[] im, double[] cos, double[] sin)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int halfLen = len >> 1, stride = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0; k < halfLen; k++)
                {
                    double wr = cos[k * stride], wi = sin[k * stride];
                    int a = i + k, b = a + halfLen;
                    double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
    }

    // Windowed-sinc low-pass (Blackman, about -74 dB) with the cut halfway
    // between pass and stop; unity gain at 0 Hz.
    private static float[] Design(int rate, double passHz, double stopHz)
    {
        int taps = (int)Math.Ceiling(5.5 * rate / (stopHz - passHz)) | 1;
        double fc = (passHz + stopHz) / 2 / rate;
        var h = new double[taps];
        double sum = 0, m = taps - 1;
        for (int k = 0; k < taps; k++)
        {
            double x = k - m / 2;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * k / m) + 0.08 * Math.Cos(4 * Math.PI * k / m);
            h[k] = sinc * w;
            sum += h[k];
        }
        return h.Select(v => (float)(v / sum)).ToArray();
    }

    /// <summary>A decimating complex FIR with real taps. The history is kept
    /// twice over so each output is one contiguous dot product.</summary>
    private sealed class Fir(float[] taps, int decimation)
    {
        private readonly float[] _h = taps;
        private readonly float[] _i = new float[2 * taps.Length], _q = new float[2 * taps.Length];
        private int _pos, _count;

        public void Reset()
        {
            Array.Clear(_i);
            Array.Clear(_q);
            _pos = _count = 0;
        }

        public bool Push(float i, float q, out float oi, out float oq)
        {
            int l = _h.Length;
            _i[_pos] = _i[_pos + l] = i;
            _q[_pos] = _q[_pos + l] = q;
            if (++_pos == l) _pos = 0;
            if (++_count < decimation) { oi = oq = 0; return false; }
            _count = 0;
            // Oldest sample is at _pos; taps are symmetric so the order doesn't matter.
            oi = Dot(_i.AsSpan(_pos, l), _h);
            oq = Dot(_q.AsSpan(_pos, l), _h);
            return true;
        }

        private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            var acc = Vector<float>.Zero;
            int k = 0, w = Vector<float>.Count;
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            for (int v = 0; v < va.Length; v++) acc += va[v] * vb[v];
            k = va.Length * w;
            float sum = Vector.Dot(acc, Vector<float>.One);
            for (; k < a.Length; k++) sum += a[k] * b[k];
            return sum;
        }
    }
}
