using OpenSim.Region.PhysicsModules.SharedBase;
using Xunit;
using OmvVector3 = OpenMetaverse.Vector3;

namespace OpenSim.Region.PhysicsModules.LegionJolt.Tests;

/// <summary>
/// JOLT-4 (audit I-2). The accumulate / hold / collision_end decision, without a Scene. On a frame whose contact
/// buffer overflowed, absence proves nothing - the contact may simply not have fit - so a prim that collided last
/// frame gets no empty update (no false collision_end) and stays tracked for the next frame.
/// </summary>
public class CollisionFrameTrackerTests
{
    private static readonly ContactPoint Cp = new(OmvVector3.Zero, OmvVector3.UnitZ, 0f);

    private static List<uint> Frame(CollisionFrameTracker t, bool overflowed, params (uint prim, uint collider)[] touches)
    {
        t.BeginFrame();
        foreach (var (prim, collider) in touches)
            t.AddCollider(prim, collider, Cp);
        return new List<uint>(t.EndFrame(overflowed));
    }

    [Fact]
    public void Normal_frame_a_prim_absent_this_frame_gets_one_empty_update()
    {
        var t = new CollisionFrameTracker();
        Assert.Empty(Frame(t, false, (7, 0), (8, 7)));
        Assert.Equal(2, t.Current.Count);

        var ended = Frame(t, false, (8, 7));
        Assert.Equal(new uint[] { 7 }, ended);
        Assert.False(t.IsTracked(7));

        Assert.Empty(Frame(t, false, (8, 7)));   // exactly once: 7 is not ended again
    }

    [Fact]
    public void Overflowed_frame_a_prim_absent_this_frame_gets_none_and_stays_tracked()
    {
        var t = new CollisionFrameTracker();
        Frame(t, false, (7, 0), (8, 7));

        Assert.Empty(Frame(t, true, (8, 7)));    // 7 absent, but the buffer overflowed: hold
        Assert.True(t.IsTracked(7));
        Assert.True(t.IsTracked(8));

        Assert.Equal(new uint[] { 7 }, Frame(t, false, (8, 7)));   // the next honest frame ends it
    }

    [Fact]
    public void Overflowed_frame_still_delivers_what_it_did_see()
    {
        var t = new CollisionFrameTracker();
        Frame(t, true, (9, 3));
        Assert.True(t.Current.ContainsKey(9));
        Assert.True(t.IsTracked(9));
    }
}
