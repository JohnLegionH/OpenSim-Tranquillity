/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Two regions of one simulator sharing one bot manager and one NPC module, as a simulator that runs several regions
/// does: region A is a standard 256 m region, region B has the size the test asks for. Each region has its own scripts
/// and its own bot database; the avatar service is shared, like the grid's.
/// </summary>
internal sealed class BotRegions : IDisposable
{
    public const int DebugChannel = 0x7FFFFFFF;

    public SchedulerHarness A { get; }
    public SchedulerHarness B { get; }
    public BotManager Bots { get; private set; }
    public FakeAvatarService Avatars { get; } = new();
    public string Dir { get; }

    public BotRegions(uint sizeB = 256, bool persistence = false)
    {
        Dir = Path.Combine(AppContext.BaseDirectory, "bot-region-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);

        void Configure(IConfigSource cfg)
        {
            cfg.AddConfig("NPC").Set("Enabled", "true");
            cfg.AddConfig("OSSL").Set("OSFunctionThreatLevel", "High");
            cfg.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            cfg.AddConfig("Chat");
            if (persistence)
            {
                var p = cfg.AddConfig("BotPersistence");
                p.Set("Enabled", "true");
                p.Set("DatabaseFile", Path.Combine(Dir, "bots.db"));
                p.Set("RespawnRate", "10");
            }
        }

        A = new SchedulerHarness(Configure);
        B = new SchedulerHarness(Configure, regionSize: sizeB, regionX: 1001);
        foreach (var h in new[] { A, B })
        {
            h.Scene.RegisterModuleInterface<IAvatarService>(Avatars);
            SceneHelpers.SetupSceneModules(h.Scene, h.Config,
                new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(),
                new BasicInventoryAccessModule(), new ChatModule());
        }
        Bots = new BotManager();
        SceneHelpers.SetupSceneModules(new Scene[] { A.Scene, B.Scene }, A.Config, new NPCModule(), Bots);
    }

    /// <summary>As the simulator does on a restart: the old bot manager leaves both regions, a new one loads into them.</summary>
    public void Restart(bool primsLoaded = true)
    {
        Bots.RemoveRegion(A.Scene);
        Bots.RemoveRegion(B.Scene);
        Bots.Close();
        Bots = new BotManager();
        Bots.Initialise(A.Config);
        foreach (var h in new[] { A, B })
        {
            Bots.AddRegion(h.Scene);
            Bots.RegionLoaded(h.Scene);
            // The simulator loads a region's objects after its modules; the persistent bots come back then.
            if (primsLoaded) h.Scene.EventManager.TriggerPrimsLoaded(h.Scene);
        }
    }

    public static string Errors(SchedulerHarness h)
        => string.Join(" | ", h.SaidOn.Where(s => s.Channel == DebugChannel).Select(s => s.Message));

    /// <summary>Rezzes a script in the region's prim and waits until it has run <paramref name="body"/>.</summary>
    public static void Run(SchedulerHarness h, string body)
    {
        string done = "done" + Guid.NewGuid().ToString("N")[..8];
        h.RezScript("default { state_entry() { " + body + " llSay(0, \"" + done + "\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Contains(done)), "said: " + string.Join(" | ", h.Said) + " errors: " + Errors(h));
    }

    /// <summary>The text a script said after <paramref name="prefix"/>, the latest such line.</summary>
    public static string SaidAfter(SchedulerHarness h, string prefix)
        => h.Said.Last(s => s.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    public static UUID KeyOf(SchedulerHarness h, string prefix) => UUID.Parse(SaidAfter(h, prefix));

    /// <summary>The script makes a bot with no outfit, says its key as "bot=", and the bot's key is returned.</summary>
    public static UUID MakeBot(SchedulerHarness h, string first, string pos = "<128, 128, 30>")
    {
        Run(h, "key b = botCreateBot(\"" + first + "\", \"Bot\", \"\", " + pos + ", 0); llSay(0, \"bot=\" + (string)b);");
        return KeyOf(h, "bot=");
    }

    public void Dispose()
    {
        Bots.Close();
        A.Dispose();
        B.Dispose();
    }
}

/// <summary>The grid's avatar service for the tests: appearances kept in memory, found by user.</summary>
internal sealed class FakeAvatarService : IAvatarService
{
    private readonly Dictionary<UUID, AvatarData> m_data = new();

    public AvatarAppearance GetAppearance(UUID userID) => GetAvatar(userID)?.ToAvatarAppearance();
    public bool SetAppearance(UUID userID, AvatarAppearance appearance) => SetAvatar(userID, new AvatarData(appearance));

    public AvatarData GetAvatar(UUID userID)
    {
        lock (m_data) return m_data.TryGetValue(userID, out var d) ? d : null;
    }

    public bool SetAvatar(UUID userID, AvatarData avatar)
    {
        lock (m_data) m_data[userID] = avatar;
        return true;
    }

    public bool ResetAvatar(UUID userID)
    {
        lock (m_data) return m_data.Remove(userID);
    }

    public bool SetItems(UUID userID, string[] names, string[] values) => true;
    public bool RemoveItems(UUID userID, string[] names) => true;
}
