/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text;
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.MeshAsset;
using N = System.Numerics;

namespace OpenSim.CharacterPublish.Objects.Tests;

/// <summary>Neutral fixtures: fixed random-looking ids, generated meshes, no real names or assets.</summary>
internal static class BundleFixtures
{
    public static readonly UUID Owner = new("66666666-7777-4888-8999-aaaaaaaaaaaa");
    public static readonly UUID Creator = new("11111111-2222-4333-8444-555555555555");
    public static readonly UUID SkinHead = new("0a1b2c3d-4e5f-4a6b-8c7d-8e9fa0b1c2d3");
    public static readonly UUID SkinUpper = new("1a2b3c4d-5e6f-4a7b-8c8d-9eaf0b1c2d3e");
    public static readonly UUID SkinLower = new("2a3b4c5d-6e7f-4a8b-8c9d-aeb00c1d2e3f");
    public static readonly UUID Iris = new("3a4b5c6d-7e8f-4a9b-8cad-bfc10d1e2f30");
    public static readonly UUID HairTexture = new("4a5b6c7d-8e9f-4aab-8dbe-c0d21e2f3041");

    public const int Date = 1_790_000_000; // 2026-09-21, a fixed time so output is repeatable

    /// <summary>A mesh asset of <paramref name="faces"/> one-triangle faces.</summary>
    public static byte[] Mesh(int faces, N.Vector3? size = null, bool rigged = true, bool normalizedScale = true)
    {
        var model = new MeshAssetModel { High = new MeshLod() };
        for (var i = 0; i < faces; i++)
        {
            var f = new MeshFace
            {
                Positions = new[] { new N.Vector3(-0.5f, -0.25f, i), new N.Vector3(0.5f, -0.5f, 0.25f + i), new N.Vector3(0, 0.5f, 0.5f + i) },
                Normals = new[] { N.Vector3.UnitZ, N.Vector3.UnitZ, N.Vector3.UnitZ },
                Uvs = new[] { new N.Vector2(0, 0), new N.Vector2(1, 0), new N.Vector2(0, 1) },
                Indices = new[] { 0, 1, 2 },
                NormalizedScale = normalizedScale ? size ?? new N.Vector3(0.62f, 0.31f, 1.78f) : null,
            };
            if (rigged)
                f.Weights = new[] { new[] { new Influence(0, 1f) }, new[] { new Influence(0, 0.5f), new Influence(1, 0.5f) }, new[] { new Influence(1, 1f) } };
            model.High.Faces.Add(f);
        }
        if (rigged)
            model.Skin = new MeshSkin
            {
                JointNames = { "mPelvis", "mTorso" },
                InverseBind = Enumerable.Range(0, 2).Select(i => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -i * 0.5, 0, 0.25, 1 }).ToList(),
            };
        return MeshCodec.Encode(model);
    }

    public const string Credit = "Mesh body: Example by Test User, CC BY 4.0, modified.";
    public const string NotecardText = "Base body credit\n\nExample body by Test User.\nLicence: https://creativecommons.org/licenses/by/4.0/legalcode\nChanges: converted to the Second Life mesh format.\n";

    /// <summary>A body, a separate head, and all four body-part wearables.</summary>
    public static CharacterDescription Description(bool hair = true) => new(
        "Example",
        new AttributionDescription(Credit, NotecardName: "Example credits", NotecardText: NotecardText),
        new List<PartDescription>
        {
            new("Example Body", PartRole.Body, "body.llmesh", new() { ["0"] = "head", ["1"] = "upper", ["2"] = "lower" }),
            new("Example Head", PartRole.Head, "head.llmesh", new() { ["0"] = "head", ["1"] = "eyes" }),
        },
        new WearablesDescription(
            new ShapeDescription(Values: new() { ["1"] = 0.4f, ["80"] = 0.25f }),
            new SkinDescription(SkinHead, SkinUpper, SkinLower),
            new EyesDescription(Iris),
            hair ? new HairDescription(HairTexture) : null));

    public static Func<string, byte[]> Files(byte[]? body = null, byte[]? head = null)
    {
        var b = body ?? Mesh(3, new N.Vector3(0.62f, 0.31f, 1.78f));
        var h = head ?? Mesh(2, new N.Vector3(0.2f, 0.22f, 0.25f), rigged: false);
        return name => name switch { "body.llmesh" => b, "head.llmesh" => h, _ => throw new FileNotFoundException(name) };
    }

    public static PublishContext Context(int folder = 1) => new(Owner, Creator, folder, Date);

    public static AssembledBundle Assemble(CharacterDescription? d = null, PublishContext? ctx = null, Func<string, byte[]>? files = null) =>
        OutfitAssembler.Assemble(d ?? Description(), ctx ?? Context(), files ?? Files());

    /// <summary>A mutable copy of a bundle, to make one defect at a time.</summary>
    public sealed class Edit
    {
        public Bundle Bundle;
        public Dictionary<UUID, byte[]> Content;
        public Edit(AssembledBundle a) { Bundle = a.Bundle; Content = new Dictionary<UUID, byte[]>(a.Content); }

        public BundleItem Item(string role) => Bundle.Items.Single(i => i.Role == role);
        public BundleAsset Asset(UUID id) => Bundle.Assets.Single(a => a.Id == id);

        public void SetItem(string role, Func<BundleItem, BundleItem> change) =>
            Bundle = Bundle with { Items = Bundle.Items.Select(i => i.Role == role ? change(i) : i).ToList() };

        /// <summary>Replaces the bytes of the asset an item holds, with an id derived from the new bytes, as the assembler would.</summary>
        public void ReplaceContent(string role, Func<byte[], byte[]> change)
        {
            var item = Item(role);
            var old = Asset(item.AssetId);
            var bytes = change(Content[old.Id]);
            var id = ContentId.OfBytes(bytes);
            Content.Remove(old.Id);
            Content[id] = bytes;
            Bundle = Bundle with { Assets = Bundle.Assets.Select(a => a.Id == old.Id ? a with { Id = id, Length = bytes.Length, File = id + Path.GetExtension(a.File) } : a).ToList() };
            SetItem(role, i => i with { AssetId = id });
        }

        public void ReplaceText(string role, Func<string, string> change) =>
            ReplaceContent(role, b => Encoding.UTF8.GetBytes(change(Encoding.UTF8.GetString(b))));

        public ValidationResult Validate() => BundleValidator.Validate(Bundle, Content);
    }
}
