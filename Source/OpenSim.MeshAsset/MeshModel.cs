/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;

namespace OpenSim.MeshAsset;

/// <summary>One joint influence on a vertex: index into <see cref="MeshSkin.JointNames"/> and weight 0..1.</summary>
public readonly record struct Influence(byte Joint, float Weight);

/// <summary>A face (one material slot) of one level of detail.</summary>
public sealed class MeshFace
{
    /// <summary>The face carries no geometry (written as a bare NoGeometry marker).</summary>
    public bool NoGeometry { get; set; }
    public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();
    public Vector3[]? Normals { get; set; }
    public Vector2[]? Uvs { get; set; }
    /// <summary>Triangle list, three per triangle, each below 65536 (the asset stores 16-bit indices).</summary>
    public int[] Indices { get; set; } = Array.Empty<int>();
    /// <summary>Per vertex, up to four influences, in the order they are stored. Null when the face has no Weights block.</summary>
    public Influence[][]? Weights { get; set; }

    /// <summary>
    /// The box the 16-bit position values span. A decoded face keeps the one it was stored with, so a re-encode
    /// reproduces the same quantised values. Null: the encoder uses the box around every face of the level of detail.
    /// </summary>
    public (Vector3 Min, Vector3 Max)? PositionDomain { get; set; }
    /// <summary>Same for texture coordinates. Null: the encoder uses the face's own UV extent.</summary>
    public (Vector2 Min, Vector2 Max)? UvDomain { get; set; }
    /// <summary>Size of the model before it was normalised into the unit box. Null when the asset has none (readers use 1,1,1).</summary>
    public Vector3? NormalizedScale { get; set; }

    public MeshFace Copy() => (MeshFace)MemberwiseClone();

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;
}

public sealed class MeshLod
{
    public List<MeshFace> Faces { get; } = new();

    /// <summary>A separate level with the same faces (the arrays inside are shared; the encoder only reads them).</summary>
    public MeshLod Copy()
    {
        var c = new MeshLod();
        foreach (var f in Faces) c.Faces.Add(f.Copy());
        return c;
    }
}

/// <summary>The skin block. Matrices are 16 values in the order the asset stores them: row major, row vectors, translation at 12..14.</summary>
public sealed class MeshSkin
{
    public List<string> JointNames { get; set; } = new();
    public List<double[]> InverseBind { get; set; } = new();
    public double[] BindShape { get; set; } = Identity();
    public List<double[]>? AltInverseBind { get; set; }
    public double? PelvisOffset { get; set; }
    public bool LockScaleIfJointPosition { get; set; }

    public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
}

/// <summary>
/// A Linden Lab mesh asset: up to four levels of detail, an optional skin, and the physics blocks carried through
/// untouched (they are not interpreted in this version).
/// </summary>
public sealed class MeshAssetModel
{
    public const int LodCount = 4;

    /// <summary>The header "version" value. Null (the default) leaves the key out, as the Linden Lab viewer's own upload does; a decoded asset keeps the value it had.</summary>
    public int? Version { get; set; }
    public MeshLod? Lowest { get; set; }
    public MeshLod? Low { get; set; }
    public MeshLod? Medium { get; set; }
    public MeshLod? High { get; set; }
    public MeshSkin? Skin { get; set; }

    /// <summary>Decoded LLSD of the physics_convex block, kept as read and written back as is.</summary>
    public object? PhysicsConvex { get; set; }
    /// <summary>Decoded LLSD of the physics_mesh block, kept as read and written back as is.</summary>
    public object? PhysicsMesh { get; set; }
    /// <summary>Header keys other than version, the block entries and the blocks' own keys, kept as read.</summary>
    public SortedDictionary<string, object?> ExtraHeader { get; } = LlsdBinary.NewMap();

    /// <summary>True when missing lower levels of detail were filled with copies of the one above (see <see cref="RepeatLodAbove"/>).</summary>
    public bool LodsRepeated { get; private set; }
    /// <summary>True when the lower levels were made by <see cref="LodGenerator"/>.</summary>
    public bool LodsGenerated { get; set; }
    public List<string> Warnings { get; } = new();

    /// <summary>Index 0 lowest, 1 low, 2 medium, 3 high.</summary>
    public MeshLod? Lod(int i) => i switch { 0 => Lowest, 1 => Low, 2 => Medium, 3 => High, _ => throw new ArgumentOutOfRangeException(nameof(i)) };

    public void SetLod(int i, MeshLod? lod)
    {
        switch (i)
        {
            case 0: Lowest = lod; break;
            case 1: Low = lod; break;
            case 2: Medium = lod; break;
            case 3: High = lod; break;
            default: throw new ArgumentOutOfRangeException(nameof(i));
        }
    }

    /// <summary>
    /// Fills every missing lower level of detail with a copy of the level above it, medium from high, low from medium, lowest
    /// from low: what the Linden Lab viewer writes when the creator chooses "use LOD above". The copies are separate
    /// levels and the encoder writes each as its own block. The mesh then costs the same to draw at any distance.
    /// </summary>
    public void RepeatLodAbove()
    {
        if (High == null) throw new InvalidOperationException("there is no high level of detail to repeat");
        for (int i = LodCount - 2; i >= 0; i--)
            if (Lod(i) == null) SetLod(i, Lod(i + 1)!.Copy());
        LodsRepeated = true;
    }
}
