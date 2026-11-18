/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>A scratch folder that exists for one test; it removes only the folder it made.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slua-test-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a test that leaves a file open is not made to fail here */ }
    }
}

/// <summary>The compile cache, with the native call replaced by a counter. No process-wide state; runs in parallel.</summary>
public class CompileCacheTests
{
    private const string Stamp = "runtime=abc123 api=3 fns=0000";
    private const string Source = "default\n{\n    state_entry()\n    {\n    }\n}\n";

    private sealed class Counter
    {
        public int Calls;
        public NativeCompile Native => (text, lsl) => { Interlocked.Increment(ref Calls); return new byte[] { 9, 8, 7, (byte)text.Length }; };
    }

    private static ScriptCompiler CompilerOver(string folder, string stamp, Counter counter, IEngineLog log = null)
        => new ScriptCompiler(null, counter.Native, log, new CompileCache(folder, stamp, log));

    [Fact]
    public void ASecondLoadOfTheSameSourceCompilesNothing()
    {
        using var dir = new TempFolder();
        var counter = new Counter();
        using var compiler = CompilerOver(dir.Path, Stamp, counter);

        CompileResult first = compiler.Compile(Source);
        CompileResult second = compiler.Compile(Source);

        Assert.True(first.Success);
        Assert.False(first.FromCache);
        Assert.True(second.Success);
        Assert.True(second.FromCache);
        Assert.Equal(first.Asset, second.Asset);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void TheCacheSurvivesANewCompilerAndANewCacheObjectOverTheSameFolder()
    {
        using var dir = new TempFolder();
        var counter = new Counter();
        using (var first = CompilerOver(dir.Path, Stamp, counter))
            Assert.True(first.Compile(Source).Success);
        using var again = CompilerOver(dir.Path, Stamp, counter); // as after a region restart

        CompileResult r = again.Compile(Source);

        Assert.True(r.FromCache);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void AChangedStampIgnoresTheOldEntryAndDoesNotDeleteIt()
    {
        using var dir = new TempFolder();
        var counter = new Counter();
        using (var old = CompilerOver(dir.Path, Stamp, counter))
            old.Compile(Source);
        string[] before = Directory.GetFiles(dir.Path);

        using var changed = CompilerOver(dir.Path, "runtime=def456 api=3 fns=0000", counter);
        CompileResult r = changed.Compile(Source);

        Assert.False(r.FromCache);
        Assert.Equal(2, counter.Calls);
        foreach (string file in before)
            Assert.True(File.Exists(file), "an entry of the old stamp was removed");
        Assert.Equal(before.Length + 1, Directory.GetFiles(dir.Path).Length);

        // The old stamp, back again, finds its entry.
        using var back = CompilerOver(dir.Path, Stamp, counter);
        Assert.True(back.Compile(Source).FromCache);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public void ADifferentFunctionListHashChangesTheStamp()
    {
        string a = CompileCache.MakeStamp("runtime=abc api=3", "1111");
        string b = CompileCache.MakeStamp("runtime=abc api=3", "2222");
        string c = CompileCache.MakeStamp("runtime=abd api=3", "1111");
        Assert.Contains("runtime=abc api=3", a);
        Assert.Contains("1111", a);
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
    }

    [Fact]
    public void ADifferentSourceOrLanguageIsADifferentEntry()
    {
        using var dir = new TempFolder();
        var cache = new CompileCache(dir.Path, Stamp);
        string lsl = cache.PathFor("x = 1", ScriptLanguage.Lsl);
        Assert.NotEqual(lsl, cache.PathFor("x = 1", ScriptLanguage.Slua));
        Assert.NotEqual(lsl, cache.PathFor("x = 2", ScriptLanguage.Lsl));
        Assert.Equal(lsl, cache.PathFor("x = 1", ScriptLanguage.Lsl));
        Assert.Equal(dir.Path, Path.GetDirectoryName(lsl));
    }

    [Fact]
    public void AFailedCompileIsNotCached()
    {
        using var dir = new TempFolder();
        int calls = 0;
        using var compiler = new ScriptCompiler(null, (t, l) => { calls++; throw new Bindings.SluaException("slua_compile", -5, "2: bad"); }, null, new CompileCache(dir.Path, Stamp));

        Assert.False(compiler.Compile(Source).Success);
        Assert.False(compiler.Compile(Source).Success);

        Assert.Equal(2, calls);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void AnEntryThatIsDamagedIsAMissAndIsWrittenAgain()
    {
        using var dir = new TempFolder();
        var counter = new Counter();
        var cache = new CompileCache(dir.Path, Stamp);
        using (var first = new ScriptCompiler(null, counter.Native, null, cache))
            first.Compile(Source);
        string file = Assert.Single(Directory.GetFiles(dir.Path));
        byte[] bytes = File.ReadAllBytes(file);
        bytes[^3] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        using var second = new ScriptCompiler(null, counter.Native, null, cache);
        CompileResult r = second.Compile(Source);

        Assert.True(r.Success);
        Assert.False(r.FromCache);
        Assert.Equal(2, counter.Calls);
        Assert.True(second.Compile(Source).FromCache); // the rewritten entry is good
    }

    [Fact]
    public void AnEntryCutShortByACrashIsAMiss()
    {
        using var dir = new TempFolder();
        var counter = new Counter();
        var cache = new CompileCache(dir.Path, Stamp);
        using (var first = new ScriptCompiler(null, counter.Native, null, cache))
            first.Compile(Source);
        string file = Assert.Single(Directory.GetFiles(dir.Path));
        byte[] bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);

        using var second = new ScriptCompiler(null, counter.Native, null, cache);
        Assert.False(second.Compile(Source).FromCache);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenStillCompilesAndSaysSoOnce()
    {
        using var dir = new TempFolder();
        string notAFolder = Path.Combine(dir.Path, "file");
        File.WriteAllText(notAFolder, "this is a file, not a folder");
        var log = new RecordingLog();
        var counter = new Counter();
        using var compiler = CompilerOver(notAFolder, Stamp, counter, log);

        Assert.True(compiler.Compile(Source).Success);
        Assert.True(compiler.Compile(Source + "// two\n").Success);

        Assert.Equal(2, counter.Calls);
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void TheCacheFolderIsInsideTheEnginesOwnFolder()
    {
        var settings = SLuaSettings.Read(new IniConfigSource(), new RecordingLog());
        string folder = CompileCache.DirectoryFor(settings);
        Assert.Equal(Path.Combine("ScriptEngines", EngineIdentity.EngineName, "cache"), folder);

        var source = new IniConfigSource();
        source.AddConfig(EngineIdentity.ConfigSection).Set("ScriptEnginesPath", "data/engines");
        Assert.Equal(Path.Combine("data/engines", EngineIdentity.EngineName, "cache"), CompileCache.DirectoryFor(SLuaSettings.Read(source, new RecordingLog())));
    }

    [Fact]
    public void TheCacheDirectoryIsMadeWhenItIsFirstWritten()
    {
        using var dir = new TempFolder();
        string nested = Path.Combine(dir.Path, "a", "b", "cache");
        var counter = new Counter();
        using var compiler = CompilerOver(nested, Stamp, counter);

        Assert.True(compiler.Compile(Source).Success);

        Assert.Single(Directory.GetFiles(nested));
    }
}
