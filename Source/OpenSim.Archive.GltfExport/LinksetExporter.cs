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
using OpenSim.Archive.Inspection;

namespace OpenSim.Archive.GltfExport;

internal sealed class BuiltBuild
{
    public BuildManifest Manifest;
    /// <summary>The glTF binary, or null when no part of the build is in the file.</summary>
    public byte[] Glb;
    /// <summary>Asset files (textures, materials) to write unchanged beside the glTF file, in a folder named textures.</summary>
    public List<AssetBlob> Copies = new List<AssetBlob>();
    /// <summary>The PNG files the glTF file points at (shared layout), by texture id; empty when the images are inside the file.</summary>
    public Dictionary<UUID, byte[]> Pngs = new Dictionary<UUID, byte[]>();
}

/// <summary>
/// Turns one linkset into a glTF document and its manifest. The object is a root node; each part is a child node
/// named exactly as the part, in link-number order, placed relative to the root part. Viewer axes (Z up) become
/// glTF axes (Y up) by (x, y, z) to (x, z, -y), which Blender's importer undoes.
/// </summary>
internal static class LinksetExporter
{
    /// <summary>Added to the object's name for the glTF node that holds the parts.</summary>
    internal const string ObjectNodeSuffix = " [build]";

    private const byte PrimCode = 9;
    private const byte GrassCode = 95;
    private const byte NewTreeCode = 111;
    private const byte TreeCode = 255;

    public static BuiltBuild Build(ParsedGroup g, string archiveName, string entry, string glbFileName, bool world = false, ArchiveAssets assets = null, bool shared = false)
    {
        // link-number order; the file order is not the link order. A stable sort keeps file order for equal numbers.
        List<ParsedPart> parts = g.Parts.Select((p, i) => (p, i)).OrderBy(t => t.p.Part.LinkNum).ThenBy(t => t.i).Select(t => t.p).ToList();
        ParsedPart root = parts.FirstOrDefault(p => p.Part.IsRoot) ?? parts[0];

        GltfDocument doc = new GltfDocument();
        // The object node is named apart from the parts: the root part has the object's name, and two nodes of one
        // name would make Blender rename one of them.
        GltfNode objectNode = new GltfNode
        {
            Name = Text.Clean(g.Name) + ObjectNodeSuffix,
            Extras = new JsonObject
            {
                ["kind"] = "object",
                ["name"] = Text.Clean(g.Name),
                ["rootPartUuid"] = root.Part.PartId.ToString(),
                ["partCount"] = parts.Count,
                ["archiveEntry"] = Text.Clean(entry),
                ["regionPosition"] = Vec(new Vector3(g.X, g.Y, g.Z)),
                ["regionRotation"] = Quat(root.RotationOffset),
                ["placement"] = world ? "world" : "root-part",
                ["coordinateSystem"] = "Node transforms and vertices are glTF Y-up: (x, z, -y) of the viewer's Z-up axes. Values in extras use the viewer's Z-up axes."
            }
        };
        if (world)
        {
            // the build node takes the root part's place in the region; its children stay relative to the root part
            objectNode.Translation = Glt(new Vector3(g.X, g.Y, g.Z));
            objectNode.Rotation = GltQuat(root.RotationOffset);
        }
        doc.Nodes.Add(objectNode);

        BuildManifest manifest = new BuildManifest
        {
            Archive = archiveName,
            ArchiveEntry = Text.Clean(entry),
            Name = Text.Clean(g.Name),
            RootPartUuid = root.Part.PartId.ToString(),
            File = glbFileName,
            PartCount = parts.Count
        };

        MaterialContext materials = new MaterialContext { Doc = doc, Assets = assets, Shared = shared };
        manifest.Layout = shared ? "shared" : "self-contained";

        foreach (ParsedPart pp in parts)
        {
            PartRecord rec = pp.Part;
            ManifestPart mp = Describe(pp);

            if (!ExportPermission.IsAllowed(rec, out string refusal))
            {
                mp.Status = ExportStatus.Skipped;
                mp.Reason = refusal;
                mp.Contents = null;
                manifest.Skipped++;
                manifest.Parts.Add(mp);
                continue;
            }

            GltfNode node = new GltfNode { Name = Text.Clean(rec.Name) };
            if (!rec.IsRoot)
            {
                node.Translation = Glt(pp.OffsetPosition);
                node.Rotation = GltQuat(pp.RotationOffset);
            }

            string kind = Classify(pp, out string pendingWhat);
            mp.Kind = kind;
            JsonObject extras = NodeExtras(pp, mp);
            bool done = false;

            if (kind == PartKinds.Prim || kind == PartKinds.Sculpt || kind == PartKinds.Mesh)
            {
                try
                {
                    GltfMesh mesh;
                    int faces, triangles;
                    if (kind == PartKinds.Prim)
                        mesh = BuildPrimMesh(pp, doc, materials, out faces, out triangles);
                    else if (!BuildAssetPart(pp, kind, doc, materials, mp, extras, out mesh, out faces, out triangles, out string assetPending))
                    {
                        mesh = null;
                        faces = 0;
                        triangles = 0;
                        pendingWhat = assetPending;
                    }
                    if (mesh != null && faces > 0)
                    {
                        doc.Meshes.Add(mesh);
                        node.Mesh = doc.Meshes.Count - 1;
                        mp.Status = ExportStatus.Exported;
                        mp.Faces = faces;
                        mp.Triangles = triangles;
                        extras["geometry"] = "exported";
                        extras["faces"] = faces;
                        extras["triangles"] = triangles;
                        manifest.Exported++;
                        done = true;
                    }
                    else if (kind == PartKinds.Prim)
                        pendingWhat = "the mesher produced no faces for this shape";
                    else if (string.IsNullOrEmpty(pendingWhat))
                        pendingWhat = "no geometry could be built from the " + (kind == PartKinds.Mesh ? "mesh asset" : "sculpt map");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    pendingWhat = "the " + (kind == PartKinds.Prim ? "mesher" : kind == PartKinds.Mesh ? "mesh decoder" : "sculpt builder") + " failed: " + Text.Clean(e.GetType().Name + ": " + e.Message);
                }
            }

            if (!done)
            {
                mp.Status = ExportStatus.Placeheld;
                mp.Reason = pendingWhat;
                extras["geometry"] = "pending";
                extras["geometryPending"] = pendingWhat;
                manifest.Placeheld++;
            }

            node.Extras = extras;
            doc.Nodes.Add(node);
            objectNode.Children.Add(doc.Nodes.Count - 1);
            manifest.Parts.Add(mp);
        }

        BuiltBuild result = new BuiltBuild { Manifest = manifest };
        manifest.Textures = materials.Used.Values.OrderBy(t => t.Role, StringComparer.Ordinal).ThenBy(t => t.TextureId, StringComparer.Ordinal).ToList();
        manifest.TexturesEmbedded = manifest.Textures.Count(t => t.Status == "embedded");
        manifest.TexturesMissing = manifest.Textures.Count(t => t.Status != "embedded" && t.Status != "copied");
        result.Copies.AddRange(materials.Copies.Values.OrderBy(c => c.Id.ToString(), StringComparer.Ordinal));
        result.Pngs = materials.Pngs;
        foreach (AssetBlob c in result.Copies)
            manifest.Files.Add(new ManifestFile { Id = c.Id.ToString(), Kind = "original", Path = materials.Prefix + c.Folder + "/" + c.Id + c.Extension });
        foreach (UUID pngId in materials.Pngs.Keys.OrderBy(k => k.ToString(), StringComparer.Ordinal))
        {
            manifest.Files.Add(new ManifestFile { Id = pngId.ToString(), Kind = "png", Path = "../textures/" + pngId + ".png" });
            foreach (ManifestTexture t in manifest.Textures.Where(t => t.TextureId == pngId.ToString() && t.Status == "embedded"))
                t.ImagePath = "../textures/" + pngId + ".png";
        }
        manifest.TexturesCopied = result.Copies.Count(c => c.Folder == "textures");
        manifest.AssetsCopied = result.Copies.Count(c => c.Folder != "textures");
        if (objectNode.Children.Count > 0)
        {
            result.Glb = doc.ToGlb();
        }
        else
            manifest.File = null;
        return result;
    }

    // ------------------------------------------------------------------ parts

    private static ManifestPart Describe(ParsedPart pp)
    {
        PartRecord rec = pp.Part;
        ManifestPart mp = new ManifestPart
        {
            LinkNumber = rec.LinkNum,
            Name = Text.Clean(rec.Name),
            Description = Text.Clean(pp.Description),
            PartUuid = rec.PartId.ToString(),
            IsRoot = rec.IsRoot,
            CreatorId = rec.Creator.Id.ToString(),
            CreatorName = Text.Clean(rec.Creator.Name),
            CreatorHome = Text.Clean(rec.Creator.Home),
            OwnerId = rec.OwnerId.ToString(),
            Permissions = new ManifestPermissions
            {
                Base = rec.Masks.Base,
                Owner = rec.Masks.Owner,
                Group = rec.Masks.Group,
                Everyone = rec.Masks.Everyone,
                NextOwner = rec.Masks.NextOwner
            },
            Contents = pp.Items.Select(i => new ManifestItem
            {
                Name = Text.Clean(i.Name),
                Type = i.AssetType.HasValue ? ArchiveInspector.TypeName(i.AssetType.Value) : null,
                InventoryType = i.InvType.HasValue ? InventoryTypeName(i.InvType.Value) : null
            }).ToList()
        };
        return mp;
    }

    private static string InventoryTypeName(int type)
    {
        string name = type >= sbyte.MinValue && type <= sbyte.MaxValue && Enum.IsDefined(typeof(InventoryType), (sbyte)type) ? ((InventoryType)(sbyte)type).ToString() : null;
        return name ?? type.ToString(CultureInfo.InvariantCulture);
    }

    private static JsonObject NodeExtras(ParsedPart pp, ManifestPart mp)
    {
        PartRecord rec = pp.Part;
        JsonObject o = new JsonObject
        {
            ["kind"] = mp.Kind,
            ["linkNumber"] = rec.LinkNum,
            ["partUuid"] = rec.PartId.ToString(),
            ["partName"] = mp.Name,
            ["description"] = mp.Description,
            ["isRoot"] = rec.IsRoot,
            ["creatorId"] = rec.Creator.Id.ToString(),
            ["creatorName"] = mp.CreatorName,
            ["ownerId"] = rec.OwnerId.ToString(),
            ["permissions"] = new JsonObject
            {
                ["base"] = Mask(rec.Masks.Base),
                ["owner"] = Mask(rec.Masks.Owner),
                ["group"] = Mask(rec.Masks.Group),
                ["everyone"] = Mask(rec.Masks.Everyone),
                ["nextOwner"] = Mask(rec.Masks.NextOwner)
            },
            ["size"] = Vec(pp.Scale),
            ["contentsCount"] = pp.Items.Count
        };
        if (pp.Shape != null)
        {
            o["pcode"] = pp.Shape.PCode;
            if (pp.Shape.SculptEntry || pp.Shape.SculptTexture != UUID.Zero || pp.Shape.SculptType != 0)
            {
                o["sculptType"] = pp.Shape.SculptType;
                o["sculptTexture"] = pp.Shape.SculptTexture.ToString();
            }
            if (mp.Kind == PartKinds.Tree || mp.Kind == PartKinds.Grass)
                o["species"] = pp.Shape.State;
        }
        return o;
    }

    private static JsonNode Mask(uint? m) => m.HasValue ? JsonValue.Create((long)m.Value) : null;

    /// <summary>What the part is, and for a part whose geometry this release does not make, why.</summary>
    internal static string Classify(ParsedPart pp, out string pending)
    {
        pending = null;
        ShapeData s = pp.Shape;
        if (s == null)
        {
            pending = "the object file has no shape for this part";
            return PartKinds.Other;
        }

        switch ((byte)s.PCode)
        {
            case PrimCode:
                bool sculptLike = s.SculptEntry || (s.SculptType & 7) != 0 && s.SculptTexture != UUID.Zero;
                if (!sculptLike)
                    return PartKinds.Prim;
                if ((s.SculptType & 7) == (int)SculptType.Mesh)
                {
                    pending = "mesh geometry is not exported yet (mesh asset " + s.SculptTexture + ")";
                    return PartKinds.Mesh;
                }
                pending = "sculpted geometry is not exported yet (sculpt texture " + s.SculptTexture + ")";
                return PartKinds.Sculpt;
            case GrassCode:
                pending = "built-in grass is not exported yet";
                return PartKinds.Grass;
            case TreeCode:
            case NewTreeCode:
                pending = "built-in tree is not exported yet";
                return PartKinds.Tree;
            default:
                pending = "shape code " + s.PCode.ToString(CultureInfo.InvariantCulture) + " is not exported";
                return PartKinds.Other;
        }
    }

    // ------------------------------------------------------------------ geometry

    private static GltfMesh BuildPrimMesh(ParsedPart pp, GltfDocument doc, MaterialContext materials, out int faceCount, out int triangleCount)
    {
        PrimParams prm = PrimParams.FromShape(pp.Shape);
        List<PrimFace> faces = PrimGeometry.BuildFaces(prm, DetailLevel.High, out int viewerFaceCount, out int unnumbered);
        return BuildPartMesh(pp, doc, materials, faces, viewerFaceCount, unnumbered, out faceCount, out triangleCount);
    }

    /// <summary>
    /// Builds a sculpt or mesh part's geometry from the asset its shape names. Returns false, with the reason in
    /// <paramref name="pending"/>, when the part stays a placeholder.
    /// </summary>
    private static bool BuildAssetPart(ParsedPart pp, string kind, GltfDocument doc, MaterialContext materials, ManifestPart mp, JsonObject extras,
        out GltfMesh mesh, out int faceCount, out int triangleCount, out string pending)
    {
        mesh = null;
        faceCount = 0;
        triangleCount = 0;
        pending = null;
        ShapeData s = pp.Shape;
        bool isMesh = kind == PartKinds.Mesh;
        string what = isMesh ? "mesh asset" : "sculpt map";
        UUID id = s.SculptTexture;
        if (id == UUID.Zero)
        {
            mp.GeometryAssetStatus = "no-asset-named";
            pending = "the part names no " + what;
            return false;
        }

        ManifestTexture use = FaceMaterials.NoteGeometryAsset(materials, id, isMesh ? "mesh" : "sculpt", out AssetBlob blob);
        mp.GeometryAssetId = id.ToString();
        mp.GeometryAssetStatus = use.Status;
        JsonObject assetInfo = new JsonObject
        {
            ["role"] = isMesh ? "mesh" : "sculpt",
            ["id"] = id.ToString(),
            ["status"] = use.Status,
            ["copiedTo"] = use.CopiedTo
        };
        extras["geometryAsset"] = assetInfo;
        if (blob == null)
        {
            pending = "the " + what + " " + id + (use.Status == ArchiveAssets.OverDiskStatus ? " was over --max-cache-mb" : use.Status == ArchiveAssets.OverMemoryStatus ? " was too large for --max-memory-mb" : " is not in the archive");
            return false;
        }

        List<PrimFace> faces;
        int viewerFaceCount;
        bool applyPartSize = true;
        if (isMesh)
        {
            if (!MeshAsset.TryDecode(blob.Bytes, out MeshModel model, out string problem))
            {
                use.Status = "undecodable";
                use.Reason = problem;
                mp.GeometryAssetStatus = "undecodable";
                assetInfo["status"] = "undecodable";
                pending = problem;
                return false;
            }
            faces = MeshGeometry.ToFaces(model, s.SculptType, out int beyond, out int noNormals, out int noUvs, out int badTriangles);
            viewerFaceCount = Math.Min(model.Faces.Count, MeshAsset.MaxFaces);

            JsonObject info = new JsonObject
            {
                ["blocks"] = new JsonArray(model.Blocks.Select(b => (JsonNode)JsonValue.Create(b)).ToArray()),
                ["submeshes"] = model.Faces.Count,
                ["levelOfDetail"] = "highest present",
                ["note"] = "lower levels of detail and the physics shape stay in the copied asset and are not in the glTF file"
            };
            if (beyond > 0) info["submeshesBeyondEightFaces"] = beyond;
            if (noNormals > 0) info["facesWithoutNormals"] = noNormals;
            if (noUvs > 0) info["facesWithoutTextureCoordinates"] = noUvs;
            if (badTriangles > 0) info["trianglesWithBadIndices"] = badTriangles;
            extras["mesh"] = info;

            mp.Rigged = model.Skin != null;
            extras["rigged"] = model.Skin != null;
            if (model.Skin != null)
                extras["bindShapeApplied"] = true;
            if (model.Skin != null)
            {
                // The viewer draws a rigged mesh with its bind shape matrix in place of the part's size (llface.cpp, getGeometryVolume:
                // mat_vert is the bind shape matrix, mat_normal its inverse transpose), so the vertices are exported multiplied by it.
                BindShape.Apply(faces, model.Skin.BindShapeMatrix);
                applyPartSize = false;
                mp.JointNames = model.Skin.JointNames.Select(Text.Clean).ToList();
                JsonObject skin = new JsonObject
                {
                    ["jointNames"] = new JsonArray(mp.JointNames.Select(j => (JsonNode)JsonValue.Create(j)).ToArray()),
                    ["inverseBindMatrices"] = model.Skin.InverseBindMatrices,
                    ["note"] = "the vertices are multiplied by the bind shape matrix (the true bind pose) and the part's size is not applied; bindShapeMatrix below is the matrix that was applied, so a return path can multiply by its inverse. The skin itself is kept in the copied asset and is not in the glTF file",
                    ["bindShapeApplied"] = true
                };
                if (model.Skin.BindShapeMatrix != null)
                    skin["bindShapeMatrix"] = new JsonArray(model.Skin.BindShapeMatrix.Select(f => (JsonNode)JsonValue.Create(float.IsFinite(f) ? f : 0f)).ToArray());
                extras["skin"] = skin;
            }
        }
        else
        {
            SculptMap map = materials.Assets.DecodeSculptMap(id, out string problem);
            if (map == null)
            {
                use.Status = "undecodable";
                use.Reason = problem;
                mp.GeometryAssetStatus = "undecodable";
                assetInfo["status"] = "undecodable";
                pending = "the sculpt map " + id + " could not be read: " + problem;
                return false;
            }
            use.Width = map.Width;
            use.Height = map.Height;
            PrimParams shapeParams = PrimParams.FromShape(s);
            bool volumeFaces = SculptGeometry.HasProfileFaces(shapeParams);
            SculptResult r = SculptGeometry.BuildVolume(map, s.SculptType, shapeParams);
            JsonObject info = new JsonObject
            {
                ["sculptType"] = s.SculptType,
                ["stitching"] = ((SculptType)(s.SculptType & 7)).ToString(),
                ["mirror"] = (s.SculptType & SculptGeometry.FlagMirror) != 0,
                ["invert"] = (s.SculptType & SculptGeometry.FlagInvert) != 0,
                ["mapWidth"] = map.Width,
                ["mapHeight"] = map.Height,
                ["gridRows"] = r.Rows,
                ["gridColumns"] = r.Columns,
                ["surfaceArea"] = float.IsFinite(r.SurfaceArea) ? r.SurfaceArea : 0f
            };
            if (r.DegenerateTrianglesDropped > 0) info["degenerateTrianglesDropped"] = r.DegenerateTrianglesDropped;
            if (volumeFaces)
                info["profileFaces"] = "the part has a profile cut, a hollow or an open path; its faces are the ones the viewer gives its profile, numbered as the viewer numbers them";
            else if (s.ProfileHollow != 0 || s.ProfileBegin != 0 || s.ProfileEnd != 0 || s.PathBegin != 0 || s.PathEnd != 0)
                info["shapeFieldsNote"] = "the part also carries profile or path cuts or a hollow on a profile or path that is not a circle; the viewer would apply them to the faces, the exporter builds the one face the map gives";
            extras["sculpt"] = info;
            if (r.Face == null)
            {
                pending = r.Problem;
                return false;
            }
            faces = r.Faces;
            viewerFaceCount = r.ViewerFaceCount;
        }

        mesh = BuildPartMesh(pp, doc, materials, faces, viewerFaceCount, 0, out faceCount, out triangleCount, applyPartSize);
        return faceCount > 0;
    }

    private static GltfMesh BuildPartMesh(ParsedPart pp, GltfDocument doc, MaterialContext materials, List<PrimFace> faces, int viewerFaceCount, int unnumbered, out int faceCount, out int triangleCount, bool applyPartSize = true)
    {
        TextureLookup textures = TextureLookup.Read(pp.TextureEntry);
        PartMaterialInfo materialInfo = PartMaterialInfo.Read(pp);

        GltfMesh mesh = new GltfMesh { Name = Text.Clean(pp.Part.Name) };
        JsonArray numbers = new JsonArray();
        // a rigged mesh is drawn with its bind shape matrix instead of the part's size (see BindShape), so its size is not applied
        float sx = applyPartSize ? pp.Scale.X : 1f, sy = applyPartSize ? pp.Scale.Y : 1f, sz = applyPartSize ? pp.Scale.Z : 1f;
        float partLink = pp.Part.LinkNum;
        float isx = sx != 0 ? 1f / sx : 1f, isy = sy != 0 ? 1f / sy : 1f, isz = sz != 0 ? 1f / sz : 1f;
        triangleCount = 0;

        foreach (PrimFace f in faces)
        {
            int n = f.Positions.Length;
            float[] pos = new float[n];
            float[] nor = new float[n];
            float[] uv = new float[f.Uvs.Length];
            for (int i = 0; i < n; i += 3)
            {
                float x = Finite(f.Positions[i]) * sx, y = Finite(f.Positions[i + 1]) * sy, z = Finite(f.Positions[i + 2]) * sz;
                pos[i] = x; pos[i + 1] = z; pos[i + 2] = -y;

                // normals of a scaled shape follow the inverse transpose of the scale
                float nx = Finite(f.Normals[i]) * isx, ny = Finite(f.Normals[i + 1]) * isy, nz = Finite(f.Normals[i + 2]) * isz;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; }
                nor[i] = nx; nor[i + 1] = nz; nor[i + 2] = -ny;
            }
            // the mesher's coordinates already have v = 0 at the top of the image, which is glTF's convention
            for (int i = 0; i < uv.Length; i++)
                uv[i] = Finite(f.Uvs[i]);

            float[] faceNumbers = new float[n / 3];
            Array.Fill(faceNumbers, (float)f.Number);
            float[] partLinks = new float[n / 3];
            Array.Fill(partLinks, partLink);

            FaceSettings settings = FaceMaterials.Read(textures.Face(f.Number), f.Number);
            int materialIndex = FaceMaterials.Material(materials, settings, materialInfo, pp, out bool bakedUv);
            if (bakedUv)
                UvTransform.Bake(uv, settings.RepeatU, settings.RepeatV, settings.OffsetU, settings.OffsetV, settings.Rotation);

            GltfPrimitive prim = new GltfPrimitive
            {
                Positions = pos,
                Normals = nor,
                Uvs = uv,
                FaceNumbers = faceNumbers,
                PartLinks = partLinks,
                Material = materialIndex,
                Extras = new JsonObject { ["primFace"] = f.Number }
            };
            mesh.Primitives.Add(prim);
            numbers.Add(f.Number);
            triangleCount += f.TriangleCount;
        }

        mesh.Extras = new JsonObject
        {
            ["primFaces"] = numbers,
            ["primFaceCount"] = viewerFaceCount,
            ["primFaceKinds"] = new JsonArray(faces.Select(f => (JsonNode)JsonValue.Create(f.Kind.ToString())).ToArray())
        };
        if (unnumbered > 0)
            mesh.Extras["unnumberedTriangles"] = unnumbered;
        faceCount = faces.Count;
        return mesh;
    }

    private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

    // ------------------------------------------------------------------ materials

    private sealed class TextureLookup
    {
        private Primitive.TextureEntry m_te;

        public static TextureLookup Read(byte[] data)
        {
            TextureLookup t = new TextureLookup();
            if (data != null && data.Length > 16)
            {
                try { t.m_te = new Primitive.TextureEntry(data, 0, data.Length); }
                catch (Exception) { t.m_te = null; }
            }
            return t;
        }

        public Primitive.TextureEntryFace Face(int number)
        {
            if (m_te == null)
                return null;
            if (number >= 0 && m_te.FaceTextures != null && number < m_te.FaceTextures.Length && m_te.FaceTextures[number] != null)
                return m_te.FaceTextures[number];
            return m_te.DefaultTexture;
        }
    }

    // ------------------------------------------------------------------ axes

    /// <summary>Viewer vector to glTF translation.</summary>
    internal static float[] Glt(Vector3 v) => new[] { v.X, v.Z, -v.Y };

    /// <summary>Viewer rotation to glTF rotation (x, y, z, w), normalised.</summary>
    internal static float[] GltQuat(Quaternion q)
    {
        float len = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (!(len > 1e-12f))
            return new[] { 0f, 0f, 0f, 1f };
        return new[] { q.X / len, q.Z / len, -q.Y / len, q.W / len };
    }

    private static JsonArray Vec(Vector3 v) => new JsonArray(v.X, v.Y, v.Z);
    private static JsonArray Quat(Quaternion q) => new JsonArray(q.X, q.Y, q.Z, q.W);
}

internal static class Text
{
    /// <summary>Control characters and unpaired surrogates removed, so the text is valid in JSON and in a file name.</summary>
    public static string Clean(string s)
    {
        s = Sanitize.Text(s);
        if (string.IsNullOrEmpty(s))
            return s ?? string.Empty;

        StringBuilder sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool bad = false;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb?.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }
                bad = true;
            }
            else if (char.IsLowSurrogate(c))
                bad = true;

            if (bad)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                continue;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }
}
