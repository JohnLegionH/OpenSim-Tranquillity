using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-21 part A. The permission bits Phlox tested were not SL's: attach and detach were gated on
/// PERMISSION_TRIGGER_ANIMATION (0x10) instead of PERMISSION_ATTACH (0x20), the implicit grants gave a
/// sitter RELEASE_OWNERSHIP and missed OVERRIDE_ANIMATIONS for a wearer, and the answer handler took any
/// item's answer and stored whatever bits the viewer sent.
/// https://wiki.secondlife.com/wiki/LlRequestPermissions (implicit grants), .../LlAttachToAvatar.
/// </summary>
[Collection("phlox-state")]
public class PermissionsTests
{
    private const int TRIGGER_ANIMATION = 0x10, ATTACH = 0x20, RELEASE_OWNERSHIP = 0x40, OVERRIDE_ANIMATIONS = 0x8000;

    /// <summary>Asks <paramref name="agent"/> for <paramref name="perm"/> at state_entry; reports each grant, then tries to attach, temp-attach and detach.</summary>
    private static string Asker(UUID agent, int perm, bool tryAttach = false, bool tryDetach = false) =>
        "default { state_entry() { llRequestPermissions(\"" + agent + "\", " + perm + "); } " +
        "run_time_permissions(integer p) { llSay(0, \"rtp=\" + (string)p); " +
        (tryAttach ? "llAttachToAvatar(ATTACH_CHEST); llAttachToAvatarTemp(ATTACH_CHEST); " : "") +
        (tryDetach ? "llDetachFromAvatar(); " : "") +
        "llSay(0, \"perms=\" + (string)llGetPermissions()); } }";

    private static TestClient Present(SchedulerHarness h, UUID id)
        => (TestClient)SceneHelpers.AddScenePresence(h.Scene, id).ControllingClient;

    private static RecordingAttachments FakeAttachments(SchedulerHarness h)
    {
        var fake = RecordingAttachments.Create(out var rec);
        h.Scene.RegisterModuleInterface<IAttachmentsModule>(fake);
        return rec;
    }

    private static void Wear(SchedulerHarness h, ScenePresence sp)
    {
        var sog = h.Prim.ParentGroup;
        sog.AttachedAvatar = sp.UUID;
        sog.IsAttachment = true;
        sog.AttachmentPoint = 3;
        sp.AddAttachment(sog);
    }

    [Fact]
    public void AnAnimationOnlyGrantCannotAttachOrTempAttach()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var owner = Present(h, h.Prim.OwnerID);
        var item = h.RezScript(Asker(h.Prim.OwnerID, TRIGGER_ANIMATION, tryAttach: true));
        h.Pump();
        Assert.Single(owner.ScriptQuestions);
        owner.FireScriptAnswer(h.Prim.UUID, item, TRIGGER_ANIMATION);
        h.Pump();
        Assert.Contains("rtp=16", h.Said);
        Assert.True(rec.Calls.Count(c => c == "AttachObject") == 0, "AttachObject reached with only TRIGGER_ANIMATION: [" + string.Join(",", rec.Calls) + "]");
    }

    [Fact]
    public void AnAnimationOnlyGrantCannotDetach()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        Present(h, h.Prim.OwnerID);
        Wear(h, h.Scene.GetScenePresence(h.Prim.OwnerID));
        h.RezScript(Asker(h.Prim.OwnerID, TRIGGER_ANIMATION, tryDetach: true));
        h.Pump();
        Assert.Contains("rtp=16", h.Said);
        Assert.True(!rec.Calls.Contains("DetachSingleAttachmentToInv"), "detached with only TRIGGER_ANIMATION: [" + string.Join(",", rec.Calls) + "]");
    }

    [Fact]
    public void AnAttachGrantFromTheOwnerReachesAttachObject()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var owner = Present(h, h.Prim.OwnerID);
        var item = h.RezScript(Asker(h.Prim.OwnerID, ATTACH, tryAttach: true));
        h.Pump();
        owner.FireScriptAnswer(h.Prim.UUID, item, ATTACH);
        h.Pump();
        Assert.Contains("rtp=32", h.Said);
        Assert.Contains("AttachObject", rec.Calls);
    }

    [Fact]
    public void AnAttachGrantFromSomeoneElseDoesNotAttach()
    {
        using var h = new SchedulerHarness();
        var rec = FakeAttachments(h);
        var other = UUID.Random();
        var client = Present(h, other);
        var item = h.RezScript("default { state_entry() { llRequestPermissions(\"" + other + "\", PERMISSION_ATTACH); } " +
                               "run_time_permissions(integer p) { llSay(0, \"rtp=\" + (string)p); llAttachToAvatar(ATTACH_CHEST); } }");
        h.Pump();
        client.FireScriptAnswer(h.Prim.UUID, item, ATTACH);
        h.Pump();
        Assert.Contains("rtp=32", h.Said);
        Assert.DoesNotContain("AttachObject", rec.Calls);
    }

    [Fact]
    public void ASitterOnAChildPrimGetsAnimationSilentlyButNotAttachOrReleaseOwnership()
    {
        using var h = new SchedulerHarness();
        var child = SceneHelpers.CreateSceneObjectPart("child", UUID.Random(), h.Prim.OwnerID);
        h.Prim.ParentGroup.AddPart(child);
        var sitterId = UUID.Random();
        var sitter = Present(h, sitterId);
        var sp = h.Scene.GetScenePresence(sitterId);
        var add = typeof(SceneObjectPart).GetMethod("AddSittingAvatar", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        Assert.True((bool)add!.Invoke(child, new object[] { sp })!);

        h.RezScript(Asker(sitterId, TRIGGER_ANIMATION));
        h.Pump();
        Assert.Contains("rtp=16", h.Said);
        Assert.True(sitter.ScriptQuestions.Count == 0, "the sitter was asked for TRIGGER_ANIMATION");

        h.RezScript(Asker(sitterId, ATTACH));
        h.RezScript(Asker(sitterId, RELEASE_OWNERSHIP));
        h.Pump();
        Assert.DoesNotContain("rtp=32", h.Said);
        Assert.DoesNotContain("rtp=64", h.Said);
        Assert.Equal(2, sitter.ScriptQuestions.Count);
    }

    [Fact]
    public void AnAttachmentAskingItsWearerForOverrideAnimationsIsGrantedSilently()
    {
        using var h = new SchedulerHarness();
        var wearer = Present(h, h.Prim.OwnerID);
        Wear(h, h.Scene.GetScenePresence(h.Prim.OwnerID));
        h.RezScript(Asker(h.Prim.OwnerID, OVERRIDE_ANIMATIONS | TRIGGER_ANIMATION));
        h.Pump();
        Assert.Contains("rtp=" + (OVERRIDE_ANIMATIONS | TRIGGER_ANIMATION), h.Said);
        Assert.Empty(wearer.ScriptQuestions);
    }

    [Fact]
    public void AnAnswerCarryingAnotherScriptsItemIdChangesNothing()
    {
        using var h = new SchedulerHarness();
        var owner = Present(h, h.Prim.OwnerID);
        var item = h.RezScript(Asker(h.Prim.OwnerID, TRIGGER_ANIMATION));
        h.Pump();
        Assert.Single(owner.ScriptQuestions);
        owner.FireScriptAnswer(h.Prim.UUID, UUID.Random(), TRIGGER_ANIMATION);
        h.Pump();
        Assert.DoesNotContain(h.Said, s => s.StartsWith("rtp="));
        Assert.Equal(0, h.Prim.Inventory.GetInventoryItem(item).PermsMask);
        // The real answer still lands afterwards: the stray one did not tear the wait down.
        owner.FireScriptAnswer(h.Prim.UUID, item, TRIGGER_ANIMATION);
        h.Pump();
        Assert.Contains("rtp=16", h.Said);
    }

    [Fact]
    public void AnAnswerWithExtraBitsIsMaskedToTheRequest()
    {
        using var h = new SchedulerHarness();
        var owner = Present(h, h.Prim.OwnerID);
        var item = h.RezScript(Asker(h.Prim.OwnerID, TRIGGER_ANIMATION));
        h.Pump();
        owner.FireScriptAnswer(h.Prim.UUID, item, TRIGGER_ANIMATION | ATTACH | 0x2 /* DEBIT */);
        h.Pump();
        Assert.Contains("rtp=16", h.Said);
        Assert.Contains("perms=16", h.Said);
        Assert.Equal(TRIGGER_ANIMATION, h.Prim.Inventory.GetInventoryItem(item).PermsMask);
    }
}

/// <summary>An IAttachmentsModule that records which members were called and does nothing.</summary>
public class RecordingAttachments : DispatchProxy
{
    public List<string> Calls { get; } = new();

    public static IAttachmentsModule Create(out RecordingAttachments rec)
    {
        var p = Create<IAttachmentsModule, RecordingAttachments>();
        rec = (RecordingAttachments)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        lock (Calls) Calls.Add(targetMethod.Name);
        var rt = targetMethod.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
