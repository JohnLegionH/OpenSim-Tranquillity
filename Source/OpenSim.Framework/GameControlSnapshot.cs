/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Framework;

/// <summary>
/// One validated game-controller state for one agent, as sent by a viewer in the GameControlData message.
/// </summary>
/// <remarks>
/// The wire format is Linden Lab's (message_template.msg in the viewer, release 26.5): six canonical axes and a
/// 32-bit canonical button mask, plus a second set of axes and buttons arranged for the viewer's current control
/// mode. LSL names for the same values are in the lsl-definitions project (GAME_CONTROL_AXIS_*,
/// GAME_CONTROL_BUTTON_*, GAME_CONTROL_MODE_*).
///
/// The values here have been range-checked: no axis is NaN or infinite, canonical axes are within -1 to 1, and
/// the semantic axes are within the range their mode gives them. Instances are immutable once created.
/// </remarks>
public sealed class GameControlSnapshot : IEquatable<GameControlSnapshot>
{
    /// <summary>Number of canonical axes: left stick X and Y, right stick X and Y, left and right trigger.</summary>
    public const int RawAxisCount = 6;

    /// <summary>The viewer reported a mode this simulator does not know. Semantic data is dropped for it.</summary>
    public const int ModeUnknown = -1;
    public const int ModeAvatar = 0;
    public const int ModeMouselook = 1;
    public const int ModeFlycam = 2;
    public const int ModeCaptive = 3;
    public const int ModeCursor = 4;

    // Semantic axes by mode (viewer message_template.msg, GameControlData):
    //   avatar, mouselook, captive: strafe, advance, turn, look, rise (5)
    //   flycam: truck, dolly, pan, tilt, boom, roll, zoom (7)
    //   cursor: dx, dy, pixel x, pixel y, normalised x, normalised y (6)
    private const int MovementAxisCount = 5;
    private const int FlycamAxisCount = 7;
    private const int CursorAxisCount = 6;
    private const int CursorPixelX = 2;
    private const int CursorPixelY = 3;
    private const int CursorNormalisedX = 4;
    private const int CursorNormalisedY = 5;

    // The wire carries signed 16-bit values; a cursor pixel position can be no larger than this.
    private const float MaxCursorPixel = short.MaxValue;

    private static readonly float[] NoAxes = Array.Empty<float>();

    private readonly float[] m_rawAxes;
    private readonly float[] m_semanticAxes;

    /// <summary>Canonical button mask, bit 0 = south face button ... bit 20 = touchpad.</summary>
    public uint RawButtons { get; }

    /// <summary>The six canonical axes, each within -1 to 1. Do not modify.</summary>
    public float[] RawAxes => m_rawAxes;

    /// <summary>One of the Mode* values, or <see cref="ModeUnknown"/>.</summary>
    public int Mode { get; }

    /// <summary>Button mask for the current mode; bit meanings depend on the mode.</summary>
    public uint SemanticButtons { get; }

    /// <summary>
    /// Axes for the current mode, <see cref="SemanticAxisCount"/> long. Do not modify.
    /// </summary>
    public float[] SemanticAxes => m_semanticAxes;

    private GameControlSnapshot(uint rawButtons, float[] rawAxes, int mode, uint semanticButtons, float[] semanticAxes)
    {
        RawButtons = rawButtons;
        m_rawAxes = rawAxes;
        Mode = mode;
        SemanticButtons = semanticButtons;
        m_semanticAxes = semanticAxes;
    }

    /// <summary>Number of semantic axes the mode carries; 0 for an unknown mode.</summary>
    public static int SemanticAxisCount(int mode)
    {
        return mode switch
        {
            ModeAvatar or ModeMouselook or ModeCaptive => MovementAxisCount,
            ModeFlycam => FlycamAxisCount,
            ModeCursor => CursorAxisCount,
            _ => 0,
        };
    }

    /// <summary>
    /// Builds a snapshot from decoded values, or refuses it.
    /// </summary>
    /// <remarks>
    /// Refused (returns false): any NaN or infinite value in either axis list, including values past the
    /// positions that would be kept, since one bad value makes the whole message suspect.
    /// Otherwise: the canonical axes are padded with zero to six or cut at six and clamped to -1 to 1; a mode
    /// outside 0 to 4 becomes <see cref="ModeUnknown"/> with no semantic axes and no semantic buttons; the
    /// semantic axes are padded or cut to the mode's count and clamped to the mode's ranges.
    /// </remarks>
    public static bool TryCreate(uint rawButtons, ReadOnlySpan<float> rawAxes, int mode,
        uint semanticButtons, ReadOnlySpan<float> semanticAxes, out GameControlSnapshot snapshot)
    {
        snapshot = null;

        if (!AllFinite(rawAxes) || !AllFinite(semanticAxes))
            return false;

        float[] raw = new float[RawAxisCount];
        for (int i = 0; i < RawAxisCount && i < rawAxes.Length; i++)
            raw[i] = Math.Clamp(rawAxes[i], -1f, 1f);

        if (mode < ModeAvatar || mode > ModeCursor)
        {
            snapshot = new GameControlSnapshot(rawButtons, raw, ModeUnknown, 0, NoAxes);
            return true;
        }

        float[] semantic = new float[SemanticAxisCount(mode)];
        for (int i = 0; i < semantic.Length && i < semanticAxes.Length; i++)
            semantic[i] = ClampSemantic(mode, i, semanticAxes[i]);

        snapshot = new GameControlSnapshot(rawButtons, raw, mode, semanticButtons, semantic);
        return true;
    }

    private static bool AllFinite(ReadOnlySpan<float> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]))
                return false;
        }
        return true;
    }

    private static float ClampSemantic(int mode, int index, float value)
    {
        if (mode == ModeCursor)
        {
            switch (index)
            {
                case CursorPixelX:
                case CursorPixelY:
                    return Math.Clamp(value, 0f, MaxCursorPixel);
                case CursorNormalisedX:
                case CursorNormalisedY:
                    return Math.Clamp(value, 0f, 1f);
            }
        }
        return Math.Clamp(value, -1f, 1f);
    }

    public bool Equals(GameControlSnapshot other)
    {
        return other is not null
            && RawButtons == other.RawButtons
            && Mode == other.Mode
            && SemanticButtons == other.SemanticButtons
            && m_rawAxes.AsSpan().SequenceEqual(other.m_rawAxes)
            && m_semanticAxes.AsSpan().SequenceEqual(other.m_semanticAxes);
    }

    public override bool Equals(object obj) => Equals(obj as GameControlSnapshot);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(RawButtons);
        hash.Add(Mode);
        hash.Add(SemanticButtons);
        foreach (float v in m_rawAxes)
            hash.Add(v);
        foreach (float v in m_semanticAxes)
            hash.Add(v);
        return hash.ToHashCode();
    }
}
