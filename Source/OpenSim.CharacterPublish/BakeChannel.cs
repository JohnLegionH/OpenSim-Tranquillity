/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;

namespace OpenSim.CharacterPublish;

/// <summary>
/// The avatar bake layers a face of a worn mesh can show (Bakes on Mesh). A face whose texture id is one of the
/// ids below shows the wearer's own bake for that layer; the viewer swaps the id in when it draws the attachment.
/// </summary>
public enum BakeChannel
{
    Head,
    Upper,
    Lower,
    Eyes,
    Skirt,
    Hair,
    LeftArm,
    LeftLeg,
    Aux1,
    Aux2,
    Aux3,
}

/// <summary>The "use baked" texture ids (Second Life viewer, llcommon/indra_constants.cpp IMG_USE_BAKED_*, lines 80-90).</summary>
public static class BakeChannelIds
{
    static readonly UUID[] Ids =
    {
        new("5a9f4a74-30f2-821c-b88d-70499d3e7183"), // Head
        new("ae2de45c-d252-50b8-5c6e-19f39ce79317"), // Upper
        new("24daea5f-0539-cfcf-047f-fbc40b2786ba"), // Lower
        new("52cc6bb6-2ee5-e632-d3ad-50197b1dcb8a"), // Eyes
        new("43529ce8-7faa-ad92-165a-bc4078371687"), // Skirt
        new("09aac1fb-6bce-0bee-7d44-caac6dbb6c63"), // Hair
        new("ff62763f-d60a-9855-890b-0c96f8f8cd98"), // LeftArm
        new("8e915e25-31d1-cc95-ae08-d58a47488251"), // LeftLeg
        new("9742065b-19b5-297c-858a-29711d539043"), // Aux1
        new("03642e83-2bd1-4eb9-34b4-4c47ed586d2d"), // Aux2
        new("edd51b77-fc10-ce7a-4b3d-011dfc349e4f"), // Aux3
    };

    public static UUID Of(BakeChannel channel) =>
        (uint)channel < Ids.Length ? Ids[(int)channel] : throw new ArgumentOutOfRangeException(nameof(channel));

    /// <summary>The channel an id stands for, or null when it is an ordinary texture id.</summary>
    public static BakeChannel? ChannelOf(UUID id)
    {
        var i = Array.IndexOf(Ids, id);
        return i < 0 ? null : (BakeChannel)i;
    }
}

/// <summary>
/// Which words a description must not start with. The server-side baking module's wearable parser reads the
/// description line as if it were a section, so a description that begins with one of these words is mis-read
/// (OpenSimNGC.Appearance.Baking/WearableParser.cs: the section scan starts at the description line).
/// Published descriptions are kept clear of them wherever they end up in a wearable.
/// </summary>
public static class SafeDescription
{
    static readonly string[] Keywords = { "type", "parameters", "textures" };

    /// <summary>True when the first word of the text is not one of the parser's section keywords.</summary>
    public static bool IsSafe(string description)
    {
        var s = description.AsSpan().TrimStart();
        foreach (var k in Keywords)
        {
            if (s.StartsWith(k, StringComparison.Ordinal) && (s.Length == k.Length || char.IsWhiteSpace(s[k.Length])))
                return false;
        }
        return true;
    }
}
