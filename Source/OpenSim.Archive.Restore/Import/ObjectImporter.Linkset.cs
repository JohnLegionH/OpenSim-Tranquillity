/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Security;
using System.Text;
using OpenMetaverse;
using OpenSim.Archive.Converter;
using OpenSim.Archive.Rewrite;
using OpenSim.Framework.Serialization;
using OpenSim.MeshAsset;
using Quaternion = System.Numerics.Quaternion;
using SysVector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Restore.Import;

/// <summary>
/// The linked-object import: an archive holding one object of several parts, each part a mesh made from a mesh tool's output folder or from a node of a
/// .glb file, placed relative to the root part, with its own name, description and contents (scripts, notecards, textures).
/// </summary>
public static partial class ObjectImporter
{
    private const int MaxNotecardBytes = 65536;
    private const int MaxNameChars = 63, MaxDescriptionChars = 127;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>A node's place as a part wants it: relative to the scene, in the viewer's axes.</summary>
    internal sealed class NodePlace
    {
        public string Glb;
        public SysVector3 Translation;
        public Quaternion Rotation = Quaternion.Identity;
    }

    private sealed class LinksetPartWork
    {
        public LinksetPart Part;
        public ImportResult Result = new ImportResult();
        public BuiltPart Built;
        public NodePlace Place;
        public Guid Id;
        public SysVector3 Offset;
        public Quaternion Rotation = Quaternion.Identity;
        public string TaskInventory = string.Empty;
        public List<ImportContentResult> Contents = new List<ImportContentResult>();
    }

    /// <summary>Writes an archive holding one linked object made from the parts of a manifest.</summary>
    public static ImportResult ImportLinkset(LinksetManifest manifest, string outputPath, ImportOptions options)
    {
        options ??= new ImportOptions();
        Action<string> say = options.Progress ?? (_ => { });
        if (manifest == null || manifest.Parts.Count == 0)
            throw new ImportException("the manifest has no parts");
        if (options.Avatar == Guid.Empty)
            throw new ImportException("an avatar is needed: it is named as the creator and the owner");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new ImportException("the output '" + outputPath + "' already exists; it is never overwritten");

        ImportResult result = new ImportResult { Output = outputPath };
        TextureCatalog catalog = new TextureCatalog(Path.Combine(manifest.Folder ?? Directory.GetCurrentDirectory(), "no-original-textures"));
        Dictionary<string, GlbFile> glbs = new Dictionary<string, GlbFile>(StringComparer.OrdinalIgnoreCase);
        List<LinksetPartWork> work = new List<LinksetPartWork>();
        string avatar = options.Avatar.ToString("D");
        long created = options.Created != 0 ? options.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // every part's mesh, pictures and place, before anything is written
        foreach (LinksetPart part in manifest.Parts)
        {
            LinksetPartWork w = new LinksetPartWork { Part = part };
            string label = "part '" + part.Name + "'";
            if (part.Model.IsFolder && !Directory.Exists(part.Model.Folder))
                throw new ImportException(label + ": the folder '" + part.Model.Folder + "' does not exist");
            ModelLevels model;
            try
            {
                model = part.Model.IsFolder ? ReadFolderModel(part.Model.Folder, FolderBase(part.Model), w.Result.Notes) : ReadNodeModel(part, label, glbs, w.Result.Notes, out w.Place);
            }
            catch (ImportException e) when (part.Model.IsFolder)
            {
                throw new ImportException(label + ": " + e.Message);
            }
            say("read " + label + ": " + string.Join(", ", model.Names));
            try
            {
                w.Built = BuildPart(model, catalog, w.Result, _ => { });
            }
            catch (ImportException e)
            {
                throw new ImportException(label + ": " + e.Message);
            }
            work.Add(w);
        }

        // places: the root's model origin is the origin of the object; a child is placed relative to it
        LinksetPartWork root = work[0];
        SysVector3 rootCentre = new SysVector3(root.Built.Centre.X, root.Built.Centre.Y, root.Built.Centre.Z);
        (SysVector3 Offset, Quaternion Rotation)[] placed = PlaceParts(work.Select(w => w.Part).ToList(),
            work.Select(w => new SysVector3(w.Built.Centre.X, w.Built.Centre.Y, w.Built.Centre.Z)).ToList(), work.Select(w => w.Place).ToList());
        for (int i = 0; i < work.Count; i++)
            (work[i].Offset, work[i].Rotation) = placed[i];

        // the contents of every part
        Dictionary<Guid, (byte[] Data, AssetType Type)> assets = new Dictionary<Guid, (byte[], AssetType)>();
        foreach (LinksetPartWork w in work)
        {
            string label = "part '" + w.Part.Name + "'";
            string name = Limit(Clean(w.Part.Name), MaxNameChars);
            w.Id = MeshId.FromBytes(Encoding.UTF8.GetBytes("oar-import|" + w.Built.MeshId.ToString("D") + "|" + name + "|" + avatar + "|" + work.IndexOf(w).ToString(Inv)));
            foreach ((Guid id, byte[] data, AssetType type) in w.Built.Assets)
                assets[id] = (data, type);
            w.TaskInventory = BuildContents(w, label, avatar, created, catalog, assets);
        }
        foreach (KeyValuePair<Guid, byte[]> t in catalog.NewAssets)
            assets[t.Key] = (t.Value, AssetType.Texture);

        // the object
        string objectName = Limit(Clean(string.IsNullOrWhiteSpace(manifest.Name) ? root.Part.Name : manifest.Name), MaxNameChars);
        Vector3 groupAt = new Vector3(options.Position.X + rootCentre.X, options.Position.Y + rootCentre.Y, options.Position.Z + rootCentre.Z);
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OwnerID/UUID"] = avatar, ["LastOwnerID/UUID"] = avatar, ["RezzerID/UUID"] = avatar,
            ["BaseMask"] = FullPermissions, ["OwnerMask"] = FullPermissions, ["NextOwnerMask"] = FullPermissions,
            ["CreationDate"] = created.ToString(Inv),
            ["GroupPosition/X"] = Text(groupAt.X), ["GroupPosition/Y"] = Text(groupAt.Y), ["GroupPosition/Z"] = Text(groupAt.Z)
        };
        List<string> partXml = new List<string>();
        for (int i = 0; i < work.Count; i++)
        {
            LinksetPartWork w = work[i];
            int linkNumber = work.Count == 1 ? 0 : i + 1;
            float[] offset = { w.Offset.X, w.Offset.Y, w.Offset.Z };
            float[] turn = { w.Rotation.X, w.Rotation.Y, w.Rotation.Z, w.Rotation.W };
            if (i == 0)
            {
                offset = new float[] { 0f, 0f, 0f };
                turn = new float[] { 0f, 0f, 0f, 1f };
            }
            string xml = ArchiveRestorer.NewPartXml(path => values.TryGetValue(path, out string v) ? v : null, null, w.Id, Limit(Clean(w.Part.Name), MaxNameChars),
                Limit(Clean(w.Part.Description ?? string.Empty), MaxDescriptionChars), linkNumber, offset, turn, w.Built.Size, w.Built.TextureEntry.GetBytes(), options.Avatar);
            List<ElementEdit> edits = new List<ElementEdit>(MeshShape.Edits(w.Built.HighFaces.Count, w.Built.MeshId, null));
            edits.Add(ElementEdit.SetText("PhysicsShapeType", w.Built.ShapeType.ToString(Inv)));
            if (w.TaskInventory.Length > 0)
                edits.Add(ElementEdit.SetInnerXmlOrAdd("TaskInventory", w.TaskInventory));
            partXml.Add(PartXml.Edit(xml, w.Id, edits));
        }
        string objectXml = "<SceneObjectGroup>" + partXml[0] + "<OtherParts>" + string.Concat(partXml.Skip(1)) + "</OtherParts></SceneObjectGroup>";

        // what was written
        result.Name = objectName;
        result.ObjectId = root.Id;
        result.Size = new SysVector3(root.Built.Size.X, root.Built.Size.Y, root.Built.Size.Z);
        result.Centre = new SysVector3(groupAt.X, groupAt.Y, groupAt.Z);
        result.MeshId = root.Built.MeshId;
        result.Faces = work.Sum(w => w.Built.HighFaces.Count);
        result.Physics = root.Result.Physics;
        result.Levels.AddRange(root.Result.Levels);
        for (int i = 0; i < work.Count; i++)
        {
            LinksetPartWork w = work[i];
            string prefix = "part '" + w.Part.Name + "': ";
            result.Notes.AddRange(w.Result.Notes.Select(n => prefix + n));
            result.Textures.AddRange(w.Result.Textures);
            result.Materials.AddRange(w.Result.Materials);
            ImportPartResult pr = new ImportPartResult
            {
                Name = Limit(Clean(w.Part.Name), MaxNameChars), Description = Limit(Clean(w.Part.Description ?? string.Empty), MaxDescriptionChars), LinkNumber = work.Count == 1 ? 0 : i + 1,
                PartId = w.Id, MeshId = w.Built.MeshId, Size = new SysVector3(w.Built.Size.X, w.Built.Size.Y, w.Built.Size.Z),
                Offset = i == 0 ? SysVector3.Zero : w.Offset, Rotation = i == 0 ? new[] { 0f, 0f, 0f, 1f } : new[] { w.Rotation.X, w.Rotation.Y, w.Rotation.Z, w.Rotation.W },
                Triangles = w.Result.Levels.Count > 0 ? w.Result.Levels[0].Triangles : 0, Physics = w.Result.Physics
            };
            pr.Contents.AddRange(w.Contents);
            result.Parts.Add(pr);
        }
        result.MeshBytes = root.Built.MeshBytes;

        List<(Guid Id, byte[] Data, AssetType Type)> assetList = assets.Select(kv => (kv.Key, kv.Value.Data, kv.Value.Type)).ToList();
        WriteArchive(outputPath, created, ArchiveConstants.CreateOarObjectPath(FileSafe(objectName), new UUID(root.Id), groupAt), Encoding.UTF8.GetBytes(objectXml), assetList);
        result.Written = true;
        say("wrote " + outputPath);
        return result;
    }

    /// <summary>
    /// Where each part's centre lies relative to the root part's centre, and how it is turned relative to the root part, in the root part's axes. The root's model
    /// origin is the origin of the object; a child's model origin is at its "position" (else its node's place, as seen from the root node when both are nodes of one file,
    /// else the origin) and it is turned by its "rotation" (same defaults); its centre is the centre of its model's box, turned and moved with it.
    /// </summary>
    internal static (SysVector3 Offset, Quaternion Rotation)[] PlaceParts(IReadOnlyList<LinksetPart> parts, IReadOnlyList<SysVector3> centres, IReadOnlyList<NodePlace> places)
    {
        (SysVector3, Quaternion)[] result = new (SysVector3, Quaternion)[parts.Count];
        result[0] = (SysVector3.Zero, Quaternion.Identity);
        for (int i = 1; i < parts.Count; i++)
        {
            NodePlace place = places[i], rootPlace = places[0];
            SysVector3 position = SysVector3.Zero;
            Quaternion rotation = Quaternion.Identity;
            if (place != null && rootPlace != null && string.Equals(place.Glb, rootPlace.Glb, StringComparison.OrdinalIgnoreCase))
            {
                // two nodes of one file: the place of the child as seen from the root node
                Quaternion inverse = Quaternion.Inverse(rootPlace.Rotation);
                position = SysVector3.Transform(place.Translation - rootPlace.Translation, inverse);
                rotation = Quaternion.Normalize(inverse * place.Rotation);
            }
            else if (place != null)
            {
                position = place.Translation;
                rotation = place.Rotation;
            }
            if (parts[i].Position != null)
                position = new SysVector3(parts[i].Position[0], parts[i].Position[1], parts[i].Position[2]);
            if (parts[i].Rotation != null)
                rotation = Quaternion.Normalize(new Quaternion(parts[i].Rotation[0], parts[i].Rotation[1], parts[i].Rotation[2], parts[i].Rotation[3]));
            result[i] = (position + SysVector3.Transform(centres[i], rotation) - centres[0], rotation);
        }
        return result;
    }

    internal static string FolderBase(ModelSource source) => FindBaseName(source.Folder, source.Base);

    private static string Limit(string text, int chars) => text.Length > chars ? text.Substring(0, chars) : text;

    // ------------------------------------------------------------------ models from the nodes of a .glb file

    private static GlbFile ReadGlb(string path, Dictionary<string, GlbFile> cache)
    {
        if (cache.TryGetValue(path, out GlbFile known))
            return known;
        if (!File.Exists(path))
            throw new ImportException("the file '" + path + "' does not exist");
        GlbFile file;
        try { file = GlbFile.Read(File.ReadAllBytes(path)); }
        catch (Exception e) when (e is InvalidDataException || e is System.Text.Json.JsonException || e is KeyNotFoundException || e is InvalidCastException || e is IOException)
        {
            throw new ImportException(Path.GetFileName(path) + " cannot be read: " + e.Message);
        }
        cache[path] = file;
        return file;
    }

    private static (GlbFile File, GlbNode Node) FindNode(ModelSource source, Dictionary<string, GlbFile> cache)
    {
        GlbFile file = ReadGlb(source.Glb, cache);
        List<GlbNode> named = file.Nodes.Where(n => n.Name == source.Node).ToList();
        string display = Path.GetFileName(source.Glb);
        if (named.Count == 0)
            throw new ImportException("the node '" + source.Node + "' is not in " + display + " (its mesh nodes: " + string.Join(", ", file.Nodes.Where(n => n.Mesh >= 0).Select(n => n.Name)) + ")");
        if (named.Count > 1)
            throw new ImportException(display + " has " + named.Count + " nodes named '" + source.Node + "'; the name must be one node");
        GlbNode node = named[0];
        if (node.Mesh < 0 || node.Mesh >= file.Meshes.Count)
            throw new ImportException("the node '" + source.Node + "' in " + display + " has no mesh");
        return (file, node);
    }

    private static string NodeDisplay(ModelSource source) => Path.GetFileName(source.Glb) + " node '" + source.Node + "'";

    internal static ModelLevels ReadNodeModel(LinksetPart part, string label, Dictionary<string, GlbFile> cache, List<string> notes, out NodePlace place)
    {
        ModelSource high = part.Model;
        ModelSource[] sources = { high, part.Lod2 ?? high, part.Lod1 ?? high, part.Lod0 ?? high };
        ModelLevels m = new ModelLevels();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                (m.Files[i], m.Nodes[i]) = FindNode(sources[i], cache);
                m.Names[i] = NodeDisplay(sources[i]);
            }
            if (part.Physics != null)
            {
                (m.PhysicsFile, m.PhysicsNode) = FindNode(part.Physics, cache);
                m.PhysicsName = NodeDisplay(part.Physics);
            }
            else
                m.NoPhysicsNote = "no collision shape is given; the part collides as the bounding hull of its mesh";
        }
        catch (ImportException e)
        {
            throw new ImportException(label + ": " + e.Message);
        }
        if (part.Lod2 == null && part.Lod1 == null && part.Lod0 == null)
            notes.Add("no lower levels of detail are given for " + NodeDisplay(high) + "; each level of detail is the full mesh");
        place = NodePlaceOf(m.Files[0], m.Nodes[0], high.Glb, label);
        return m;
    }

    /// <summary>The node's place in the file: its translation and turn with the translations of the nodes above it (a turned or scaled node above it is refused).</summary>
    private static NodePlace NodePlaceOf(GlbFile file, GlbNode node, string glb, string label)
    {
        SysVector3 translation = Gltf(node.Translation);
        Quaternion rotation = node.Rotation != null && node.Rotation.Length >= 4 ? Quaternion.Normalize(new Quaternion(node.Rotation[0], -node.Rotation[2], node.Rotation[1], node.Rotation[3])) : Quaternion.Identity;
        GlbNode at = node;
        for (int guard = 0; guard < 64; guard++)
        {
            GlbNode parent = file.Nodes.FirstOrDefault(n => n.Children.Contains(at.Index));
            if (parent == null)
                break;
            bool turned = parent.Rotation != null && parent.Rotation.Length >= 3 && (Math.Abs(parent.Rotation[0]) > 1e-6f || Math.Abs(parent.Rotation[1]) > 1e-6f || Math.Abs(parent.Rotation[2]) > 1e-6f);
            bool scaled = parent.Scale != null && parent.Scale.Any(v => Math.Abs(v - 1f) > 1e-6f);
            if (turned || scaled)
                throw new ImportException(label + ": the node above it, '" + parent.Name + "', is turned or scaled; apply that transform in the modelling tool, or give the part its \"position\" and \"rotation\"");
            translation += Gltf(parent.Translation);
            at = parent;
        }
        return new NodePlace { Glb = Path.GetFullPath(glb), Translation = translation, Rotation = rotation };
    }

    /// <summary>A glTF vector (x, y, z) in the viewer's axes (x, -z, y); null is zero.</summary>
    private static SysVector3 Gltf(float[] v) => v == null || v.Length < 3 ? SysVector3.Zero : new SysVector3(v[0], -v[2], v[1]);

    // ------------------------------------------------------------------ contents

    private static string BuildContents(LinksetPartWork w, string label, string avatar, long created, TextureCatalog catalog, Dictionary<Guid, (byte[] Data, AssetType Type)> assets)
    {
        StringBuilder sb = new StringBuilder();
        int j = 0;
        foreach (LinksetContent c in w.Part.Contents)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(c.File); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new ImportException(label + ": the " + c.Type + " file '" + c.File + "' cannot be read: " + e.Message);
            }
            Guid asset;
            int type;
            switch (c.Type)
            {
                case "script":
                    // the text as written; the region compiles it when the object loads, and a script with no saved state starts running
                    asset = MeshId.FromBytes(bytes);
                    assets[asset] = (bytes, AssetType.LSLText);
                    type = (int)AssetType.LSLText;
                    break;
                case "notecard":
                    string text = Utf8.GetString(bytes).Replace("\r\n", "\n");
                    int length = Utf8.GetByteCount(text);
                    if (length > MaxNotecardBytes)
                        throw new ImportException(label + ": the notecard '" + c.Name + "' is " + length + " bytes; a notecard holds at most " + MaxNotecardBytes);
                    byte[] card = Utf8.GetBytes("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + length.ToString(Inv) + "\n" + text + "}\n");
                    asset = MeshId.FromBytes(card);
                    assets[asset] = (card, AssetType.Notecard);
                    type = (int)AssetType.Notecard;
                    break;
                case "texture":
                    Guid? id = catalog.Resolve(bytes, out _, out string problem);
                    if (id == null)
                        throw new ImportException(label + ": the texture '" + c.Name + "' (" + Path.GetFileName(c.File) + ") cannot be used: " + problem);
                    asset = id.Value;
                    type = (int)AssetType.Texture;
                    w.Result.Textures.Add((asset, c.Name, "contents"));
                    break;
                default:
                    throw new ImportException(label + ": the content type '" + c.Type + "' is not one of " + string.Join(", ", LinksetManifest.ContentTypes));
            }
            string itemName = Limit(Clean(c.Name), MaxNameChars);
            Guid item = MeshId.FromBytes(Encoding.UTF8.GetBytes("oar-import-item|" + w.Id.ToString("D") + "|" + j.ToString(Inv) + "|" + itemName));
            string zero = Guid.Empty.ToString("D");
            sb.Append("<TaskInventoryItem>");
            sb.Append("<AssetID><UUID>").Append(asset.ToString("D")).Append("</UUID></AssetID>");
            sb.Append("<BasePermissions>").Append(FullPermissions).Append("</BasePermissions><CreationDate>").Append(created.ToString(Inv)).Append("</CreationDate>");
            sb.Append("<CreatorID><UUID>").Append(avatar).Append("</UUID></CreatorID>");
            sb.Append("<Description>").Append(SecurityElement.Escape(Limit(Clean(c.Description ?? string.Empty), MaxDescriptionChars))).Append("</Description><EveryonePermissions>0</EveryonePermissions><Flags>0</Flags>");
            sb.Append("<GroupID><UUID>").Append(zero).Append("</UUID></GroupID><GroupPermissions>0</GroupPermissions><InvType>").Append(type.ToString(Inv)).Append("</InvType>");
            sb.Append("<ItemID><UUID>").Append(item.ToString("D")).Append("</UUID></ItemID><OldItemID><UUID>").Append(zero).Append("</UUID></OldItemID>");
            sb.Append("<LastOwnerID><UUID>").Append(avatar).Append("</UUID></LastOwnerID>");
            sb.Append("<Name>").Append(SecurityElement.Escape(itemName)).Append("</Name><NextPermissions>").Append(FullPermissions).Append("</NextPermissions>");
            sb.Append("<OwnerID><UUID>").Append(avatar).Append("</UUID></OwnerID><CurrentPermissions>").Append(FullPermissions).Append("</CurrentPermissions>");
            sb.Append("<ParentID><UUID>").Append(w.Id.ToString("D")).Append("</UUID></ParentID><ParentPartID><UUID>").Append(w.Id.ToString("D")).Append("</UUID></ParentPartID>");
            sb.Append("<PermsGranter><UUID>").Append(zero).Append("</UUID></PermsGranter><PermsMask>0</PermsMask>");
            sb.Append("<Type>").Append(type.ToString(Inv)).Append("</Type><OwnerChanged>false</OwnerChanged></TaskInventoryItem>");
            w.Contents.Add(new ImportContentResult { Type = c.Type, Name = itemName, ItemId = item, AssetId = asset });
            j++;
        }
        return sb.ToString();
    }
}
