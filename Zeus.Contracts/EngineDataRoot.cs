// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR fork patch: give the engine its own data folder, so it never
// shares files with an installed Zeus.

namespace Zeus.Contracts;

/// <summary>
/// Where the engine keeps the files that otherwise go to Zeus's own folder
/// (LocalApplicationData/Zeus): FFT wisdom, the LAN certificate, NR3 models, the
/// default profile folder and, on Windows, the WSPR work folder. Set
/// <c>STATION_ENGINE_DATA_DIR</c> to a folder of the host's to use it instead.
/// While it is set the engine also leaves Zeus Link's files out of backups and
/// restores, since they belong to another program.
/// </summary>
public static class EngineDataRoot
{
    public const string EnvironmentVariable = "STATION_ENGINE_DATA_DIR";

    /// <summary>The host's folder, or null when the variable isn't set.</summary>
    public static string? Override
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir.Trim());
        }
    }

    /// <summary>True when the engine runs with a folder of its own.</summary>
    public static bool IsIsolated => Override is not null;

    /// <summary>The host's folder, or LocalApplicationData/<paramref name="zeusFolder"/> as upstream.</summary>
    public static string Resolve(string zeusFolder = "Zeus") =>
        Override ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.Create),
            zeusFolder);
}
