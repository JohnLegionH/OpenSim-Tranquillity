using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Xunit;
using Xunit.Abstractions;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace InWorldz.Phlox.Tests;

/// <summary>PHLOX-21 part E. Small correctness fixes, one test each.</summary>
[Collection("phlox-state")]
public class SmallCorrectnessTests
{
    private readonly ITestOutputHelper _out;
    public SmallCorrectnessTests(ITestOutputHelper o) => _out = o;

    private static string Said(SchedulerHarness h) => string.Join(" | ", h.Said);

    // ---- E1: llGetMassMKS is 100 x llGetMass (wiki: mass in kilograms; YEngine LSL_Api.llGetMassMKS) ----

    [Fact]
    public void GetMassMksIsAHundredTimesGetMass()
    {
        using var h = new SchedulerHarness();
        h.Prim.PhysActor = new MassiveActor();   // the test scene's physics gives every prim mass 0
        h.RezScript("default { state_entry() { llSay(0, \"mass=\" + (string)llGetMass() + \" mks=\" + (string)llGetMassMKS()); } }");
        h.Pump();
        var line = h.Said.FirstOrDefault(s => s.StartsWith("mass="));
        Assert.True(line != null, Said(h));
        var parts = line.Split(' ');
        float mass = float.Parse(parts[0].Substring(5), CultureInfo.InvariantCulture);
        float mks = float.Parse(parts[1].Substring(4), CultureInfo.InvariantCulture);
        _out.WriteLine(line);
        Assert.True(mass > 0, "the test prim has no mass: " + line);
        Assert.InRange(mks, mass * 100 * 0.999f, mass * 100 * 1.001f);
    }

    // ---- E2: llClearLinkMedia(link, face) clears that face of that link ----

    [Fact]
    public void ClearLinkMediaClearsTheNamedFaceOfTheNamedLink()
    {
        using var h = new SchedulerHarness();
        var moap = RecordingMoap.Create(out var rec);
        h.Scene.RegisterModuleInterface<IMoapModule>(moap);
        h.RezScript("default { state_entry() { llSay(0, \"status=\" + (string)llClearLinkMedia(LINK_THIS, 2)); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        Assert.True(h.Said.Contains("status=0"), Said(h));
        Assert.Equal(new[] { (h.Prim.LocalId, 2) }, rec.Cleared);
    }

    // ---- E3: DATA_ONLINE answers "1" for an avatar that is online ----

    [Fact]
    public void DataOnlineIsOneForAPresentAvatar()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        h.RezScript($"default {{ state_entry() {{ llRequestAgentData(\"{client.AgentId}\", DATA_ONLINE); }} dataserver(key q, string d) {{ llSay(0, \"online=\" + d); }} }}");
        h.PumpFor(TimeSpan.FromSeconds(2));
        Assert.True(h.Said.Contains("online=1"), Said(h));
    }

    [Fact]
    public void DataOnlineIsZeroForAnAvatarThatIsNowhere()
    {
        using var h = new SchedulerHarness();
        h.RezScript($"default {{ state_entry() {{ llRequestAgentData(\"{UUID.Random()}\", DATA_ONLINE); }} dataserver(key q, string d) {{ llSay(0, \"online=\" + d); }} }}");
        h.PumpFor(TimeSpan.FromSeconds(2));
        Assert.True(h.Said.Contains("online=0"), Said(h));
    }

    // ---- E4: llSetInventoryPermMask is a god function (YEngine: AllowGodFunctions and an administrator owner) ----

    private static TaskInventoryItem AddTexture(SchedulerHarness h)
    {
        uint full = (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Move);
        var item = new TaskInventoryItem
        {
            Name = "tex", AssetID = UUID.Random(), ItemID = UUID.Random(),
            Type = (int)AssetType.Texture, InvType = (int)InventoryType.Texture,
            BasePermissions = full, CurrentPermissions = full, NextPermissions = full, OwnerID = h.Prim.OwnerID,
        };
        h.Prim.Inventory.AddInventoryItem(item, true);
        return h.Prim.Inventory.GetInventoryItem(item.ItemID);
    }

    private const string SetNextToCopyOnly = "default { state_entry() { llSetInventoryPermMask(\"tex\", MASK_NEXT, PERM_COPY); llSay(0, \"done\"); } }";

    [Fact]
    public void SetInventoryPermMaskWorksForAGodWhenGodFunctionsAreAllowed()
    {
        using var h = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("AllowGodFunctions", "true"));
        var owner = h.Prim.OwnerID;
        h.Scene.Permissions.OnIsAdministrator += id => id == owner;
        var item = AddTexture(h);
        h.RezScript(SetNextToCopyOnly);
        h.Pump();
        Assert.Contains("done", h.Said);
        Assert.Equal((uint)PermissionMask.Copy, item.NextPermissions & (uint)(PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer));
    }

    [Fact]
    public void SetInventoryPermMaskDoesNothingForAnOrdinaryOwner()
    {
        using var h = new SchedulerHarness(c => c.Configs["InWorldz.Phlox"].Set("AllowGodFunctions", "true"));
        // The test scene has no permissions module, and with no handler IsAdministrator says yes to
        // everyone; a region's PermissionsModule answers no for an ordinary owner.
        h.Scene.Permissions.OnIsAdministrator += id => false;
        var item = AddTexture(h);
        uint before = item.NextPermissions;
        h.RezScript(SetNextToCopyOnly);
        h.Pump();
        Assert.Contains("done", h.Said);
        Assert.Equal(before, item.NextPermissions);
    }

    // ---- E5: llManageEstateAccess returns an integer, and leaves the operand stack clean ----

    [Fact]
    public void ManageEstateAccessReturnsAnIntegerAndLeavesTheStackClean()
    {
        using var h = new SchedulerHarness();
        h.Scene.RegionInfo.EstateSettings.EstateOwner = h.Prim.OwnerID;
        // A region always has an estate data service; the test scene has none (Scene.EstateDataService throws).
        h.Scene.RegisterModuleInterface<OpenSim.Services.Interfaces.IEstateDataService>(RecordingEstates.Create(out var estates));
        var client = h.AddClient();
        var item = h.RezScript($"default {{ state_entry() {{ integer r = llManageEstateAccess(ESTATE_ACCESS_ALLOWED_AGENT_ADD, \"{client.AgentId}\"); " +
                               "integer bad = llManageEstateAccess(ESTATE_ACCESS_ALLOWED_AGENT_ADD, \"not a key\"); " +
                               "llSay(0, \"r=\" + (string)r + \" bad=\" + (string)bad); } }");
        h.PumpFor(TimeSpan.FromSeconds(2));
        _out.WriteLine(Said(h) + " || " + h.DumpFrame(item));
        Assert.True(h.Said.Contains("r=1 bad=0"), Said(h));
        Assert.Contains(client.AgentId, h.Scene.RegionInfo.EstateSettings.EstateAccess);
        Assert.Equal(1, estates.Stores);
        var ops = h.StateOf(item)?.GetType().GetProperty("Operands")?.GetValue(h.StateOf(item)) as System.Collections.ICollection
                  ?? h.StateOf(item)?.GetType().GetField("Operands")?.GetValue(h.StateOf(item)) as System.Collections.ICollection;
        Assert.NotNull(ops);
        Assert.Empty(ops);
    }

    [Fact]
    public void ManageEstateAccessIsFalseForAnOwnerWhoIsNotAManager()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();
        h.RezScript($"default {{ state_entry() {{ llSay(0, \"r=\" + (string)llManageEstateAccess(ESTATE_ACCESS_ALLOWED_AGENT_ADD, \"{client.AgentId}\")); }} }}");
        h.PumpFor(TimeSpan.FromSeconds(2));
        Assert.True(h.Said.Contains("r=0"), Said(h));
    }
}

/// <summary>A physics actor with a mass, so llGetMass has something to report.</summary>
public class MassiveActor : OpenSim.Region.PhysicsModules.SharedBase.NullPhysicsActor
{
    public override float Mass => 1.25f;
}

/// <summary>An IEstateDataService that counts StoreEstateSettings and does nothing else.</summary>
public class RecordingEstates : DispatchProxy
{
    public int Stores;

    public static OpenSim.Services.Interfaces.IEstateDataService Create(out RecordingEstates rec)
    {
        var p = Create<OpenSim.Services.Interfaces.IEstateDataService, RecordingEstates>();
        rec = (RecordingEstates)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "StoreEstateSettings") System.Threading.Interlocked.Increment(ref Stores);
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}

/// <summary>An IMoapModule that records every ClearMediaEntry as (part local id, face).</summary>
public class RecordingMoap : DispatchProxy
{
    public List<(uint, int)> Cleared { get; } = new();

    public static IMoapModule Create(out RecordingMoap rec)
    {
        var p = Create<IMoapModule, RecordingMoap>();
        rec = (RecordingMoap)(object)p;
        return p;
    }

    protected override object Invoke(MethodInfo m, object[] a)
    {
        if (m.Name == "ClearMediaEntry") lock (Cleared) Cleared.Add((((SceneObjectPart)a[0]).LocalId, (int)a[1]));
        var rt = m.ReturnType;
        return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
    }
}
