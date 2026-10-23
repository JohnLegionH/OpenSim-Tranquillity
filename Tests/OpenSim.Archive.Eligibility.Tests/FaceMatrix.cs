/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// The 133 prim variants of the face-numbering test in OpenSim.Archive.GltfExport.Tests (seven basic shapes, each with nineteen
/// modifiers), written as the stored shape fields an object file holds.
/// </summary>
internal static class FaceMatrix
{
    public static readonly string[] Shapes = { "box", "cylinder", "prism", "sphere", "torus", "tube", "ring" };

    private static PrimParams Base(string shape)
    {
        PrimParams p = new PrimParams { ProfileEnd = 1f, PathEnd = 1f, PathScaleX = 1f, PathScaleY = 1f, PathRevolutions = 1f };
        switch (shape)
        {
            case "box": p.ProfileCurve = 1; p.PathCurve = 16; break;
            case "cylinder": p.ProfileCurve = 0; p.PathCurve = 16; break;
            case "prism": p.ProfileCurve = 3; p.PathCurve = 16; break;
            case "sphere": p.ProfileCurve = 5; p.PathCurve = 32; break;
            case "torus": p.ProfileCurve = 0; p.PathCurve = 32; p.PathScaleY = 0.25f; break;
            case "tube": p.ProfileCurve = 1; p.PathCurve = 32; p.PathScaleY = 0.25f; break;
            case "ring": p.ProfileCurve = 3; p.PathCurve = 32; p.PathScaleY = 0.25f; break;
        }
        return p;
    }

    public static readonly (string name, Action<PrimParams> apply)[] Modifiers =
    {
        ("plain", p => { }),
        ("hollow", p => p.ProfileHollow = 0.4f),
        ("hollow-circle-hole", p => { p.ProfileHollow = 0.4f; p.HoleType = 16; }),
        ("hollow-triangle-hole", p => { p.ProfileHollow = 0.4f; p.HoleType = 48; }),
        ("profile-cut-quarter", p => { p.ProfileBegin = 0.25f; p.ProfileEnd = 0.75f; }),
        ("profile-cut-off-grid", p => { p.ProfileBegin = 0.1f; p.ProfileEnd = 0.8f; }),
        ("profile-cut-begin-only", p => p.ProfileBegin = 0.3f),
        ("profile-cut-end-only", p => p.ProfileEnd = 0.6f),
        ("hollow+profile-cut", p => { p.ProfileHollow = 0.4f; p.ProfileBegin = 0.25f; p.ProfileEnd = 0.75f; }),
        ("hollow+profile-cut-off-grid", p => { p.ProfileHollow = 0.4f; p.ProfileBegin = 0.1f; p.ProfileEnd = 0.8f; }),
        ("path-cut", p => { p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("hollow+path-cut", p => { p.ProfileHollow = 0.4f; p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("profile-cut+path-cut", p => { p.ProfileBegin = 0.25f; p.ProfileEnd = 0.75f; p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("hollow+profile-cut+path-cut", p => { p.ProfileHollow = 0.4f; p.ProfileBegin = 0.1f; p.ProfileEnd = 0.8f; p.PathBegin = 0.2f; p.PathEnd = 0.8f; }),
        ("twist", p => p.PathTwist = 0.25f),
        ("taper", p => { p.PathTaperX = 0.3f; p.PathScaleX = 0.7f; }),
        ("skew", p => p.PathSkew = 0.2f),
        ("radius-offset", p => p.PathRadiusOffset = 0.3f),
        ("hollow+twist", p => { p.ProfileHollow = 0.4f; p.PathTwist = 0.25f; }),
    };

    /// <summary>
    /// More single-field variations beyond the 133, for the fields the server and the viewer read differently: taper on the other
    /// axis, shear, revolutions, a start twist, a radius offset with a closed hole size, a tiny hollow.
    /// </summary>
    public static readonly (string name, Action<PrimParams> apply)[] ExtraModifiers =
    {
        ("taper-y", p => { p.PathTaperY = 0.3f; p.PathScaleY = p.PathScaleY * 0.7f; }),
        ("shear-x", p => p.PathShearX = 0.2f),
        ("shear-y", p => p.PathShearY = 0.2f),
        ("revolutions", p => p.PathRevolutions = 1.3f),
        ("twist-begin-only", p => p.PathTwistBegin = 0.25f),
        ("twist-and-twist-begin", p => { p.PathTwist = 0.25f; p.PathTwistBegin = 0.25f; }),
        ("hole-size-one+radius-offset", p => { p.PathScaleY = 1f; p.PathRadiusOffset = 0.3f; }),
        ("hollow-tiny", p => p.ProfileHollow = 0.0001f),
        ("negative-skew", p => p.PathSkew = -0.2f),
    };

    public static IEnumerable<object[]> ExtraCases()
    {
        foreach (string shape in Shapes)
            foreach ((string name, _) in ExtraModifiers)
                yield return new object[] { shape, name };
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (string shape in Shapes)
            foreach ((string name, _) in Modifiers)
                yield return new object[] { shape, name };
    }

    public static PrimParams Make(string shape, string modifier)
    {
        PrimParams p = Base(shape);
        (string name, Action<PrimParams> apply) m = Modifiers.Concat(ExtraModifiers).Single(x => x.name == modifier);
        m.apply(p);
        return p;
    }

    private static int R(double x) => (int)Math.Round(x);

    /// <summary>The fields as an object file stores them: the inverse of PrimParams.FromShape.</summary>
    public static ShapeData Stored(PrimParams p) => new ShapeData
    {
        ProfileCurve = p.ProfileCurve | p.HoleType,
        PathCurve = p.PathCurve,
        ProfileBegin = R(p.ProfileBegin / 2.0e-5),
        ProfileEnd = R((1.0 - p.ProfileEnd) / 2.0e-5),
        ProfileHollow = R(p.ProfileHollow / 2.0e-5),
        PathBegin = R(p.PathBegin / 2.0e-5),
        PathEnd = R((1.0 - p.PathEnd) / 2.0e-5),
        PathScaleX = 200 - R(p.PathScaleX * 100),
        PathScaleY = 200 - R(p.PathScaleY * 100),
        PathShearX = R(p.PathShearX * 100),
        PathShearY = R(p.PathShearY * 100),
        PathTwist = R(p.PathTwist * 100),
        PathTwistBegin = R(p.PathTwistBegin * 100),
        PathRadiusOffset = R(p.PathRadiusOffset * 100),
        PathTaperX = R(p.PathTaperX * 100),
        PathTaperY = R(p.PathTaperY * 100),
        PathSkew = R(p.PathSkew * 100),
        PathRevolutions = R((p.PathRevolutions - 1.0) / 0.015),
        PCode = 9
    };

    public static ShapeData Shape(string shape, string modifier) => Stored(Make(shape, modifier));
}
