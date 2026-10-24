/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace OpenSim.MeshAsset;

/// <summary>
/// The Second Life avatar skeleton at its default pose, read from the viewer's avatar_skeleton.xml (the file is
/// supplied by the caller; nothing from it is built in). Joint matrices are in the row-vector layout of
/// <see cref="Matrix4x4"/>, the same layout the mesh asset's skin block uses. A joint's local matrix is its scale,
/// then its rotation, then its position; its world matrix is the local matrix times its parent's world matrix.
/// Bones in the file have unit scale and no rotation; the collision volumes carry both, which is why the inverse
/// bind matrix of a collision volume cannot be left as the unscaled one a glTF exporter writes.
/// </summary>
public sealed class RestSkeleton
{
    public sealed class Joint
    {
        public required string Name { get; init; }
        public string? Parent { get; init; }
        public bool IsCollisionVolume { get; init; }
        public Vector3 Position { get; init; }
        public Vector3 RotationDegrees { get; init; }
        public Vector3 Scale { get; init; } = Vector3.One;
    }

    readonly Dictionary<string, Joint> _byName = new(StringComparer.Ordinal);
    readonly Dictionary<string, Joint> _byAlias = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Joint> Joints => _byName.Values;

    public static RestSkeleton Load(string path)
    {
        using var s = File.OpenRead(path);
        return Parse(XDocument.Load(s));
    }

    public static RestSkeleton Parse(XDocument doc)
    {
        var sk = new RestSkeleton();
        var root = doc.Root ?? throw new InvalidDataException("empty skeleton file");
        foreach (var e in root.Elements()) sk.Walk(e, null);
        return sk;
    }

    void Walk(XElement e, string? parent)
    {
        bool isBone = e.Name.LocalName == "bone";
        bool isCv = e.Name.LocalName == "collision_volume";
        if (!isBone && !isCv) return;
        var name = (string?)e.Attribute("name") ?? throw new InvalidDataException("joint without a name");
        var j = new Joint
        {
            Name = name,
            Parent = parent,
            IsCollisionVolume = isCv,
            Position = Vec((string?)e.Attribute("pos"), Vector3.Zero),
            RotationDegrees = Vec((string?)e.Attribute("rot"), Vector3.Zero),
            Scale = Vec((string?)e.Attribute("scale"), Vector3.One),
        };
        _byName[name] = j;
        foreach (var a in ((string?)e.Attribute("aliases") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            _byAlias.TryAdd(a, j);
        foreach (var c in e.Elements()) Walk(c, name);
    }

    static Vector3 Vec(string? s, Vector3 fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Vector3(F(p[0]), F(p[1]), F(p[2]));
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    public Joint? Find(string name) =>
        _byName.TryGetValue(name, out var j) ? j : _byAlias.TryGetValue(name, out j) ? j : null;

    /// <summary>
    /// A copy of the skeleton at the avatar's default shape: every skeletal shape parameter of avatar_lad.xml
    /// (the viewer's avatar definition) at its default value moves and scales the bones it names, by the default value
    /// times the parameter's per-bone scale and offset. A few defaults are not zero (for example the arm length), so the
    /// default avatar is not the bare skeleton file.
    /// </summary>
    public RestSkeleton WithDefaultShape(string avatarDefinitionPath)
    {
        var pos = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var scale = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var doc = XDocument.Load(avatarDefinitionPath);
        foreach (var param in doc.Descendants("param"))
        {
            var sk = param.Element("param_skeleton");
            if (sk == null) continue;
            float w = F((string?)param.Attribute("value_default") ?? "0");
            if (w == 0) continue;
            foreach (var b in sk.Elements("bone"))
            {
                var j = Find((string?)b.Attribute("name") ?? "");
                if (j == null) continue;
                if (b.Attribute("scale") != null)
                {
                    var delta = w * Vec((string?)b.Attribute("scale"), Vector3.Zero);
                    scale[j.Name] = scale.GetValueOrDefault(j.Name) + delta;
                    // A collision volume under a scaled bone is scaled along with it: its own scale times the bone's
                    // scale deformation (LLPolySkeletalDistortion::setInfo, the children that inherit scale).
                    foreach (var child in _byName.Values)
                        if (child.IsCollisionVolume && child.Parent == j.Name)
                            scale[child.Name] = scale.GetValueOrDefault(child.Name) + child.Scale * delta;
                }
                if (b.Attribute("offset") != null)
                    pos[j.Name] = pos.GetValueOrDefault(j.Name) + w * Vec((string?)b.Attribute("offset"), Vector3.Zero);
            }
        }
        var copy = new RestSkeleton();
        foreach (var j in _byName.Values)
        {
            var c = new Joint
            {
                Name = j.Name, Parent = j.Parent, IsCollisionVolume = j.IsCollisionVolume,
                Position = j.Position + pos.GetValueOrDefault(j.Name),
                RotationDegrees = j.RotationDegrees,
                Scale = j.Scale + scale.GetValueOrDefault(j.Name),
            };
            copy._byName[c.Name] = c;
        }
        foreach (var (alias, j) in _byAlias) copy._byAlias[alias] = copy._byName[j.Name];
        return copy;
    }

    /// <summary>The joint's rotation: X, then Y, then Z, as the viewer's own glTF loader applies them, for row vectors.</summary>
    static Matrix4x4 Rotation(Joint j)
    {
        var r = j.RotationDegrees * (MathF.PI / 180f);
        return Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationX(r.X);
    }

    /// <summary>
    /// The joint's world matrix. The viewer scales a joint's own geometry by its scale and also scales the offset of each
    /// child by it, but a scale never rotates or shears the children: the world rotation is the plain chain of rotations
    /// and the world position of a joint is its parent's, plus its offset scaled by the parent's scale and rotated by the
    /// parent's world rotation (Linden Lab's LLXformMatrix::update). <paramref name="positionOverrides"/> replaces the
    /// position (relative to the parent) of the joints it names, as the viewer does for a mesh uploaded with joint positions.
    /// </summary>
    public Matrix4x4 World(string name, IReadOnlyDictionary<string, Vector3>? positionOverrides = null)
    {
        var j = Find(name) ?? throw new KeyNotFoundException($"joint {name} is not in the skeleton");
        var (rot, pos) = Chain(j, positionOverrides);
        return Matrix4x4.CreateScale(j.Scale) * rot * Matrix4x4.CreateTranslation(pos);
    }

    (Matrix4x4 Rotation, Vector3 Position) Chain(Joint j, IReadOnlyDictionary<string, Vector3>? overrides)
    {
        var own = overrides != null && overrides.TryGetValue(j.Name, out var ov) ? ov : j.Position;
        if (j.Parent == null) return (Rotation(j), own);
        var parent = _byName[j.Parent];
        var (prot, ppos) = Chain(parent, overrides);
        return (Rotation(j) * prot, Vector3.Transform(own * parent.Scale, prot) + ppos);
    }
}
