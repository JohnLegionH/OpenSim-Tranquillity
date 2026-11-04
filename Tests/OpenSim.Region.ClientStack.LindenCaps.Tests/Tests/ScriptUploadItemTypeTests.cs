/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    /// <summary>
    /// A script sent through the NewFileAgentInventory upload (the route a library takes when it makes a script item
    /// from source) is stored as a script item: inventory type LSL and asset type LSLText. With both left at zero the
    /// item is a texture as far as the region is concerned: it is not in the prim's list of scripts once copied into
    /// an object, and the script engine hands object events (touch, changed, link_message) only to script items.
    /// </summary>
    // Each test builds its own scene and an unstarted HTTP server and touches nothing process-wide, so the class runs
    // in parallel.
    public class ScriptUploadItemTypeTests
    {
        private static InventoryItemBase Upload(string inventoryType, string assetType)
        {
            Scene scene = new SceneHelpers().SetupScene();
            UUID agent = UUID.Random();
            var caps = new Caps(new BaseHttpServer(0), "localhost", 9000, "/CAPS/" + UUID.Random(), agent, "Test");
            var options = new BunchOfCapsConfigOptions { ModelCost = new ModelCost(scene) };
            var bunch = new BunchOfCaps(scene, agent, caps, options);

            InventoryItemBase stored = null;
            bunch.AddNewAsset = a => { };
            bunch.AddNewInventoryItem = (id, item, cost) => stored = item;

            string error = null;
            int next = 0, group = 0, everyone = 0;
            bunch.UploadCompleteHandler("touch script", "test", UUID.Random(), UUID.Random(), UUID.Random(),
                Encoding.UTF8.GetBytes("default { touch_start(integer n) { llSay(0, \"x\"); } }"),
                inventoryType, assetType, 0, UUID.Zero, 0, 0, 0, false, ref error,
                ref next, ref group, ref everyone, null);

            Assert.Null(error);
            Assert.NotNull(stored);
            return stored;
        }

        [Fact]
        public void AScriptUploadIsStoredAsAScriptItem()
        {
            InventoryItemBase item = Upload("script", "lsltext");

            Assert.Equal((int)InventoryType.LSL, item.InvType);
            Assert.Equal((int)AssetType.LSLText, item.AssetType);
        }

        [Fact]
        public void ASoundUploadKeepsItsTypes()
        {
            InventoryItemBase item = Upload("sound", "ogg");

            Assert.Equal((int)InventoryType.Sound, item.InvType);
            Assert.Equal((int)AssetType.Sound, item.AssetType);
        }
    }
}
