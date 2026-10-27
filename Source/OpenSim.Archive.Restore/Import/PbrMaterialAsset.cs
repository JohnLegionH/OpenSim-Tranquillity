/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Restore.Import;

/// <summary>The fields of one glTF PBR material asset.</summary>
public sealed class PbrMaterialData
{
    public float[] BaseColor { get; set; }
    public float Metallic { get; set; }
    public float Roughness { get; set; }
    public float[] Emissive { get; set; }
    public string AlphaMode { get; set; }
    public float AlphaCutoff { get; set; }
    public bool DoubleSided { get; set; }
    public Guid BaseColorTexture { get; set; }
    public Guid NormalTexture { get; set; }
    public Guid MetalRoughTexture { get; set; }
    public Guid EmissiveTexture { get; set; }
}

public static class PbrMaterialAsset
{
    public static byte[] Build(PbrMaterialData data) => Array.Empty<byte>();

    public static PbrMaterialData Parse(byte[] asset) => null;
}
