/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Avatar.AvatarFactory;
using OpenSim.Region.CoreModules.Avatar.Chat;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.OptionalModules.World.NPC;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The simulator's plugin loader gives each plugin assembly its own load context, so the bot module that the simulator
/// builds is not always the BotManager type that the script engine was compiled against: the script engine sees only
/// the shared IBotManager. A script's bot persistence functions therefore have to work through IBotManager alone; a
/// cast to BotManager fails for such a module and made botSetPersistent answer BotPersistError.DISABLED in a running
/// region while persistence was on. These tests load a second copy of the bot module assembly in its own load context
/// and run the persistence functions against it. No process-wide state: each test has its own region and database
/// folder, so the class runs in parallel.
/// </summary>
public class BotModuleOtherContextTests
{
    /// <summary>Loads OpenSim.Region.OptionalModules a second time; every other assembly comes from the default context.</summary>
    private sealed class OtherContext : AssemblyLoadContext
    {
        private readonly string m_path;

        public OtherContext(string path) : base("bot-module-other-context") { m_path = path; }

        protected override Assembly Load(AssemblyName name)
            => name.Name == "OpenSim.Region.OptionalModules" ? LoadFromAssemblyPath(m_path) : null;
    }

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly IBotManager Manager;
        public readonly string Dir;
        private readonly ISharedRegionModule m_module;

        public Rig(bool persistence)
        {
            Dir = Path.Combine(AppContext.BaseDirectory, "bot-region-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);

            H = new SchedulerHarness(cfg =>
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
                }
            });
            H.Scene.RegisterModuleInterface<OpenSim.Services.Interfaces.IAvatarService>(new FakeAvatarService());
            SceneHelpers.SetupSceneModules(H.Scene, H.Config,
                new AvatarFactoryModule(), new UserManagementModule(), new AttachmentsModule(),
                new BasicInventoryAccessModule(), new ChatModule());

            var context = new OtherContext(typeof(BotManager).Assembly.Location);
            Type other = context.LoadFromAssemblyPath(typeof(BotManager).Assembly.Location).GetType(typeof(BotManager).FullName);
            object module = Activator.CreateInstance(other);
            // The setup of these tests: the module is not the BotManager the script engine was built against.
            Assert.False(module is BotManager, "the second copy of the bot module is the same type");
            m_module = Assert.IsAssignableFrom<ISharedRegionModule>(module);
            Manager = Assert.IsAssignableFrom<IBotManager>(module);

            SceneHelpers.SetupSceneModules(H.Scene, H.Config, new NPCModule(), module);
            Assert.Same(module, H.Scene.RequestModuleInterface<IBotManager>());

            UUID owner = UUID.Random();
            SceneHelpers.AddScenePresence(H.Scene, owner);
            H.Prim.OwnerID = owner;
        }

        public void Dispose()
        {
            m_module.Close();
            H.Dispose();
        }
    }

    private static int Call(SchedulerHarness h, string expression)
    {
        BotRegions.Run(h, "llSay(0, \"r=\" + (string)(" + expression + "));");
        return int.Parse(BotRegions.SaidAfter(h, "r="));
    }

    [Fact]
    public void BotSetPersistentWorksWhenTheBotModuleIsLoadedInAnotherContext()
    {
        using var r = new Rig(persistence: true);
        UUID bot = BotRegions.MakeBot(r.H, "Aye");

        Assert.Equal(BotPersistError.OK, Call(r.H, "botSetPersistent(\"" + bot + "\", 0)"));
        Assert.Equal(1, Call(r.H, "botIsPersistent(\"" + bot + "\")"));
        Assert.True(r.Manager.IsBotPersistent(r.H.Scene, bot));
        Assert.Empty(BotRegions.Errors(r.H));
    }

    [Fact]
    public void ThePersistentDataAndRemoveFunctionsWorkWhenTheBotModuleIsLoadedInAnotherContext()
    {
        using var r = new Rig(persistence: true);
        UUID bot = BotRegions.MakeBot(r.H, "Aye");
        Assert.Equal(BotPersistError.OK, Call(r.H, "botSetPersistent(\"" + bot + "\", 0)"));

        Assert.Equal(BotPersistError.OK, Call(r.H, "botSetPersistentData(\"" + bot + "\", \"mood\", \"calm\")"));
        BotRegions.Run(r.H, "llSay(0, \"d=\" + botGetPersistentData(\"" + bot + "\", \"mood\"));");
        Assert.Equal("calm", BotRegions.SaidAfter(r.H, "d="));

        Assert.Equal(BotPersistError.OK, Call(r.H, "botRemovePersistent(\"" + bot + "\")"));
        Assert.Equal(0, Call(r.H, "botIsPersistent(\"" + bot + "\")"));
    }

    [Fact]
    public void BotSetPersistentAnswersDisabledOnlyWhenPersistenceIsOffInTheConfig()
    {
        using var r = new Rig(persistence: false);
        UUID bot = BotRegions.MakeBot(r.H, "Aye");

        Assert.Equal(BotPersistError.DISABLED, Call(r.H, "botSetPersistent(\"" + bot + "\", 0)"));
        Assert.Equal(0, Call(r.H, "botIsPersistent(\"" + bot + "\")"));
    }
}
