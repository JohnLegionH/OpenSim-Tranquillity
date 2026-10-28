/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using SLua.ScriptEngine.Bindings;
using Xunit;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// What Second Life keeps of a script beyond its variables travels in the record: the detected objects of the events that wait in its
/// queue, its repeating sensor and taken controls, and the permission record it holds.
/// </summary>
public class ScriptStateRecordExtrasTests
{
    private static ScriptStateRecord WithExtras() => new ScriptStateRecord
    {
        Engine = "SLua",
        Misc = new List<SavedMisc>
        {
            new SavedMisc { Key = 3, Values = new object[] { "name", "00000000-0000-0000-0000-000000000009", 1, 20.5f, 3.14f, 5.0f } },
            new SavedMisc { Key = 5, Values = new object[] { 1, 1, 0 } },
            new SavedMisc { Key = 7, Values = Array.Empty<object>() },
        },
        PermsGranter = "00000000-0000-0000-0000-000000000001",
        PermsMask = 20,
        PermsOwner = "00000000-0000-0000-0000-000000000002",
        PermsExperience = "00000000-0000-0000-0000-000000000003",
        PermsUnverified = true,
        Events = new List<SavedEvent>
        {
            new SavedEvent
            {
                EventIndex = 4,
                Args = new[] { Value.Int(1) },
                Detected = new[]
                {
                    new DetectedRecord
                    {
                        Key = "00000000-0000-0000-0000-0000000000a1", Name = "Test User", Owner = "00000000-0000-0000-0000-0000000000a2",
                        Group = "00000000-0000-0000-0000-0000000000a3", Position = new Vector3(1, 2, 3), Rotation = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f),
                        Velocity = new Vector3(4, 5, 6), LinkNumber = 2, Type = 3, OffsetPos = new Vector3(7, 8, 9), TouchFace = 4,
                        TouchUV = new Vector3(0.25f, 0.75f, 0), TouchST = new Vector3(0.5f, 0.5f, 0), TouchNormal = new Vector3(0, 0, 1),
                        TouchBinormal = new Vector3(1, 0, 0), TouchPos = new Vector3(10, 11, 12), Damage = 2.5f, DamageType = 6, OriginalDamage = 3.5f,
                    },
                },
            },
            new SavedEvent { EventIndex = 8 },
        },
    };

    [Fact]
    public void TheAttributesAndThePermissionRecordComeBackWithTheRecord()
    {
        ScriptStateRecord r = ScriptStateRecord.Decode(WithExtras().Encode(), "SLua", out string problem);

        Assert.Null(problem);
        Assert.NotNull(r);
        Assert.Equal(3, r.Misc.Count);
        Assert.Equal(3, r.Misc[0].Key);
        Assert.Equal(new object[] { "name", "00000000-0000-0000-0000-000000000009", 1, 20.5f, 3.14f, 5.0f }, r.Misc[0].Values);
        Assert.Equal(new object[] { 1, 1, 0 }, r.Misc[1].Values);
        Assert.Empty(r.Misc[2].Values);
        Assert.Equal("00000000-0000-0000-0000-000000000001", r.PermsGranter);
        Assert.Equal(20, r.PermsMask);
        Assert.Equal("00000000-0000-0000-0000-000000000002", r.PermsOwner);
        Assert.Equal("00000000-0000-0000-0000-000000000003", r.PermsExperience);
        Assert.True(r.PermsUnverified);
    }

    [Fact]
    public void TheDetectedObjectsOfAQueuedEventComeBackWithTheEvent()
    {
        ScriptStateRecord r = ScriptStateRecord.Decode(WithExtras().Encode(), "SLua", out _);

        Assert.NotNull(r);
        Assert.NotNull(r.Events[0].Detected);
        DetectedRecord d = Assert.Single(r.Events[0].Detected);
        DetectedRecord want = WithExtras().Events[0].Detected[0];
        Assert.Equal(want.Key, d.Key);
        Assert.Equal("Test User", d.Name);
        Assert.Equal(want.Owner, d.Owner);
        Assert.Equal(want.Group, d.Group);
        Assert.Equal(want.Position, d.Position);
        Assert.Equal(want.Rotation, d.Rotation);
        Assert.Equal(want.Velocity, d.Velocity);
        Assert.Equal(2, d.LinkNumber);
        Assert.Equal(3, d.Type);
        Assert.Equal(want.OffsetPos, d.OffsetPos);
        Assert.Equal(4, d.TouchFace);
        Assert.Equal(want.TouchUV, d.TouchUV);
        Assert.Equal(want.TouchST, d.TouchST);
        Assert.Equal(want.TouchNormal, d.TouchNormal);
        Assert.Equal(want.TouchBinormal, d.TouchBinormal);
        Assert.Equal(want.TouchPos, d.TouchPos);
        Assert.Equal(2.5f, d.Damage);
        Assert.Equal(6, d.DamageType);
        Assert.Equal(3.5f, d.OriginalDamage);
        Assert.Null(r.Events[1].Detected); // an event with no detected objects has none
    }

    [Fact]
    public void ARecordCutShortInTheExtrasIsNotRead()
    {
        byte[] bytes = WithExtras().Encode();
        byte[] cut = bytes[..(bytes.Length - 40)];

        Assert.Null(ScriptStateRecord.Decode(cut, "SLua", out string problem));
        Assert.NotNull(problem);
    }
}
