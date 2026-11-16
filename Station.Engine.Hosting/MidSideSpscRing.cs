// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;

namespace SimpleHPSDR.Server;

/// <summary>
/// A single-producer, single-consumer ring of audio frames, each a mid (mono)
/// sample and a side sample: left = mid + side, right = mid - side. The two
/// are stored together so they can never slip against each other. Counts are
/// in frames. Same threading rules as <see cref="FloatSpscRing"/>.
/// </summary>
internal sealed class MidSideSpscRing
{
    private readonly float[] _buffer;   // mid, side, mid, side, ...
    private readonly int _mask;
    private readonly int _capacity;     // frames
    private FloatSpscPaddedCursors _cursors;

    public MidSideSpscRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacityPowerOfTwo));
        _buffer = new float[capacityPowerOfTwo * 2];
        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            long diff = Volatile.Read(ref _cursors.Tail) - Volatile.Read(ref _cursors.Head);
            return diff < 0 ? 0 : diff > _capacity ? _capacity : (int)diff;
        }
    }

    /// <summary>Producer side. A side span shorter than mid (or empty) is
    /// filled out with zeros: plain mono. Returns the frames written.</summary>
    public int Write(ReadOnlySpan<float> mid, ReadOnlySpan<float> side = default)
    {
        long tail = _cursors.Tail;
        long space = _capacity - (tail - Volatile.Read(ref _cursors.Head));
        if (space <= 0) return 0;
        int n = Math.Min(mid.Length, (int)space);
        for (int i = 0; i < n; i++)
        {
            int at = (int)((tail + i) & _mask) * 2;
            _buffer[at] = mid[i];
            _buffer[at + 1] = i < side.Length ? side[i] : 0f;
        }
        Volatile.Write(ref _cursors.Tail, tail + n);
        return n;
    }

    /// <summary>Consumer side. Reads up to mid.Length frames; side must be at
    /// least as long as mid. Returns the frames read.</summary>
    public int Read(Span<float> mid, Span<float> side)
    {
        long head = _cursors.Head;
        long avail = Volatile.Read(ref _cursors.Tail) - head;
        if (avail <= 0) return 0;
        int n = Math.Min(mid.Length, (int)avail);
        for (int i = 0; i < n; i++)
        {
            int at = (int)((head + i) & _mask) * 2;
            mid[i] = _buffer[at];
            side[i] = _buffer[at + 1];
        }
        Volatile.Write(ref _cursors.Head, head + n);
        return n;
    }

    /// <summary>Consumer side: drop up to count of the oldest frames.</summary>
    public int Skip(int count)
    {
        if (count <= 0) return 0;
        long head = _cursors.Head;
        long avail = Volatile.Read(ref _cursors.Tail) - head;
        if (avail <= 0) return 0;
        int n = Math.Min(count, (int)avail);
        Volatile.Write(ref _cursors.Head, head + n);
        return n;
    }

    public void Clear() => Volatile.Write(ref _cursors.Head, Volatile.Read(ref _cursors.Tail));
}
