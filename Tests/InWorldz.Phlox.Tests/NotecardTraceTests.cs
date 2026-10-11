/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The notecard trace ([InWorldz.Phlox] NotecardTrace, off by default): one INFO line per step of a notecard request,
/// each carrying the query id - the request, a fetch's start and end, the post, each receiving script's queued or
/// dropped (with the rule) or cleared, and the handler start - and the script's own requests kept until delivered or
/// lost, shown in its status output. The log is captured through LoggerProvider, which is process-wide, hence the
/// "phlox-state" collection. No network; every wait is for the result.
/// </summary>
[Collection("phlox-state")]
public class NotecardTraceTests : IDisposable
{
    private readonly Capture m_log = new();
    private readonly ILoggerFactory m_previous;

    private readonly ITestOutputHelper _out;

    public NotecardTraceTests(ITestOutputHelper o)
    {
        _out = o;
        m_previous = LoggerProvider.LoggerFactory;
        LoggerProvider.LoggerFactory = m_log;
    }

    public void Dispose() => LoggerProvider.LoggerFactory = m_previous;

    private sealed class Capture : ILoggerFactory, ILoggerProvider
    {
        private readonly List<string> m_lines = new();
        public string[] Lines { get { lock (m_lines) return m_lines.ToArray(); } }
        public ILogger CreateLogger(string category) => new L(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class L : ILogger
        {
            private readonly Capture m_c;
            public L(Capture c) => m_c = c;
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            { lock (m_c.m_lines) m_c.m_lines.Add(formatter(state, exception)); }
        }
    }

    private const string Reader = "default { dataserver(key q, string d) { llSay(0, \"ds [\" + d + \"]\"); } }";

    /// <summary>Answers a dataserver event with a line; "nap" puts it to sleep for good.</summary>
    private const string Sleeper = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); }
            listen(integer c, string n, key k, string m) { if (m == ""nap"") { llSay(0, ""asleep""); llSleep(100000.0); } }
            dataserver(key q, string d) { llSay(0, ""ds ["" + d + ""]""); }
        }";

    private static InventoryGivesRig Rig(string source, bool trace = true)
        => new(source, cfg => { if (trace) cfg.Configs["InWorldz.Phlox"].Set("NotecardTrace", "true"); });

    private string[] TraceLines => m_log.Lines.Where(l => l.Contains(PhloxNotecardTrace.Tag)).ToArray();

    private string[] For(string q) => TraceLines.Where(l => l.Contains("q=" + q + " ")).ToArray();

    /// <summary>The step each of a query's lines is: its first word, or two for a fetch's start and end.</summary>
    private string[] Steps(string q) => For(q).Select(l =>
    {
        string[] words = l[(l.IndexOf("q=" + q + " ", StringComparison.Ordinal) + 2 + q.Length + 1)..].Split(' ');
        return words[0] == "fetch" ? words[0] + " " + words[1] : words[0];
    }).ToArray();

    private static string Ask(InventoryGivesRig r, Func<LSLSystemAPI, object> call)
        => (string)r.Accounted(call).Ret;

    /// <summary>Ask, then pump until the script said what it got.</summary>
    private static string Read(InventoryGivesRig r, Func<LSLSystemAPI, object> call)
    {
        r.H.ClearSaid(r.Item);
        string q = Ask(r, call);
        r.WaitSaid("ds [");
        return q;
    }

    private static TaskInventoryItem AddCard(InventoryGivesRig r, string name = "card", string text = "line zero\nline one")
        => InventoryGivesRig.AddNotecard(r.H, r.H.Prim, name, text);

    private void Say(InventoryGivesRig r, string msg)
        => r.H.Scene.SimChat(msg, ChatTypeEnum.Region, 7, r.H.Prim.AbsolutePosition, "tester", UUID.Random(), false);

    private static void ExpectLine(string[] lines, string step, string contains)
        => Assert.True(lines.Any(l => l.Contains(" " + step) && l.Contains(contains)),
            "no '" + step + "' line with '" + contains + "' in:\n" + string.Join("\n", lines));

    private static PhloxExecutionScheduler Exe(InventoryGivesRig r) => SavedStateRig.Exe(r.H);

    // ── the steps, in order ──────────────────────────────────────────────

    [Fact]
    public void AFetchedLineAndACachedLineLogEveryStepInOrder()
    {
        using var r = Rig(Reader);
        TaskInventoryItem card = AddCard(r);
        string script = "script=\"" + r.Self.Name + "\" (" + r.Item + ")";

        string fetched = Read(r, a => a.llGetNotecardLine("card", 0));
        Assert.Equal(new[] { "request", "fetch start", "fetch end", "post", "queued", "deliver" }, Steps(fetched));
        string[] f = For(fetched);
        Assert.Contains(script, f[0]);
        Assert.Contains("prim=\"" + r.H.Prim.Name + "\" (" + r.H.Prim.LocalId + ")", f[0]);
        Assert.Contains("notecard=\"card\" asset=" + card.AssetID + " line 0 source=fetch", f[0]);
        Assert.Matches(@"fetch end took=\d+ ms result=lines=2$", f[2]);
        Assert.Contains("goes to 1 script(s) of the prim", f[3]);
        Assert.Contains(script + " depth=0", f[4]);
        Assert.Matches(@"deliver " + Regex.Escape(script) + @" handler starts \d+ ms after the request$", f[5]);

        string cached = Read(r, a => a.llGetNotecardLine("card", 1));
        Assert.Equal(new[] { "request", "post", "queued", "deliver" }, Steps(cached));
        Assert.Contains("line 1 source=cache", For(cached)[0]);

        _out.WriteLine("fetched line:\n" + string.Join("\n", f) + "\ncached line:\n" + string.Join("\n", For(cached)));
    }

    [Fact]
    public void ACountRequestSaysCountAndASegmentSaysItsOffsetAndLength()
    {
        using var r = Rig(Reader);
        AddCard(r);
        string count = Read(r, a => a.llGetNumberOfNotecardLines("card"));
        Assert.Contains("request llGetNumberOfNotecardLines", For(count)[0]);
        Assert.Contains(" count source=", For(count)[0]);
        string segment = Read(r, a => a.iwGetNotecardSegment("card", 1, 2, 5));
        Assert.Contains("request iwGetNotecardSegment", For(segment)[0]);
        Assert.Contains("line 1 offset 2 length 5 source=cache", For(segment)[0]);
    }

    // ── losses ────────────────────────────────────────────────────────────

    [Fact]
    public void AFetchThatGivesNullIsLostWithItsResult()
    {
        using var r = Rig(Reader);
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = "ghost", AssetID = UUID.Random(), ItemID = UUID.Random(),
            Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard
        }, true);
        string q = Ask(r, a => a.llGetNotecardLine("ghost", 0));
        Assert.True(r.H.PumpUntil(() => For(q).Any(l => l.Contains(" lost "))), string.Join("\n", For(q)));

        Assert.Equal(new[] { "request", "fetch start", "fetch end", "lost" }, Steps(q));
        Assert.Matches(@"fetch end took=\d+ ms result=null$", For(q)[2]);
        Assert.Contains("no answer will be posted: fetch result null", For(q)[3]);
        string[] status = r.H.Engine.NotecardTraceStatusLines(r.Item);
        Assert.Contains("0 request(s) waiting for an answer, 1 lost", status[0]);
    }

    [Fact]
    public void AnAssetThatIsNotANotecardIsLostWithItsType()
    {
        using var r = Rig(Reader);
        UUID asset = UUID.Random();
        r.H.Scene.AssetService.Store(AssetHelpers.CreateAsset(asset, AssetType.Texture, new byte[] { 1, 2, 3 }, UUID.Zero));
        r.H.Prim.Inventory.AddInventoryItem(new TaskInventoryItem
        {
            Name = "pic", AssetID = asset, ItemID = UUID.Random(), Type = (int)AssetType.Notecard, InvType = (int)InventoryType.Notecard
        }, true);
        string q = Ask(r, a => a.llGetNotecardLine("pic", 0));
        Assert.True(r.H.PumpUntil(() => For(q).Any(l => l.Contains(" lost "))), string.Join("\n", For(q)));
        Assert.Matches(@"fetch end took=\d+ ms result=wrong type \(", For(q)[2]);
    }

    [Fact]
    public void ARequestForANotecardThatIsNotThereIsRefusedAndCounted()
    {
        using var r = Rig(Reader);
        string q = Ask(r, a => a.llGetNotecardLine("nothing", 3));
        Assert.Equal(UUID.Zero.ToString(), q);
        string line = TraceLines.Single(l => l.Contains("q=none request llGetNotecardLine"));
        Assert.Contains("notecard=\"nothing\" line 3 refused", line);
        Assert.Contains("1 lost", r.H.Engine.NotecardTraceStatusLines(r.Item)[0]);
    }

    [Fact]
    public void AnAnswerForAFullQueueIsDroppedWithTheQueueFullRule()
    {
        using var r = Rig(Sleeper);
        AddCard(r);
        Say(r, "nap");
        Assert.True(r.H.PumpUntil(() => r.H.RunStateOf(r.Item) == "Sleeping"));
        for (int i = 0; i < 64; i++) Assert.True(r.H.Engine.PostScriptEvent(r.Item, "dataserver", new object[] { "x", "y" }));
        Assert.True(r.H.PumpUntil(() => Exe(r).GetStatus(r.Item).QueuedEvents == 64));

        string q = Ask(r, a => a.llGetNotecardLine("card", 0));
        Assert.True(r.H.PumpUntil(() => For(q).Any(l => l.Contains(" dropped "))), string.Join("\n", For(q)));
        Assert.Equal(new[] { "request", "fetch start", "fetch end", "post", "dropped" }, Steps(q));
        Assert.Contains("reason=queue full (64 events)", For(q)[4]);
        Assert.Contains("0 request(s) waiting for an answer, 1 lost", r.H.Engine.NotecardTraceStatusLines(r.Item)[0]);
    }

    [Fact]
    public void AnotherScriptsAnswerForAQueueAtHalfItsLimitIsDroppedAndTheAskerStillGetsIt()
    {
        using var r = Rig(Reader);
        AddCard(r);
        UUID sleeper = r.H.RezScript(Sleeper);
        r.WaitLoaded(sleeper);
        Say(r, "nap");
        Assert.True(r.H.PumpUntil(() => r.H.RunStateOf(sleeper) == "Sleeping"));
        for (int i = 0; i < 40; i++) Assert.True(r.H.Engine.PostScriptEvent(sleeper, "dataserver", new object[] { "x", "y" }));
        Assert.True(r.H.PumpUntil(() => Exe(r).GetStatus(sleeper).QueuedEvents == 40));

        string q = Read(r, a => a.llGetNotecardLine("card", 0));
        string sleeperName = r.H.Prim.Inventory.GetInventoryItem(sleeper).Name;
        ExpectLine(For(q), "dropped", "script=\"" + sleeperName + "\" (" + sleeper + ") reason=half limit (another script's answer");
        ExpectLine(For(q), "deliver", "script=\"" + r.Self.Name + "\" (" + r.Item + ")");
        Assert.Contains("0 request(s) waiting for an answer, 0 lost", r.H.Engine.NotecardTraceStatusLines(r.Item)[0]);
    }

    [Fact]
    public void AnAnswerForAScriptWithNoDataserverHandlerIsDropped()
    {
        using var r = Rig("default { state_entry() { } }");
        AddCard(r);
        string q = Ask(r, a => a.llGetNotecardLine("card", 0));
        Assert.True(r.H.PumpUntil(() => For(q).Any(l => l.Contains(" dropped "))), string.Join("\n", For(q)));
        ExpectLine(For(q), "dropped", "reason=no dataserver handler in the script's current state");
        Assert.Contains("1 lost", r.H.Engine.NotecardTraceStatusLines(r.Item)[0]);
    }

    [Fact]
    public void AStoppedScriptsAnswerIsDroppedAsDisabled()
    {
        using var r = Rig(Reader);
        AddCard(r);
        r.H.Scene.EventManager.TriggerStopScript(r.H.Prim.LocalId, r.Item);
        Assert.True(r.H.PumpUntil(() => !Exe(r).GetStatus(r.Item).Enabled));
        string q = Ask(r, a => a.llGetNotecardLine("card", 0));
        Assert.True(r.H.PumpUntil(() => For(q).Any(l => l.Contains(" dropped "))), string.Join("\n", For(q)));
        ExpectLine(For(q), "dropped", "reason=script disabled");
    }

    [Fact]
    public void AStateChangeClearsTheRequestAndTheAnswerAndSaysSo()
    {
        const string src = @"
            default {
                state_entry() { llListen(7, """", NULL_KEY, """"); }
                listen(integer c, string n, key k, string m) {
                    if (m == ""warm"") llGetNotecardLine(""card"", 0);
                    else if (m == ""go"") { llGetNotecardLine(""card"", 0); state two; }
                }
                dataserver(key q, string d) { llSay(0, ""ds ["" + d + ""]""); }
            }
            state two { state_entry() { llSay(0, ""two""); } dataserver(key q, string d) { llSay(0, ""two ds""); } }";
        using var r = Rig(src);
        AddCard(r);
        Say(r, "warm");
        r.WaitSaid("ds [");   // the card is cached now

        r.H.ClearSaid(r.Item);
        Say(r, "go");
        r.WaitSaid("two");
        string q = TraceLines.Last(l => l.Contains(" request llGetNotecardLine")).Split(' ')[1]["q=".Length..];
        r.H.PumpUntilIdle(TimeSpan.FromSeconds(10));

        ExpectLine(For(q), "lost", "request cleared by state change");
        ExpectLine(For(q), "dropped", "reason=cleared by state change");
        Assert.DoesNotContain("two ds", r.H.Said);
        Assert.Contains("0 request(s) waiting for an answer, 1 lost", r.H.Engine.NotecardTraceStatusLines(r.Item)[0]);
    }

    [Fact]
    public void AResetClearsTheRequestAndTheAnswerAndSaysSo()
    {
        using var r = Rig(Reader);
        AddCard(r);
        Read(r, a => a.llGetNotecardLine("card", 0));   // cached from here on
        string q = Ask(r, a => a.llGetNotecardLine("card", 1));   // answer posted, not yet taken in
        Assert.True(Exe(r).ResetNow(r.Item));

        ExpectLine(For(q), "lost", "request cleared by reset");
        ExpectLine(For(q), "dropped", "reason=cleared by reset (posted, not yet in the script's queue)");
    }

    // ── what the script is waiting on ─────────────────────────────────────

    [Fact]
    public void TheStatusShowsAWaitingRequestAndClearsItOnDelivery()
    {
        using var r = Rig(Reader);
        AddCard(r);
        Read(r, a => a.llGetNotecardLine("card", 0));   // cached from here on
        Assert.Contains("0 request(s) waiting for an answer, 0 lost", r.H.Engine.NotecardTraceStatusLines(r.Item)[0]);

        string q = Ask(r, a => a.llGetNotecardLine("card", 1));   // answer posted, not delivered: no pump yet
        string[] waiting = r.H.Engine.NotecardTraceStatusLines(r.Item);
        Assert.Contains("1 request(s) waiting for an answer, 0 lost", waiting[0]);
        Assert.Matches(@"oldest: \d+ ms, notecard ""card"", line 1, query " + q + "$", waiting[1]);

        Assert.True(r.H.PumpUntil(() => r.H.Said.Any(s => s.StartsWith("ds ["))));
        string[] delivered = r.H.Engine.NotecardTraceStatusLines(r.Item);
        Assert.Contains("0 request(s) waiting for an answer, 0 lost", delivered[0]);
        Assert.Single(delivered);
    }

    // ── a region stop and a restore ───────────────────────────────────────

    [Fact]
    public void ARegionStopLogsTheRequestsStillWaitingAndTheAnswerNotYetQueued()
    {
        using var r = Rig(Reader);
        AddCard(r);
        Read(r, a => a.llGetNotecardLine("card", 0));   // cached from here on
        string q = Ask(r, a => a.llGetNotecardLine("card", 1));   // answer posted, not taken in

        r.H.StopRegionAsTheSimulatorDoes();

        ExpectLine(For(q), "still", "waiting at region stop script=\"" + r.Self.Name + "\" (" + r.Item + ") notecard=\"card\" line 1");
        ExpectLine(For(q), "script=", "answer still in the scheduler's incoming queue at the stop: not saved");
    }

    [Fact]
    public void ARestoredScriptSaysItsStateAndThatTheSavedStateHoldsNoRequest()
    {
        const string src = "default { state_entry() { llSay(0, \"up\"); } }";
        UUID asset = UUID.Random(), item = UUID.Random();
        using (var h1 = new SchedulerHarness())
        {
            SavedStateRig.Rez(h1, h1.Prim, src, asset, item, 0, true, 1);
            Assert.True(h1.PumpUntil(() => h1.Said.Contains("up")));
            h1.SaveState(item);
        }
        using var h2 = new SchedulerHarness(cfg => cfg.Configs["InWorldz.Phlox"].Set("NotecardTrace", "true"));
        h2.RezScript(src, asset, item);
        Assert.True(h2.PumpUntil(() => TraceLines.Any(l => l.Contains(" restored "))), string.Join("\n", TraceLines));
        string line = TraceLines.Single(l => l.Contains(" restored "));
        Assert.True(Regex.IsMatch(line, Regex.Escape("(" + item + ") state=") + @"\w+ queued=0 events; waiting on 0 request\(s\)"), line);
        Assert.True(line.Contains("not part of the saved state"), line);
    }

    // ── off ───────────────────────────────────────────────────────────────

    [Fact]
    public void WithTheTraceOffNothingIsTrackedLoggedOrShownInTheStatus()
    {
        using var r = Rig(Reader, trace: false);
        AddCard(r);
        Assert.Null(r.H.Engine.NotecardTrace);

        Read(r, a => a.llGetNotecardLine("card", 0));   // fetched
        Read(r, a => a.llGetNotecardLine("card", 1));   // cached
        string refused = Ask(r, a => a.llGetNotecardLine("nothing", 0));

        Assert.Equal(UUID.Zero.ToString(), refused);
        Assert.Empty(TraceLines);
        Assert.DoesNotContain(m_log.Lines, l => l.Contains("NotecardTrace"));
        Assert.Empty(r.H.Engine.NotecardTraceStatusLines(r.Item));
        Assert.Null(r.H.Engine.NotecardTrace);
    }
}
