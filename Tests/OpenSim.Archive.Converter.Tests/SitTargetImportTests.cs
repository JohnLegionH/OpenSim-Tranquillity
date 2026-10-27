/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Xml.Linq;
using OpenSim.Archive.Restore.Import;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// A part's "sit" key in the manifest: the position and rotation of its sit target in the part's own axes, written the way the region archives them
/// (SitTargetPosition, SitTargetOrientation, their LL twins, and SitTargetActive so a target at the exact centre still counts as set). Each test writes
/// its own folder and archive and shares nothing, so the class runs in parallel.
/// </summary>
public class SitTargetImportTests
{
    private static float F(XElement part, string path) => MaterialKit.F(part, path);

    [Fact]
    public void TheManifestReadsASitTargetsPositionAndRotationAndTurnsRotationIntoAUnitQuaternion()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"sit\": { \"position\": [0.3, -0.5, 0.1], \"rotation\": [0, 0, 1, 1] }");
        LinksetPart part = LinksetManifest.Load(b.ManifestPath).Parts[0];
        Assert.NotNull(part.SitTarget);
        Assert.Equal(new[] { 0.3f, -0.5f, 0.1f }, part.SitTarget.Position);
        Assert.Equal(0f, part.SitTarget.Rotation[0], 5);
        Assert.Equal(0.7071068f, part.SitTarget.Rotation[2], 5);
        Assert.Equal(0.7071068f, part.SitTarget.Rotation[3], 5);
    }

    [Fact]
    public void ARotationLeftOutIsNoTurn()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"sit\": { \"position\": [0.3, 0, 0.1] }");
        LinksetPart part = LinksetManifest.Load(b.ManifestPath).Parts[0];
        Assert.NotNull(part.SitTarget);
        Assert.Equal(new[] { 0f, 0f, 0f, 1f }, part.SitTarget.Rotation);
    }

    [Fact]
    public void APartWithNoSitKeyHasNoSitTarget()
    {
        MaterialKit.Built b = MaterialKit.Write();
        Assert.Null(LinksetManifest.Load(b.ManifestPath).Parts[0].SitTarget);
    }

    [Theory]
    [InlineData("\"sit\": { \"rotation\": [0, 0, 0, 1] }", "position")]
    [InlineData("\"sit\": { \"position\": [1, 2] }", "position")]
    [InlineData("\"sit\": { \"position\": [0, 0, 0], \"rotation\": [0, 0, 0, 0] }", "rotation")]
    [InlineData("\"sit\": { \"position\": [0, 0, 0], \"turn\": [0, 0, 0, 1] }", "turn")]
    [InlineData("\"sit\": [0, 0, 0]", "sit")]
    public void ASitKeyThatIsIncompleteOrUnknownIsRefusedByName(string keys, string named)
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: keys);
        ImportException e = Assert.Throws<ImportException>(() => LinksetManifest.Load(b.ManifestPath));
        Assert.Contains(named, e.Message);
    }

    [Fact]
    public void ImportWritesTheSitTargetOfThePartThatHasOneAndNoneForThePartThatHasNot()
    {
        MaterialKit.Built b = MaterialKit.Write(
            rootKeys: "\"sit\": { \"position\": [0.342, -0.5, 0.1], \"rotation\": [0, 0, 0.7071068, 0.7071068] }",
            childKeys: "");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        List<XElement> parts = MaterialKit.Parts(oar);
        XElement seat = parts[0], frame = parts[1];
        Assert.Equal(0.342f, F(seat, "SitTargetPosition/X"), 5);
        Assert.Equal(-0.5f, F(seat, "SitTargetPosition/Y"), 5);
        Assert.Equal(0.1f, F(seat, "SitTargetPosition/Z"), 5);
        Assert.Equal(0.7071068f, F(seat, "SitTargetOrientation/Z"), 5);
        Assert.Equal(0.7071068f, F(seat, "SitTargetOrientation/W"), 5);
        Assert.Equal(0f, F(seat, "SitTargetOrientation/X"), 5);
        Assert.Equal("true", ImportKit.Text(seat, "SitTargetActive"));
        Assert.Equal(0f, F(frame, "SitTargetPosition/X"), 5);
        Assert.Equal(1f, F(frame, "SitTargetOrientation/W"), 5);
        Assert.Null(ImportKit.Text(frame, "SitTargetActive"));
    }

    [Fact]
    public void TheLlTwinsCarryTheSameValuesSoTheRegionReadsOneTarget()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"sit\": { \"position\": [0.25, 0.5, 0.75], \"rotation\": [0, 0.7071068, 0, 0.7071068] }");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement seat = MaterialKit.Parts(oar)[0];
        foreach (string axis in new[] { "X", "Y", "Z" })
            Assert.Equal(F(seat, "SitTargetPosition/" + axis), F(seat, "SitTargetPositionLL/" + axis), 6);
        foreach (string axis in new[] { "X", "Y", "Z", "W" })
            Assert.Equal(F(seat, "SitTargetOrientation/" + axis), F(seat, "SitTargetOrientationLL/" + axis), 6);
        Assert.Equal(0.75f, F(seat, "SitTargetPositionLL/Z"), 5);
        Assert.Equal(0.7071068f, F(seat, "SitTargetOrientationLL/Y"), 5);
    }

    [Fact]
    public void ATargetAtTheExactCentreWithNoTurnIsStillSetBecauseSitTargetActiveIsWritten()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"sit\": { \"position\": [0, 0, 0] }");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement seat = MaterialKit.Parts(oar)[0];
        Assert.Equal("true", ImportKit.Text(seat, "SitTargetActive"));
    }

    [Fact]
    public void SitTargetActiveComesAfterThePositionAndTheOrientationBecauseTheirSettersResetIt()
    {
        MaterialKit.Built b = MaterialKit.Write(rootKeys: "\"sit\": { \"position\": [0.1, 0.2, 0.3] }");
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(b);
        XElement seat = MaterialKit.Parts(oar)[0];
        List<string> names = seat.Elements().Select(e => e.Name.LocalName).ToList();
        int active = names.IndexOf("SitTargetActive");
        Assert.True(active >= 0, "no SitTargetActive element");
        foreach (string before in new[] { "SitTargetPosition", "SitTargetOrientation", "SitTargetPositionLL", "SitTargetOrientationLL" })
            Assert.True(names.IndexOf(before) >= 0 && names.IndexOf(before) < active, before + " must come before SitTargetActive");
    }

    [Fact]
    public void APartWithNoSitKeyWritesNoActiveFlagAndKeepsTheZeroTargetElements()
    {
        MaterialKit.Built plain = MaterialKit.Write();
        (_, Dictionary<string, byte[]> oar) = MaterialKit.Import(plain);
        XElement seat = MaterialKit.Parts(oar)[0];
        Assert.Null(ImportKit.Text(seat, "SitTargetActive"));
        Assert.Equal(0f, F(seat, "SitTargetPosition/Z"), 6);
    }
}
