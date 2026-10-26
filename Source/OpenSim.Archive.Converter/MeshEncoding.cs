/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.Inspection;
using System.Text;

namespace OpenSim.Archive.Converter;

/// <summary>
/// One face of a mesh: an indexed triangle list in the part's own space (the unit box, never scaled to the part's size).
/// Texture coordinates use the viewer's convention (v = 0 at the bottom of the image), so a texture entry that drew a prim
/// face draws the mesh face the same way. A face with no triangles is an empty slot; it keeps its face number.
/// </summary>
public sealed class EncoderFace
{
    /// <summary>x, y, z for each vertex.</summary>
    public float[] Positions = Array.Empty<float>();
    /// <summary>x, y, z for each vertex.</summary>
    public float[] Normals = Array.Empty<float>();
    /// <summary>u, v for each vertex.</summary>
    public float[] Uvs = Array.Empty<float>();
    /// <summary>Three vertex numbers for each triangle.</summary>
    public uint[] Indices = Array.Empty<uint>();
    /// <summary>True when cleaning found only triangles without area in the face and left it as it was.</summary>
    public bool KeptAsWas;

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
    public bool IsEmpty => Indices.Length == 0;
}

public sealed class EncodeOptions
{
    /// <summary>
    /// The collision shape to write into the asset's physics_mesh block, in the unit-size space of the faces; null writes none
    /// (the region then collides with a medium level of detail of the display mesh).
    /// </summary>
    public PhysicsGeometry Physics { get; set; }

    /// <summary>
    /// The medium, low and lowest levels of detail, in that order, one face list for each with the same number of faces as the full mesh; null
    /// makes them with the viewer profile (a third, a ninth and a twenty-seventh of the triangles). When they are given, the physics_convex bounding
    /// hull is made from the full mesh, so a level that leaves out small members does not shrink it.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<EncoderFace>> Lods { get; set; }

    /// <summary>
    /// A hull for each part of the shape, in the unit-size space of the faces: written as the hull list of the physics_convex block (the hull of
    /// all of them stays as its bounding hull). Null writes the bounding hull only.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<System.Numerics.Vector3>> Hulls { get; set; }
}

/// <summary>
/// Turns a list of faces into the bytes of a mesh asset. The converter chooses the number of faces (the list's length) and
/// the shape fields that report it; the encoder only writes the asset.
/// </summary>
public interface IMeshEncoder
{
    /// <summary>A short name for the report.</summary>
    string Name { get; }

    /// <summary>False for an encoder whose output is not a mesh a viewer or region can load (the stand-in).</summary>
    bool ProducesValidMesh { get; }

    /// <summary>The asset bytes for the faces; the same faces always give the same bytes.</summary>
    byte[] Encode(IReadOnlyList<EncoderFace> faces, EncodeOptions options);
}

/// <summary>
/// A stand-in encoder for testing the rest of the conversion. It returns deterministic bytes derived from the faces and
/// marks them with a text header. THE BYTES ARE NOT A VALID MESH: a viewer or region cannot load them. It exists so the
/// pipeline, the ids, the shape edits and the parity check can be proved before a real encoder is plugged in.
/// </summary>
public sealed class StandInMeshEncoder : IMeshEncoder
{
    public const string Marker = "STAND-IN, NOT A VALID MESH: bytes derived from the faces so the conversion can be tested\n";

    public string Name => "stand-in (not a valid mesh)";
    public bool ProducesValidMesh => false;

    public byte[] Encode(IReadOnlyList<EncoderFace> faces, EncodeOptions options)
    {
        using MemoryStream ms = new MemoryStream();
        using (BinaryWriter w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes(Marker));
            w.Write(faces.Count);
            foreach (EncoderFace f in faces)
            {
                w.Write(f.VertexCount);
                w.Write(f.TriangleCount);
                foreach (float v in f.Positions) w.Write(v);
                foreach (float v in f.Normals) w.Write(v);
                foreach (float v in f.Uvs) w.Write(v);
                foreach (uint v in f.Indices) w.Write(v);
            }
        }
        return ms.ToArray();
    }
}

public static class MeshId
{
    /// <summary>
    /// The asset id for a mesh: the first 16 bytes of the SHA-256 of its bytes, with the version and variant bits of a
    /// custom (version 8) UUID. The same bytes always give the same id, so a rerun writes the same archive and identical
    /// meshes share one asset.
    /// </summary>
    public static Guid FromBytes(byte[] data)
    {
        byte[] hash = ManagedDigest.Sha256(data);
        byte[] b = new byte[16];
        Array.Copy(hash, b, 16);
        b[6] = (byte)((b[6] & 0x0F) | 0x80);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }
}
