/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using OpenMetaverse;
using OpenSim.CharacterPublish.Objects;
using OpenSim.MeshAsset;
using N = System.Numerics;

namespace OpenSim.CharacterPublish.Services.Tests;

/// <summary>Neutral fixtures: fixed random-looking ids, a generated mesh, no real names or assets.</summary>
internal static class BundleFixtures
{
    public static readonly UUID Creator = new("11111111-2222-4333-8444-555555555555");
    public static readonly UUID SkinHead = new("0a1b2c3d-4e5f-4a6b-8c7d-8e9fa0b1c2d3");
    public static readonly UUID SkinUpper = new("1a2b3c4d-5e6f-4a7b-8c8d-9eaf0b1c2d3e");
    public static readonly UUID SkinLower = new("2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f");
    public static readonly UUID Iris = new("3a4b5c6d-7e8f-4a9b-8cad-bfc10d1e2f30");

    public const int Date = 1_790_000_000; // 2026-09-21, a fixed time so output is repeatable

    /// <summary>A name that is short, stable and different for each test, so a test's wearables, object, mesh and notecard are new to the shared asset store.</summary>
    public static string UniqueName(string caller) => "T" + ContentId.Derive("test", caller).ToString()[..6];

    public static byte[] Mesh(int faces, string seed = "")
    {
        var shift = (ContentId.Derive("mesh", seed).GetHashCode() & 0xFFFF) / 65536f; // makes the bytes, and so the content-derived id, differ per seed
        return MeshOf(faces, shift);
    }

    static byte[] MeshOf(int faces, float shift)
    {
        var model = new MeshAssetModel { High = new MeshLod() };
        for (var i = 0; i < faces; i++)
        {
            var f = new MeshFace
            {
                Positions = new[] { new N.Vector3(-0.5f, -0.25f, i + shift), new N.Vector3(0.5f, -0.5f, 0.25f + i), new N.Vector3(0, 0.5f, 0.5f + i) },
                Normals = new[] { N.Vector3.UnitZ, N.Vector3.UnitZ, N.Vector3.UnitZ },
                Uvs = new[] { new N.Vector2(0, 0), new N.Vector2(1, 0), new N.Vector2(0, 1) },
                Indices = new[] { 0, 1, 2 },
                NormalizedScale = new N.Vector3(0.62f, 0.31f, 1.78f),
                Weights = new[] { new[] { new Influence(0, 1f) }, new[] { new Influence(0, 0.5f), new Influence(1, 0.5f) }, new[] { new Influence(1, 1f) } },
            };
            model.High.Faces.Add(f);
        }
        model.Skin = new MeshSkin
        {
            JointNames = { "mPelvis", "mTorso" },
            InverseBind = Enumerable.Range(0, 2).Select(i => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -i * 0.5, 0, 0.25, 1 }).ToList(),
        };
        return MeshCodec.Encode(model);
    }

    public const string Credit = "Mesh body: Example by Test User, CC BY 4.0, modified.";
    public const string NotecardText = "Base body credit\n\nExample body by Test User.\nLicence: https://creativecommons.org/licenses/by/4.0/legalcode\nChanges: converted to the Second Life mesh format.\n";

    public static CharacterDescription Description(string name, UUID? skinHead = null) => new(
        name,
        new AttributionDescription(Credit, NotecardName: name + " credits", NotecardText: NotecardText + name + "\n"),
        new List<PartDescription> { new(name + " Body", PartRole.Body, "body.llmesh", new() { ["0"] = "head", ["1"] = "upper", ["2"] = "lower" }) },
        new WearablesDescription(
            new ShapeDescription(Values: new() { ["1"] = 0.4f, ["80"] = 0.25f }),
            new SkinDescription(skinHead ?? SkinHead, SkinUpper, SkinLower),
            new EyesDescription(Iris),
            null));

    /// <summary>
    /// The synthetic character as a validated bundle made for <paramref name="owner"/>. Without a name, the character is named for the
    /// calling test, so one test's bundles share assets with each other and with no other test's.
    /// </summary>
    public static AssembledBundle Assemble(UUID owner, int folderNumber = 1, string? name = null, bool saveLook = true, int date = Date, UUID? skinHead = null,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        name ??= UniqueName(caller);
        var mesh = Mesh(3, name);
        var a = OutfitAssembler.Assemble(Description(name, skinHead), new PublishContext(owner, Creator, folderNumber, date, SaveCurrentLook: saveLook),
            f => f == "body.llmesh" ? mesh : throw new FileNotFoundException(f));
        Assert.True(a.Validation.Ok, a.Validation.ToString());
        return a;
    }
}
