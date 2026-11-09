// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Generic;

namespace Zeus.Contracts;

/// <summary>The physical control kind a mapping expects — drives how a raw
/// MIDI event is interpreted (button = note-on/off, knob/slider = absolute
/// 0..127 value, wheel = relative ±delta encoder). Append-only.</summary>
public enum MidiControlType : byte
{
    Button = 0,
    KnobOrSlider = 1,
    Wheel = 2,
}

/// <summary>One operator-defined binding: a physical control on a named device
/// mapped to a Zeus command. <paramref name="ControlId"/> is the engine's
/// stable identifier for the control (e.g. "note:0:60" or "cc:0:7"). Min/Max
/// shape the value scaling for knobs/sliders; <paramref name="Toggle"/> marks a
/// button as a latching toggle vs momentary.</summary>
public sealed record MidiMappingDto(
    string DeviceName,
    string ControlId,
    MidiControlType ControlType,
    ZeusMidiCommand Command,
    int Min = 0,
    int Max = 127,
    bool Toggle = false);

/// <summary>One Stream Deck button → command binding, keyed by device serial
/// and zero-based button index.</summary>
public sealed record StreamDeckMappingDto(
    string Serial,
    int ButtonIndex,
    ZeusMidiCommand Command);

/// <summary>The full persisted controller binding document — MIDI control maps
/// plus Stream Deck key maps — versioned for forward migration. Stored as a
/// single row in <c>MidiConfigStore</c>. <paramref name="VfoWheelMessagesPerStep"/>
/// is the VFO wheel sensitivity (Thetis <c>MidiMessagesPerTuneStep</c>): how
/// many wheel messages make one tune step, 1..64. Optional so documents saved
/// before it existed load as 1 (every detent tunes).</summary>
public sealed record MidiBindingsDoc(
    int Version,
    IReadOnlyList<MidiMappingDto> Mappings,
    IReadOnlyList<StreamDeckMappingDto> StreamDeckMappings,
    int VfoWheelMessagesPerStep = 1)
{
    public const int CurrentVersion = 1;

    public static MidiBindingsDoc Empty { get; } = new(
        CurrentVersion,
        new List<MidiMappingDto>(),
        new List<StreamDeckMappingDto>());
}

/// <summary>A live control event pushed to the UI during Learn mode (MsgType
/// 0x3B). The panel highlights the matching control so the operator can bind
/// it. <paramref name="ControlType"/> is the engine's best guess from the wire
/// shape; <paramref name="Value"/> is the absolute 0..127 reading (or button
/// velocity); <paramref name="Delta"/> the relative encoder step when known.
/// Stream Deck buttons arrive with <paramref name="ControlType"/> = Button and
/// a "sd:&lt;serial&gt;:&lt;index&gt;" <paramref name="ControlId"/>; dials use
/// Wheel controls named "sd:&lt;serial&gt;:dial:&lt;index&gt;".</summary>
public sealed record MidiLearnFrame(
    string DeviceName,
    string ControlId,
    MidiControlType ControlType,
    int Value,
    int Delta);

