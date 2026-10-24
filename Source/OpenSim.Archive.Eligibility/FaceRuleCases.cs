/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Eligibility;

/// <summary>
/// The shapes on which the region server's face count (SceneObjectPart.GetNumberOfSides) and the viewer's differ, and the
/// reason for each, settled from the viewer source. The viewer's list of faces is a profile's faces plus an end cap at each
/// end of the path when the path is open (LLProfile::generate, indra/llmath/llvolume.cpp); a circular path is open
/// when it is cut, skewed, tapered, given a radius change between its ends, or twisted (LLPath::genNGon, same file). Three
/// disagreements follow from that and in each the viewer is right:
/// <list type="bullet">
/// <item>A tapered sphere: a taper opens the path, so the viewer gives two end caps; the server's sphere rule has no taper term.</item>
/// <item>A circular path with more than one revolution: revolutions are not in the path's open test, so the path stays closed; the server adds two cut faces whenever revolutions are set.</item>
/// <item>A circular path with a radius offset and a hole size of 1: the radius change is scaled by one minus the hole size, so it is zero and the path stays closed; the server adds two cut faces for any radius offset.</item>
/// </list>
/// The viewer's count is right in all three, and the mesher in viewer mode builds the viewer's faces in all three (the third case
/// needed the mesher to close the path when the radius change is within 0.001, as the viewer does). A part whose counts differ
/// for any other reason is not converted (FACERULE_UNVERIFIED).
/// </summary>
public static class FaceRuleCases
{
    public const string TaperedSphere = "tapered-sphere";
    public const string Revolutions = "revolutions";
    public const string ClosedHoleRadiusOffset = "closed-hole-radius-offset";

    /// <summary>
    /// The settled reason the counts differ for this shape and the converter can build the viewer's geometry for it, or null.
    /// </summary>
    public static string Explain(ShapeData shape) => Cause(shape);

    /// <summary>
    /// The settled reason the counts differ for this shape, or null when the difference is not one of the three cases. A case
    /// counts as explained only when taking its one cause away makes the server and the viewer agree.
    /// </summary>
    public static string Cause(ShapeData shape)
    {
        PrimParams p = PrimParams.FromShape(shape);
        int curve = p.PathCurve & 0xF0;
        if (curve != 0x20 && curve != 0x30)
            return null;

        bool taperedSphere = (p.ProfileCurve & 0x0F) == 5 && (Math.Abs(p.PathTaperX) > 0.001f || Math.Abs(p.PathTaperY) > 0.001f);
        bool revolutions = shape.PathRevolutions != 0;
        bool closedHoleRadius = shape.PathRadiusOffset != 0
            && Math.Abs(p.PathRadiusOffset) * (1f - p.PathScaleY) * 0.5f <= 0.001f;

        int causes = (taperedSphere ? 1 : 0) + (revolutions ? 1 : 0) + (closedHoleRadius ? 1 : 0);
        if (causes != 1)
            return null;

        FaceCounts now = FaceAgreement.Measure(shape);
        if (revolutions)
        {
            ShapeData neutral = shape.Clone();
            neutral.PathRevolutions = 0;
            FaceCounts without = FaceAgreement.Measure(neutral);
            // the viewer ignores revolutions, so its count is unchanged, and without them the server agrees with it
            return without.Agree && without.Viewer == now.Viewer ? Revolutions : null;
        }
        if (closedHoleRadius)
        {
            ShapeData neutral = shape.Clone();
            neutral.PathRadiusOffset = 0;
            FaceCounts without = FaceAgreement.Measure(neutral);
            return without.Agree && without.Viewer == now.Viewer ? ClosedHoleRadiusOffset : null;
        }
        {
            ShapeData neutral = shape.Clone();
            neutral.PathTaperX = 0;
            neutral.PathTaperY = 0;
            FaceCounts without = FaceAgreement.Measure(neutral);
            // the server's count does not move with the taper; the viewer's gains the two end caps of an open path
            return without.Agree && without.Server == now.Server && now.Viewer == without.Viewer + 2 ? TaperedSphere : null;
        }
    }
}
