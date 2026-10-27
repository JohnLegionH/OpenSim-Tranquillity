/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text.Json;
using OpenSim.Archive.Rewrite;

namespace OpenSim.Archive.Restore.Import;

/// <summary>Where one level of a part's model comes from: a mesh tool's output folder, or a node of a .glb file.</summary>
public sealed class ModelSource
{
    /// <summary>A folder holding <c>base.glb</c>, <c>base_lod2.glb</c>, <c>base_lod1.glb</c>, <c>base_lod0.glb</c> and, optionally, <c>base_physics.glb</c>; absolute.</summary>
    public string Folder { get; set; }
    /// <summary>The file name prefix inside <see cref="Folder"/>; empty finds it as the single-object import does.</summary>
    public string Base { get; set; } = string.Empty;
    /// <summary>A .glb file with one or more mesh nodes; absolute.</summary>
    public string Glb { get; set; }
    /// <summary>The name of the node in <see cref="Glb"/>.</summary>
    public string Node { get; set; }
    public bool IsFolder => !string.IsNullOrEmpty(Folder);
}

/// <summary>One item in a part's contents: a script, a notecard or a texture, from a file.</summary>
public sealed class LinksetContent
{
    /// <summary>script, notecard or texture.</summary>
    public string Type { get; set; }
    /// <summary>The file, absolute.</summary>
    public string File { get; set; }
    public string Name { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>A part's sit target: where an avatar sits and how it is turned, in the part's own axes.</summary>
public sealed class LinksetSitTarget
{
    public float[] Position { get; set; }
    public float[] Rotation { get; set; }
}

/// <summary>A face's legacy specular map and its settings.</summary>
public sealed class LinksetSpecular
{
    public string File { get; set; }
    public float[] Color { get; set; }
    public int? Glossiness { get; set; }
    public int? Environment { get; set; }
}

/// <summary>Settings for one face of a part, named by its material or its number.</summary>
public sealed class LinksetFace
{
    public string Material { get; set; }
    public int Face { get; set; } = -1;
    public float? Glow { get; set; }
    public bool? Fullbright { get; set; }
    public LinksetSpecular Specular { get; set; }
}

/// <summary>One part of the linked object: the root (the first in the list) or a child.</summary>
public sealed class LinksetPart
{
    public string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>The full mesh and, for a mesh tool's folder, every other level and the collision shape.</summary>
    public ModelSource Model { get; set; }
    /// <summary>For a node source, the other levels of detail and the collision shape when they are their own nodes; null takes the full mesh's node for a level, and the bounding hull for the shape.</summary>
    public ModelSource Lod2 { get; set; }
    public ModelSource Lod1 { get; set; }
    public ModelSource Lod0 { get; set; }
    public ModelSource Physics { get; set; }
    /// <summary>Where the part's model origin lands, relative to the root part's model origin, in the viewer's axes; null takes the node's own place (a node source) or 0, 0, 0.</summary>
    public float[] Position { get; set; }
    /// <summary>The part's turn relative to the root part as x, y, z, w; null takes the node's own turn (a node source) or none.</summary>
    public float[] Rotation { get; set; }
    public List<LinksetContent> Contents { get; } = new List<LinksetContent>();
    /// <summary>Where an avatar sits on the part; null leaves the part with no sit target.</summary>
    public LinksetSitTarget SitTarget { get; set; }
    public List<LinksetFace> Faces { get; } = new List<LinksetFace>();
}

/// <summary>The manifest of one linked object: the object's name and its parts, root first.</summary>
public sealed class LinksetManifest
{
    /// <summary>The most parts a linked object has in the viewer.</summary>
    public const int MaxParts = 256;
    public static readonly string[] ContentTypes = { "script", "notecard", "texture" };

    private static readonly string[] ManifestKeys = { "name", "description", "pbr", "jointPositions", "parts" };
    private static readonly string[] PartKeys = { "name", "description", "folder", "base", "glb", "node", "lod2", "lod1", "lod0", "physics", "position", "rotation", "contents", "sit", "faces" };
    private static readonly string[] SourceKeys = { "glb", "node" };
    private static readonly string[] ContentKeys = { "type", "file", "name", "description" };
    private static readonly string[] SitKeys = { "position", "rotation" };
    private static readonly string[] FaceKeys = { "material", "face", "glow", "fullbright", "specular" };
    private static readonly string[] SpecularKeys = { "map", "color", "glossiness", "environment" };

    public string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>True when the manifest asks for glTF PBR materials on every face that has a material.</summary>
    public bool Pbr { get; set; }

    /// <summary>True when the manifest asks for the joint positions of a rigged mesh to be written.</summary>
    public bool JointPositions { get; set; }
    /// <summary>The folder the manifest's relative paths are read from.</summary>
    public string Folder { get; set; }
    /// <summary>The manifest file this was read from and the SHA-256 of its bytes; null when it was parsed from text.</summary>
    public string SourceFile { get; set; }
    public string SourceSha256 { get; set; }
    public List<LinksetPart> Parts { get; } = new List<LinksetPart>();

    /// <summary>Reads a manifest file; its relative paths are read from the folder it is in.</summary>
    public static LinksetManifest Load(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            throw new ImportException("the manifest '" + path + "' cannot be read: " + e.Message);
        }
        string text;
        using (StreamReader reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        LinksetManifest manifest = Parse(text, Path.GetDirectoryName(Path.GetFullPath(path)));
        manifest.SourceFile = Path.GetFullPath(path);
        manifest.SourceSha256 = ImportProvenance.Sha256(bytes);
        return manifest;
    }

    /// <summary>Reads manifest text; <paramref name="folder"/> is where its relative paths start.</summary>
    public static LinksetManifest Parse(string json, string folder)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            throw new ImportException("the manifest is not valid JSON: " + e.Message);
        }
        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ImportException("the manifest must be a JSON object with a name and a list of parts");
            Known(root, ManifestKeys, "the manifest");
            LinksetManifest m = new LinksetManifest { Folder = folder };
            m.Name = Text(root, "name", "the manifest", required: true);
            m.Description = Text(root, "description", "the manifest", required: false) ?? string.Empty;
            m.Pbr = Flag(root, "pbr", "the manifest") ?? false;
            m.JointPositions = Flag(root, "jointPositions", "the manifest") ?? false;
            if (!root.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
                throw new ImportException("the manifest needs \"parts\": a list with at least one part, the root part first");
            if (parts.GetArrayLength() > MaxParts)
                throw new ImportException("the manifest has " + parts.GetArrayLength() + " parts; a linked object has at most " + MaxParts);
            int index = 0;
            foreach (JsonElement pe in parts.EnumerateArray())
            {
                string label = "part " + (index + 1).ToString(CultureInfo.InvariantCulture);
                if (pe.ValueKind != JsonValueKind.Object)
                    throw new ImportException(label + " must be a JSON object");
                Known(pe, PartKeys, label);
                LinksetPart part = new LinksetPart { Name = Text(pe, "name", label, required: true) };
                label = "part '" + part.Name + "'";
                part.Description = Text(pe, "description", label, required: false) ?? string.Empty;
                string dir = Text(pe, "folder", label, required: false), glb = Text(pe, "glb", label, required: false), node = Text(pe, "node", label, required: false);
                if (dir != null && (glb != null || node != null))
                    throw new ImportException(label + " names both a \"folder\" and a \"glb\"/\"node\"; a part has one source");
                if (dir != null)
                    part.Model = new ModelSource { Folder = Resolve(folder, dir), Base = Text(pe, "base", label, required: false) ?? string.Empty };
                else if (glb != null && node != null)
                    part.Model = new ModelSource { Glb = Resolve(folder, glb), Node = node };
                else
                    throw new ImportException(label + " has no source: give a \"folder\" (a mesh tool's output folder) or a \"glb\" and a \"node\"");
                if (dir != null && (pe.TryGetProperty("lod2", out _) || pe.TryGetProperty("lod1", out _) || pe.TryGetProperty("lod0", out _) || pe.TryGetProperty("physics", out _)))
                    throw new ImportException(label + ": \"lod2\", \"lod1\", \"lod0\" and \"physics\" are for a node source; a folder holds its own levels and collision shape");
                if (dir == null && pe.TryGetProperty("base", out _))
                    throw new ImportException(label + ": \"base\" is for a folder source");
                part.Lod2 = Source(pe, "lod2", label, folder);
                part.Lod1 = Source(pe, "lod1", label, folder);
                part.Lod0 = Source(pe, "lod0", label, folder);
                part.Physics = Source(pe, "physics", label, folder);
                part.Position = Numbers(pe, "position", 3, label);
                part.Rotation = Numbers(pe, "rotation", 4, label);
                if (part.Rotation != null)
                {
                    float length = MathF.Sqrt(part.Rotation.Sum(v => v * v));
                    if (length < 1e-6f)
                        throw new ImportException(label + ": \"rotation\" is all zeros; give x, y, z, w with w 1 for no turn");
                    part.Rotation = part.Rotation.Select(v => v / length).ToArray();
                }
                if (index == 0)
                {
                    if (part.Position != null && part.Position.Any(v => Math.Abs(v) > 1e-6f))
                        throw new ImportException(label + " is the root part: its model origin is the origin of the object, so its \"position\" must be 0, 0, 0 or left out");
                    if (part.Rotation != null && (Math.Abs(part.Rotation[0]) > 1e-6f || Math.Abs(part.Rotation[1]) > 1e-6f || Math.Abs(part.Rotation[2]) > 1e-6f))
                        throw new ImportException(label + " is the root part: the parts are placed relative to it, so it cannot be turned; turn the whole object in the region, or turn the model in the modelling tool");
                }
                if (pe.TryGetProperty("contents", out JsonElement contents))
                {
                    if (contents.ValueKind != JsonValueKind.Array)
                        throw new ImportException(label + ": \"contents\" must be a list");
                    foreach (JsonElement ce in contents.EnumerateArray())
                    {
                        if (ce.ValueKind != JsonValueKind.Object)
                            throw new ImportException(label + ": each item of \"contents\" must be a JSON object");
                        Known(ce, ContentKeys, label + " contents");
                        string type = Text(ce, "type", label + " contents", required: true);
                        if (Array.IndexOf(ContentTypes, type) < 0)
                            throw new ImportException(label + ": the content type '" + type + "' is not one of " + string.Join(", ", ContentTypes));
                        string file = Resolve(folder, Text(ce, "file", label + " contents", required: true));
                        if (!File.Exists(file))
                            throw new ImportException(label + ": the " + type + " file '" + file + "' does not exist");
                        part.Contents.Add(new LinksetContent
                        {
                            Type = type, File = file,
                            Name = Text(ce, "name", label + " contents", required: false) ?? Path.GetFileNameWithoutExtension(file),
                            Description = Text(ce, "description", label + " contents", required: false) ?? string.Empty
                        });
                    }
                }
                if (pe.TryGetProperty("sit", out JsonElement sit))
                    part.SitTarget = ReadSit(sit, label);
                if (pe.TryGetProperty("faces", out JsonElement faces))
                    ReadFaces(faces, part, label, folder);
                m.Parts.Add(part);
                index++;
            }
            return m;
        }
    }

    /// <summary>A sit target: "position" (x, y, z in the part's own axes) and an optional "rotation" (x, y, z, w; none when left out).</summary>
    private static LinksetSitTarget ReadSit(JsonElement e, string label)
    {
        string where = label + " \"sit\"";
        if (e.ValueKind != JsonValueKind.Object)
            throw new ImportException(label + ": \"sit\" must be an object with a \"position\" and an optional \"rotation\"");
        Known(e, SitKeys, where);
        float[] position = Numbers(e, "position", 3, where) ?? throw new ImportException(where + " needs \"position\": a list of 3 numbers");
        float[] rotation = Numbers(e, "rotation", 4, where) ?? new[] { 0f, 0f, 0f, 1f };
        float length = MathF.Sqrt(rotation.Sum(v => v * v));
        if (length < 1e-6f)
            throw new ImportException(where + ": \"rotation\" is all zeros; give x, y, z, w with w 1 for no turn");
        return new LinksetSitTarget { Position = position, Rotation = rotation.Select(v => v / length).ToArray() };
    }

    /// <summary>The "faces" list: each entry names a face by "material" or by "face" and gives its glow, fullbright and legacy specular map.</summary>
    private static void ReadFaces(JsonElement list, LinksetPart part, string label, string folder)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw new ImportException(label + ": \"faces\" must be a list");
        foreach (JsonElement fe in list.EnumerateArray())
        {
            string where = label + " faces";
            if (fe.ValueKind != JsonValueKind.Object)
                throw new ImportException(where + ": each entry must be a JSON object");
            Known(fe, FaceKeys, where);
            LinksetFace face = new LinksetFace { Material = Text(fe, "material", where, required: false) };
            bool hasFace = fe.TryGetProperty("face", out JsonElement number);
            if (hasFace && (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out int n) || n < 0 || n >= MeshShape.MaxFaces))
                throw new ImportException(where + ": \"face\" must be a whole number from 0 to " + (MeshShape.MaxFaces - 1));
            if (hasFace)
                face.Face = number.GetInt32();
            if (face.Material != null && hasFace)
                throw new ImportException(where + ": an entry names a face by \"material\" or by \"face\", not both");
            if (face.Material == null && !hasFace)
                throw new ImportException(where + ": an entry needs \"material\" (the glTF material name) or \"face\" (the face number)");
            if (fe.TryGetProperty("glow", out JsonElement glow))
            {
                if (glow.ValueKind != JsonValueKind.Number || !glow.TryGetSingle(out float g) || !float.IsFinite(g) || g < 0f || g > 1f)
                    throw new ImportException(where + ": \"glow\" must be a number from 0 to 1");
                face.Glow = g;
            }
            face.Fullbright = Flag(fe, "fullbright", where);
            if (fe.TryGetProperty("specular", out JsonElement spec))
                face.Specular = ReadSpecular(spec, where, folder);
            part.Faces.Add(face);
        }
    }

    private static LinksetSpecular ReadSpecular(JsonElement e, string label, string folder)
    {
        string where = label + " specular";
        if (e.ValueKind != JsonValueKind.Object)
            throw new ImportException(label + ": \"specular\" must be an object with a \"map\"");
        Known(e, SpecularKeys, where);
        string map = Text(e, "map", where, required: true);
        string file = Resolve(folder, map);
        if (!File.Exists(file))
            throw new ImportException(where + ": the specular map file '" + file + "' does not exist");
        LinksetSpecular s = new LinksetSpecular { File = file, Color = Numbers(e, "color", 3, where) };
        if (s.Color != null && s.Color.Any(v => v < 0f || v > 1f))
            throw new ImportException(where + ": \"color\" must be red, green and blue from 0 to 1");
        s.Glossiness = Byte(e, "glossiness", where);
        s.Environment = Byte(e, "environment", where);
        return s;
    }

    private static int? Byte(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v))
            return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n) || n < 0 || n > 255)
            throw new ImportException(where + ": \"" + key + "\" must be a whole number from 0 to 255");
        return n;
    }

    private static bool? Flag(JsonElement e, string key, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v))
            return null;
        if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
            throw new ImportException(where + ": \"" + key + "\" must be true or false");
        return v.GetBoolean();
    }

    private static string Resolve(string folder, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder ?? string.Empty, path));

    private static void Known(JsonElement e, string[] keys, string where)
    {
        foreach (JsonProperty p in e.EnumerateObject())
            if (Array.IndexOf(keys, p.Name) < 0)
                throw new ImportException(where + " has a key '" + p.Name + "' the manifest does not know; the keys are " + string.Join(", ", keys));
    }

    private static string Text(JsonElement e, string key, string where, bool required)
    {
        if (!e.TryGetProperty(key, out JsonElement v))
        {
            if (required)
                throw new ImportException(where + " needs \"" + key + "\"");
            return null;
        }
        if (v.ValueKind != JsonValueKind.String || (required && string.IsNullOrWhiteSpace(v.GetString())))
            throw new ImportException(where + ": \"" + key + "\" must be a text" + (required ? " that is not empty" : string.Empty));
        return v.GetString();
    }

    private static float[] Numbers(JsonElement e, string key, int count, string where)
    {
        if (!e.TryGetProperty(key, out JsonElement v))
            return null;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != count)
            throw new ImportException(where + ": \"" + key + "\" must be a list of " + count + " numbers");
        float[] result = new float[count];
        int i = 0;
        foreach (JsonElement n in v.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetSingle(out float f) || !float.IsFinite(f))
                throw new ImportException(where + ": \"" + key + "\" must be a list of " + count + " numbers");
            result[i++] = f;
        }
        return result;
    }

    private static ModelSource Source(JsonElement e, string key, string where, string folder)
    {
        if (!e.TryGetProperty(key, out JsonElement v))
            return null;
        if (v.ValueKind != JsonValueKind.Object)
            throw new ImportException(where + ": \"" + key + "\" must be an object with \"glb\" and \"node\"");
        Known(v, SourceKeys, where + " " + key);
        return new ModelSource { Glb = Resolve(folder, Text(v, "glb", where + " " + key, required: true)), Node = Text(v, "node", where + " " + key, required: true) };
    }
}
