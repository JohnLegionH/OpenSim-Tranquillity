/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.Restore.Import;

/// <summary>Writes an archive holding one mesh object made from a folder of .glb files (a level of detail each, a physics shape) and their pictures.</summary>
public static class ObjectImporter
{
    public static ImportResult Import(string folder, string outputPath, ImportOptions options) => new ImportResult();
}
