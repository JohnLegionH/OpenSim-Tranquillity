/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.GltfExport;

/// <summary>
/// The exporter turns every texture into a PNG through a JPEG 2000 decoder and a native image library. Where either is
/// missing or broken, every texture would be reported as undecodable and the export would still finish. So a run begins by
/// decoding a small image built into the program through the same path the textures take, and refuses to start when that fails.
/// </summary>
public static class ImageSelfCheck
{
    private const int Size = 4;

    // A 4 x 4 lossless JPEG 2000 image; the pixel at column x, row y is (x * 80, y * 80, 200).
    private const string ImageBase64 = "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAABAAAAAQAAwcHAQAAAAAPY29scgEAAAAAABAAAAEWanAyY/9P/1EALwAAAAAABAAAAAQAAAAAAAAAAAAAAAQAAAAEAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAQUEBAAB/1wAEyBASEhQSEhQSEhQSEhQSEhQ/10AFAEgSFBQWFBQWFBQWFBQWFBQWP9dABQCIEhQUFhQUFhQUFhQUFhQUFj/kAAKAAAAAACKAAH/XQAUASBIUFBYUFBYUFBYUFBYUFBY/10AFAIgSFBQWFBQWFBQWFBQWFBQWP+Tw+QEBWDPtAgBbYAAAAAAAAAAAADH1AU+0CADHwMfp+AEBc/PwAp+AEADHwXPw+cHH1AgDP9gCzxjBKPtBAAMFzWgx9oJH2ggDP9hQAwXNaD/2Q==";

    private static readonly Lazy<string> Result = new Lazy<string>(Run);

    /// <summary>Null when the image library decodes the built-in image correctly; otherwise the reason it does not. Decided once per process.</summary>
    public static string Problem() => Result.Value;

    /// <summary>Runs the check now (the tests use it; the program uses <see cref="Problem"/>).</summary>
    internal static string Run()
    {
        try
        {
            AssetBlob blob = new AssetBlob { Type = (int)OpenMetaverse.AssetType.Texture, Bytes = Convert.FromBase64String(ImageBase64) };

            TextureImage png = TextureDecoder.Decode(blob, out string problem);
            if (png == null)
                return "the built-in test image could not be decoded: " + problem;
            if (png.Width != Size || png.Height != Size || png.Png == null || png.Png.Length < 8 || png.Png[0] != 0x89 || png.Png[1] != (byte)'P')
                return "the built-in test image came out as " + png.Width + " x " + png.Height + " and not as a PNG";

            SculptMap pixels = TextureDecoder.DecodeRgb(blob, out problem);
            if (pixels == null)
                return "the built-in test image could not be read back: " + problem;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int at = (y * pixels.Width + x) * 3;
                    if (pixels.Width != Size || pixels.Height != Size || pixels.Rgb[at] != x * 80 || pixels.Rgb[at + 1] != y * 80 || pixels.Rgb[at + 2] != 200)
                        return "the built-in test image decoded to the wrong colours at column " + x + ", row " + y;
                }
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "the image library failed on the built-in test image: " + e.GetType().Name + ": " + e.Message;
        }
    }
}

/// <summary>The export was refused because the image library does not work; nothing was written.</summary>
public sealed class ImageLibraryException : IOException
{
    public ImageLibraryException(string problem) : base("the image library is not working, so no textures could be exported and nothing was written: " + problem) { }
}
