/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text.Json;
using OpenMetaverse;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Tests.Common;
using Xunit;
using static OpenSim.Archive.Export.Tests.TestKit;

namespace OpenSim.Archive.Export.Tests;

/// <summary>The exporter's reading against what the repository's own serializer writes, field by field.</summary>
[Collection(SceneStateCollection.Name)]
public class SerializerParityTests : OpenSimTestCase
{
    private static SceneObjectGroup Build(out UUID texture)
    {
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        TestScene scene = new SceneHelpers().SetupScene("Parity", UUID.Random(), 1000, 1000);
        SceneObjectGroup sog = SceneHelpers.CreateSceneObject(3, Alice, "Parity-", 0x900);
        sog.Name = "Parity set";
        texture = UUID.Random();

        int i = 0;
        foreach (SceneObjectPart p in sog.Parts)
        {
            i++;
            p.Name = "part " + i + " <&>";
            p.Description = "description " + i;
            p.Scale = new Vector3(1 + i, 2 + i, 3 + i);
            p.CreatorID = Bob;
            if (p != sog.RootPart)
            {
                p.OffsetPosition = new Vector3(i, -i, 0.5f * i);
                p.RotationOffset = Quaternion.Normalize(new Quaternion(0.1f * i, 0.2f, 0.3f, 0.9f));
            }
            PrimitiveBaseShape s = p.Shape;
            s.ProfileCurve = (byte)(i == 1 ? 0x21 : 5);
            s.PathCurve = (byte)(i == 1 ? 16 : 32);
            s.ProfileBegin = (ushort)(1000 * i);
            s.ProfileEnd = (ushort)(2000 * i);
            s.ProfileHollow = (ushort)(3000 * i);
            s.PathBegin = (ushort)(400 * i);
            s.PathEnd = (ushort)(500 * i);
            s.PathScaleX = (byte)(100 + i);
            s.PathScaleY = (byte)(100 - i);
            s.PathShearX = (byte)(i);
            s.PathShearY = (byte)(256 - i);
            s.PathTwist = (sbyte)(-10 * i);
            s.PathTwistBegin = (sbyte)(5 * i);
            s.PathRadiusOffset = (sbyte)(-3 * i);
            s.PathTaperX = (sbyte)(7 * i);
            s.PathTaperY = (sbyte)(-7 * i);
            s.PathRevolutions = (byte)(10 * i);
            s.PathSkew = (sbyte)(-4 * i);
            Primitive.TextureEntry te = new Primitive.TextureEntry(texture);
            te.CreateFace(2).TextureID = UUID.Random();
            s.TextureEntry = te.GetBytes();
        }
        scene.AddNewSceneObject(sog, false);
        return sog;
    }

    [Fact]
    public void EveryFieldTheExporterReadsMatchesWhatTheSerializerWrote()
    {
        SceneObjectGroup sog = Build(out UUID texture);
        string xml = SceneObjectSerializer.ToXml2Format(sog);
        ParsedGroup mine = ObjectXmlParser.Parse(Utf8(xml)).Groups.Single();

        Assert.Equal(sog.Parts.Length, mine.Parts.Count);
        foreach (SceneObjectPart real in sog.Parts)
        {
            ParsedPart got = mine.Parts.Single(p => p.Part.PartId == real.UUID);
            Assert.Equal(real.Name, got.Part.Name);
            Assert.Equal(real.Description, got.Description);
            Assert.Equal(real.LinkNum, got.Part.LinkNum);
            Assert.Equal(real.OffsetPosition, got.OffsetPosition);
            Assert.Equal(real.RotationOffset.X, got.RotationOffset.X, 5);
            Assert.Equal(real.RotationOffset.Y, got.RotationOffset.Y, 5);
            Assert.Equal(real.RotationOffset.Z, got.RotationOffset.Z, 5);
            Assert.Equal(real.RotationOffset.W, got.RotationOffset.W, 5);
            Assert.Equal(real.Scale, got.Scale);
            Assert.Equal(real.UUID == sog.RootPart.UUID, got.Part.IsRoot);

            PrimitiveBaseShape s = real.Shape;
            ShapeData d = got.Shape;
            Assert.Equal(s.ProfileCurve, (byte)d.ProfileCurve);
            Assert.Equal(s.PathCurve, (byte)d.PathCurve);
            Assert.Equal(s.ProfileBegin, (ushort)d.ProfileBegin);
            Assert.Equal(s.ProfileEnd, (ushort)d.ProfileEnd);
            Assert.Equal(s.ProfileHollow, (ushort)d.ProfileHollow);
            Assert.Equal(s.PathBegin, (ushort)d.PathBegin);
            Assert.Equal(s.PathEnd, (ushort)d.PathEnd);
            Assert.Equal(s.PathScaleX, (byte)d.PathScaleX);
            Assert.Equal(s.PathScaleY, (byte)d.PathScaleY);
            Assert.Equal(s.PathShearX, (byte)d.PathShearX);
            Assert.Equal(s.PathShearY, (byte)d.PathShearY);
            Assert.Equal(s.PathTwist, (sbyte)d.PathTwist);
            Assert.Equal(s.PathTwistBegin, (sbyte)d.PathTwistBegin);
            Assert.Equal(s.PathRadiusOffset, (sbyte)d.PathRadiusOffset);
            Assert.Equal(s.PathTaperX, (sbyte)d.PathTaperX);
            Assert.Equal(s.PathTaperY, (sbyte)d.PathTaperY);
            Assert.Equal(s.PathRevolutions, (byte)d.PathRevolutions);
            Assert.Equal(s.PathSkew, (sbyte)d.PathSkew);
            Assert.Equal(s.PCode, (byte)d.PCode);
            Assert.Equal(s.TextureEntry, got.TextureEntry);
            Assert.Equal(s.ProfileShape.ToString(), d.ProfileShapeName);
            Assert.Equal(s.HollowShape.ToString(), d.HollowShapeName);
        }
        _ = texture;
    }

    [Fact]
    public void ThroughTheExporterAParsedSerializerObjectComesOutInLinkOrderWithItsNames()
    {
        SceneObjectGroup sog = Build(out _);
        string xml = SceneObjectSerializer.ToXml2Format(sog);
        ParsedGroup g = ObjectXmlParser.Parse(Utf8(xml)).Groups.Single();
        BuiltBuild b = LinksetExporter.Build(g, "parity.oar", "objects/parity.xml", "parity.glb");

        Assert.Equal(sog.Parts.OrderBy(p => p.LinkNum).Select(p => p.Name).ToArray(), b.Manifest.Parts.Select(p => p.Name).ToArray());
        Glb glb = ReadGlb(b.Glb);
        Assert.Equal(sog.Parts.OrderBy(p => p.LinkNum).Select(p => p.Name).ToArray(), glb.PartNodes.Select(n => n.GetProperty("name").GetString()).ToArray());
        Assert.All(b.Manifest.Parts, p => Assert.Equal(ExportStatus.Exported, p.Status));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public class SceneStateCollection
{
    /// <summary>Tests that build scenes share process-wide state: SceneManager.Instance and Util.FireAndForgetMethod.</summary>
    public const string Name = "scene state";
}
