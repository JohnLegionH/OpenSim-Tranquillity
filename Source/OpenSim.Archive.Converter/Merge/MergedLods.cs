/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Numerics;

namespace OpenSim.Archive.Converter.Merge;

/// <summary>The screen the levels of detail of a merged part are checked on, and how much a level may differ from the full mesh there.</summary>
public sealed class LodOptions
{
    /// <summary>The most a level may differ from the full mesh, in screen pixels, at the distance the viewer switches to it.</summary>
    public double PixelLimit { get; set; } = 1.0;
    /// <summary>Height of the window in pixels (a larger window shows the same difference in more pixels).</summary>
    public double ScreenHeightPixels { get; set; } = 1080;
    /// <summary>Vertical field of view in radians; the viewer's default is 60 degrees.</summary>
    public double FieldOfView { get; set; } = Math.PI / 3;
    /// <summary>The viewer's object detail setting (RenderVolumeLODFactor).</summary>
    public double LodFactor { get; set; } = 1.0;
}

/// <summary>
/// The viewer's choice of level of detail for a mesh, written from what it does. References, all in the viewer source:
/// indra/newview/llvovolume.cpp calcLOD (1507-1650) and computeLODDetail (1451-1465): the level comes from tan = lodFactor * radius / distance,
/// where distance is the camera distance times sDistanceFactor (llviewercontrol.cpp 322-323: 1 - lodFactor * 0.1) times pi/3 (llvovolume.cpp 1611);
/// indra/llmath/llvolumemgr.cpp 33-40 (getDetailFromTan 328-340): level 0 (lowest) up to tan 0.03, 1 (low) up to 0.06, 2 (medium) up to 0.24, else 3 (high);
/// the radius is the volume's scale times its LOD scale bias (llvovolume.cpp 1566; llvolume.cpp 1967 sets 1, 2055 sets 0.5).
/// </summary>
public static class ViewerLod
{
    /// <summary>Largest tan of the angle at which each level (0 lowest .. 3 high) is shown: 0.03, 0.06, 0.24 and 3.0 (llvolumemgr.cpp 36-40).</summary>
    public static readonly double[] Thresholds = { 0.03, 0.06, 0.24, 3.0 };

    /// <summary>The radius the viewer uses for a mesh of this size. The smaller bias (0.5) is taken: a smaller radius switches levels at a shorter distance, so a given error shows in more pixels.</summary>
    public static double Radius(Vector3 scale) => 0.5 * scale.Length();

    /// <summary>The camera distance, in metres, at which the viewer starts to draw level <paramref name="lod"/> (the nearest it is drawn).</summary>
    public static double SwitchDistance(int lod, double radius, LodOptions options) => throw new NotImplementedException();

    /// <summary>The level the viewer draws at a camera distance.</summary>
    public static int LevelAt(double distance, double radius, LodOptions options) => throw new NotImplementedException();

    /// <summary>Screen pixels in one metre across the view at a camera distance (the viewer's vertical field of view over the window height).</summary>
    public static double PixelsPerMeter(double distance, LodOptions options) => throw new NotImplementedException();
}

/// <summary>One lower level of detail of a merged part, with what was checked about it.</summary>
public sealed class LodLevel
{
    /// <summary>0 lowest, 1 low, 2 medium.</summary>
    public int Level { get; set; }
    public List<EncoderFace> Faces { get; set; } = new List<EncoderFace>();
    public int Triangles { get; set; }
    /// <summary>Triangles aimed for (a third, ninth or twenty-seventh of the full mesh) and what was kept after the error check asked for more.</summary>
    public int TargetTriangles { get; set; }
    public double SwitchDistance { get; set; }
    /// <summary>The most this level may differ from the full mesh, in metres, at the switch distance (the pixel limit there).</summary>
    public double AllowedMeters { get; set; }
    /// <summary>The measured differences from the full mesh, in metres: of a vertex from the surface of its slot, and of a texture coordinate (converted by how far the slot's surface runs per unit of coordinate).</summary>
    public double PositionErrorMeters { get; set; }
    public double UvErrorMeters { get; set; }
    public double ErrorPixels { get; set; }
    public int DroppedMembers { get; set; }
    public string Method { get; set; } = "";
}

/// <summary>
/// The lower levels of detail of a merged part. Each is made from the full mesh: members whose screen size at the level's switch distance is under
/// the pixel limit are left out, and each slot is simplified on its own, with the vertices on texture seams and on the slot's border kept where
/// they are, so every vertex of a level is a vertex of the full mesh in the same slot and no corner takes another corner's coordinate. The level
/// keeps as few triangles as the viewer's profile aims for (a third, a ninth, a twenty-seventh) unless a measured difference from the full mesh
/// would exceed the pixel limit at the switch distance; then it keeps more, up to the level above.
/// </summary>
public static class MergedLods
{
    /// <summary>The levels medium, low and lowest, in that order.</summary>
    public static List<LodLevel> Build(MergedGeometry geometry, IReadOnlyList<MergeMember> members, LodOptions options = null) => throw new NotImplementedException();
}
