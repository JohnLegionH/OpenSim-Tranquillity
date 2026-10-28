/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using Xunit;

namespace OpenSim.Archive.GltfExport.Tests;

/// <summary>
/// Console.Out and Console.Error are process-wide. Test classes that redirect them while the export command runs must not run at the same
/// time, or one class's capture swallows the other's output.
/// </summary>
public class ConsoleCollectionTests
{
    private static string CollectionOf(Type type) =>
        type.GetCustomAttributesData().Where(a => a.AttributeType == typeof(CollectionAttribute)).Select(a => a.ConstructorArguments[0].Value as string).FirstOrDefault();

    [Fact]
    public void TheClassesThatRedirectTheConsoleRunInOneCollection()
    {
        string archive = CollectionOf(typeof(ArchiveExportTests));
        string clean = CollectionOf(typeof(CleanExportCommandTests));

        Assert.False(string.IsNullOrEmpty(archive), "ArchiveExportTests is not in a collection");
        Assert.Equal(archive, clean);
    }
}
