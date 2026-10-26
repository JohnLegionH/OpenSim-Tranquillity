/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Converter.Merge;

/// <summary>Decides whether a face is drawn alpha-blended.</summary>
public static class FaceAlpha
{
    /// <summary>
    /// A face blends when its colour alpha is below 1, or its legacy material blends, or its texture has an alpha channel that is not
    /// fully opaque and the face's legacy material does not say otherwise (alpha mode none or mask).
    /// </summary>
    /// <param name="material">The face's legacy material, or null when it has none.</param>
    public static bool IsAlpha(float colorAlpha, bool textureTranslucent, LegacyMaterialInfo material) => throw new NotImplementedException();
}

/// <summary>Reads the alpha channel of a texture.</summary>
public static class TextureAlpha
{
    /// <summary>True when the JPEG 2000 texture decodes and has at least one pixel that is not fully opaque; false when it is opaque or cannot be decoded.</summary>
    public static bool IsTranslucent(byte[] jpeg2000) => throw new NotImplementedException();
}
