/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Shared;
using Xunit;
using LslVector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;
using LslRotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The detected data an event carries (DetectParams, as every engine receives it) is copied into the engine's own
/// record when the event is posted, field for field.
/// </summary>
public class DetectedRecordTests
{
    private static DetectParams Full()
    {
        var dp = new DetectParams
        {
            Key = UUID.Random(),
            Name = "Test User",
            Owner = UUID.Random(),
            Group = UUID.Random(),
            Position = new LslVector(10, 20, 30),
            Rotation = new LslRotation(0.1, 0.2, 0.3, 0.9),
            Velocity = new LslVector(1, 2, 3),
            LinkNum = 4,
            Type = DetectParams.AGENT | DetectParams.ACTIVE,
            OffsetPos = new LslVector(0.5, 0.25, 0.125),
            Damage = 12.5f,
            DamageType = -1,
            OriginalDamage = 20f,
            AdjustDamage = _ => { },
        };
        dp.SurfaceTouchArgs = new SurfaceTouchEventArgs
        {
            FaceIndex = 3,
            UVCoord = new Vector3(0.1f, 0.2f, 0f),
            STCoord = new Vector3(0.3f, 0.4f, 0f),
            Normal = new Vector3(0f, 0f, 1f),
            Binormal = new Vector3(1f, 0f, 0f),
            Position = new Vector3(5f, 6f, 7f),
        };
        return dp;
    }

    [Fact]
    public void EveryFieldIsCopiedInOrder()
    {
        DetectParams a = Full(), b = Full();
        b.Name = "Second User";

        DetectedRecord[] records = DetectedRecord.From(new[] { a, b });

        Assert.Equal(2, records.Length);
        DetectedRecord r = records[0];
        Assert.Equal(a.Key.ToString(), r.Key);
        Assert.Equal("Test User", r.Name);
        Assert.Equal(a.Owner.ToString(), r.Owner);
        Assert.Equal(a.Group.ToString(), r.Group);
        Assert.Equal(new Vector3(10f, 20f, 30f), r.Position);
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f), r.Rotation);
        Assert.Equal(new Vector3(1f, 2f, 3f), r.Velocity);
        Assert.Equal(4, r.LinkNumber);
        Assert.Equal(DetectParams.AGENT | DetectParams.ACTIVE, r.Type);
        Assert.Equal(new Vector3(0.5f, 0.25f, 0.125f), r.OffsetPos);
        Assert.Equal(3, r.TouchFace);
        Assert.Equal(new Vector3(0.1f, 0.2f, 0f), r.TouchUV);
        Assert.Equal(new Vector3(0.3f, 0.4f, 0f), r.TouchST);
        Assert.Equal(new Vector3(0f, 0f, 1f), r.TouchNormal);
        Assert.Equal(new Vector3(1f, 0f, 0f), r.TouchBinormal);
        Assert.Equal(new Vector3(5f, 6f, 7f), r.TouchPos);
        Assert.Equal(12.5f, r.Damage);
        Assert.Equal(-1, r.DamageType);
        Assert.Equal(20f, r.OriginalDamage);
        Assert.Same(a.AdjustDamage, r.AdjustDamage);
        Assert.Equal("Second User", records[1].Name);
    }

    [Fact]
    public void ANoSurfaceTouchGivesTheInvalidFaceAndTextureCoordinates()
    {
        DetectedRecord r = DetectedRecord.From(new[] { new DetectParams { Name = "Test User" } })[0];

        Assert.Equal(-1, r.TouchFace);
        Assert.Equal(new Vector3(-1f, -1f, 0f), r.TouchUV);
        Assert.Equal(new Vector3(-1f, -1f, 0f), r.TouchST);
    }

    [Fact]
    public void NothingDetectedGivesNoRecords()
    {
        Assert.Null(DetectedRecord.From(null));
        Assert.Null(DetectedRecord.From(Array.Empty<DetectParams>()));
    }

    [Fact]
    public void ARecordDoesNotChangeWhenTheSourceDoes()
    {
        DetectParams dp = Full();
        DetectedRecord r = DetectedRecord.From(new[] { dp })[0];

        dp.Name = "Changed";
        dp.Position = new LslVector(0, 0, 0);

        Assert.Equal("Test User", r.Name);
        Assert.Equal(new Vector3(10f, 20f, 30f), r.Position);
    }
}
