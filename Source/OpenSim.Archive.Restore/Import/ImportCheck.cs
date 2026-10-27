/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Archive.GltfExport;
using OpenSim.Framework;
using OpenSim.MeshAsset;
using SkiaSharp;
using MeshFace = OpenSim.MeshAsset.MeshFace;
using SysVector3 = System.Numerics.Vector3;

namespace OpenSim.Archive.Restore.Import;

/// <summary>One level (or the physics shape, or a picture) compared with its input.</summary>
public sealed class CheckLine
{
    public string What { get; set; }
    public int InputTriangles { get; set; }
    public int OutputTriangles { get; set; }
    /// <summary>Largest position difference in metres and texture-coordinate difference found.</summary>
    public float MaxPosition { get; set; }
    public float MaxUv { get; set; }
    public bool Ok { get; set; }
    /// <summary>False for a line that compares counts only, so no position or texture-coordinate difference is printed for it.</summary>
    public bool Measured { get; set; } = true;
    public string Detail { get; set; }
}

/// <summary>The result of writing an imported archive back out to glTF and comparing it with the input.</summary>
public sealed class ImportCheckResult
{
    public bool Passed { get; set; }
    public List<CheckLine> Lines { get; } = new List<CheckLine>();

    public string ToText()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        foreach (CheckLine l in Lines)
        {
            sb.Append(l.Ok ? "  ok      " : "  FAILED  ").Append(l.What).Append(": ");
            if (l.InputTriangles > 0 || l.OutputTriangles > 0)
                sb.Append("input ").Append(l.InputTriangles.ToString(c)).Append(", output ").Append(l.OutputTriangles.ToString(c)).Append("; ");
            if (l.Ok && l.Measured && (l.InputTriangles > 0 || l.OutputTriangles > 0))
                sb.Append("largest position difference ").Append((l.MaxPosition * 1000f).ToString("0.###", c)).Append(" mm, texture coordinates ").Append(l.MaxUv.ToString("0.#####", c)).Append("; ");
            sb.Append(l.Detail).Append('\n');
        }
        sb.Append("round trip: ").Append(Passed ? "PASSED" : "FAILED").Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// The round trip of an imported archive. The archive is written out to glTF by the exporter (the full mesh, its pictures) and its mesh asset is read
/// back (every level of detail, the physics shape, the normal map), and each is compared with the files it was made from: triangle counts equal,
/// positions within half a millimetre, texture coordinates and pictures the same.
/// </summary>
public static partial class ImportCheck
{
    /// <summary>Position difference, in metres, the round trip may show (half a millimetre).</summary>
    public const float PositionTolerance = 0.0005f;
    public const float UvTolerance = 1e-4f;
    private static readonly string[] LevelLines = { "high level", "medium level", "low level", "lowest level" };

    public static ImportCheckResult Run(string archivePath, string folder, ImportOptions options, string workFolder)
    {
        ImportCheckResult result = new ImportCheckResult();
        try
        {
            RunCore(result, archivePath, folder, options, workFolder);
        }
        catch (Exception e) when (e is ImportException || e is IOException || e is InvalidDataException || e is MeshFormatException || e is InvalidOperationException || e is ArgumentException || e is UnauthorizedAccessException)
        {
            result.Lines.Add(new CheckLine { What = "check", Ok = false, Detail = "the check could not run: " + e.Message });
        }
        result.Passed = result.Lines.Count > 0 && result.Lines.All(l => l.Ok);
        return result;
    }

    private static void RunCore(ImportCheckResult result, string archivePath, string folder, ImportOptions options, string workFolder)
    {
        string baseName = ObjectImporter.FindBaseName(folder, options?.BaseName);
        string[] fileNames = { baseName + ".glb", baseName + "_lod2.glb", baseName + "_lod1.glb", baseName + "_lod0.glb" };
        GlbFile[] files = new GlbFile[4];
        GlbNode[] nodes = new GlbNode[4];
        List<string> notes = new List<string>();
        for (int i = 0; i < 4; i++)
            (files[i], nodes[i]) = ObjectImporter.ReadModel(Path.Combine(folder, fileNames[i]), fileNames[i], notes);
        PartGeometry highIn = PartGeometry.FromNode(files[0], nodes[0], nodes[0].Scale, true);
        List<GlbPrimitive> highPrimitives = files[0].Meshes[nodes[0].Mesh].Primitives;
        string[] slotNames = highPrimitives.Select(p => ObjectImporter.MaterialName(files[0], p.Material)).ToArray();
        (Vector3 size, Vector3 centre) = ObjectImporter.Box(highIn);
        // the exporter writes the part about its own centre; the input files have their own origin
        float[] shift = { centre.X, centre.Z, -centre.Y };
        GeometryTolerance tolerance = new GeometryTolerance { Position = PositionTolerance, Normal = GeometryTolerance.DefaultNormal, Uv = UvTolerance };

        Dictionary<string, byte[]> entries = ReadEntries(archivePath);
        string meshKey = entries.Keys.FirstOrDefault(k => k.EndsWith("_mesh.llmesh", StringComparison.Ordinal))
            ?? throw new ImportException("the archive holds no mesh asset");
        MeshAssetModel model = MeshCodec.Decode(entries[meshKey]);

        // the full mesh and its pictures, through the exporter
        ExportOptions export = new ExportOptions { OutputDirectory = workFolder, SelfContained = true };
        export.MaxMemoryMegabytes = ArchiveExporter.MaxMemoryMegabytesFor(export);
        ArchiveExporter.Export(archivePath, export);
        string glb = Directory.Exists(workFolder) ? Directory.EnumerateFiles(workFolder, "*.glb", SearchOption.AllDirectories).FirstOrDefault() : null;
        GlbFile exported = null;
        if (glb == null)
            result.Lines.Add(new CheckLine { What = LevelLines[0], Ok = false, Detail = "the exporter wrote no glTF file" });
        else
        {
            exported = GlbFile.Read(File.ReadAllBytes(glb));
            GlbNode exportedNode = exported.Nodes.FirstOrDefault(n => n.Mesh >= 0 && n.Mesh < exported.Meshes.Count)
                ?? throw new ImportException("the exported glTF has no mesh");
            result.Lines.Add(Compare(LevelLines[0], highIn, PartGeometry.FromNode(exported, exportedNode), shift, tolerance));
        }

        // the lower levels, read back out of the mesh asset
        for (int level = 1; level < 4; level++)
        {
            MeshLod decoded = model.Lod(3 - level) ?? throw new ImportException("the mesh asset has no " + LevelLines[level]);
            PartGeometry input = ObjectImporter.PerSlot(files[level], nodes[level], fileNames[level], fileNames[0], slotNames);
            result.Lines.Add(Compare(LevelLines[level], input, FromMesh(decoded, size), shift, tolerance));
        }

        string physicsPath = Path.Combine(folder, baseName + "_physics.glb");
        if (File.Exists(physicsPath))
            result.Lines.Add(ComparePhysics(model, physicsPath, size, shift, tolerance, notes));

        // the pictures, face by face: the exported glTF against the input
        for (int slot = 0; slot < highPrimitives.Count; slot++)
        {
            GlbPrimitive prim = highPrimitives[slot];
            if (prim.Material < 0 || prim.Material >= files[0].Materials.Count || files[0].Materials[prim.Material].Image == null)
                continue;
            GlbMaterial input = files[0].Materials[prim.Material];
            string name = slotNames[slot];
            GlbMaterial output = exported == null ? null : ExportedMaterial(exported, slot);
            result.Lines.Add(ComparePicture("picture of " + name, input.Image, output?.Image, false));
        }

        // the normal maps: the material asset the face names, and the texture asset that material names
        XElement part = XDocument.Parse(Encoding.UTF8.GetString(entries[entries.Keys.First(k => k.StartsWith("objects/", StringComparison.Ordinal))])).Root.Element("SceneObjectPart");
        byte[] teBytes = Convert.FromBase64String(part.Element("Shape").Element("TextureEntry").Value);
        Primitive.TextureEntry te = new Primitive.TextureEntry(teBytes, 0, teBytes.Length);
        for (int slot = 0; slot < highPrimitives.Count; slot++)
        {
            GlbPrimitive prim = highPrimitives[slot];
            if (prim.Material < 0 || prim.Material >= files[0].Materials.Count || files[0].Materials[prim.Material].NormalImage == null)
                continue;
            Primitive.TextureEntryFace face = (slot < te.FaceTextures.Length ? te.FaceTextures[slot] : null) ?? te.DefaultTexture;
            byte[] written = null;
            string detail = null;
            if (face == null || face.MaterialID == UUID.Zero || !entries.TryGetValue("assets/" + face.MaterialID + "_material.xml", out byte[] materialXml))
                detail = "the face names no material";
            else if (OSDParser.Deserialize(materialXml) is not OSDMap map || map["NormMap"].AsUUID() == UUID.Zero)
                detail = "the face's material has no normal map";
            else if (!entries.TryGetValue("assets/" + map["NormMap"].AsUUID() + "_texture.jp2", out written))
                detail = "the normal map texture is not in the archive";
            CheckLine line = ComparePicture("normal map of " + slotNames[slot], files[0].Materials[prim.Material].NormalImage, written, true);
            if (detail != null)
            {
                line.Ok = false;
                line.Detail = detail;
            }
            result.Lines.Add(line);
        }
    }

    // ------------------------------------------------------------------ geometry

    private static CheckLine Compare(string what, PartGeometry input, PartGeometry output, float[] shift, GeometryTolerance tolerance)
    {
        PartGeometry moved = Shifted(input, shift);
        GeometryComparison c = GeometryComparer.Compare(moved, output, tolerance);
        CheckLine line = new CheckLine { What = what, InputTriangles = input.TriangleCount, OutputTriangles = output.TriangleCount, Ok = c.Equal, MaxPosition = c.MaxPosition, MaxUv = c.MaxUv };
        if (c.Equal)
        {
            line.Detail = "every triangle matched";
            return line;
        }
        line.Detail = c.Difference;
        // the same triangles with a wider tolerance say how far apart they are
        GeometryComparison loose = GeometryComparer.Compare(moved, output, new GeometryTolerance { Position = 0.05f, Normal = 2f, Uv = 0.05f });
        if (loose.Equal)
        {
            line.MaxPosition = loose.MaxPosition;
            line.MaxUv = loose.MaxUv;
            line.Detail = "position differs by up to " + (loose.MaxPosition * 1000f).ToString("0.###", CultureInfo.InvariantCulture) + " mm and texture coordinates by up to "
                + loose.MaxUv.ToString("0.#####", CultureInfo.InvariantCulture) + " (limits " + (PositionTolerance * 1000f).ToString("0.###", CultureInfo.InvariantCulture) + " mm and "
                + UvTolerance.ToString("0.#####", CultureInfo.InvariantCulture) + ")";
        }
        return line;
    }

    /// <summary>A copy of the input with the part centre taken off every position (the exporter writes a part about its own centre).</summary>
    private static PartGeometry Shifted(PartGeometry g, float[] shift)
    {
        PartGeometry result = new PartGeometry();
        foreach (KeyValuePair<int, List<float[]>> kv in g.Faces)
        {
            List<float[]> moved = new List<float[]>(kv.Value.Count);
            foreach (float[] tri in kv.Value)
            {
                float[] copy = (float[])tri.Clone();
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 3; k++)
                        copy[c * 8 + k] -= shift[k];
                moved.Add(copy);
            }
            result.Faces[kv.Key] = moved;
            result.Materials[kv.Key] = new List<int>(g.Materials[kv.Key]);
        }
        return result;
    }

    /// <summary>A level of the mesh asset as glTF triangles about the part centre: the unit box scaled to the part size, viewer axes turned into glTF's, v turned over.</summary>
    private static PartGeometry FromMesh(MeshLod level, Vector3 size)
    {
        PartGeometry g = new PartGeometry();
        for (int f = 0; f < level.Faces.Count; f++)
        {
            MeshFace face = level.Faces[f];
            if (face.NoGeometry || face.Indices == null || face.Indices.Length == 0)
                continue;
            List<float[]> tris = new List<float[]>(face.Indices.Length / 3);
            for (int t = 0; t + 2 < face.Indices.Length; t += 3)
            {
                float[] tri = new float[PartGeometry.TriangleFloats];
                for (int c = 0; c < 3; c++)
                {
                    int v = face.Indices[t + c], o = c * 8;
                    SysVector3 p = face.Positions[v];
                    tri[o] = p.X * size.X; tri[o + 1] = p.Z * size.Z; tri[o + 2] = -p.Y * size.Y;
                    SysVector3 n = face.Normals != null && v < face.Normals.Length ? face.Normals[v] : SysVector3.UnitZ;
                    // the inverse of the importer: its normal is the glTF normal times the size, turned into viewer axes and made unit again
                    float nx = n.X / size.X, ny = n.Z / size.Z, nz = -n.Y / size.Y;
                    float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; }
                    tri[o + 3] = nx; tri[o + 4] = ny; tri[o + 5] = nz;
                    if (face.Uvs != null && v < face.Uvs.Length)
                    {
                        tri[o + 6] = face.Uvs[v].X;
                        tri[o + 7] = 1f - face.Uvs[v].Y;
                    }
                }
                tris.Add(tri);
            }
            g.Faces[f] = tris;
            g.Materials[f] = new List<int>(new int[tris.Count]);
        }
        return g;
    }

    // ------------------------------------------------------------------ the physics shape

    private static CheckLine ComparePhysics(MeshAssetModel model, string physicsPath, Vector3 size, float[] shift, GeometryTolerance tolerance, List<string> notes)
    {
        (GlbFile file, GlbNode node) = ObjectImporter.ReadModel(physicsPath, Path.GetFileName(physicsPath), notes);
        PartGeometry input = PartGeometry.FromNode(file, node, node.Scale, true);
        CheckLine line = new CheckLine { What = "physics shape" };
        List<SysVector3> inputPoints = Shifted(input, shift).Faces.Values.SelectMany(l => l).SelectMany(t => new[] { new SysVector3(t[0], t[1], t[2]), new SysVector3(t[8], t[9], t[10]), new SysVector3(t[16], t[17], t[18]) }).Distinct().ToList();
        if (model.PhysicsMesh != null)
        {
            MeshFace mesh = ReadPhysicsMesh(model.PhysicsMesh);
            PartGeometry output = new PartGeometry();
            List<float[]> tris = new List<float[]>();
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                float[] tri = new float[PartGeometry.TriangleFloats];
                for (int c = 0; c < 3; c++)
                {
                    SysVector3 p = mesh.Positions[mesh.Indices[t + c]];
                    tri[c * 8] = p.X * size.X; tri[c * 8 + 1] = p.Z * size.Z; tri[c * 8 + 2] = -p.Y * size.Y;
                }
                tris.Add(tri);
            }
            output.Faces[0] = tris;
            output.Materials[0] = new List<int>(new int[tris.Count]);
            // the physics file has no texture coordinates and the physics mesh no normals: only the positions are compared
            PartGeometry moved = Shifted(input, shift);
            foreach (List<float[]> list in moved.Faces.Values)
                foreach (float[] tri in list)
                    for (int c = 0; c < 3; c++)
                        for (int k = 3; k < 8; k++)
                            tri[c * 8 + k] = 0f;
            GeometryComparison compare = GeometryComparer.Compare(moved, output, new GeometryTolerance { Position = tolerance.Position, Normal = 2f, Uv = 2f });
            line.InputTriangles = input.TriangleCount;
            line.OutputTriangles = tris.Count;
            line.Ok = compare.Equal;
            line.MaxPosition = compare.MaxPosition;
            line.Detail = "triangle mesh" + (compare.Equal ? ", every triangle matched" : ": " + compare.Difference);
            return line;
        }
        List<List<SysVector3>> hulls = PhysicsBlock.ReadHullList(model.PhysicsConvex);
        if (hulls.Count == 0)
        {
            line.Detail = "the mesh asset has neither a physics mesh nor a convex hull for the physics file";
            return line;
        }
        List<SysVector3> outputPoints = hulls.SelectMany(h => h).Select(p => new SysVector3(p.X * size.X, p.Z * size.Z, -p.Y * size.Y)).ToList();
        line.InputTriangles = inputPoints.Count;
        line.OutputTriangles = outputPoints.Count;
        float worst = 0f;
        foreach (SysVector3 p in outputPoints)
            worst = Math.Max(worst, inputPoints.Min(q => SysVector3.Distance(p, q)));
        SysVector3 inMin = inputPoints.Aggregate(SysVector3.Min), inMax = inputPoints.Aggregate(SysVector3.Max);
        SysVector3 outMin = outputPoints.Aggregate(SysVector3.Min), outMax = outputPoints.Aggregate(SysVector3.Max);
        float box = Math.Max(SysVector3.Distance(inMin, outMin), SysVector3.Distance(inMax, outMax));
        line.MaxPosition = Math.Max(worst, box);
        line.Ok = hulls.Count == 1 && outputPoints.Count >= 4 && line.MaxPosition <= tolerance.Position;
        line.Detail = "convex hull of " + outputPoints.Count.ToString(CultureInfo.InvariantCulture) + " points, each one of the " + inputPoints.Count.ToString(CultureInfo.InvariantCulture)
            + " points of the physics file, and the same box" + (line.Ok ? string.Empty : " (not so: largest difference " + (line.MaxPosition * 1000f).ToString("0.###", CultureInfo.InvariantCulture) + " mm)");
        return line;
    }

    /// <summary>The submesh of a physics_mesh block, read through the codec: the block is wrapped as the high level of a one-level asset.</summary>
    private static MeshFace ReadPhysicsMesh(object block)
    {
        byte[] raw = LlsdBinary.Write(block);
        using MemoryStream zipped = new MemoryStream();
        using (ZLibStream z = new ZLibStream(zipped, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        SortedDictionary<string, object> entry = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["offset"] = 0, ["size"] = (int)zipped.Length };
        SortedDictionary<string, object> header = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["high_lod"] = entry };
        using MemoryStream asset = new MemoryStream();
        asset.Write(LlsdBinary.Write(header));
        asset.Write(zipped.ToArray());
        MeshAssetModel carrier = MeshCodec.Decode(asset.ToArray());
        return carrier.High?.Faces.FirstOrDefault(f => !f.NoGeometry) ?? throw new ImportException("the physics mesh block holds no triangles");
    }

    // ------------------------------------------------------------------ pictures

    private static GlbMaterial ExportedMaterial(GlbFile exported, int face)
    {
        foreach (GlbMesh mesh in exported.Meshes)
            foreach (GlbPrimitive p in mesh.Primitives)
                if (p.FaceNumbers != null && p.FaceNumbers.Length > 0 && (int)Math.Round(p.FaceNumbers[0]) == face && p.Material >= 0 && p.Material < exported.Materials.Count)
                    return exported.Materials[p.Material];
        return null;
    }

    private static CheckLine ComparePicture(string what, byte[] input, byte[] output, bool outputIsJpeg2000)
    {
        CheckLine line = new CheckLine { What = what };
        if (output == null)
        {
            line.Detail = "no picture came back";
            return line;
        }
        byte[] a = Pixels(input, false, out int aw, out int ah), b = Pixels(output, outputIsJpeg2000, out int bw, out int bh);
        if (a == null || b == null)
        {
            line.Detail = "a picture cannot be decoded";
            return line;
        }
        line.Ok = aw == bw && ah == bh && a.AsSpan().SequenceEqual(b);
        line.Detail = line.Ok ? aw.ToString(CultureInfo.InvariantCulture) + " x " + ah.ToString(CultureInfo.InvariantCulture) + " pixels, all equal"
            : "the pixels differ (input " + aw.ToString(CultureInfo.InvariantCulture) + " x " + ah.ToString(CultureInfo.InvariantCulture) + ", output " + bw.ToString(CultureInfo.InvariantCulture) + " x " + bh.ToString(CultureInfo.InvariantCulture) + ")";
        return line;
    }

    private static byte[] Pixels(byte[] encoded, bool jpeg2000, out int width, out int height)
    {
        width = height = 0;
        SKBitmap bitmap;
        if (!(jpeg2000 ? SkiaImageUtils.TryDecodeFromJ2K(encoded, out bitmap) : SkiaImageUtils.TryDecodeFromBytes(encoded, out bitmap)) || bitmap == null)
            return null;
        using (bitmap)
        using (SKBitmap rgba = bitmap.Copy(SKColorType.Rgba8888))
        {
            width = rgba.Width;
            height = rgba.Height;
            return rgba.Bytes;
        }
    }

    // ------------------------------------------------------------------ the archive

    private static Dictionary<string, byte[]> ReadEntries(string path)
    {
        Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using GZipStream gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using TarReader tar = new TarReader(gz);
        TarEntry entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                continue;
            using MemoryStream ms = new MemoryStream();
            entry.DataStream.CopyTo(ms);
            entries[entry.Name] = ms.ToArray();
        }
        return entries;
    }
}
