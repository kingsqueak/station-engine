// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR: Brick SDR radios (Linoobs hardware, gateware by EU1SW).
//
// A Brick announces itself as an ordinary Hermes (Brick2) or Angelia (Brick3)
// board; what tells it apart is the locally administered MAC address its
// firmware sets: 02:B2:xx for a Brick2 (xx = 01 14-bit low power, 02 16-bit
// low power, 03 14-bit high power) and 02:B3:xx for a Brick3. Same scheme
// deskHPSDR uses (new_discovery.c).

namespace SimpleHPSDR.Contracts;

public enum BrickModel : byte
{
    None = 0,
    /// <summary>Brick2: Hermes / ANAN-10E class, one ADC, one antenna, 15 W.</summary>
    Brick2 = 2,
    /// <summary>Brick3: Angelia (ANAN-100D) class, two ADCs; its STM32 follows
    /// the band from the DDC0/DDC1 frequency words.</summary>
    Brick3 = 3,
}

public static class BrickRadios
{
    public static BrickModel FromMac(ReadOnlySpan<byte> mac) =>
        mac.Length >= 2 && mac[0] == 0x02
            ? mac[1] switch { 0xB2 => BrickModel.Brick2, 0xB3 => BrickModel.Brick3, _ => BrickModel.None }
            : BrickModel.None;

    public static BrickModel FromMac(System.Net.NetworkInformation.PhysicalAddress? mac) =>
        mac is null ? BrickModel.None : FromMac(mac.GetAddressBytes());

    /// <summary>A name for the radio picker, or null when it isn't a Brick.</summary>
    public static string? Name(System.Net.NetworkInformation.PhysicalAddress? mac)
    {
        if (mac is null) return null;
        var b = mac.GetAddressBytes();
        return FromMac(b) switch
        {
            BrickModel.Brick2 => b.Length > 2 ? b[2] switch
            {
                0x01 => "Brick2 14-bit LP",
                0x02 => "Brick2 16-bit LP",
                0x03 => "Brick2 14-bit HP",
                _ => "Brick2",
            } : "Brick2",
            BrickModel.Brick3 => "Brick3",
            _ => null,
        };
    }
}
