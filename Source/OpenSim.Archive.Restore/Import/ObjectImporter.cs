/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Converter.Merge;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework.Serialization;
using OpenSim.MeshAsset;
using SysVector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Restore.Import;

/// <summary>
/// Writes an archive holding one mesh object made from a folder of .glb files: <c>name.glb</c> (the full mesh), <c>name_lod2.glb</c> (medium),
/// <c>name_lod1.glb</c> (low), <c>name_lod0.glb</c> (lowest) and, optionally, <c>name_physics.glb</c> (the collision shape). The pictures are inside the
/// files. The mesh asset is written by the same encoder the converter and the restore use; a face is one material of the full mesh, and a lower level
/// puts each of its materials in the face of the same material name (a level that leaves a material out leaves that face empty).
/// </summary>
public static partial class ObjectImporter
{
    private const string BlankTexture = "5748decc-f629-461c-9a36-a35a221fe21f";
    private const string PlywoodTexture = "89556747-24cb-43ed-920b-47caed15465f";
    private const float MinPrimSize = 0.01f, MaxPrimSize = 64f;
    private const int ShapeTypePrim = 0, ShapeTypeConvex = 2;
    /// <summary>The copy, modify and transfer permission bits, with every other bit, as a prim built in the region by its owner has them.</summary>
    private const string FullPermissions = "2147483647";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] LevelNames = { "high", "medium", "low", "lowest" };

    public static ImportResult Import(string folder, string outputPath, ImportOptions options)
    {
        options ??= new ImportOptions();
        Action<string> say = options.Progress ?? (_ => { });
        if (options.Avatar == Guid.Empty)
            throw new ImportException("an avatar is needed: it is named as the creator and the owner");
        if (!Directory.Exists(folder))
            throw new ImportException("the folder '" + folder + "' does not exist");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new ImportException("the output '" + outputPath + "' already exists; it is never overwritten");

        string baseName = FindBaseName(folder, options.BaseName);
        ImportResult result = new ImportResult { Output = outputPath };
        ModelLevels model = ReadFolderModel(folder, baseName, result.Notes);
        ImportProvenance provenance = new ImportProvenance(options.ToolVersion);
        provenance.SourceFolder(folder);
        foreach (string file in FolderModelFiles(folder, model))
            provenance.File(file, folder);
        result.Provenance = provenance.ToString();
        say("read " + string.Join(", ", model.Names));
        TextureCatalog catalog = new TextureCatalog(Path.Combine(folder, "no-original-textures"));
        BuiltPart built = BuildPart(model, catalog, result, say, null, options.Pbr);
        List<(Guid Id, byte[] Data, AssetType Type)> assets = built.Assets;
        foreach (KeyValuePair<Guid, byte[]> t in catalog.NewAssets)
            assets.Add((t.Key, t.Value, AssetType.Texture));
        (Vector3 size, Vector3 center, List<EncoderFace> highFaces, Guid meshId, int shapeType, Primitive.TextureEntry te) = (built.Size, built.Centre, built.HighFaces, built.MeshId, built.ShapeType, built.TextureEntry);

        // the object: one part, alone, so link number 0 and no offset
        string name = Clean(string.IsNullOrWhiteSpace(options.Name) ? baseName : options.Name);
        if (name.Length > 63)
            name = name.Substring(0, 63);
        string description = Clean(options.Description ?? string.Empty);
        long created = options.Created != 0 ? options.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Vector3 centre = new Vector3(options.Position.X + center.X, options.Position.Y + center.Y, options.Position.Z + center.Z);
        Guid partId = MeshId.FromBytes(Encoding.UTF8.GetBytes("oar-import|" + meshId.ToString("D") + "|" + name + "|" + options.Avatar.ToString("D")));
        string avatar = options.Avatar.ToString("D");
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OwnerID/UUID"] = avatar, ["LastOwnerID/UUID"] = avatar, ["RezzerID/UUID"] = avatar,
            ["BaseMask"] = FullPermissions, ["OwnerMask"] = FullPermissions, ["NextOwnerMask"] = FullPermissions,
            ["CreationDate"] = created.ToString(Inv),
            ["GroupPosition/X"] = Text(centre.X), ["GroupPosition/Y"] = Text(centre.Y), ["GroupPosition/Z"] = Text(centre.Z)
        };
        string part = ArchiveRestorer.NewPartXml(path => values.TryGetValue(path, out string v) ? v : null, null, partId, name, description, 0,
            new float[] { 0f, 0f, 0f }, new float[] { 0f, 0f, 0f, 1f }, size, te.GetBytes(), options.Avatar);
        List<ElementEdit> edits = new List<ElementEdit>(MeshShape.Edits(highFaces.Count, meshId, null));
        edits.Add(ElementEdit.SetText("PhysicsShapeType", shapeType.ToString(Inv)));
        AddPbrFaces(edits, built.PbrFaces);
        part = PartXml.Edit(part, partId, edits);
        string objectXml = "<SceneObjectGroup>" + part + "<OtherParts /></SceneObjectGroup>";

        result.Name = name;
        result.ObjectId = partId;
        result.Size = new SysVector3(size.X, size.Y, size.Z);
        result.Centre = new SysVector3(centre.X, centre.Y, centre.Z);
        WriteArchive(outputPath, created, ArchiveConstants.CreateOarObjectPath(FileSafe(name), new UUID(partId), centre),
            Encoding.UTF8.GetBytes(objectXml), assets, provenance.ToBytes());
        result.Written = true;
        say("wrote " + outputPath);
        return result;
    }

    // ------------------------------------------------------------------ one part

    /// <summary>The files of one model: the four levels of detail, and the collision shape when there is one.</summary>
    internal sealed class ModelLevels
    {
        public GlbFile[] Files = new GlbFile[4];
        public GlbNode[] Nodes = new GlbNode[4];
        /// <summary>What each level is called in messages (a file name, or a file and node).</summary>
        public string[] Names = new string[4];
        public GlbFile PhysicsFile;
        public GlbNode PhysicsNode;
        public string PhysicsName;
        /// <summary>The note to add when there is no collision shape.</summary>
        public string NoPhysicsNote;
    }

    private sealed class BuiltPart
    {
        public Vector3 Size, Centre;
        public List<EncoderFace> HighFaces;
        public Guid MeshId;
        public long MeshBytes;
        public int ShapeType;
        public Primitive.TextureEntry TextureEntry;
        public List<(Guid Id, byte[] Data, AssetType Type)> Assets;
        /// <summary>The PBR material assigned to each face that has one: the face number and the material asset.</summary>
        public List<(byte Face, Guid Id)> PbrFaces = new List<(byte, Guid)>();
    }

    internal static ModelLevels ReadFolderModel(string folder, string baseName, List<string> notes)
    {
        string[] fileNames = { baseName + ".glb", baseName + "_lod2.glb", baseName + "_lod1.glb", baseName + "_lod0.glb" };
        ModelLevels m = new ModelLevels { Names = fileNames };
        for (int i = 0; i < 4; i++)
        {
            string path = Path.Combine(folder, fileNames[i]);
            if (!File.Exists(path))
                throw new ImportException("the " + LevelNames[i] + " level of detail '" + fileNames[i] + "' is not in the folder" + (i == 0 ? string.Empty : "; a mesh with all four levels is needed"));
            (m.Files[i], m.Nodes[i]) = ReadModel(path, fileNames[i], notes);
        }
        string physicsPath = Path.Combine(folder, baseName + "_physics.glb");
        m.PhysicsName = baseName + "_physics.glb";
        if (File.Exists(physicsPath))
            (m.PhysicsFile, m.PhysicsNode) = ReadModel(physicsPath, m.PhysicsName, notes);
        else
            m.NoPhysicsNote = m.PhysicsName + " is not in the folder; the part collides as the bounding hull of its mesh";
        return m;
    }

    /// <summary>
    /// The mesh asset, the faces and the pictures of one part, from its four levels and its collision shape. Notes, textures and materials go to <paramref name="result"/>.
    /// </summary>
    private static BuiltPart BuildPart(ModelLevels model, TextureCatalog catalog, ImportResult result, Action<string> say, IReadOnlyList<LinksetFace> settings = null, bool pbr = false)
    {
        GlbFile[] files = model.Files;
        GlbNode[] nodes = model.Nodes;
        string[] fileNames = model.Names;

        // the full mesh: one face for each primitive (each material); the part is the box around it
        PartGeometry high = PartGeometry.FromNode(files[0], nodes[0], nodes[0].Scale, true);
        if (high.TriangleCount == 0)
            throw new ImportException(fileNames[0] + " has no triangles");
        List<GlbPrimitive> highPrimitives = files[0].Meshes[nodes[0].Mesh].Primitives;
        int slotCount = highPrimitives.Count;
        if (slotCount > MeshShape.MaxFaces)
            throw new ImportException(fileNames[0] + " has " + slotCount + " materials; a mesh part has at most " + MeshShape.MaxFaces + " faces");
        string[] slotNames = highPrimitives.Select(p => MaterialName(files[0], p.Material)).ToArray();
        (Vector3 size, Vector3 center) = Box(high);
        if (size.X > MaxPrimSize || size.Y > MaxPrimSize || size.Z > MaxPrimSize)
            result.Notes.Add("the part is larger than " + MaxPrimSize.ToString("0", Inv) + " m in some direction; many regions refuse a prim that size");

        List<EncoderFace> highFaces = ArchiveRestorer.BuildFaces(high, files[0], size, center, slotCount, out List<int> sourceSlot);
        List<List<EncoderFace>> lods = new List<List<EncoderFace>>();
        result.Levels.Add(new ImportLevel { Name = LevelNames[0], File = fileNames[0], Triangles = highFaces.Sum(f => f.TriangleCount), Faces = highFaces.Count });
        for (int level = 1; level < 4; level++)
        {
            PartGeometry g = PerSlot(files[level], nodes[level], fileNames[level], fileNames[0], slotNames);
            List<EncoderFace> faces = ArchiveRestorer.BuildFaces(g, files[level], size, center, slotCount, out List<int> slots);
            if (!slots.SequenceEqual(sourceSlot))
                throw new ImportException(fileNames[level] + " would be cut into a different set of faces than " + fileNames[0] + " (a face of more than 65 532 vertices is split)");
            lods.Add(faces);
            result.Levels.Add(new ImportLevel { Name = LevelNames[level], File = fileNames[level], Triangles = faces.Sum(f => f.TriangleCount), Faces = faces.Count });
            if (faces.All(f => f.IsEmpty))
                throw new ImportException(fileNames[level] + " has no triangles");
        }
        // the viewer lists the levels high to lowest; the encoder takes the lower ones medium, low, lowest
        say("faces built: " + string.Join(", ", result.Levels.Select(l => l.Name + " " + l.Triangles)));

        // the collision shape
        PhysicsGeometry physics = null;
        List<IReadOnlyList<SysVector3>> hulls = null;
        int shapeType = ShapeTypeConvex;
        if (model.PhysicsFile != null)
        {
            PartGeometry pg = PartGeometry.FromNode(model.PhysicsFile, model.PhysicsNode, model.PhysicsNode.Scale, true);
            if (pg.TriangleCount == 0)
                throw new ImportException(model.PhysicsName + " has no triangles");
            (List<SysVector3> points, PhysicsGeometry mesh) = PhysicsFrom(pg, size, center);
            if (IsConvex(pg))
            {
                List<SysVector3> hull = ConvexHull.Compute(points, PhysicsBlock.MaxHullVertices, out bool flat);
                if (!flat && hull.Count >= 4)
                {
                    hulls = new List<IReadOnlyList<SysVector3>> { hull };
                    result.Physics = "convex hull";
                    result.PhysicsTriangles = 0;
                }
            }
            if (hulls == null)
            {
                if (mesh.VertexCount > 65535)
                    throw new ImportException(model.PhysicsName + " has " + mesh.VertexCount + " vertices; the asset stores 16-bit indices");
                physics = mesh;
                shapeType = ShapeTypePrim;
                result.Physics = "triangle mesh";
                result.PhysicsTriangles = mesh.TriangleCount;
            }
        }
        else
        {
            result.Physics = "bounding hull";
            result.Notes.Add(model.NoPhysicsNote);
        }

        byte[] meshBytes;
        try
        {
            meshBytes = new MeshAssetEncoder().Encode(highFaces, new EncodeOptions { Physics = physics, Lods = lods.Select(l => (IReadOnlyList<EncoderFace>)l).ToList(), Hulls = hulls });
        }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is OverflowException || e is IndexOutOfRangeException || e is MeshFormatException)
        {
            throw new ImportException("the mesh cannot be written: " + e.Message);
        }
        List<string> problems = MeshValidator.Check(meshBytes, highFaces, out _, physics);
        if (problems.Count > 0)
            throw new ImportException("the mesh fails its check: " + string.Join("; ", problems));
        Guid meshId = MeshId.FromBytes(meshBytes);
        result.MeshId = meshId;
        result.MeshBytes = meshBytes.Length;
        result.Faces = highFaces.Count;

        // the pictures and the normal maps
        List<(Guid Id, byte[] Data, AssetType Type)> assets = new List<(Guid, byte[], AssetType)> { (meshId, meshBytes, AssetType.Mesh) };
        Primitive.TextureEntry te = new Primitive.TextureEntry(new UUID(PlywoodTexture));
        HashSet<Guid> materialsDone = new HashSet<Guid>();
        Dictionary<int, LinksetFace> faceSettings = FaceSettings(settings, highFaces.Count, slotNames, sourceSlot);
        List<(byte Face, Guid Id)> pbrFaces = new List<(byte, Guid)>();
        for (int face = 0; face < highFaces.Count; face++)
        {
            faceSettings.TryGetValue(face, out LinksetFace setting);
            GlbPrimitive prim = highPrimitives[sourceSlot[face]];
            if (prim.Material < 0 || prim.Material >= files[0].Materials.Count)
            {
                if (setting != null)
                    ApplyFaceSetting(te.CreateFace((uint)face), setting);
                continue;
            }
            GlbMaterial m = files[0].Materials[prim.Material];
            string materialName = MaterialName(files[0], prim.Material);
            if (m.HasTextureTransform)
                result.Notes.Add("material '" + materialName + "' gives a picture a texture transform (offset, scale or rotation); it is not applied to the face");
            Primitive.TextureEntryFace tf = te.CreateFace((uint)face);
            float alpha = m.AlphaMode == "OPAQUE" ? 1f : m.BaseColor[3];
            tf.RGBA = new Color4(Clamp01(m.BaseColor[0]), Clamp01(m.BaseColor[1]), Clamp01(m.BaseColor[2]), Clamp01(alpha));
            tf.TextureID = new UUID(BlankTexture);
            Guid? picture = null, normal = null, specular = null, metalRough = null, emissive = null;
            if (m.Image != null)
            {
                picture = catalog.Resolve(m.Image, out _, out string problem);
                if (picture == null)
                    result.Notes.Add("the picture of material '" + materialName + "' cannot be used (" + problem + "); the face is a plain colour");
                else
                {
                    tf.TextureID = new UUID(picture.Value);
                    if (!result.Textures.Any(t => t.Id == picture.Value && t.Role == "picture"))
                        result.Textures.Add((picture.Value, materialName, "picture"));
                }
            }
            if (m.NormalImage != null)
            {
                normal = catalog.Resolve(m.NormalImage, out _, out string problem);
                if (normal == null)
                    result.Notes.Add("the normal map of material '" + materialName + "' cannot be used (" + problem + "); the face has none");
                else if (!result.Textures.Any(t => t.Id == normal.Value && t.Role == "normal map"))
                    result.Textures.Add((normal.Value, materialName, "normal map"));
            }
            if (setting?.Specular != null)
            {
                specular = ReadSpecularMap(setting.Specular, catalog, materialName);
                if (!result.Textures.Any(t => t.Id == specular.Value && t.Role == "specular map"))
                    result.Textures.Add((specular.Value, materialName, "specular map"));
            }
            if (normal != null || specular != null)
            {
                // a legacy material with the maps drawn as the picture is, with no transform and no alpha blending
                LinksetSpecular spec = setting?.Specular;
                float[] colour = spec?.Color ?? new[] { 1f, 1f, 1f };
                OSDMap map = new OSDMap
                {
                    ["NormMap"] = OSD.FromUUID(normal.HasValue ? new UUID(normal.Value) : UUID.Zero),
                    ["SpecMap"] = OSD.FromUUID(specular.HasValue ? new UUID(specular.Value) : UUID.Zero),
                    ["SpecColor"] = new OSDArray { OSD.FromInteger(Byte(colour[0])), OSD.FromInteger(Byte(colour[1])), OSD.FromInteger(Byte(colour[2])), OSD.FromInteger(255) },
                    ["SpecExp"] = OSD.FromInteger(spec?.Glossiness ?? 51),
                    ["EnvIntensity"] = OSD.FromInteger(spec?.Environment ?? 0),
                    ["DiffuseAlphaMode"] = OSD.FromInteger(0),
                    ["AlphaMaskCutoff"] = OSD.FromInteger(0)
                };
                (Guid materialId, byte[] materialBytes) = MaterialBaking.WithoutMapTransforms(map);
                tf.MaterialID = new UUID(materialId);
                if (materialsDone.Add(materialId))
                {
                    assets.Add((materialId, materialBytes, AssetType.OSMaterial));
                    result.Materials.Add(materialId);
                }
            }
            if (setting != null)
                ApplyFaceSetting(tf, setting);
            if (pbr)
            {
                if (m.MetalRoughImage != null)
                    metalRough = PbrPicture(m.MetalRoughImage, "occlusion roughness metallic", catalog, result, materialName);
                if (m.EmissiveImage != null)
                    emissive = PbrPicture(m.EmissiveImage, "emissive", catalog, result, materialName);
                PbrMaterialData data = new PbrMaterialData
                {
                    BaseColor = m.BaseColor, Metallic = m.Metallic, Roughness = m.Roughness, Emissive = m.Emissive, AlphaMode = m.AlphaMode,
                    AlphaCutoff = m.AlphaCutoff, DoubleSided = m.DoubleSided,
                    BaseColorTexture = picture ?? Guid.Empty, NormalTexture = normal ?? Guid.Empty, MetalRoughTexture = metalRough ?? Guid.Empty, EmissiveTexture = emissive ?? Guid.Empty
                };
                byte[] bytes = PbrMaterialAsset.Build(data);
                Guid pbrId = MeshId.FromBytes(bytes);
                if (materialsDone.Add(pbrId))
                {
                    assets.Add((pbrId, bytes, AssetType.Material));
                    result.PbrMaterials.Add(pbrId);
                }
                pbrFaces.Add(((byte)face, pbrId));
            }
        }
        return new BuiltPart { Size = size, Centre = center, HighFaces = highFaces, MeshId = meshId, MeshBytes = meshBytes.Length, ShapeType = shapeType, TextureEntry = te, Assets = assets, PbrFaces = pbrFaces };
    }

    // ------------------------------------------------------------------ face settings and PBR pictures

    /// <summary>The settings of a part by face number: a setting names its face by glTF material name (every face of that material) or by number.</summary>
    private static Dictionary<int, LinksetFace> FaceSettings(IReadOnlyList<LinksetFace> settings, int faceCount, string[] slotNames, List<int> sourceSlot)
    {
        Dictionary<int, LinksetFace> byFace = new Dictionary<int, LinksetFace>();
        if (settings == null)
            return byFace;
        foreach (LinksetFace s in settings)
        {
            List<int> faces = new List<int>();
            if (s.Material != null)
            {
                for (int f = 0; f < faceCount; f++)
                    if (slotNames[sourceSlot[f]] == s.Material)
                        faces.Add(f);
                if (faces.Count == 0)
                    throw new ImportException("a face setting names the material '" + s.Material + "', which this part does not have (its materials: " + string.Join(", ", slotNames.Select(n => "'" + n + "'")) + ")");
            }
            else
            {
                if (s.Face >= faceCount)
                    throw new ImportException("a face setting names face " + s.Face.ToString(Inv) + ", but this part has " + faceCount.ToString(Inv) + " face" + (faceCount == 1 ? string.Empty : "s"));
                faces.Add(s.Face);
            }
            foreach (int f in faces)
                if (!byFace.TryAdd(f, s))
                    throw new ImportException("face " + f.ToString(Inv) + " is set twice in this part's faces list");
        }
        return byFace;
    }

    private static void ApplyFaceSetting(Primitive.TextureEntryFace face, LinksetFace setting)
    {
        if (setting.Glow.HasValue)
            face.Glow = setting.Glow.Value;
        if (setting.Fullbright.HasValue)
            face.Fullbright = setting.Fullbright.Value;
    }

    private static Guid ReadSpecularMap(LinksetSpecular specular, TextureCatalog catalog, string materialName)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(specular.File); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            throw new ImportException("the specular map '" + specular.File + "' cannot be read: " + e.Message);
        }
        Guid? id = catalog.Resolve(bytes, out _, out string problem);
        if (id == null)
            throw new ImportException("the specular map '" + Path.GetFileName(specular.File) + "' of material '" + materialName + "' cannot be used (" + problem + ")");
        return id.Value;
    }

    private static Guid? PbrPicture(byte[] picture, string role, TextureCatalog catalog, ImportResult result, string materialName)
    {
        Guid? id = catalog.Resolve(picture, out _, out string problem);
        if (id == null)
        {
            result.Notes.Add("the " + role + " picture of material '" + materialName + "' cannot be used (" + problem + "); the PBR material has none");
            return null;
        }
        if (!result.Textures.Any(t => t.Id == id.Value && t.Role == role))
            result.Textures.Add((id.Value, materialName, role));
        return id;
    }

    private static int Byte(float unit) => (int)Math.Round(Math.Min(1f, Math.Max(0f, unit)) * 255f, MidpointRounding.AwayFromZero);

    /// <summary>Adds the PBR face references to a part's mesh edits: the ExtraParams value of the edits gets the PBR block beside the mesh block.</summary>
    internal static void AddPbrFaces(List<ElementEdit> edits, IReadOnlyList<(byte Face, Guid Id)> faces)
    {
        if (faces.Count == 0)
            return;
        int at = edits.FindIndex(e => e.Path == "Shape/ExtraParams");
        if (at < 0)
            throw new ImportException("the mesh edits have no ExtraParams element to add the PBR faces to");
        byte[] extra = MeshShape.WithRenderMaterials(Convert.FromBase64String(edits[at].Value), faces);
        edits[at] = ElementEdit.SetText("Shape/ExtraParams", Convert.ToBase64String(extra));
    }

    // ------------------------------------------------------------------ the folder and the files

    internal static string FindBaseName(string folder, string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;
        string[] physics = Directory.GetFiles(folder, "*_physics.glb");
        if (physics.Length == 1)
            return Path.GetFileName(physics[0]).Substring(0, Path.GetFileName(physics[0]).Length - "_physics.glb".Length);
        if (physics.Length > 1)
            throw new ImportException("the folder holds the physics files of several models (" + string.Join(", ", physics.Select(Path.GetFileName)) + "); name the model with --base");
        List<string> plain = Directory.GetFiles(folder, "*.glb").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => !Regex.IsMatch(n, "_(lod[0-2]|physics)$")).ToList();
        if (plain.Count == 1)
            return plain[0];
        throw new ImportException(plain.Count == 0 ? "the folder has no .glb model files" : "the folder holds several models (" + string.Join(", ", plain) + "); name the model with --base");
    }

    internal static (GlbFile File, GlbNode Node) ReadModel(string path, string display, List<string> notes)
    {
        GlbFile file;
        try { file = GlbFile.Read(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is InvalidDataException || e is System.Text.Json.JsonException || e is KeyNotFoundException || e is InvalidCastException || e is IOException)
        {
            throw new ImportException(display + " cannot be read: " + e.Message);
        }
        foreach (GlbNode n in file.Nodes)
            if (Moved(n))
                throw new ImportException(display + ": the node '" + n.Name + "' is moved or turned; apply the transforms in the modelling tool and export again");
        List<GlbNode> meshNodes = file.Nodes.Where(n => n.Mesh >= 0 && n.Mesh < file.Meshes.Count).ToList();
        if (meshNodes.Count == 0)
            throw new ImportException(display + " has no mesh");
        if (meshNodes.Count > 1)
            notes.Add(display + " has " + meshNodes.Count + " mesh nodes; only the first ('" + meshNodes[0].Name + "') is used");
        return (file, meshNodes[0]);
    }

    private static bool Moved(GlbNode n) =>
        (n.Translation != null && n.Translation.Any(v => Math.Abs(v) > 1e-6f)) ||
        (n.Rotation != null && n.Rotation.Length >= 3 && (Math.Abs(n.Rotation[0]) > 1e-6f || Math.Abs(n.Rotation[1]) > 1e-6f || Math.Abs(n.Rotation[2]) > 1e-6f));

    internal static string MaterialName(GlbFile file, int index) =>
        index < 0 || index >= file.Materials.Count ? string.Empty : string.IsNullOrEmpty(file.Materials[index].Name) ? "material " + index.ToString(Inv) : file.Materials[index].Name;

    /// <summary>A lower level with its triangles moved to the face (the slot) of the full mesh that has the same material name.</summary>
    internal static PartGeometry PerSlot(GlbFile file, GlbNode node, string display, string highDisplay, string[] slotNames)
    {
        PartGeometry byPrimitive = PartGeometry.FromNode(file, node, node.Scale, true);
        List<GlbPrimitive> primitives = file.Meshes[node.Mesh].Primitives;
        PartGeometry result = new PartGeometry();
        foreach (KeyValuePair<int, List<float[]>> kv in byPrimitive.Faces)
        {
            if (kv.Key < 0 || kv.Key >= primitives.Count)
                throw new ImportException(display + " numbers its faces itself (a primitive has a face number attribute); oar-import takes files that have one primitive for each material");
            string name = MaterialName(file, primitives[kv.Key].Material);
            int slot = Array.IndexOf(slotNames, name);
            if (slot < 0)
                throw new ImportException(display + " uses the material '" + name + "' that " + highDisplay + " does not have");
            if (!result.Faces.TryGetValue(slot, out List<float[]> list))
            {
                result.Faces[slot] = list = new List<float[]>();
                result.Materials[slot] = new List<int>();
            }
            list.AddRange(kv.Value);
            result.Materials[slot].AddRange(byPrimitive.Materials[kv.Key]);
        }
        return result;
    }

    /// <summary>The box around the triangles in the viewer axes (glTF x, y, z are the viewer x, -z, y): its size, never under 1 cm, and its centre.</summary>
    internal static (Vector3 Size, Vector3 Centre) Box(PartGeometry g)
    {
        float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
        foreach (List<float[]> tris in g.Faces.Values)
            foreach (float[] tri in tris)
                for (int c = 0; c < 3; c++)
                {
                    float x = tri[c * 8], y = -tri[c * 8 + 2], z = tri[c * 8 + 1];
                    min[0] = Math.Min(min[0], x); max[0] = Math.Max(max[0], x);
                    min[1] = Math.Min(min[1], y); max[1] = Math.Max(max[1], y);
                    min[2] = Math.Min(min[2], z); max[2] = Math.Max(max[2], z);
                }
        Vector3 size = new Vector3(Math.Max(max[0] - min[0], MinPrimSize), Math.Max(max[1] - min[1], MinPrimSize), Math.Max(max[2] - min[2], MinPrimSize));
        Vector3 centre = new Vector3((min[0] + max[0]) / 2f, (min[1] + max[1]) / 2f, (min[2] + max[2]) / 2f);
        if (!float.IsFinite(size.X + size.Y + size.Z + centre.X + centre.Y + centre.Z))
            throw new ImportException("the vertices of the full mesh are not finite numbers");
        return (size, centre);
    }

    // ------------------------------------------------------------------ the collision shape

    /// <summary>The physics triangles in the unit box of the part, welded by position: the distinct points, and the triangle mesh over them.</summary>
    private static (List<SysVector3> Points, PhysicsGeometry Mesh) PhysicsFrom(PartGeometry g, Vector3 size, Vector3 centre)
    {
        Dictionary<(int, int, int), int> seen = new Dictionary<(int, int, int), int>();
        List<SysVector3> points = new List<SysVector3>();
        List<int> indices = new List<int>();
        foreach (List<float[]> tris in g.Faces.Values)
            foreach (float[] tri in tris)
                for (int c = 0; c < 3; c++)
                {
                    int o = c * 8;
                    SysVector3 p = new SysVector3((tri[o] - centre.X) / size.X, (-tri[o + 2] - centre.Y) / size.Y, (tri[o + 1] - centre.Z) / size.Z);
                    var key = (BitConverter.SingleToInt32Bits(p.X + 0f), BitConverter.SingleToInt32Bits(p.Y + 0f), BitConverter.SingleToInt32Bits(p.Z + 0f));
                    if (!seen.TryGetValue(key, out int at))
                    {
                        at = points.Count;
                        seen[key] = at;
                        points.Add(p);
                    }
                    indices.Add(at);
                }
        float[] positions = new float[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            positions[i * 3] = points[i].X; positions[i * 3 + 1] = points[i].Y; positions[i * 3 + 2] = points[i].Z;
        }
        return (points, new PhysicsGeometry { Positions = positions, Indices = indices.ToArray() });
    }

    /// <summary>The largest number of triangles checked for convexity; a larger shape is a triangle mesh.</summary>
    private const int MaxConvexTriangles = 20000;
    private const float ConvexTolerance = 1e-4f;

    /// <summary>True when the shape is convex: every point lies on one side of the plane of every triangle (to a tenth of a millimetre).</summary>
    private static bool IsConvex(PartGeometry g)
    {
        List<SysVector3> corners = new List<SysVector3>();
        foreach (List<float[]> tris in g.Faces.Values)
            foreach (float[] tri in tris)
                for (int c = 0; c < 3; c++)
                    corners.Add(new SysVector3(tri[c * 8], tri[c * 8 + 1], tri[c * 8 + 2]));
        if (corners.Count / 3 > MaxConvexTriangles)
            return false;
        List<SysVector3> points = corners.Distinct().ToList();
        for (int t = 0; t < corners.Count; t += 3)
        {
            SysVector3 n = SysVector3.Cross(corners[t + 1] - corners[t], corners[t + 2] - corners[t]);
            float length = n.Length();
            if (length < 1e-12f)
                continue;
            n /= length;
            float d = SysVector3.Dot(n, corners[t]), above = 0f, below = 0f;
            foreach (SysVector3 p in points)
            {
                float s = SysVector3.Dot(n, p) - d;
                above = Math.Max(above, s);
                below = Math.Min(below, s);
            }
            if (above > ConvexTolerance && below < -ConvexTolerance)
                return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ the archive

    /// <summary>The .glb files a model folder was read from: the four levels in order, then the collision shape when the folder has one.</summary>
    internal static List<string> FolderModelFiles(string folder, ModelLevels model)
    {
        List<string> files = model.Names.Select(n => Path.Combine(folder, n)).ToList();
        if (model.PhysicsFile != null)
            files.Add(Path.Combine(folder, model.PhysicsName));
        return files;
    }

    private static void WriteArchive(string outputPath, long created, string objectPath, byte[] objectXml, List<(Guid Id, byte[] Data, AssetType Type)> assets, byte[] notes)
    {
        string control = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<archive major_version=\"0\" minor_version=\"8\">\n  <creation_info>\n    <datetime>" + created.ToString(Inv) +
            "</datetime>\n  </creation_info>\n  <assets_included>True</assets_included>\n</archive>";
        using MemoryStream packed = new MemoryStream();
        using (GZipStream gz = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        using (TarWriter tar = new TarWriter(gz, leaveOpen: true))
        {
            tar.WriteFile(ArchiveConstants.CONTROL_FILE_PATH, new UTF8Encoding(false).GetBytes(control));
            tar.WriteFile(objectPath, objectXml);
            foreach ((Guid id, byte[] data, AssetType type) in assets.OrderBy(a => a.Id))
            {
                if (!ArchiveConstants.ASSET_TYPE_TO_EXTENSION.TryGetValue((sbyte)type, out string extension))
                    throw new ImportException("the archive format has no file extension for asset type " + type);
                tar.WriteFile(ArchiveConstants.ASSETS_PATH + id.ToString("D") + extension, data);
            }
            tar.WriteFile(ImportProvenance.ArchiveEntry, notes);
            tar.Finish();
        }
        byte[] bytes = packed.ToArray();
        // the framework puts its platform code in the OS byte of the gzip header (RFC 1952); a fixed value gives the same file on every platform
        if (bytes.Length > 9)
            bytes[9] = 10;
        string temporary = outputPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        try
        {
            using (FileStream fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(bytes, 0, bytes.Length);
            File.Move(temporary, outputPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Move(temporary, Path.ChangeExtension(temporary, ".incomplete"));
            throw;
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Min(1f, Math.Max(0f, v)) : 0f;

    private static string Text(float v) => (Math.Abs(v) < 1e-7f ? 0f : (float)Math.Round(v, 6)).ToString("R", Inv);

    private static string Clean(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (System.Xml.XmlConvert.IsXmlChar(c))
                sb.Append(c);
        return sb.ToString();
    }

    private static string FileSafe(string name)
    {
        StringBuilder sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c);
        return sb.ToString();
    }
}
