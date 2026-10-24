/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Numerics;
using OpenSim.MeshAsset;

namespace OpenSim.CharacterPublish;

/// <summary>What a bundle needs to know about a mesh asset.</summary>
/// <param name="FaceCount">The number of faces (submeshes) of the highest level of detail present.</param>
/// <param name="NormalizedScale">
/// The size in metres the model had before the uploader scaled it into a unit box (the face's NormalizedScale), or null
/// when the asset has none (older uploads).
/// </param>
/// <param name="Rigged">True when the asset carries a skin block.</param>
public sealed record MeshInfo(int FaceCount, Vector3? NormalizedScale, bool Rigged);

public static class MeshInspector
{
    /// <summary>Decodes the asset with the shared mesh library. Throws when it does not decode or has no geometry.</summary>
    public static MeshInfo Inspect(byte[] asset)
    {
        var model = MeshCodec.Decode(asset);
        var lod = model.High ?? model.Medium ?? model.Low ?? model.Lowest
            ?? throw new InvalidDataException("the mesh has no level of detail");
        if (lod.Faces.Count == 0) throw new InvalidDataException("the mesh has no faces");
        // The viewer's uploader writes the model's own size on every face (LLModel::writeModel, NormalizedScale), and the
        // object it makes takes that size. The first face that has one stands for the model.
        var ns = lod.Faces.FirstOrDefault(f => f.NormalizedScale is not null)?.NormalizedScale;
        return new MeshInfo(lod.Faces.Count, ns, model.Skin != null);
    }
}
