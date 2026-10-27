/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using OpenSim.Archive.ManagedHash;
using OpenSim.Archive.PackagePatch;
using Xunit;

namespace OpenSim.Archive.Converter.Tests;

/// <summary>
/// The OpenMetaverse libraries create their MD5, SHA-1 and SHA-256 objects when their Utils class starts, and the
/// framework's classes load the system's OpenSSL on Linux. The package patch swaps in the managed ones. These tests patch
/// the libraries the tools ship (the copies in the test output) and read the result back.
/// </summary>
public class HashPatchTests
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly string work = Path.Combine(Path.GetTempPath(), "hashpatch-" + Guid.NewGuid().ToString("N"));

    public HashPatchTests() => Directory.CreateDirectory(work);

    private static string Library(string name) => Path.Combine(AppContext.BaseDirectory, name);

    private string Output(string name) => Path.Combine(work, name);

    private string Patched(string name)
    {
        HashPatcher.Patch(Library(name), Output(name));
        return Output(name);
    }

    /// <summary>Every place the assembly names a framework hash class's constructor or a Create method of MD5, SHA1 or SHA256.</summary>
    private static List<string> FrameworkHashCreations(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        List<string> found = new List<string>();
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) != "System.Security.Cryptography")
                continue;
            string typeName = reader.GetString(type.Name), memberName = reader.GetString(member.Name);
            bool provider = typeName is "MD5CryptoServiceProvider" or "SHA1CryptoServiceProvider" or "SHA256Managed";
            bool factory = typeName is "MD5" or "SHA1" or "SHA256";
            if (provider && memberName == ".ctor" || factory && memberName == "Create")
                found.Add(typeName + "::" + memberName);
        }
        return found;
    }

    private static List<string> MethodNames(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        List<string> names = new List<string>();
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                names.Add(reader.GetString(type.Name) + "::" + reader.GetString(reader.GetMethodDefinition(methodHandle).Name));
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string Hex(object digest) => Convert.ToHexString((byte[])digest);

    [Fact]
    public void ThePatchReplacesTheThreeHashObjectsOpenMetaverseUtilsCreatesWhenItStarts()
    {
        Assert.Equal(3, HashPatcher.Patch(Library("OpenMetaverse.Types.dll"), Output("OpenMetaverse.Types.dll")));
    }

    [Fact]
    public void ThePatchedOpenMetaverseLibraryNamesNoFrameworkHashCreation()
    {
        Assert.NotEmpty(FrameworkHashCreations(Library("OpenMetaverse.Types.dll")));
        Assert.Empty(FrameworkHashCreations(Patched("OpenMetaverse.Types.dll")));
    }

    [Fact]
    public void ThePatchedOpenMetaverseUtilsStartsWithManagedHashObjectsAndHashesLikeTheFramework()
    {
        AssemblyLoadContext context = new AssemblyLoadContext("patched-openmetaverse-types", true);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(Patched("OpenMetaverse.Types.dll"));
            Type utils = assembly.GetType("OpenMetaverse.Utils", true);
            Assert.IsType<ManagedMD5>(utils.GetField("MD5Builder", AnyStatic).GetValue(null));
            Assert.IsType<ManagedSHA1>(utils.GetField("SHA1Builder", AnyStatic).GetValue(null));
            Assert.IsType<ManagedSHA256>(utils.GetField("SHA256Builder", AnyStatic).GetValue(null));
            byte[] data = "the same bytes for every digest"u8.ToArray();
            object[] arguments = { data };
            Assert.Equal(Hex(MD5.HashData(data)), Hex(utils.GetMethod("MD5", AnyStatic, new[] { typeof(byte[]) }).Invoke(null, arguments)));
            Assert.Equal(Hex(SHA1.HashData(data)), Hex(utils.GetMethod("SHA1", AnyStatic, new[] { typeof(byte[]) }).Invoke(null, arguments)));
            Assert.Equal(Hex(SHA256.HashData(data)), Hex(utils.GetMethod("SHA256", AnyStatic, new[] { typeof(byte[]) }).Invoke(null, arguments)));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void ThePatchedOpenMetaverseLibraryHasEveryMethodTheOriginalHad()
    {
        Assert.Equal(3, HashPatcher.Patch(Library("OpenMetaverse.Types.dll"), Output("OpenMetaverse.Types.dll")));
        Assert.Equal(MethodNames(Library("OpenMetaverse.Types.dll")), MethodNames(Output("OpenMetaverse.Types.dll")));
    }

    [Fact]
    public void ThePatchedOpenMetaverseLibraryKeepsItsNameAndVersion()
    {
        Assert.Equal(3, HashPatcher.Patch(Library("OpenMetaverse.Types.dll"), Output("OpenMetaverse.Types.dll")));
        AssemblyName original = AssemblyName.GetAssemblyName(Library("OpenMetaverse.Types.dll"));
        AssemblyName patched = AssemblyName.GetAssemblyName(Output("OpenMetaverse.Types.dll"));
        Assert.Equal(original.Name, patched.Name);
        Assert.Equal(original.Version, patched.Version);
    }

    [Fact]
    public void PatchingAPatchedLibraryReplacesNothingMore()
    {
        Assert.Equal(3, HashPatcher.Patch(Library("OpenMetaverse.Types.dll"), Output("once.dll")));
        Assert.Equal(0, HashPatcher.Patch(Output("once.dll"), Output("twice.dll")));
    }

    [Fact]
    public void ALibraryThatCreatesNoHashObjectsComesBackByteForByte()
    {
        Assert.Equal(3, HashPatcher.Patch(Library("OpenMetaverse.Types.dll"), Output("OpenMetaverse.Types.dll")));
        Assert.Equal(0, HashPatcher.Patch(Library("OpenMetaverse.StructuredData.dll"), Output("OpenMetaverse.StructuredData.dll")));
        Assert.Equal(File.ReadAllBytes(Library("OpenMetaverse.StructuredData.dll")), File.ReadAllBytes(Output("OpenMetaverse.StructuredData.dll")));
    }

    [Fact]
    public void TheViewerSideLibrariesHashObjectsAreReplacedToo()
    {
        Assert.NotEmpty(FrameworkHashCreations(Library("LibreMetaverse.Types.dll")));
        Assert.NotEmpty(FrameworkHashCreations(Library("LibreMetaverse.dll")));
        Assert.Equal(3, HashPatcher.Patch(Library("LibreMetaverse.Types.dll"), Output("LibreMetaverse.Types.dll")));
        Assert.Equal(1, HashPatcher.Patch(Library("LibreMetaverse.dll"), Output("LibreMetaverse.dll")));
        Assert.Empty(FrameworkHashCreations(Output("LibreMetaverse.Types.dll")));
        Assert.Empty(FrameworkHashCreations(Output("LibreMetaverse.dll")));
    }

    [Fact]
    public void ThePatchedViewerSideUtilsStartsWithManagedHashObjectsAndHashesLikeTheFramework()
    {
        AssemblyLoadContext context = new AssemblyLoadContext("patched-libremetaverse-types", true);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(Patched("LibreMetaverse.Types.dll"));
            Type utils = assembly.GetType("LibreMetaverse.Utils", true);
            Assert.IsType<ManagedMD5>(utils.GetField("MD5Builder", AnyStatic).GetValue(null));
            byte[] data = "the same bytes for every digest"u8.ToArray();
            Assert.Equal(Hex(MD5.HashData(data)), Hex(utils.GetMethod("MD5", AnyStatic, new[] { typeof(byte[]) }).Invoke(null, new object[] { data })));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void AFileThatIsNotAnAssemblyIsRefused()
    {
        File.WriteAllText(Output("readme.txt"), "not an assembly");
        Assert.ThrowsAny<Exception>(() => HashPatcher.Patch(Output("readme.txt"), Output("out.dll")));
    }
}
