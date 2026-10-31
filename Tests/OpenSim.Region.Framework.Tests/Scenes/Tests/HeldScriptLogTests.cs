/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests;

/// <summary>
/// The editor's Save of a script (SceneObjectPartInventory.CreateScriptInstanceEr) waits for the engine's compile reply.
/// While the console switch "debug scene set scripting false" is in effect the script is held back and no engine is
/// asked, so no reply can come: the Save must answer at once and the log must say, at information level, that the
/// script is held until scripting is turned on, not log an error about a reply that timed out. A script held back
/// still starts when scripting is turned on. The tests swap the process-wide logger factory, so they share the
/// "process-logger" collection with the other tests that do.
/// </summary>
[Collection("process-logger")]
public class HeldScriptLogTests
{
    // The wait in CreateScriptInstanceEr is 15 s; a held script must not get near it.
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GenerousCap = TimeSpan.FromSeconds(60);

    private sealed class Capture : ILoggerFactory
    {
        public readonly List<(LogLevel Level, string Message)> Lines = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        public List<(LogLevel Level, string Message)> Snapshot()
        {
            lock (Lines)
                return Lines.ToList();
        }

        private sealed class Logger : ILogger
        {
            private readonly Capture m_capture;
            public Logger(Capture capture) { m_capture = capture; }
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (m_capture.Lines)
                    m_capture.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class Fixture
    {
        public TestScene Scene;
        public ConsoleScriptingSwitchTests.RecordingEngine Engine = new();
        public SceneObjectGroup Box;
        public TaskInventoryItem Script;

        public Fixture()
        {
            Scene = new SceneHelpers().SetupScene();
            SceneHelpers.SetupSceneModules(Scene, new IniConfigSource(), Engine);
            Box = SceneHelpers.AddSceneObject(Scene, 1, TestHelpers.ParseTail(1), "box", 0x10);
            Script = TaskInventoryHelpers.AddScript(Scene.AssetService, Box.RootPart, "script", "default { state_entry() { } }");
        }
    }

    /// <summary>Runs the Save on its own thread, as the caps thread does, and reports whether it answered in time.</summary>
    private static (bool Answered, ArrayList Errors, Capture Log) Save(Fixture f, TimeSpan wait)
    {
        var capture = new Capture();
        ILoggerFactory saved = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = capture;
        try
        {
            Task<ArrayList> save = Task.Run(() =>
                f.Box.RootPart.Inventory.CreateScriptInstanceEr(f.Script.ItemID, 0, false, string.Empty, 1));
            bool answered = save.Wait(wait);
            // Without the fix the answer comes after 15 s; wait for it so the log is complete before it is read.
            if (!answered)
                save.Wait(GenerousCap);
            return (answered, save.IsCompletedSuccessfully ? save.Result : null, capture);
        }
        finally { LoggerProvider.LoggerFactory = saved; }
    }

    [Fact]
    public void ASaveWhileScriptingIsOffAnswersAtOnce()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.ScriptsEnabled = false;

        var (answered, errors, _) = Save(f, Promptly);

        Assert.True(answered, "the Save waited for a compile reply that cannot come");
        Assert.NotNull(errors);
        Assert.Empty(f.Engine.Started);
    }

    [Fact]
    public void ASaveWhileScriptingIsOffTellsTheEditorTheScriptIsHeld()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.ScriptsEnabled = false;

        var (_, errors, _) = Save(f, Promptly);

        string message = Assert.IsType<string>(Assert.Single(errors));
        Assert.Contains("held until scripting is turned on", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveWhileScriptingIsOffLogsOneInformationLineAndNoError()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.ScriptsEnabled = false;

        var (_, _, log) = Save(f, Promptly);

        var about = log.Snapshot().Where(l => l.Message.Contains(f.Script.ItemID.ToString(), StringComparison.Ordinal)).ToList();
        var line = Assert.Single(about);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("held until scripting is turned on", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Snapshot(), l => l.Level >= LogLevel.Warning
            && l.Message.Contains("timedout waiting for script", StringComparison.Ordinal));
    }

    [Fact]
    public void AScriptSavedWhileScriptingIsOffStartsWhenScriptingIsTurnedOn()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.ScriptsEnabled = false;
        Save(f, Promptly);
        Assert.Empty(f.Engine.Started);

        f.Scene.ScriptsEnabled = true;

        Assert.Equal(new[] { f.Script.ItemID }, f.Engine.Started);
    }

    [Fact]
    public void ASaveWithScriptingOnStillStartsTheScriptAndLogsNothingAboutAHold()
    {
        TestHelpers.InMethod();
        var f = new Fixture();

        var (answered, errors, log) = Save(f, GenerousCap);

        Assert.True(answered);
        Assert.Empty(errors);
        Assert.Equal(new[] { f.Script.ItemID }, f.Engine.Started);
        Assert.DoesNotContain(log.Snapshot(), l => l.Message.Contains("held until", StringComparison.Ordinal));
    }

    [Fact]
    public void ASaveWhenTheRegionSettingDisablesScriptsAnswersAtOnceAndLogsNoError()
    {
        TestHelpers.InMethod();
        var f = new Fixture();
        f.Scene.RegionInfo.RegionSettings.DisableScripts = true;

        var (answered, errors, log) = Save(f, Promptly);

        Assert.True(answered, "the Save waited for a compile reply that cannot come");
        Assert.Single(errors);
        Assert.Empty(f.Engine.Started);
        Assert.DoesNotContain(log.Snapshot(), l => l.Level >= LogLevel.Warning
            && l.Message.Contains("timedout waiting for script", StringComparison.Ordinal));
    }
}
