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

    /// <summary>
    /// The most a coordinate of a face moves when the mesh asset stores it, in texels. The asset keeps each coordinate in 16 bits over the
    /// range of its face (Q(x / range * 65535), rounded), so it is off by at most half a step: range / (2 * 65535).
    /// </summary>
    public static double Texels(UvBounds bounds, int width, int height) =>
        Math.Max(bounds.RangeU * (double)Math.Max(1, width), bounds.RangeV * (double)Math.Max(1, height)) / (2.0 * 65535);

    private static double Texels(UvBounds bounds, MergeFace face) =>
        Texels(bounds, face.TextureWidth > 0 ? face.TextureWidth : UnknownResolution, face.TextureHeight > 0 ? face.TextureHeight : UnknownResolution);

    /// <summary>True when the face, drawn alone from a mesh asset, would be off by more than <see cref="MaxTexels"/>.</summary>
    public static bool TooWide(MergeFace face) => Texels(face.Uv, face) > MaxTexels;

    /// <summary>
    /// Splits the faces of one material into groups that can share a mesh face: the coordinates of a group, taken together, stay within
    /// <see cref="MaxTexels"/>. Faces are taken in order of their lowest coordinates and each joins the first group it fits; the result
    /// depends only on the faces and their order.
    /// </summary>
    public static List<List<MergeFace>> Groups(IReadOnlyList<MergeFace> faces)
    {
        List<(UvBounds bounds, List<MergeFace> faces)> groups = new List<(UvBounds, List<MergeFace>)>();
        foreach (int i in Enumerable.Range(0, faces.Count).OrderBy(i => faces[i].Uv.MinU).ThenBy(i => faces[i].Uv.MinV).ThenBy(i => i))
        {
            MergeFace f = faces[i];
            int at = -1;
            for (int g = 0; g < groups.Count && at < 0; g++)
                if (Texels(groups[g].bounds.Union(f.Uv), f) <= MaxTexels)
                    at = g;
            if (at < 0)
                groups.Add((f.Uv, new List<MergeFace> { f }));
            else
            {
                groups[at] = (groups[at].bounds.Union(f.Uv), groups[at].faces);
                groups[at].faces.Add(f);
            }
        }
        return groups.Select(g => g.faces).ToList();
    }
}
