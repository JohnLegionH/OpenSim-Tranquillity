/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Archive.Eligibility;

/// <summary>The face count the region server gives a shape and the one the viewer's rule gives it.</summary>
public readonly struct FaceCounts
{
    /// <summary>What <c>llGetNumberOfSides</c> returns (SceneObjectPart.GetNumberOfSides).</summary>
    public int Server { get; }
    /// <summary>The number of faces the viewer builds (ViewerFaceRule): the texture entry slots a converted part can use.</summary>
    public int Viewer { get; }

    public FaceCounts(int server, int viewer)
    {
        Server = server;
        Viewer = viewer;
    }

    public bool Agree => Server == Viewer;
}

/// <summary>
/// Compares the two face counts for a stored shape. Textures land by the viewer's face numbers and scripts count by the
/// server's, so a shape on which they differ changes what a script sees when the part becomes a mesh with the viewer's
/// number of faces.
/// </summary>
public static class FaceAgreement
{
    /// <summary>Builds the server's shape object from the stored fields, with the same text-form override the loader applies.</summary>
    public static PrimitiveBaseShape ToServerShape(ShapeData s)
    {
        PrimitiveBaseShape p = new PrimitiveBaseShape();
        p.ProfileCurve = unchecked((byte)s.ProfileCurve);
        if (!string.IsNullOrEmpty(s.ProfileShapeName) && Enum.TryParse(s.ProfileShapeName, true, out ProfileShape ps))
            p.ProfileShape = ps;
        if (!string.IsNullOrEmpty(s.HollowShapeName) && Enum.TryParse(s.HollowShapeName, true, out HollowShape hs))
            p.HollowShape = hs;
        p.PathCurve = unchecked((byte)s.PathCurve);
        p.PathBegin = unchecked((ushort)s.PathBegin);
        p.PathEnd = unchecked((ushort)s.PathEnd);
        p.PathScaleX = unchecked((byte)s.PathScaleX);
        p.PathScaleY = unchecked((byte)s.PathScaleY);
        p.PathShearX = unchecked((byte)s.PathShearX);
        p.PathShearY = unchecked((byte)s.PathShearY);
        p.PathTwist = unchecked((sbyte)s.PathTwist);
        p.PathTwistBegin = unchecked((sbyte)s.PathTwistBegin);
        p.PathRadiusOffset = unchecked((sbyte)s.PathRadiusOffset);
        p.PathRevolutions = unchecked((byte)s.PathRevolutions);
        p.PathTaperX = unchecked((sbyte)s.PathTaperX);
        p.PathTaperY = unchecked((sbyte)s.PathTaperY);
        p.PathSkew = unchecked((sbyte)s.PathSkew);
        p.ProfileBegin = unchecked((ushort)s.ProfileBegin);
        p.ProfileEnd = unchecked((ushort)s.ProfileEnd);
        p.ProfileHollow = unchecked((ushort)s.ProfileHollow);
        p.PCode = unchecked((byte)s.PCode);
        p.SculptEntry = s.SculptEntry;
        p.SculptType = unchecked((byte)s.SculptType);
        p.SculptTexture = s.SculptTexture;
        return p;
    }

    /// <summary>What the server reports for the shape: the real <c>SceneObjectPart.GetNumberOfSides</c>.</summary>
    public static int ServerCount(ShapeData s) => new SceneObjectPart { Shape = ToServerShape(s) }.GetNumberOfSides();

    /// <summary>
    /// The faces a prim of this shape has by the viewer's rule. A sculpt is one face, except a circle profile on a circular
    /// path (what a sculpt is made with), whose profile cut, hollow or open path give it the faces the viewer's rule lists.
    /// </summary>
    public static int ViewerCount(ShapeData s)
    {
        PrimParams p = PrimParams.FromShape(s);
        if (s.SculptEntry && !SculptGeometry.Described(p))
            return 1;
        return ViewerFaceRule.Faces(p).Count;
    }

    public static FaceCounts Measure(ShapeData s) => new FaceCounts(ServerCount(s), ViewerCount(s));
}
