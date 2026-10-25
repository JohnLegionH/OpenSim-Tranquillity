/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace OpenSim.Archive.Restore.Tests;

/// <summary>
/// The archive tools are published as self-contained programs. On Linux the framework's hash classes load the system's
/// OpenSSL, which minimal images do not have, so our own assemblies take their digests from OpenSim.Archive.Inspection.ManagedDigest.
/// The check reads each assembly's metadata, so a using directive nothing calls does not count and a call does.
/// </summary>
public class PackagedAssemblyGuardTests
{
    private static readonly string[] OwnAssemblies =
    {
        "OpenSim.Archive.Compare", "OpenSim.Archive.Converter", "OpenSim.Archive.Eligibility", "OpenSim.Archive.GltfExport",
        "OpenSim.Archive.Inspection", "OpenSim.Archive.Restore", "OpenSim.Archive.Rewrite", "OpenSim.Archive.ScriptScan",
        "OpenSim.ArchiveInspector",
    };

    private static bool IsHashType(string name) =>
        name.StartsWith("SHA", StringComparison.Ordinal) || name.StartsWith("MD5", StringComparison.Ordinal)
        || name.StartsWith("HMAC", StringComparison.Ordinal) || name is "HashAlgorithm" or "KeyedHashAlgorithm" or "IncrementalHash";

    private static List<string> HashTypeReferences(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        List<string> found = new List<string>();
        foreach (TypeReferenceHandle handle in reader.TypeReferences)
        {
            TypeReference type = reader.GetTypeReference(handle);
            if (reader.GetString(type.Namespace) == "System.Security.Cryptography" && IsHashType(reader.GetString(type.Name)))
                found.Add(reader.GetString(type.Name));
        }
        return found;
    }

    [Fact]
    public void TheGuardFindsEveryOneOfOurPackagedAssemblies()
    {
        foreach (string name in OwnAssemblies)
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll")), name + ".dll is not in the test output");
    }

    [Fact]
    public void NoneOfOurPackagedAssembliesReferencesAFrameworkHashType()
    {
        List<string> offenders = new List<string>();
        foreach (string name in OwnAssemblies)
        {
            string path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(path))
                offenders.AddRange(HashTypeReferences(path).Distinct().Select(t => name + " -> System.Security.Cryptography." + t));
        }
        Assert.True(offenders.Count == 0, "These need libssl on Linux:\n" + string.Join("\n", offenders));
    }
}
