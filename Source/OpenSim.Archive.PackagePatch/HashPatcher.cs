/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Mono.Cecil;
using Mono.Cecil.Cil;
using OpenSim.Archive.ManagedHash;

namespace OpenSim.Archive.PackagePatch;

/// <summary>
/// Replaces the hash objects an assembly creates from the framework's classes with the managed ones of
/// OpenSim.Archive.ManagedHash. The framework's MD5, SHA1 and SHA256 call the system's OpenSSL on Linux, and the
/// OpenMetaverse libraries create theirs when their Utils class starts, so a program that merely uses a UUID aborts on a
/// system without libssl.
/// </summary>
public static class HashPatcher
{
    private const string FrameworkNamespace = "System.Security.Cryptography";

    // The two spellings of "make a hash object" the libraries use, each with the base class the replacement derives from and
    // the factory that gives it: "new MD5CryptoServiceProvider()" (the OpenMetaverse libraries) and "MD5.Create()" (the
    // viewer-side libraries).
    private static readonly Dictionary<string, (string Base, string Factory)> Constructed = new()
    {
        ["MD5CryptoServiceProvider"] = ("MD5", nameof(ManagedHashes.CreateMd5)),
        ["SHA1CryptoServiceProvider"] = ("SHA1", nameof(ManagedHashes.CreateSha1)),
        ["SHA256Managed"] = ("SHA256", nameof(ManagedHashes.CreateSha256)),
    };

    private static readonly Dictionary<string, (string Base, string Factory)> Created = new()
    {
        ["MD5"] = ("MD5", nameof(ManagedHashes.CreateMd5)),
        ["SHA1"] = ("SHA1", nameof(ManagedHashes.CreateSha1)),
        ["SHA256"] = ("SHA256", nameof(ManagedHashes.CreateSha256)),
    };

    /// <summary>
    /// Copies <paramref name="inputPath"/> to <paramref name="outputPath"/> with its hash objects replaced and returns how many
    /// call sites it replaced. An assembly with none comes back byte for byte. A file that is not an assembly throws.
    /// </summary>
    public static int Patch(string inputPath, string outputPath)
    {
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters { ReadingMode = ReadingMode.Immediate });
        ModuleDefinition module = assembly.MainModule;
        AssemblyNameReference managed = new AssemblyNameReference(typeof(ManagedHashes).Assembly.GetName().Name, typeof(ManagedHashes).Assembly.GetName().Version);
        module.AssemblyReferences.Add(managed);
        TypeReference hashes = new TypeReference(typeof(ManagedHashes).Namespace, nameof(ManagedHashes), module, managed);
        int replaced = 0;
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
            {
                foreach (Instruction instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference target || target.DeclaringType.Namespace != FrameworkNamespace || target.Parameters.Count != 0)
                        continue;
                    if (!TryFind(instruction.OpCode, target, out (string Base, string Factory) creation))
                        continue;
                    TypeReference result = new TypeReference(FrameworkNamespace, creation.Base, module, target.DeclaringType.Scope);
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = new MethodReference(creation.Factory, result, hashes);
                    replaced++;
                }
            }
        }
        if (replaced == 0)
        {
            File.Copy(inputPath, outputPath, true);
            return 0;
        }
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (FieldDefinition field in type.Fields)
            {
                // a field declared with the concrete class would no longer fit the managed object stored in it
                if (field.FieldType.Namespace == FrameworkNamespace && Constructed.TryGetValue(field.FieldType.Name, out (string Base, string Factory) declared))
                    field.FieldType = new TypeReference(FrameworkNamespace, declared.Base, module, field.FieldType.Scope);
            }
        }
        assembly.Write(outputPath);
        return replaced;
    }

    private static bool TryFind(OpCode opCode, MethodReference target, out (string Base, string Factory) creation)
    {
        creation = default;
        if (opCode == OpCodes.Newobj && target.Name == ".ctor")
            return Constructed.TryGetValue(target.DeclaringType.Name, out creation);
        if (opCode == OpCodes.Call && target.Name == "Create")
            return Created.TryGetValue(target.DeclaringType.Name, out creation);
        return false;
    }
}
