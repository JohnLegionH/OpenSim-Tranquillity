/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Converter.Merge;

/// <summary>The smallest box that holds the texture coordinates of a face, in texture units.</summary>
public readonly record struct UvBounds(float MinU, float MaxU, float MinV, float MaxV)
{
    public float RangeU => MaxU - MinU;
    public float RangeV => MaxV - MinV;

    public UvBounds Union(UvBounds o) => new UvBounds(Math.Min(MinU, o.MinU), Math.Max(MaxU, o.MaxU), Math.Min(MinV, o.MinV), Math.Max(MaxV, o.MaxV));
}

/// <summary>How far a texture coordinate can move when a mesh asset stores it, measured in texels of the texture it is drawn with.</summary>
public static class UvPrecision
{
    /// <summary>The most a merge lets a coordinate move, in texels of its face's texture.</summary>
    public const double MaxTexels = 0.5;

    /// <summary>The size assumed for a texture the archive does not hold.</summary>
    public const int UnknownResolution = 1024;

    public static double Texels(UvBounds bounds, int width, int height) => 0;
}
