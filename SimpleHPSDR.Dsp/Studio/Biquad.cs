// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Biquad filters for the SimpleStudio effects: RBJ "Audio EQ Cookbook"
// designs, run in transposed direct form II in double precision. Ported from
// Patch Prime (engine/src/eq.cpp and multiband.cpp, commit 2608d58).

namespace SimpleHPSDR.Dsp.Studio;

internal enum BiquadType
{
    Bell,
    LowShelf,
    HighShelf,
    /// <summary>A 12 dB/octave high-pass.</summary>
    LowCut,
    /// <summary>A 12 dB/octave low-pass.</summary>
    HighCut,
}

/// <summary>Normalised coefficients (a0 = 1).</summary>
internal struct BiquadCoefficients
{
    public double B0, B1, B2, A1, A2;

    public static BiquadCoefficients Identity => new() { B0 = 1.0 };
}

/// <summary>One filter's memory. A mutable struct: keep it in a field or an
/// array element and call Step on it there, never on a copy.</summary>
internal struct BiquadState
{
    public double Z0, Z1;

    public double Step(in BiquadCoefficients c, double x)
    {
        double y = (c.B0 * x) + Z0;
        Z0 = (c.B1 * x) - (c.A1 * y) + Z1;
        Z1 = (c.B2 * x) - (c.A2 * y);
        return y;
    }

    /// <summary>Out of denormals once the input has gone quiet.</summary>
    public void Flush(double below = 1e-18)
    {
        if (Math.Abs(Z0) < below) Z0 = 0.0;
        if (Math.Abs(Z1) < below) Z1 = 0.0;
    }

    public void Clear() => Z0 = Z1 = 0.0;
}

internal static class Biquad
{
    public const double MinFreq = 20.0;
    public const double MaxFreq = 20000.0;
    public const double MinGainDb = -24.0;
    public const double MaxGainDb = 24.0;
    public const double MinQ = 0.1;
    public const double MaxQ = 10.0;
    public static readonly double ButterworthQ = Math.Sqrt(2.0) / 2.0;

    public static BiquadCoefficients Design(BiquadType type, double freq, double gainDb, double q,
        double sampleRate)
    {
        // Keep clear of Nyquist, where the formulas fall apart.
        freq = Math.Clamp(freq, MinFreq, Math.Min(MaxFreq, 0.45 * sampleRate));
        q = Math.Clamp(q, MinQ, MaxQ);
        gainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cosw = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * q);
        double a = Math.Pow(10.0, gainDb / 40.0);
        double b0 = 1.0, b1 = 0.0, b2 = 0.0, a0 = 1.0, a1 = 0.0, a2 = 0.0;
        switch (type)
        {
            case BiquadType.Bell:
                b0 = 1.0 + (alpha * a);
                b1 = -2.0 * cosw;
                b2 = 1.0 - (alpha * a);
                a0 = 1.0 + (alpha / a);
                a1 = -2.0 * cosw;
                a2 = 1.0 - (alpha / a);
                break;
            case BiquadType.LowShelf:
            {
                double s = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) - ((a - 1.0) * cosw) + s);
                b1 = 2.0 * a * ((a - 1.0) - ((a + 1.0) * cosw));
                b2 = a * ((a + 1.0) - ((a - 1.0) * cosw) - s);
                a0 = (a + 1.0) + ((a - 1.0) * cosw) + s;
                a1 = -2.0 * ((a - 1.0) + ((a + 1.0) * cosw));
                a2 = (a + 1.0) + ((a - 1.0) * cosw) - s;
                break;
            }
            case BiquadType.HighShelf:
            {
                double s = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) + ((a - 1.0) * cosw) + s);
                b1 = -2.0 * a * ((a - 1.0) + ((a + 1.0) * cosw));
                b2 = a * ((a + 1.0) + ((a - 1.0) * cosw) - s);
                a0 = (a + 1.0) - ((a - 1.0) * cosw) + s;
                a1 = 2.0 * ((a - 1.0) - ((a + 1.0) * cosw));
                a2 = (a + 1.0) - ((a - 1.0) * cosw) - s;
                break;
            }
            case BiquadType.LowCut:
                b0 = (1.0 + cosw) / 2.0;
                b1 = -(1.0 + cosw);
                b2 = (1.0 + cosw) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosw;
                a2 = 1.0 - alpha;
                break;
            case BiquadType.HighCut:
                b0 = (1.0 - cosw) / 2.0;
                b1 = 1.0 - cosw;
                b2 = (1.0 - cosw) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosw;
                a2 = 1.0 - alpha;
                break;
        }
        return new BiquadCoefficients { B0 = b0 / a0, B1 = b1 / a0, B2 = b2 / a0, A1 = a1 / a0, A2 = a2 / a0 };
    }

    /// <summary>A second-order allpass at freq (Butterworth Q): flat, with the
    /// phase of a Linkwitz-Riley crossover there.</summary>
    public static BiquadCoefficients Allpass(double freq, double sampleRate)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cosw = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * ButterworthQ);
        double a0 = 1.0 + alpha;
        return new BiquadCoefficients
        {
            B0 = (1.0 - alpha) / a0,
            B1 = (-2.0 * cosw) / a0,
            B2 = 1.0,
            A1 = (-2.0 * cosw) / a0,
            A2 = (1.0 - alpha) / a0,
        };
    }

    /// <summary>The filter's magnitude in dB at freq.</summary>
    public static double ResponseDb(in BiquadCoefficients c, double freq, double sampleRate)
    {
        double w = 2.0 * Math.PI * freq / sampleRate;
        var z1 = System.Numerics.Complex.FromPolarCoordinates(1.0, -w);
        var z2 = z1 * z1;
        var h = (c.B0 + (c.B1 * z1) + (c.B2 * z2)) / (1.0 + (c.A1 * z1) + (c.A2 * z2));
        return 20.0 * Math.Log10(Math.Max(h.Magnitude, 1e-9));
    }
}
