/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Converter;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// The viewer's default texture coordinates for twisted, tapered, sheared, cut and circular paths, checked against the
/// mesher's viewer-mode output (with the 1 - v turn of <see cref="FaceBuilder.ToMeshFace"/>). The rule is read from
/// indra/llmath/llvolume.cpp:
/// a side face has u = the profile point's texture position (LLProfile::genNGon 455-545, third component: the fraction t along
/// the profile, so it does not depend on the path) and v = the path point's mTexT (linear path 1500-1510: the path parameter t;
/// circular path LLPath::genNGon 1217-1375: the path parameter t from the path cut's begin to its end, whatever the number of
/// revolutions), see LLVolumeFace::createSide 6795-6890; a cap has the profile point itself, u = x + 0.5 and v = y + 0.5 (v = 0.5 - y
/// on the bottom cap), see LLVolumeFace::createCap 6165-6250, and the profile point is the one before the path's twist, taper and
/// shear are applied.
/// </summary>
public class ViewerUvRuleTests
{
    private const float Tol = 2e-3f;

    /// <summary>The same profile and hole size with a plain path: no twist, taper, shear, skew, radius offset, revolutions or path cut.</summary>
    internal static PrimParams Plain(PrimParams p)
    {
        PrimParams b = FaceMatrix.Make(p.PathCurve == 16 ? (p.ProfileCurve == 0 ? "cylinder" : p.ProfileCurve == 3 ? "prism" : "box")
                                                         : p.ProfileCurve == 5 ? "sphere" : p.ProfileCurve == 0 ? "torus" : p.ProfileCurve == 3 ? "ring" : "tube", "plain");
        b.ProfileBegin = p.ProfileBegin; b.ProfileEnd = p.ProfileEnd; b.ProfileHollow = p.ProfileHollow; b.HoleType = p.HoleType;
        if (p.PathCurve != 16)
        {
            b.PathScaleX = p.PathScaleX; b.PathScaleY = p.PathScaleY;
            // a closed circular path has no caps; the reference is open so that it has them
            b.PathEnd = 0.75f;
        }
        return b;
    }

    private static List<(PrimFace face, EncoderFace mesh)> Build(PrimParams p) =>
        PrimGeometry.BuildFaces(p).Select(f => (f, FaceBuilder.ToMeshFace(f))).ToList();

    private static IEnumerable<(float u, float v, float x, float y, float z)> Verts(EncoderFace m)
    {
        for (int i = 0; i < m.VertexCount; i++)
            yield return (m.Uvs[i * 2], m.Uvs[i * 2 + 1], m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
    }

    private static bool In(IEnumerable<float> set, float x) => set.Any(s => Math.Abs(s - x) < Tol);

    /// <summary>Every place the mesher's coordinates differ from the viewer's rule; empty when they agree.</summary>
    internal static List<string> Violations(PrimParams p)
    {
        List<string> bad = new List<string>();
        List<(PrimFace face, EncoderFace mesh)> faces = Build(p);
        List<(PrimFace face, EncoderFace mesh)> plain = Build(Plain(p));
        bool linear = p.PathCurve == 16 || p.PathCurve == 128;

        foreach ((PrimFace f, EncoderFace m) in faces)
        {
            string name = f.Kind + "#" + f.Number;
            // the reference face of the same kind and place among the faces of that kind
            int place = faces.Where(x => x.face.Kind == f.Kind).ToList().FindIndex(x => x.face == f);
            (PrimFace face, EncoderFace mesh)? same = plain.Where(x => x.face.Kind == f.Kind).Cast<(PrimFace face, EncoderFace mesh)?>().ElementAtOrDefault(place);

            if (f.Kind == PrimFaceKind.Top || f.Kind == PrimFaceKind.Bottom)
            {
                // the cap's coordinates are the profile point's: the same set as the plain shape's cap
                if (same == null) { bad.Add(name + ": no plain cap to compare with"); continue; }
                List<(float u, float v, float x, float y, float z)> pv = Verts(same.Value.mesh).ToList();
                foreach (var v in Verts(m))
                    if (!pv.Any(q => Math.Abs(q.u - v.u) < Tol && Math.Abs(q.v - v.v) < Tol))
                    {
                        bad.Add(name + ": cap coordinate (" + v.u.ToString("0.###") + "," + v.v.ToString("0.###") + ") is not a profile point of the plain shape");
                        break;
                    }
                continue;
            }

            if (same == null) { bad.Add(name + ": no plain face to compare with"); continue; }
            List<float> plainU = Verts(same.Value.mesh).Select(q => q.u).ToList();
            foreach (var v in Verts(m))
                if (!In(plainU, v.u))
                {
                    bad.Add(name + ": u " + v.u.ToString("0.###") + " is not a profile texture position");
                    break;
                }

            if (linear)
            {
                // path parameter t is the height plus one half, before the part's size is applied
                var worst = Verts(m).OrderByDescending(v => Math.Abs(v.v - (v.z + 0.5f))).First();
                if (Math.Abs(worst.v - (worst.z + 0.5f)) > Tol)
                    bad.Add(name + ": v " + worst.v.ToString("0.###") + " but height + 0.5 is " + (worst.z + 0.5f).ToString("0.###"));
            }
            else if (f.Kind == PrimFaceKind.Outer || f.Kind == PrimFaceKind.Inner)
            {
                float lo = Verts(m).Min(v => v.v), hi = Verts(m).Max(v => v.v);
                if (Math.Abs(lo - p.PathBegin) > Tol || Math.Abs(hi - p.PathEnd) > Tol)
                    bad.Add(name + ": v runs " + lo.ToString("0.###") + " to " + hi.ToString("0.###") + " but the path runs " + p.PathBegin.ToString("0.###") + " to " + p.PathEnd.ToString("0.###"));

                // Without a y shear every profile lies in a plane through the path's axis (skew and x shear move a point along
                // the axis only), so v times the revolutions is the vertex's angle round the axis, in turns: the viewer's path
                // point is (0, cos a, sin a) with a = 2 pi t revolutions (LLPath::genNGon, llvolume.cpp:1306-1311), so the axis
                // is x and v = 0 lies on +y, turning toward +z. A twist or a closed hole size can put a profile point on the
                // other side of the axis, half a turn away, so those are compared modulo half a turn.
                if (p.PathShearY == 0)
                {
                    float modulus = (p.PathTwist != 0 || p.PathTwistBegin != 0 || p.PathScaleY > 0.99f) ? 0.5f : 1f;
                    foreach (var v in Verts(m))
                    {
                        if (Math.Sqrt(v.y * v.y + v.z * v.z) < 0.02) continue;
                        float turns = MathF.Atan2(v.z, v.y) / (2f * MathF.PI);
                        float d = (((v.v * p.PathRevolutions - turns) % modulus) + modulus) % modulus;
                        if (d > 0.01f && Math.Abs(d - modulus) > 0.01f)
                        {
                            bad.Add(name + ": v " + v.v.ToString("0.###") + " but the angle round the axis is " + ((turns + 1f) % 1f).ToString("0.###") + " turns");
                            break;
                        }
                    }
                }
            }
        }
        return bad;
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (string shape in FaceMatrix.Shapes)
            foreach (string modifier in FaceMatrix.Modifiers.Concat(FaceMatrix.ExtraModifiers).Select(m => m.name))
                yield return new object[] { shape, modifier };
    }

    /// <summary>
    /// The one place the mesher's coordinates differ from the viewer's: a circular path with a y shear. The mesher turns the
    /// path's angle by the shear (PrimMesher.cs:1382-1383, 1426, 1433) where the viewer shears the path point (llvolume.cpp:1306-1311),
    /// so v is offset by the shear. The mesher is shared with the exporter and is not changed here; eligibility keeps these
    /// shapes as prims (<see cref="UvRules.Unverified"/>).
    /// </summary>
    private static bool KnownMismatch(PrimParams p) => p.PathCurve != 16 && p.PathCurve != 128 && p.PathShearY != 0;

    [Theory]
    [MemberData(nameof(Matrix))]
    public void TheMeshersCoordinatesAreTheViewersRuleUnlessTheShapeIsKept(string shape, string modifier)
    {
        PrimParams p = FaceMatrix.Make(shape, modifier);
        List<string> bad = Violations(p);
        if (KnownMismatch(p))
        {
            Assert.NotEmpty(bad);
            Assert.True(UvRules.Unverified(FaceMatrix.Stored(p)) != null, "a shape with a known mismatch is not kept");
        }
        else
        {
            Assert.Empty(bad);
            Assert.Null(UvRules.Unverified(FaceMatrix.Stored(p)));
        }
    }
}
