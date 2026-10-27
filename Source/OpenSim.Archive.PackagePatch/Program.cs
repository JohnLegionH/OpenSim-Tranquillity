/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Archive.PackagePatch;

// usage: OpenSim.Archive.PackagePatch <input assembly> <output assembly>
// Exit codes: 0 patched (the number of replaced call sites is printed), 2 bad arguments or an assembly it cannot read.
if (args.Length != 2)
{
    Console.Error.WriteLine("usage: OpenSim.Archive.PackagePatch <input assembly> <output assembly>");
    return 2;
}
try
{
    int replaced = HashPatcher.Patch(args[0], args[1]);
    Console.WriteLine(Path.GetFileName(args[0]) + ": " + replaced + " hash call sites replaced");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("cannot patch " + args[0] + ": " + e.Message);
    return 2;
}
