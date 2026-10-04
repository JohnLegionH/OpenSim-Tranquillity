/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using InWorldz.Phlox.Serialization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Phlox.ScriptEngine;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// A grant from an Experience saved by a real region stop (Scene.Close) and given back from the state database at the
/// next start ends there when its Experience is now disabled or suspended, as the land ends one at the start: the grant
/// and the controls it took go, the next save holds no grant, and the script is told once with
/// experience_permissions_denied and the Experience's state, XP_ERROR_EXPERIENCE_DISABLED (8) or
/// XP_ERROR_EXPERIENCE_SUSPENDED (9), 8 when both are set (SL wiki llGetExperienceErrorMessage). An enabled Experience,
/// or one the Experience service cannot be asked about at that moment, gives the grant back whole with no event.
/// </summary>
// Runs in parallel: each test has its own harnesses, avatar, Experience service stand-in and Experience module on its own
// scenes, and rows under a random item id in the state database every harness shares; nothing process-wide is changed.
public class ExperienceStateAtRestartTests
{
    /// <summary>SL's list: TAKE_CONTROLS | TRIGGER_ANIMATION | ATTACH | TRACK_CAMERA | CONTROL_CAMERA | TELEPORT.</summary>
    private const int ExperiencePerms = 0x4 | 0x10 | 0x20 | 0x400 | 0x800 | 0x1000;
    private const int RegionStart = 0;
    private const string Phlox = "InWorldz.Phlox";
    private const int Disabled = (int)ExperienceFlags.Disabled;
    private const int Suspended = (int)ExperienceFlags.Suspended;

    /// <summary>Driven over channel 7 as "xp KEY"; a touch reports the grant.</summary>
    private const string Game = @"
        default {
            state_entry() { llListen(7, """", NULL_KEY, """"); llSay(0, ""entry""); }
            touch_start(integer t) { llSay(0, ""perms="" + (string)llGetPermissions() + "" key="" + (string)llGetPermissionsKey()); }
            listen(integer c, string n, key k, string m) {
                list w = llParseString2List(m, ["" ""], []);
                if (llList2String(w, 0) == ""xp"") llRequestExperiencePermissions(llList2Key(w, 1), """");
            }
            experience_permissions(key a) { llSay(0, ""xp="" + (string)a); }
            experience_permissions_denied(key a, integer r) { llSay(0, ""xpdenied="" + (string)a + "" "" + (string)r); }
            run_time_permissions(integer p) { llSay(0, ""rtp="" + (string)p); }
        }";

    private readonly UUID m_owner = UUID.Random(), m_visitor = UUID.Random(), m_experience = UUID.Random(),
                          m_asset = UUID.Random(), m_item = UUID.Random();

    /// <summary>
    /// The core's Experience module on <paramref name="h"/>'s scene, over a service that knows the Experience with
    /// <paramref name="properties"/>; the estate allows it.
    /// </summary>
    private ExperienceStateTests.StateService Region(SchedulerHarness h, int properties)
    {
        foreach (SceneObjectPart p in h.Prim.ParentGroup.Parts) p.OwnerID = m_owner;
        var service = ExperienceStateTests.StateService.Create(new ExperienceInfo
        {
            public_id = m_experience, owner_id = UUID.Random(), group_id = UUID.Random(), name = "Example Experience", properties = properties
        });
        h.Scene.RegisterModuleInterface<IExperienceService>((IExperienceService)(object)service);
        h.Scene.RegionInfo.EstateSettings.AllowedExperiences = new[] { m_experience };
        var config = new IniConfigSource();
        config.AddConfig("Experience").Set("Enabled", "true");
        var module = new ExperienceModule();
        SceneHelpers.SetupSceneModules(h.Scene, config, module);
        return service;
    }

    /// <summary>The visitor grants the script the Experience's list, and the region stops as the simulator stops it.</summary>
    private void GrantAndStopRegion()
    {
        using var h1 = new SchedulerHarness();
        Region(h1, 0);
        SceneHelpers.AddScenePresence(h1.Scene, m_visitor);
        Assert.True(h1.Scene.RequestModuleInterface<OpenSim.Region.Framework.Interfaces.IExperienceModule>()
                      .SetExperiencePermissions(m_visitor, m_experience, true));
        var inv = TaskInventoryHelpers.AddScript(h1.Scene.AssetService, h1.Prim, m_item, m_asset, "game", Game);
        inv.ExperienceID = m_experience;
        Assert.True(h1.Prim.Inventory.CreateScriptInstance(m_item, 0, false, Phlox, RegionStart));
        h1.Prim.ParentGroup.ResumeScripts();
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("entry")), SavedStateRig.SaidText(h1));
        h1.Scene.SimChat("xp " + m_visitor, ChatTypeEnum.Region, 7, h1.Prim.AbsolutePosition, "tester", UUID.Random(), false);
        Assert.True(h1.PumpUntil(() => h1.Said.Contains("xp=" + m_visitor)), SavedStateRig.SaidText(h1));
        h1.PumpUntilIdle(TimeSpan.FromSeconds(2));
        h1.StopRegionAsTheSimulatorDoes();
        Assert.Equal(m_experience.ToString(), RowState().PermsExperience);
    }

    /// <summary>The next start: a new region holding the same item, its Experience now with <paramref name="properties"/>.</summary>
    private SchedulerHarness Restart(int properties, bool serviceUnreachable = false)
    {
        var h2 = new SchedulerHarness();
        var service = Region(h2, properties);
        service.LookupFails = serviceUnreachable;
        var inv = TaskInventoryHelpers.AddScript(h2.Scene.AssetService, h2.Prim, m_item, m_asset, "game", Game);
        inv.ExperienceID = m_experience;
        Assert.Equal(1, h2.Prim.ParentGroup.CreateScriptInstances(0, false, Phlox, RegionStart));
        h2.Prim.ParentGroup.ResumeScripts();
        Assert.True(h2.PumpUntil(() => h2.InterpreterFor(m_item) != null), "the script did not load");
        h2.PumpUntilIdle(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("entry", h2.Said);   // restored, not started fresh
        return h2;
    }

    private SerializedRuntimeState RowState() => StateManager.Decode(SavedStateRig.Row(m_item)!.Value.Blob);

    private string Report(SchedulerHarness h)
    {
        static bool Line(string s) => s.StartsWith("perms=", StringComparison.Ordinal);
        int before = h.Said.Count(Line);
        h.PostTouch(m_item);
        Assert.True(h.PumpUntil(() => h.Said.Count(Line) > before), SavedStateRig.SaidText(h));
        return h.Said.Last(Line);
    }

    private static string Perms(int mask, UUID key) => "perms=" + mask + " key=" + key;

    private static int Denials(SchedulerHarness h) => h.Said.Count(s => s.StartsWith("xpdenied=", StringComparison.Ordinal));

    [Theory]
    [InlineData(Suspended, 9)]
    [InlineData(Disabled, 8)]
    [InlineData(Disabled | Suspended, 8)]
    public void AGrantRestoredWhileItsExperienceCannotRunEndsAtTheStartAndIsToldOnce(int properties, int code)
    {
        GrantAndStopRegion();

        using var h2 = Restart(properties);
        Assert.True(h2.PumpUntil(() => h2.Said.Contains("xpdenied=" + m_visitor + " " + code)), SavedStateRig.SaidText(h2));
        Assert.Equal(0, h2.Prim.Inventory.GetInventoryItem(m_item).PermsMask);
        Assert.Equal(Perms(0, UUID.Zero), Report(h2));
        h2.PumpUntilIdle(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Denials(h2));
        Assert.DoesNotContain(h2.Said, s => s.StartsWith("xp=", StringComparison.Ordinal) || s.StartsWith("rtp=", StringComparison.Ordinal));

        // The ended grant is not in the next row either.
        h2.SaveState(m_item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Null(RowState().PermsGranter);
        Assert.Null(RowState().PermsExperience);
    }

    [Fact]
    public void AGrantRestoredWhileItsExperienceIsEnabledComesBackWhole()
    {
        GrantAndStopRegion();

        using var h2 = Restart(0);
        Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));
        Assert.Equal(0, Denials(h2));
        h2.SaveState(m_item);
        SavedStateRig.WaitForWrites(h2);
        Assert.Equal(m_experience.ToString(), RowState().PermsExperience);
    }

    [Fact]
    public void AGrantRestoredWhileTheExperienceServiceCannotBeReachedComesBackWhole()
    {
        GrantAndStopRegion();

        using var h2 = Restart(Suspended, serviceUnreachable: true);
        Assert.Equal(Perms(ExperiencePerms, m_visitor), Report(h2));
        Assert.Equal(0, Denials(h2));
    }
}
