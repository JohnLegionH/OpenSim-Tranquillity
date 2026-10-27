/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenSim.Archive.Restore.Import;

/// <summary>What the importer is asked to write.</summary>
public sealed class ImportOptions
{
    /// <summary>The object's name; empty takes the file name prefix of the model.</summary>
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>The avatar named as creator and owner (required).</summary>
    public Guid Avatar { get; set; }
    /// <summary>The file name prefix of the model (<c>radio</c> for radio.glb, radio_lod2.glb and radio_physics.glb); empty finds it from the *_physics.glb or the only .glb in the folder.</summary>
    public string BaseName { get; set; } = string.Empty;
    /// <summary>Where the model's origin (0, 0, 0 in the .glb) lands in the region, in the viewer's axes.</summary>
    public Vector3 Position { get; set; } = new Vector3(128f, 128f, 25f);
    /// <summary>Creation time in seconds since 1970; 0 takes the current time.</summary>
    public long Created { get; set; }
    public Action<string> Progress { get; set; }
}

/// <summary>One level of detail of the written mesh.</summary>
public sealed class ImportLevel
{
    /// <summary>high, medium, low or lowest.</summary>
    public string Name { get; set; }
    public string File { get; set; }
    public int Triangles { get; set; }
    public int Faces { get; set; }
}

/// <summary>What the importer wrote.</summary>
public sealed class ImportResult
{
    public bool Written { get; set; }
    public string Output { get; set; }
    public string Name { get; set; }
    public Guid ObjectId { get; set; }
    public Guid MeshId { get; set; }
    public long MeshBytes { get; set; }
    /// <summary>The part size in metres, in the viewer axes (the box around the high level).</summary>
    public Vector3 Size { get; set; }
    /// <summary>Where the centre of the part is in the region.</summary>
    public Vector3 Centre { get; set; }
    public int Faces { get; set; }
    public List<ImportLevel> Levels { get; } = new List<ImportLevel>();
    /// <summary>convex hull, triangle mesh or bounding hull.</summary>
    public string Physics { get; set; }
    public int PhysicsTriangles { get; set; }
    /// <summary>The texture assets written, with the .glb material each is for.</summary>
    public List<(Guid Id, string Material, string Role)> Textures { get; } = new List<(Guid, string, string)>();
    public List<Guid> Materials { get; } = new List<Guid>();
    public List<string> Notes { get; } = new List<string>();

    public string ToText()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        sb.Append("object '").Append(Name).Append("' (").Append(ObjectId.ToString("D")).Append(") written to ").Append(Output).Append('\n');
        sb.Append("size ").Append(Size.X.ToString("0.###", c)).Append(" x ").Append(Size.Y.ToString("0.###", c)).Append(" x ").Append(Size.Z.ToString("0.###", c))
          .Append(" m, centre at <").Append(Centre.X.ToString("0.###", c)).Append(", ").Append(Centre.Y.ToString("0.###", c)).Append(", ").Append(Centre.Z.ToString("0.###", c)).Append(">, ")
          .Append(Faces.ToString(c)).Append(" faces\n");
        sb.Append("mesh ").Append(MeshId.ToString("D")).Append(", ").Append(MeshBytes.ToString(c)).Append(" bytes\n");
        foreach (ImportLevel l in Levels)
            sb.Append("  ").Append(l.Name).Append(": ").Append(l.Triangles.ToString(c)).Append(" triangles in ").Append(l.Faces.ToString(c)).Append(" faces (").Append(l.File).Append(")\n");
        sb.Append("physics: ").Append(Physics);
        if (PhysicsTriangles > 0)
            sb.Append(", ").Append(PhysicsTriangles.ToString(c)).Append(" triangles");
        sb.Append('\n');
        foreach ((Guid id, string material, string role) in Textures)
            sb.Append("  texture ").Append(id.ToString("D")).Append(" (").Append(role).Append(" of ").Append(material).Append(")\n");
        foreach (Guid m in Materials)
            sb.Append("  material ").Append(m.ToString("D")).Append(" (normal map)\n");
        foreach (string n in Notes)
            sb.Append("note: ").Append(n).Append('\n');
        return sb.ToString();
    }
}

/// <summary>The input cannot be turned into an object; the message says why and nothing was written.</summary>
public sealed class ImportException : Exception
{
    public ImportException(string message) : base(message) { }
}
