/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

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
}

/// <summary>The manifest of one linked object: the object's name and its parts, root first.</summary>
public sealed class LinksetManifest
{
    public string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>The folder the manifest's relative paths are read from.</summary>
    public string Folder { get; set; }
    public List<LinksetPart> Parts { get; } = new List<LinksetPart>();

    public static LinksetManifest Load(string path) => new LinksetManifest();

    public static LinksetManifest Parse(string json, string folder) => new LinksetManifest();
}
