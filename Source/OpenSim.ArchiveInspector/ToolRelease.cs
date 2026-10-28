/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.ArchiveInspector;

/// <summary>The release of the oar tools. oar-convert, oar-export, oar-restore, oar-import and oar-inspect are packaged and numbered together, so all of them print this in --version. It is separate from <see cref="OpenSim.Archive.GltfExport.ExportStamp.CurrentVersion"/>, the file format version written into a .glb, which only changes when the .glb itself changes.</summary>
internal static class ToolRelease
{
    public const string Version = "0.4.4";
}
