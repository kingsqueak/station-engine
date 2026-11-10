// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Reflection;

namespace SimpleHPSDR.Server;

/// <summary>
/// Engine-only resolver and ABI probe for the RADE native shim. A replacement
/// placed in the documented override directory wins over the bundled RID
/// artifact. An invalid replacement fails closed instead of silently selecting
/// the bundled copy. The probing itself lives in <see cref="EngineNativeLibraryProbe"/>.
/// </summary>
internal static class RadeNativeLoader
{
    internal const string OverrideDirectoryEnvironmentVariable =
        "SIMPLEHPSDR_RADE_NATIVE_OVERRIDE_DIR";

    private static readonly EngineNativeLibraryProbe Probe = new(
        RadeNativeMethods.LibraryName,
        OverrideDirectoryEnvironmentVariable,
        "rade",
        [
            "simplehpsdr_rade_global_init", "simplehpsdr_rade_global_shutdown",
            "simplehpsdr_rade_open", "simplehpsdr_rade_close", "simplehpsdr_rade_nin",
            "simplehpsdr_rade_nin_max", "simplehpsdr_rade_max_pcm_per_rx", "simplehpsdr_rade_rx",
            "simplehpsdr_rade_sync", "simplehpsdr_rade_freq_offset", "simplehpsdr_rade_snr_db",
            "simplehpsdr_rade_get_eoo_callsign", "simplehpsdr_rade_n_speech_samples",
            "simplehpsdr_rade_n_tx_out", "simplehpsdr_rade_n_tx_eoo_out", "simplehpsdr_rade_tx",
            "simplehpsdr_rade_tx_eoo", "simplehpsdr_rade_set_tx_callsign",
        ]);

    internal static bool TryProbeRade()
    {
        EngineNativeLibraryResolver.EnsureRegistered();
        return Probe.TryProbe(typeof(RadeNativeMethods).Assembly);
    }

    internal static string CurrentRid => EngineNativeLibraryProbe.CurrentRid;
    internal static string? SelectedPath => Probe.SelectedPath;
    internal static string? SelectedSha256 => Probe.SelectedSha256;
    internal static string? Failure => Probe.Failure;

    internal static string DefaultOverrideDirectory() => Probe.DefaultOverrideDirectory();

    internal static void ResetProbe() => Probe.Reset();

    internal static IntPtr ResolveLibrary(Assembly assembly) =>
        Probe.ResolveLibrary(assembly);
}
