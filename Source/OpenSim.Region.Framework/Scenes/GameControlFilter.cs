/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse.Packets;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes;

/// <summary>
/// Turns the viewer's GameControlData messages for one agent into validated <see cref="GameControlSnapshot"/>s,
/// and drops the ones that carry nothing the script side needs to hear about.
/// </summary>
/// <remarks>
/// Format reference: the GameControlData entry in the Linden Lab viewer's message_template.msg (release 26.5).
/// The message has a one-byte sequence number (wraps at 255), the viewer's control mode, and four variable
/// lists: canonical axes (signed 16-bit, six used), canonical buttons (one 32-bit mask), mode axes (count
/// depends on the mode) and mode buttons (one mask). A list the viewer left out means zero.
///
/// The viewer repeats its last state with growing gaps until it hears nothing new, up to 45 times a second
/// while input is changing. Per message this filter:
/// <list type="number">
/// <item>drops it if the sequence number is the one just seen or up to 127 behind it (late or repeated);</item>
/// <item>decodes it, cutting lists to their allowed length, and drops it if a value is not finite;</item>
/// <item>drops it if it is identical to the last state it passed (a resend);</item>
/// <item>drops it if <see cref="MaxEventsPerSecond"/> states have already passed in the current second.</item>
/// </list>
/// Only the last step's drops lose information, and the viewer's repeat of its final state restores it.
/// A message dropped by the rate cap is not remembered as the last state, so it is not mistaken for a resend.
///
/// One instance per agent. Not thread safe; the packet handler for one agent runs on one thread at a time.
/// </remarks>
public sealed class GameControlFilter
{
    /// <summary>
    /// Most states passed on per second for one agent. The viewer sends at most 45 messages a second (its
    /// minimum send period is 1/45 s), so 60 leaves a third of headroom for messages that arrive bunched
    /// together after network jitter, while still bounding what a modified viewer can make the region do.
    /// </summary>
    public const int MaxEventsPerSecond = 60;

    private const long RateWindowMilliseconds = 1000;
    private const int MaxCanonicalAxes = GameControlSnapshot.RawAxisCount;

    // A signed 16-bit axis value of 32767 is full deflection (1.0); -32768 is clamped to -1.0.
    private const float AxisScale = 1f / short.MaxValue;

    private const int CursorPixelX = 2;
    private const int CursorPixelY = 3;

    private readonly Func<long> m_nowMilliseconds;

    private bool m_haveSequence;
    private byte m_lastSequence;
    private GameControlSnapshot m_lastPassed;
    private long m_windowStart = -RateWindowMilliseconds;   // so the first message always opens a window
    private int m_passedInWindow;

    /// <param name="nowMilliseconds">Clock for the rate cap; defaults to the process tick count.</param>
    public GameControlFilter(Func<long> nowMilliseconds = null)
    {
        m_nowMilliseconds = nowMilliseconds ?? (() => Environment.TickCount64);
    }

    /// <summary>
    /// Returns the validated state to hand on, or null if the message is to be ignored.
    /// </summary>
    public GameControlSnapshot Process(GameControlDataPacket packet)
    {
        if (packet?.AgentData is null)
            return null;

        byte sequence = packet.AgentData.Packet;
        if (m_haveSequence)
        {
            // Distance ahead of the last message, modulo 256: 0 is a repeat, 128 or more means it is behind.
            byte ahead = (byte)(sequence - m_lastSequence);
            if (ahead == 0 || ahead >= 128)
                return null;
        }
        m_haveSequence = true;
        m_lastSequence = sequence;

        if (!TryDecode(packet, out GameControlSnapshot snapshot))
            return null;

        if (snapshot.Equals(m_lastPassed))
            return null;

        long now = m_nowMilliseconds();
        if (now - m_windowStart >= RateWindowMilliseconds)
        {
            m_windowStart = now;
            m_passedInWindow = 0;
        }
        if (m_passedInWindow >= MaxEventsPerSecond)
            return null;

        m_passedInWindow++;
        m_lastPassed = snapshot;
        return snapshot;
    }

    private static bool TryDecode(GameControlDataPacket packet, out GameControlSnapshot snapshot)
    {
        int mode = packet.AgentData.ActionMode;

        Span<float> raw = stackalloc float[MaxCanonicalAxes];
        DecodeAxes(packet.Axes, raw);

        int semanticCount = GameControlSnapshot.SemanticAxisCount(mode);
        Span<float> semantic = stackalloc float[semanticCount];
        DecodeModeAxes(packet.ModeAxes, semantic, isCursor: mode == GameControlSnapshot.ModeCursor);

        uint rawButtons = 0;
        if (packet.Buttons is { Length: > 0 } && packet.Buttons[0] is not null)
            rawButtons = packet.Buttons[0].Flags;

        uint semanticButtons = 0;
        if (packet.ModeButtons is { Length: > 0 } && packet.ModeButtons[0] is not null)
            semanticButtons = packet.ModeButtons[0].Flags;

        return GameControlSnapshot.TryCreate(rawButtons, raw, mode, semanticButtons, semantic, out snapshot);
    }

    private static void DecodeAxes(GameControlDataPacket.AxesBlock[] blocks, Span<float> into)
    {
        if (blocks is null)
            return;
        for (int i = 0; i < into.Length && i < blocks.Length; i++)
        {
            if (blocks[i] is not null)
                into[i] = blocks[i].Value * AxisScale;
        }
    }

    private static void DecodeModeAxes(GameControlDataPacket.ModeAxesBlock[] blocks, Span<float> into, bool isCursor)
    {
        if (blocks is null)
            return;
        for (int i = 0; i < into.Length && i < blocks.Length; i++)
        {
            if (blocks[i] is null)
                continue;
            short value = blocks[i].Value;
            // The cursor's pixel position is sent as whole pixels, not as a fraction of full deflection.
            bool isPixel = isCursor && (i == CursorPixelX || i == CursorPixelY);
            into[i] = isPixel ? value : value * AxisScale;
        }
    }
}
