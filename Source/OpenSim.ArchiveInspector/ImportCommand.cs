/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.ArchiveInspector;

/// <summary>The oar-import command: write an archive holding one mesh object made from a folder of .glb files.</summary>
internal static class ImportCommand
{
    public const string Usage = "Usage: OpenSim.ArchiveInspector oar-import <folder> --out <new.oar> --avatar <uuid>";

    public static int Run(string[] args) => 1;
}
