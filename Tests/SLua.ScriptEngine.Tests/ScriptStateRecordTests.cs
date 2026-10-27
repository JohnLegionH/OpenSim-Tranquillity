/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.VM;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>The engine's own record of a script, written next to the runtime's blob: what it holds, and the records it does not read.</summary>
public class ScriptStateRecordTests
{
    private static ScriptStateRecord Full() => new ScriptStateRecord
    {
        Engine = "SLua",
        Language = ScriptLanguage.Lsl,
        Running = false,
        Faulted = true,
        FaultKind = 2,
        FaultMessage = "ran out of memory",
        LslState = 3,
        TimerInterval = 10.5,
        TimerLeft = 3.25,
        SluaTimer = 4.5,
        SleepLeft = 1.75,
        MinEventDelay = 2,
        NextEventIn = 0.5,
        Events = new List<SavedEvent>
        {
            new SavedEvent { EventIndex = 7, Args = new[] { Value.Int(5), Value.Num(2.5), Value.Str("text"), Value.Key("k"), Value.Vec(1, 2, 3), Value.Rot(0, 0, 0, 1), Value.List(Value.Int(1), Value.Str("x")), Value.Bool(true), Value.Nil } },
            new SavedEvent { EventIndex = 9 },
        },
        Listens = new List<ActiveListen>
        {
            new ActiveListen { Handle = 4, Channel = 12, Name = "name", Key = "key", Message = "msg", Inactive = true, RegexBitfield = 3, HostKey = "host" },
        },
        Native = new byte[] { 1, 2, 3, 250 },
    };

    [Fact]
    public void ARecordComesBackWithEveryFieldItHeld()
    {
        ScriptStateRecord r = ScriptStateRecord.Decode(Full().Encode(), "SLua", out string problem);

        Assert.Null(problem);
        Assert.NotNull(r);
        Assert.Equal("SLua", r.Engine);
        Assert.Equal(ScriptLanguage.Lsl, r.Language);
        Assert.False(r.Running);
        Assert.True(r.Faulted);
        Assert.Equal(2, r.FaultKind);
        Assert.Equal("ran out of memory", r.FaultMessage);
        Assert.Equal(3, r.LslState);
        Assert.Equal(10.5, r.TimerInterval);
        Assert.Equal(3.25, r.TimerLeft);
        Assert.Equal(4.5, r.SluaTimer);
        Assert.Equal(1.75, r.SleepLeft);
        Assert.Equal(2, r.MinEventDelay);
        Assert.Equal(0.5, r.NextEventIn);
        Assert.Equal(2, r.Events.Count);
        Assert.Equal(7, r.Events[0].EventIndex);
        Assert.Equal(Full().Events[0].Args.Select(v => v.ToString()), r.Events[0].Args.Select(v => v.ToString()));
        Assert.Equal(9, r.Events[1].EventIndex);
        Assert.Empty(r.Events[1].Args);
        ActiveListen l = Assert.Single(r.Listens);
        Assert.Equal(Full().Listens[0], l);
        Assert.Equal(new byte[] { 1, 2, 3, 250 }, r.Native);
    }

    [Fact]
    public void ARecordAnotherEngineWroteIsNotRead()
    {
        byte[] theirs = new ScriptStateRecord { Engine = "Other" }.Encode();

        Assert.Null(ScriptStateRecord.Decode(theirs, "SLua", out string problem));
        Assert.Contains("Other", problem);
        Assert.Contains("SLua", problem);
    }

    [Fact]
    public void ARecordOfAnotherLayoutIsNotRead()
    {
        byte[] bytes = Full().Encode();
        Assert.NotEmpty(bytes);
        bytes[4] = 99; // the layout number follows the four-byte mark

        Assert.Null(ScriptStateRecord.Decode(bytes, "SLua", out string problem));
        Assert.Contains("layout", problem);
    }

    [Fact]
    public void BytesThatAreNotARecordAreNotRead()
    {
        Assert.Null(ScriptStateRecord.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "SLua", out string garbage));
        Assert.False(string.IsNullOrEmpty(garbage));
        Assert.Null(ScriptStateRecord.Decode(Array.Empty<byte>(), "SLua", out string empty));
        Assert.False(string.IsNullOrEmpty(empty));
    }

    [Fact]
    public void ARecordCutShortIsNotReadAtAnyLength()
    {
        byte[] bytes = Full().Encode();
        Assert.NotEmpty(bytes);
        for (int length = 0; length < bytes.Length; length += Math.Max(1, bytes.Length / 40))
        {
            byte[] cut = bytes.Take(length).ToArray();
            Assert.Null(ScriptStateRecord.Decode(cut, "SLua", out string problem));
            Assert.False(string.IsNullOrEmpty(problem));
        }
    }
}
