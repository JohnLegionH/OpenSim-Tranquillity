/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse.StructuredData;
using Xunit;

namespace OpenSim.Archive.Inspection.Tests;

/// <summary>
/// Unwrapping a PBR material asset the way the viewer reads one: an LLSD map (binary, XML or notation, told apart by the header) whose version is 1.0 or 1.1,
/// whose type is "GLTF 2.0" and whose data is a string (indra/newview/llgltfmateriallist.cpp, onAssetLoadComplete; llmaterialeditor.cpp, decodeAsset).
/// Plain glTF text is not a map the viewer accepts, but the archive tools have always read it. No test shares state, so the class runs in parallel.
/// </summary>
public class PbrMaterialTextTests
{
    private const string Gltf = "{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"alphaMode\":\"BLEND\"}]}";

    private static OSDMap Map(string version = "1.1", string type = "GLTF 2.0", OSD data = null) =>
        new OSDMap { ["version"] = version, ["type"] = type, ["data"] = data ?? Gltf };

    private static byte[] Binary(OSDMap map)
    {
        byte[] header = Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n"), body = OSDParser.SerializeLLSDBinary(map, false);
        return header.Concat(body).ToArray();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public void TheTextOfABinaryMapOfAnAcceptedVersionComesOut(string version)
    {
        Assert.Equal(Gltf, Encoding.UTF8.GetString(PbrMaterialText.Unwrap(Binary(Map(version))) ?? Array.Empty<byte>()));
    }

    [Fact]
    public void TheTextOfAnXmlMapComesOut()
    {
        byte[] xml = OSDParser.SerializeLLSDXmlBytes(Map());
        Assert.Equal(Gltf, Encoding.UTF8.GetString(PbrMaterialText.Unwrap(xml) ?? Array.Empty<byte>()));
    }

    [Fact]
    public void PlainGltfTextComesOutAsItIs()
    {
        Assert.Equal(Gltf, Encoding.UTF8.GetString(PbrMaterialText.Unwrap(Encoding.UTF8.GetBytes(Gltf)) ?? Array.Empty<byte>()));
    }

    [Fact]
    public void AMapTheViewerWouldRefuseComesOutAsNothing()
    {
        Assert.Null(PbrMaterialText.Unwrap(Binary(Map(version: "2.0"))));
        Assert.Null(PbrMaterialText.Unwrap(Binary(Map(type: "GLTF 1.0"))));
        Assert.Null(PbrMaterialText.Unwrap(Binary(Map(data: OSD.FromInteger(7)))));
        OSDMap noData = Map();
        noData.Remove("data");
        Assert.Null(PbrMaterialText.Unwrap(Binary(noData)));
    }

    [Fact]
    public void BytesThatAreNotAMaterialComeOutAsNothing()
    {
        Assert.Null(PbrMaterialText.Unwrap(null));
        Assert.Null(PbrMaterialText.Unwrap(Array.Empty<byte>()));
        Assert.Null(PbrMaterialText.Unwrap(Encoding.ASCII.GetBytes("not a material")));
        Assert.Null(PbrMaterialText.Unwrap(Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n{\0\0\0\x09")));
    }
}
