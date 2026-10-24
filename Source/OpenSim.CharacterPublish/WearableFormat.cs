/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;

namespace OpenSim.CharacterPublish;

/// <summary>Number and limit rules shared by the wearable writer and reader.</summary>
public static class WearableFormat
{
    /// <summary>The viewer reads at most this many parameters from a wearable (llwearable.cpp:220-221 constants).</summary>
    public const int MaxParameters = 1000;

    /// <summary>The viewer reads at most this many textures from a wearable.</summary>
    public const int MaxTextures = 100;

    /// <summary>Texture slots run 0 to TEX_NUM_INDICES - 1 (llavatarappearancedefines.h:96); 45 slots.</summary>
    public const int TextureSlotCount = 45;

    /// <summary>The viewer reads each line into a 2048 byte buffer, so a name or description must be shorter.</summary>
    public const int MaxLineBytes = 2047;

    /// <summary>Digits after the point in a written weight (the viewer prints two).</summary>
    const int Decimals = 2;

    /// <summary>
    /// A parameter weight as the viewer writes it: printf "%.2f", then trailing zeros and a bare point removed,
    /// and the leading zero of a fraction dropped ("1.20" is "1.2", "24.00" is "24", "0.59" is ".59", "-0.59" is "-.59").
    /// A weight that rounds to zero is "0", or "-0" when it was negative. Culture-invariant.
    /// </summary>
    public static string TerseFloat(float f)
    {
        if (!float.IsFinite(f)) throw new ArgumentOutOfRangeException(nameof(f), "a wearable parameter must be a finite number");
        // Widening to double is exact, so this rounds the float's exact value, as printf does for a promoted float.
        var s = ((double)f).ToString("F" + Decimals, CultureInfo.InvariantCulture);
        var negative = s[0] == '-';
        if (negative) s = s[1..];
        var point = s.IndexOf('.');
        var whole = s[..point];
        var fraction = s[(point + 1)..].TrimEnd('0');
        if (whole == "0" && fraction.Length > 0) whole = "";
        var body = fraction.Length > 0 ? whole + "." + fraction : whole;
        return negative ? "-" + body : body;
    }
}
