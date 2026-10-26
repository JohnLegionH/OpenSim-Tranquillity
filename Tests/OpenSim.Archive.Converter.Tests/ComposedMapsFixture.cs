/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Eligibility.Tests;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// Archives whose legacy materials draw their normal map with a transform that is not the face transform, in ways a merged face material can
/// state (the map transform taken relative to the diffuse transform that is baked into the mesh) and in one way it cannot. Two objects of eight
/// parts (the parts of <see cref="MapsFixture"/>); in every part
///   face 0 is default-mapped, face transform uniform, the normal map turned and scaled differently, the specular map drawn like the diffuse;
///   face 1 is planar, face transform not uniform and not turned, the normal map scaled and moved differently;
///   face 2 is default-mapped, face transform not uniform and turned, the normal map turned by another angle: the relative transform is a shear;
///   face 3 is plain.
/// </summary>
internal static class ComposedMapsFixture
{
    public static readonly Guid MaterialUniform = Guid.Parse("41111111-2222-4333-8444-555555555555");
    public static readonly Guid MaterialStretched = Guid.Parse("42111111-2222-4333-8444-555555555555");
    public static readonly Guid MaterialSheared = Guid.Parse("43111111-2222-4333-8444-555555555555");

    public static readonly FaceTransform FaceUniform = MapsFixture.AsStored(new FaceTransform(2f, 2f, 0.25f, -0.5f, 0.3f));
    public static readonly FaceTransform NormalUniform = MapsFixture.AsStored(new FaceTransform(3f, 3f, 0.1f, 0.2f, 0.7f));
    public static readonly FaceTransform FaceStretched = MapsFixture.AsStored(new FaceTransform(2f, 0.5f, -0.125f, 0.0625f, 0f));
    public static readonly FaceTransform NormalStretched = MapsFixture.AsStored(new FaceTransform(4f, 3f, 0.2f, 0.1f, 0f));
    public static readonly FaceTransform FaceSheared = MapsFixture.AsStored(new FaceTransform(2f, 3f, 0.1f, 0.2f, 0.2f));
    public static readonly FaceTransform NormalSheared = MapsFixture.AsStored(new FaceTransform(2f, 3f, 0.1f, 0.2f, 0.9f));

    public static Dictionary<string, byte[]> Assets()
    {
        Dictionary<string, byte[]> assets = MapsFixture.Assets();
        assets["assets/" + MaterialUniform + "_material.xml"] = MapsFixture.MaterialXml(MapsFixture.NormalMap, NormalUniform, MapsFixture.SpecularMap, FaceUniform);
        assets["assets/" + MaterialStretched + "_material.xml"] = MapsFixture.MaterialXml(MapsFixture.NormalMap, NormalStretched, Guid.Empty, FaceTransform.Identity);
        assets["assets/" + MaterialSheared + "_material.xml"] = MapsFixture.MaterialXml(MapsFixture.NormalMap, NormalSheared, Guid.Empty, FaceTransform.Identity);
        return assets;
    }

    private static void Face(Primitive.TextureEntry te, uint index, MappingType mapping, Guid material, FaceTransform t)
    {
        Primitive.TextureEntryFace f = te.CreateFace(index);
        f.TextureID = new UUID(MapsFixture.Diffuse);
        f.MaterialID = new UUID(material);
        f.TexMapType = mapping;
        f.RepeatU = t.RepeatU; f.RepeatV = t.RepeatV; f.OffsetU = t.OffsetU; f.OffsetV = t.OffsetV; f.Rotation = t.Rotation;
    }

    public static string TextureEntry()
    {
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(MapsFixture.Diffuse));
        Face(te, 0, MappingType.Default, MaterialUniform, FaceUniform);
        Face(te, 1, MappingType.Planar, MaterialStretched, FaceStretched);
        Face(te, 2, MappingType.Default, MaterialSheared, FaceSheared);
        return Convert.ToBase64String(te.GetBytes());
    }

    public static string Write() => EligibilityKit.WriteArchive(MapsFixture.Parts(TextureEntry()), Assets(), PlanarFixture.FlatTerrain());
}
