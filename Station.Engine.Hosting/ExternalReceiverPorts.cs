// SPDX-License-Identifier: GPL-2.0-or-later

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Server;

/// <summary>
/// Product-neutral pull source for one external 48 kHz mono RX stream.
/// </summary>
/// <remarks>
/// <see cref="Read"/> is a realtime callback: implementations must not
/// allocate, block, log, or allow exceptions to escape.
/// </remarks>
public interface IExternalRxAudioSource
{
    bool Active { get; }
    int Read(Span<float> destination);
}

/// <summary>Standalone external-audio default that produces no samples.</summary>
public sealed class NullExternalRxAudioSource : IExternalRxAudioSource
{
    public bool Active => false;
    public int Read(Span<float> destination) => 0;
}
