/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Inspection;
using OpenSim.Framework;

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// Texture coordinates of a face are transformed by the viewer about the centre of the face: rotate, then scale by the
/// repeats, then add the offset (LLFace's xform in the Second Life viewer's indra/newview/llface.cpp, with v up). glTF's
/// KHR_texture_transform applies scale, then rotation, then offset, about the origin, and the exported coordinates have
/// v down (v = 0 at the top of the image). This turns the first into the second.
/// </summary>
internal static class UvTransform
{
    /// <param name="exact">
    /// False when the face has both a rotation and unequal repeats: the viewer rotates before it scales, glTF scales before
    /// it rotates, and no single KHR_texture_transform gives the same result. The centre of the face then lands correctly
    /// and the transform is the closest the extension can express; the exact values are in the material's extras.
    /// </param>
    public static void ToKhr(float repeatU, float repeatV, float offsetU, float offsetV, float rotation, out float[] offset, out float[] scale, out bool exact)
    {
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        // KHR: rotation(r) * scale
        float m00 = c * repeatU, m01 = -s * repeatV, m10 = s * repeatU, m11 = c * repeatV;
        // the viewer's centre of the face (0.5, 0.5) goes to (offsetU + 0.5, 0.5 - offsetV) in the down convention
        offset = new[] { offsetU + 0.5f - (m00 * 0.5f + m01 * 0.5f), 0.5f - offsetV - (m10 * 0.5f + m11 * 0.5f) };
        scale = new[] { repeatU, repeatV };
        exact = MathF.Abs(s * (repeatU - repeatV)) < 1e-6f;
    }

    /// <summary>True when KHR_texture_transform can say this face's transform exactly (no rotation, or equal repeats).</summary>
    public static bool IsExactInKhr(float repeatU, float repeatV, float rotation) =>
        MathF.Abs(MathF.Sin(rotation) * (repeatU - repeatV)) < 1e-6f;

    /// <summary>
    /// Applies the viewer's transform to texture coordinates in place, for a face the extension cannot describe. The
    /// coordinates have v down, the viewer's rule has v up, so v is flipped before and after.
    /// </summary>
    public static void Bake(float[] uvs, float repeatU, float repeatV, float offsetU, float offsetV, float rotation)
    {
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        for (int i = 0; i + 1 < uvs.Length; i += 2)
        {
            float u = uvs[i] - 0.5f;
            float t = (1f - uvs[i + 1]) - 0.5f;
            float ru = u * c + t * s;
            float rt = -u * s + t * c;
            ru = ru * repeatU + offsetU + 0.5f;
            rt = rt * repeatV + offsetV + 0.5f;
            uvs[i] = ru;
            uvs[i + 1] = 1f - rt;
        }
    }
}

/// <summary>The settings one face carries in a texture entry, as stored.</summary>
internal sealed class FaceSettings
{
    public int Number;
    public UUID TextureId;
    public Color4 Color;
    public float RepeatU, RepeatV, OffsetU, OffsetV, Rotation, Glow;
    public bool Fullbright, MediaFlag;
    public Shininess Shiny;
    public Bumpiness Bump;
    public MappingType Mapping;
    public UUID MaterialId;
}

/// <summary>A legacy material (normal and specular maps and their settings) as the region stores it.</summary>
internal sealed class LegacyMaterial
{
    public UUID NormalMap, SpecularMap;
    public float NormalOffsetX, NormalOffsetY, NormalRepeatX = 1, NormalRepeatY = 1, NormalRotation;
    public float SpecularOffsetX, SpecularOffsetY, SpecularRepeatX = 1, SpecularRepeatY = 1, SpecularRotation;
    public int SpecularColorR = 255, SpecularColorG = 255, SpecularColorB = 255;
    public int Glossiness, EnvironmentIntensity, DiffuseAlphaMode, AlphaMaskCutoff;

    /// <summary>Reads the LLSD the region stores for a legacy material; the offsets, repeats and rotations are stored times 10000.</summary>
    public static LegacyMaterial Parse(byte[] xml)
    {
        OSD osd = OSDParser.Deserialize(xml);
        if (osd is not OSDMap map)
            return null;
        const float scale = 0.0001f;
        LegacyMaterial m = new LegacyMaterial
        {
            NormalMap = map["NormMap"].AsUUID(),
            NormalOffsetX = scale * (float)map["NormOffsetX"].AsReal(),
            NormalOffsetY = scale * (float)map["NormOffsetY"].AsReal(),
            NormalRepeatX = scale * (float)map["NormRepeatX"].AsReal(),
            NormalRepeatY = scale * (float)map["NormRepeatY"].AsReal(),
            NormalRotation = scale * (float)map["NormRotation"].AsReal(),
            SpecularMap = map["SpecMap"].AsUUID(),
            SpecularOffsetX = scale * (float)map["SpecOffsetX"].AsReal(),
            SpecularOffsetY = scale * (float)map["SpecOffsetY"].AsReal(),
            SpecularRepeatX = scale * (float)map["SpecRepeatX"].AsReal(),
            SpecularRepeatY = scale * (float)map["SpecRepeatY"].AsReal(),
            SpecularRotation = scale * (float)map["SpecRotation"].AsReal(),
            Glossiness = (int)map["SpecExp"].AsUInteger(),
            EnvironmentIntensity = (int)map["EnvIntensity"].AsUInteger(),
            DiffuseAlphaMode = (int)map["DiffuseAlphaMode"].AsUInteger(),
            AlphaMaskCutoff = (int)map["AlphaMaskCutoff"].AsUInteger()
        };
        if (map["SpecColor"] is OSDArray color && color.Count >= 3)
        {
            m.SpecularColorR = color[0].AsInteger();
            m.SpecularColorG = color[1].AsInteger();
            m.SpecularColorB = color[2].AsInteger();
        }
        return m;
    }
}

/// <summary>What a part stores beside its texture entry that bears on its faces' materials.</summary>
/// <summary>
/// The textures a PBR material asset (a glTF material text, as the viewer uploads it) names: the images list holds their ids as
/// URIs, and the material points at them through the textures list.
/// </summary>
internal sealed class PbrMaterial
{
    public UUID BaseColor, Normal, MetallicRoughness, Emissive, Occlusion;

    public IEnumerable<UUID> All => new[] { BaseColor, Normal, MetallicRoughness, Emissive, Occlusion }.Where(u => u != UUID.Zero);

    public static PbrMaterial Parse(byte[] text)
    {
        if (text == null || text.Length == 0 || text.Length > 4 * 1024 * 1024)
            return null;
        try
        {
            JsonNode root = JsonNode.Parse(text);
            JsonArray images = root?["images"] as JsonArray, textures = root?["textures"] as JsonArray, materials = root?["materials"] as JsonArray;
            if (images == null || materials == null || materials.Count == 0)
                return null;
            UUID Resolve(JsonNode reference)
            {
                int? index = reference?["index"]?.GetValue<int>();
                if (!index.HasValue || index < 0)
                    return UUID.Zero;
                int source = index.Value;
                if (textures != null)
                {
                    if (index >= textures.Count)
                        return UUID.Zero;
                    int? s = textures[index.Value]?["source"]?.GetValue<int>();
                    if (!s.HasValue)
                        return UUID.Zero;
                    source = s.Value;
                }
                if (source < 0 || source >= images.Count)
                    return UUID.Zero;
                string uri = images[source]?["uri"]?.GetValue<string>();
                return UUID.TryParse(uri, out UUID id) ? id : UUID.Zero;
            }
            JsonNode m = materials[0];
            JsonNode mr = m?["pbrMetallicRoughness"];
            return new PbrMaterial
            {
                BaseColor = Resolve(mr?["baseColorTexture"]),
                MetallicRoughness = Resolve(mr?["metallicRoughnessTexture"]),
                Normal = Resolve(m?["normalTexture"]),
                Emissive = Resolve(m?["emissiveTexture"]),
                Occlusion = Resolve(m?["occlusionTexture"])
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal sealed class PartMaterialInfo
{
    /// <summary>Face number to the PBR material asset the face names, from the part's render materials.</summary>
    public Dictionary<int, UUID> PbrBase = new Dictionary<int, UUID>();
    /// <summary>Face number to the PBR material override text, as stored.</summary>
    public Dictionary<int, string> PbrOverride = new Dictionary<int, string>();
    public bool HasAnimation;
    public Primitive.TextureAnimation Animation;
    public string AnimationRaw;

    public static PartMaterialInfo Read(ParsedPart pp)
    {
        PartMaterialInfo info = new PartMaterialInfo();
        try
        {
            if (pp.ExtraParams.Length > 0 || pp.MaterialOverrides.Length > 0)
            {
                PrimitiveBaseShape shape = new PrimitiveBaseShape();
                if (pp.ExtraParams.Length > 0)
                    shape.ReadInExtraParamsBytes(pp.ExtraParams);
                if (pp.MaterialOverrides.Length > 0)
                    shape.RenderMaterialsOvrFromRawBin(pp.MaterialOverrides);
                if (shape.RenderMaterials?.entries != null)
                    foreach (var e in shape.RenderMaterials.entries)
                        info.PbrBase[e.te_index] = e.id;
                if (shape.RenderMaterials?.overrides != null)
                    foreach (var o in shape.RenderMaterials.overrides)
                        if (!string.IsNullOrEmpty(o.data))
                            info.PbrOverride[o.te_index] = o.data;
            }
        }
        catch (Exception)
        {
            // damaged material data: the faces keep their ordinary texture settings
        }
        try
        {
            if (pp.TextureAnimation.Length >= 16)
            {
                info.Animation = new Primitive.TextureAnimation(pp.TextureAnimation, 0);
                info.HasAnimation = info.Animation.Flags != 0;
                info.AnimationRaw = Convert.ToBase64String(pp.TextureAnimation);
            }
        }
        catch (Exception)
        {
        }
        return info;
    }
}

/// <summary>Everything the materials of one build share: the glTF document, the archive's assets and what has been created so far.</summary>
internal sealed class MaterialContext
{
    public GltfDocument Doc;
    public ArchiveAssets Assets;
    public Dictionary<string, int> Materials = new Dictionary<string, int>();
    public Dictionary<UUID, int> GltfTextures = new Dictionary<UUID, int>();
    /// <summary>The textures the build used, by id and role.</summary>
    public Dictionary<(UUID, string), ManifestTexture> Used = new Dictionary<(UUID, string), ManifestTexture>();
    /// <summary>The asset files to copy unchanged into the build's textures folder.</summary>
    public Dictionary<UUID, AssetBlob> Copies = new Dictionary<UUID, AssetBlob>();
    public Dictionary<UUID, LegacyMaterial> LegacyCache = new Dictionary<UUID, LegacyMaterial>();
}

internal static class FaceMaterials
{
    private static readonly UUID PlywoodTexture = new UUID("89556747-24cb-43ed-920b-47caed15465f");

    public static FaceSettings Read(Primitive.TextureEntryFace face, int number)
    {
        face ??= new Primitive.TextureEntry(PlywoodTexture).DefaultTexture;
        return new FaceSettings
        {
            Number = number,
            TextureId = face.TextureID,
            Color = face.RGBA,
            RepeatU = face.RepeatU, RepeatV = face.RepeatV, OffsetU = face.OffsetU, OffsetV = face.OffsetV, Rotation = face.Rotation,
            Glow = face.Glow, Fullbright = face.Fullbright, MediaFlag = face.MediaFlags,
            Shiny = face.Shiny, Bump = face.Bump, Mapping = face.TexMapType, MaterialId = face.MaterialID
        };
    }

    /// <summary>Adds the assets a part's faces name: textures, legacy materials and PBR materials.</summary>
    public static void CollectAssetIds(ParsedPart pp, HashSet<UUID> into)
    {
        if (pp.Shape == null)
            return;
        // a sculpt map or mesh asset is named by the shape
        if (pp.Shape.SculptTexture != UUID.Zero && (pp.Shape.SculptEntry || (pp.Shape.SculptType & 7) != 0))
            into.Add(pp.Shape.SculptTexture);
        if (pp.TextureEntry.Length <= 16)
            return;
        try
        {
            Primitive.TextureEntry te = new Primitive.TextureEntry(pp.TextureEntry, 0, pp.TextureEntry.Length);
            foreach (Primitive.TextureEntryFace face in new[] { te.DefaultTexture }.Concat(te.FaceTextures ?? Array.Empty<Primitive.TextureEntryFace>()))
            {
                if (face == null)
                    continue;
                if (face.TextureID != UUID.Zero)
                    into.Add(face.TextureID);
                if (face.MaterialID != UUID.Zero)
                    into.Add(face.MaterialID);
            }
        }
        catch (Exception)
        {
            // a damaged texture entry names nothing
        }
        foreach (UUID pbr in PartMaterialInfo.Read(pp).PbrBase.Values)
            if (pbr != UUID.Zero)
                into.Add(pbr);
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    private static JsonArray Pair(float a, float b) => new JsonArray(Finite(a), Finite(b));

    /// <summary>
    /// The glTF material for a face, created once per distinct combination of texture and settings. The material's extras
    /// hold every value the face carries, so its name (the texture id) can be changed by a tool without losing anything.
    /// </summary>
    public static int Material(MaterialContext ctx, FaceSettings f, PartMaterialInfo info, ParsedPart pp, out bool bakedUv)
    {
        bakedUv = false;
        JsonObject extras = new JsonObject
        {
            ["kind"] = "face-material",
            ["textureId"] = f.TextureId.ToString(),
            ["tint"] = new JsonArray(Finite(f.Color.R), Finite(f.Color.G), Finite(f.Color.B), Finite(f.Color.A)),
            ["repeat"] = Pair(f.RepeatU, f.RepeatV),
            ["offset"] = Pair(f.OffsetU, f.OffsetV),
            ["rotation"] = Finite(f.Rotation),
            ["glow"] = Finite(f.Glow),
            ["fullbright"] = f.Fullbright,
            ["shiny"] = f.Shiny.ToString(),
            ["bumpmap"] = f.Bump.ToString(),
            ["mapping"] = f.Mapping == MappingType.Planar ? "planar" : "default",
            ["media"] = f.MediaFlag
        };
        if (f.Mapping == MappingType.Planar)
            extras["mappingNote"] = "planar mapping is not applied to the texture coordinates; they are the default ones";
        if (f.Bump != Bumpiness.None)
            extras["bumpmapNote"] = "legacy bump map (a built-in brightness or darkness map), not a normal map";

        GltfMaterial m = new GltfMaterial
        {
            Name = f.TextureId.ToString(),
            Color = new[] { Finite(f.Color.R), Finite(f.Color.G), Finite(f.Color.B), Finite(f.Color.A) }
        };

        // legacy material: normal and specular maps
        LegacyMaterial legacy = null;
        if (f.MaterialId != UUID.Zero)
            legacy = ReadLegacy(ctx, f.MaterialId, extras);

        // the diffuse texture
        TextureImage image = null;
        string status = TextureStatus(ctx, f.TextureId, "diffuse", out image);
        extras["textureStatus"] = status;
        extras["placeholder"] = image == null;
        if (image != null)
        {
            m.BaseColorTexture = TextureRef(ctx, f.TextureId, image);
            if (UvTransform.IsExactInKhr(f.RepeatU, f.RepeatV, f.Rotation))
            {
                UvTransform.ToKhr(f.RepeatU, f.RepeatV, f.OffsetU, f.OffsetV, f.Rotation, out float[] off, out float[] scale, out _);
                m.BaseColorTexture.Offset = off;
                m.BaseColorTexture.Rotation = f.Rotation;
                m.BaseColorTexture.Scale = scale;
                extras["khrTextureTransformExact"] = true;
            }
            else
            {
                // rotation with unequal repeats: the extension cannot say it, so the face's UVs carry the viewer's
                // transform and the extension is the identity
                m.BaseColorTexture.Offset = new[] { 0f, 0f };
                m.BaseColorTexture.Rotation = 0f;
                m.BaseColorTexture.Scale = new[] { 1f, 1f };
                extras["khrTextureTransformExact"] = true;
                extras["uvTransformBaked"] = true;
                bakedUv = true;
            }
        }

        bool tintBlend = f.Color.A < 0.999f;
        bool textureAlpha = image != null && image.HasAlpha;
        if (legacy != null)
        {
            switch (legacy.DiffuseAlphaMode)
            {
                case 2: // alpha masking
                    m.AlphaMode = "MASK";
                    m.AlphaCutoff = legacy.AlphaMaskCutoff / 255f;
                    break;
                case 1: // alpha blending
                    m.AlphaMode = tintBlend || textureAlpha ? "BLEND" : "OPAQUE";
                    break;
                default: // none, or emissive (the alpha is a glow mask)
                    m.AlphaMode = tintBlend ? "BLEND" : "OPAQUE";
                    break;
            }
        }
        else
            m.AlphaMode = tintBlend || textureAlpha ? "BLEND" : "OPAQUE";
        extras["alphaMode"] = m.AlphaMode;
        extras["textureHasAlpha"] = textureAlpha;

        if (legacy != null)
        {
            // the normal map: exported as the material's normal texture; the specular map goes to extras only
            TextureImage normal = null;
            if (legacy.NormalMap != UUID.Zero)
            {
                string normalStatus = TextureStatus(ctx, legacy.NormalMap, "normal", out normal);
                ((JsonObject)extras["legacyMaterial"])["normalMapStatus"] = normalStatus;
                if (normal != null)
                {
                    m.NormalTexture = TextureRef(ctx, legacy.NormalMap, normal);
                    UvTransform.ToKhr(legacy.NormalRepeatX, legacy.NormalRepeatY, legacy.NormalOffsetX, legacy.NormalOffsetY, legacy.NormalRotation, out float[] off, out float[] scale, out _);
                    m.NormalTexture.Offset = off;
                    m.NormalTexture.Rotation = legacy.NormalRotation;
                    m.NormalTexture.Scale = scale;
                }
            }
            if (legacy.SpecularMap != UUID.Zero)
                ((JsonObject)extras["legacyMaterial"])["specularMapStatus"] = TextureStatus(ctx, legacy.SpecularMap, "specular", out _);
        }

        // PBR material from the part's render materials: exported as stored
        if (info.PbrBase.TryGetValue(f.Number, out UUID pbrId) || info.PbrOverride.ContainsKey(f.Number))
        {
            JsonObject pbr = new JsonObject();
            if (pbrId != UUID.Zero)
            {
                pbr["materialId"] = pbrId.ToString();
                AssetBlob blob = ctx.Assets?.Get(pbrId);
                pbr["materialStatus"] = blob != null ? "read" : "missing-from-archive";
                if (blob?.Bytes != null && blob.Bytes.Length <= 256 * 1024)
                    pbr["material"] = Encoding.UTF8.GetString(blob.Bytes);
                if (blob != null)
                    ctx.Copies[pbrId] = blob;
                PbrMaterial parsed = blob != null ? PbrMaterial.Parse(blob.Bytes) : null;
                if (parsed != null)
                {
                    // the textures the material names are fetched, copied and embedded like a face's own texture
                    JsonObject textures = new JsonObject();
                    GltfTextureRef Fetch(UUID id, string role)
                    {
                        if (id == UUID.Zero)
                            return null;
                        string st = TextureStatus(ctx, id, role, out TextureImage img);
                        textures[role] = new JsonObject { ["id"] = id.ToString(), ["status"] = st };
                        return img != null ? TextureRef(ctx, id, img) : null;
                    }
                    GltfTextureRef baseColor = Fetch(parsed.BaseColor, "pbr-base-color");
                    GltfTextureRef normal = Fetch(parsed.Normal, "pbr-normal");
                    GltfTextureRef mr = Fetch(parsed.MetallicRoughness, "pbr-metallic-roughness");
                    GltfTextureRef emissive = Fetch(parsed.Emissive, "pbr-emissive");
                    Fetch(parsed.Occlusion, "pbr-occlusion");
                    // a face that names a PBR material has no diffuse texture of its own to keep
                    if (m.BaseColorTexture == null) m.BaseColorTexture = baseColor;
                    if (m.NormalTexture == null) m.NormalTexture = normal;
                    m.MetallicRoughnessTexture = mr;
                    m.EmissiveTexture = emissive;
                    pbr["textures"] = textures;
                }
            }
            if (info.PbrOverride.TryGetValue(f.Number, out string ovr))
                pbr["override"] = ovr;
            pbr["note"] = "stored PBR data is carried as it is; the textures its material names are copied and embedded as images of the glTF material";
            extras["pbr"] = pbr;
        }

        if (info.HasAnimation && (info.Animation.Face == 255 || info.Animation.Face == (uint)f.Number || (int)info.Animation.Face == -1))
        {
            extras["textureAnimation"] = new JsonObject
            {
                ["flags"] = info.Animation.Flags.ToString(),
                ["face"] = (int)info.Animation.Face,
                ["sizeX"] = (int)info.Animation.SizeX,
                ["sizeY"] = (int)info.Animation.SizeY,
                ["start"] = Finite(info.Animation.Start),
                ["length"] = Finite(info.Animation.Length),
                ["rate"] = Finite(info.Animation.Rate),
                ["raw"] = info.AnimationRaw
            };
        }
        _ = pp;

        m.Extras = extras;
        string key = extras.ToJsonString();
        if (ctx.Materials.TryGetValue(key, out int index))
            return index;
        ctx.Doc.Materials.Add(m);
        index = ctx.Doc.Materials.Count - 1;
        ctx.Materials[key] = index;
        return index;
    }

    private static LegacyMaterial ReadLegacy(MaterialContext ctx, UUID id, JsonObject extras)
    {
        JsonObject lm = new JsonObject { ["materialId"] = id.ToString() };
        extras["legacyMaterial"] = lm;
        AssetBlob blob = ctx.Assets?.Get(id);
        if (blob?.Bytes == null)
        {
            lm["status"] = "missing-from-archive";
            return null;
        }
        if (!ctx.LegacyCache.TryGetValue(id, out LegacyMaterial m))
        {
            try { m = LegacyMaterial.Parse(blob.Bytes); }
            catch (Exception) { m = null; }
            ctx.LegacyCache[id] = m;
        }
        if (m == null)
        {
            lm["status"] = "unreadable";
            return null;
        }
        ctx.Copies[id] = blob;
        lm["status"] = "read";
        lm["normalMap"] = m.NormalMap.ToString();
        lm["normalOffset"] = Pair(m.NormalOffsetX, m.NormalOffsetY);
        lm["normalRepeat"] = Pair(m.NormalRepeatX, m.NormalRepeatY);
        lm["normalRotation"] = Finite(m.NormalRotation);
        lm["specularMap"] = m.SpecularMap.ToString();
        lm["specularOffset"] = Pair(m.SpecularOffsetX, m.SpecularOffsetY);
        lm["specularRepeat"] = Pair(m.SpecularRepeatX, m.SpecularRepeatY);
        lm["specularRotation"] = Finite(m.SpecularRotation);
        lm["specularColor"] = new JsonArray(m.SpecularColorR, m.SpecularColorG, m.SpecularColorB);
        lm["glossiness"] = m.Glossiness;
        lm["environmentIntensity"] = m.EnvironmentIntensity;
        lm["diffuseAlphaMode"] = m.DiffuseAlphaMode;
        lm["alphaMaskCutoff"] = m.AlphaMaskCutoff;
        return m;
    }

    /// <summary>Notes the use of a texture, queues the asset for copying, and returns its decoded image when there is one.</summary>
    private static string TextureStatus(MaterialContext ctx, UUID id, string role, out TextureImage image)
    {
        image = null;
        (UUID, string) key = (id, role);
        if (!ctx.Used.TryGetValue(key, out ManifestTexture use))
        {
            use = new ManifestTexture { TextureId = id.ToString(), Role = role };
            ctx.Used[key] = use;
            AssetBlob blob = ctx.Assets?.Get(id);
            if (blob == null)
            {
                use.Status = ctx.Assets != null && ctx.Assets.OverLimit.Contains(id) ? "over-size-limit"
                    : WellKnownAssets.IsWellKnown(id) || id == PlywoodTexture ? "library-default" : "missing-from-archive";
            }
            else
            {
                ctx.Copies[id] = blob;
                use.CopiedTo = "textures/" + id + blob.Extension;
                TextureImage decoded = ctx.Assets.Decode(id, out string problem);
                if (decoded == null)
                {
                    use.Status = "undecodable";
                    use.Reason = problem;
                }
                else
                {
                    // a specular map is kept as a copy and in the extras; it is not a glTF texture of the material
                    use.Status = role == "specular" ? "copied" : "embedded";
                    use.Width = decoded.Width;
                    use.Height = decoded.Height;
                    use.HasAlpha = decoded.HasAlpha;
                }
            }
        }
        use.Faces++;
        if (use.Status == "embedded")
            image = ctx.Assets.Decode(id, out _);
        return use.Status;
    }

    /// <summary>
    /// Notes the use of the asset a sculpt or mesh part is built from (role "sculpt" or "mesh"), queues it for copying
    /// unchanged and returns the manifest row. The caller sets the status once it has tried to read the asset.
    /// </summary>
    public static ManifestTexture NoteGeometryAsset(MaterialContext ctx, UUID id, string role, out AssetBlob blob)
    {
        blob = ctx.Assets?.Get(id);
        (UUID, string) key = (id, role);
        if (!ctx.Used.TryGetValue(key, out ManifestTexture use))
        {
            use = new ManifestTexture { TextureId = id.ToString(), Role = role };
            ctx.Used[key] = use;
            if (blob == null)
                use.Status = ctx.Assets != null && ctx.Assets.OverLimit.Contains(id) ? "over-size-limit" : "missing-from-archive";
            else
            {
                ctx.Copies[id] = blob;
                use.CopiedTo = blob.Folder + "/" + id + blob.Extension;
                use.Status = "copied";
            }
        }
        use.Faces++;
        return use;
    }

    private static GltfTextureRef TextureRef(MaterialContext ctx, UUID id, TextureImage image)
    {
        if (!ctx.GltfTextures.TryGetValue(id, out int texture))
        {
            ctx.Doc.Images.Add(new GltfImage { Name = id.ToString(), Png = image.Png });
            ctx.Doc.Textures.Add(ctx.Doc.Images.Count - 1);
            texture = ctx.Doc.Textures.Count - 1;
            ctx.GltfTextures[id] = texture;
        }
        return new GltfTextureRef { Texture = texture };
    }

    internal static string Number(float v) => v.ToString("R", CultureInfo.InvariantCulture);
}
