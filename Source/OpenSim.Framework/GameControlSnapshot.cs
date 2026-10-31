/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Framework;

// Temporary stub so the tests compile: every member returns a default value.
public sealed class GameControlSnapshot
{
    public const int RawAxisCount = 6;
    public const int ModeUnknown = -1;
    public const int ModeAvatar = 0;
    public const int ModeMouselook = 1;
    public const int ModeFlycam = 2;
    public const int ModeCaptive = 3;
    public const int ModeCursor = 4;

    public uint RawButtons { get; }
    public float[] RawAxes { get; } = Array.Empty<float>();
    public int Mode { get; }
    public uint SemanticButtons { get; }
    public float[] SemanticAxes { get; } = Array.Empty<float>();

    public static int SemanticAxisCount(int mode) => 0;

    public static bool TryCreate(uint rawButtons, ReadOnlySpan<float> rawAxes, int mode,
        uint semanticButtons, ReadOnlySpan<float> semanticAxes, out GameControlSnapshot snapshot)
    {
        snapshot = null;
        return false;
    }
}
