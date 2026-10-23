/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenSim.Archive.GltfExport;
using OpenSim.Archive.Inspection;
using Xunit;

namespace OpenSim.Archive.Eligibility.Tests;

/// <summary>
/// The face count the region server gives a shape (SceneObjectPart.GetNumberOfSides, what llGetNumberOfSides returns) against the
/// one the viewer's rule gives it (ViewerFaceRule), over the 133 prim variants.
/// </summary>
public class FaceAgreementTests
{
    [Fact]
    public void TheStoredFieldsRoundTripThroughTheDecoder()
    {
        foreach (object[] c in FaceMatrix.Cases())
        {
            PrimParams want = FaceMatrix.Make((string)c[0], (string)c[1]);
            PrimParams got = PrimParams.FromShape(FaceMatrix.Stored(want));
            string label = c[0] + " " + c[1];
            Assert.True(Math.Abs(want.ProfileBegin - got.ProfileBegin) < 1e-4 && Math.Abs(want.ProfileEnd - got.ProfileEnd) < 1e-4, label);
            Assert.True(Math.Abs(want.PathBegin - got.PathBegin) < 1e-4 && Math.Abs(want.PathEnd - got.PathEnd) < 1e-4, label);
            Assert.True(Math.Abs(want.ProfileHollow - got.ProfileHollow) < 1e-4, label);
            Assert.Equal(want.ProfileCurve, got.ProfileCurve);
            Assert.Equal(want.HoleType, got.HoleType);
            Assert.Equal(want.PathCurve, got.PathCurve);
            Assert.True(Math.Abs(want.PathTwist - got.PathTwist) < 1e-4 && Math.Abs(want.PathSkew - got.PathSkew) < 1e-4 && Math.Abs(want.PathTaperX - got.PathTaperX) < 1e-4, label);
        }
    }

    [Fact]
    public void TheViewerCountIsTheCountOfTheViewerRule()
    {
        foreach (object[] c in FaceMatrix.Cases())
            Assert.Equal(ViewerFaceRule.Faces(PrimParams.FromShape(FaceMatrix.Shape((string)c[0], (string)c[1]))).Count,
                FaceAgreement.ViewerCount(FaceMatrix.Shape((string)c[0], (string)c[1])));
    }

    private static List<(string shape, string modifier, int server, int viewer)> Measure(bool extra = false)
    {
        List<(string, string, int, int)> all = new List<(string, string, int, int)>();
        foreach (object[] c in extra ? FaceMatrix.ExtraCases() : FaceMatrix.Cases())
        {
            FaceCounts f = FaceAgreement.Measure(FaceMatrix.Shape((string)c[0], (string)c[1]));
            all.Add(((string)c[0], (string)c[1], f.Server, f.Viewer));
        }
        return all;
    }

    [Fact]
    public void AllVariantsAreMeasured()
    {
        Assert.Equal(133, Measure().Count);
        // Setting OARX_FACE_TABLE to a file name writes the full table there (tab separated), for the design notes.
        string path = Environment.GetEnvironmentVariable("OARX_FACE_TABLE");
        if (!string.IsNullOrEmpty(path))
        {
            StringBuilder sb = new StringBuilder("shape\tmodifier\tserver\tviewer\tagree\n");
            foreach ((string s, string m, int server, int viewer) in Measure())
                sb.Append(s).Append('\t').Append(m).Append('\t').Append(server).Append('\t').Append(viewer).Append('\t').Append(server == viewer ? "yes" : "NO").Append('\n');
            File.WriteAllText(path, sb.ToString());
            StringBuilder ex = new StringBuilder("shape\tmodifier\tserver\tviewer\tagree\n");
            foreach ((string s, string m, int server, int viewer) in Measure(true))
                ex.Append(s).Append('\t').Append(m).Append('\t').Append(server).Append('\t').Append(viewer).Append('\t').Append(server == viewer ? "yes" : "NO").Append('\n');
            File.WriteAllText(path + ".extra", ex.ToString());
        }
    }

    /// <summary>
    /// The variants on which the two counts differ. Pinned, so a change to the server's formula or the viewer rule that moves
    /// a count shows up here.
    /// </summary>
    [Fact]
    public void TheCountsDifferOnlyWhereMeasured()
    {
        List<string> differ = Measure().Where(x => x.server != x.viewer).Select(x => x.shape + "/" + x.modifier + " " + x.server + "!=" + x.viewer).ToList();
        Assert.Equal(new[] { "sphere/taper 1!=3" }, differ);
    }

    [Fact]
    public void TheExtendedProbeFindsTheOtherDisagreementClasses()
    {
        // taper on a sphere (the server's sphere rule ignores it), and revolutions or a radius offset on a closed hole size
        // on the circular-path shapes (the server counts two cut faces for them, the viewer's rule does not)
        List<string> differ = Measure(true).Where(x => x.server != x.viewer).Select(x => x.shape + "/" + x.modifier + " " + x.server + "!=" + x.viewer).ToList();
        Assert.Equal(new[]
        {
            "sphere/taper-y 1!=3",
            "torus/revolutions 3!=1", "torus/hole-size-one+radius-offset 3!=1",
            "tube/revolutions 6!=4", "tube/hole-size-one+radius-offset 6!=4",
            "ring/revolutions 5!=3", "ring/hole-size-one+radius-offset 5!=3"
        }, differ);
    }
}
