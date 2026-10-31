/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;

namespace OpenSim.Region.Framework.Scenes.Tests
{
    /// <summary>
    /// A game-control grant is either automatic (a seat's or an attachment's, given without a dialog) or answered by the
    /// avatar through the permission dialog. The item notes which (<see cref="TaskInventoryItem.SilentPermsMask"/>).
    /// Standing up from the object, and detaching it, end only the automatic grants; the item's note travels with the
    /// object when it is serialized for a crossing.
    ///
    /// <para>Every test builds its own scene and changes nothing process-wide, so the class runs in parallel.</para>
    /// </summary>
    public class GameControlGrantKindTests : OpenSimTestCase
    {
        private const int GameControl = 0x100000, TakeControls = 0x4;

        private static TaskInventoryItem AddHeldScript(Scene scene, SceneObjectPart part, UUID granter, int mask, int silent)
        {
            UUID itemId = UUID.Random();
            TaskInventoryItem item = TaskInventoryHelpers.AddScript(scene.AssetService, part, itemId, UUID.Random(), "script", "default{}");
            item.PermsGranter = granter;
            item.PermsMask = mask;
            item.SilentPermsMask = silent;
            return item;
        }

        // ── the note travels with the object ─────────────────────────────────────

        [Fact]
        public void TheNoteOfAnAutomaticGrantSurvivesTheXml2FormatARegionCrossingUses()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random(), "Vehicle", 0x31);
            UUID granter = UUID.Random();
            TaskInventoryItem item = AddHeldScript(scene, sog.RootPart, granter, GameControl | TakeControls, GameControl);

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(sog));

            TaskInventoryItem arrived = back.RootPart.Inventory.GetInventoryItem(item.ItemID);
            Assert.Equal(GameControl | TakeControls, arrived.PermsMask);
            Assert.Equal(GameControl, arrived.SilentPermsMask);
        }

        [Fact]
        public void TheNoteOfAnAutomaticGrantSurvivesTheOriginalXmlFormat()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random(), "Worn", 0x32);
            TaskInventoryItem item = AddHeldScript(scene, sog.RootPart, UUID.Random(), GameControl, GameControl);

            SceneObjectGroup back = SceneObjectSerializer.FromOriginalXmlFormat(SceneObjectSerializer.ToOriginalXmlFormat(sog));

            Assert.Equal(GameControl, back.RootPart.Inventory.GetInventoryItem(item.ItemID).SilentPermsMask);
        }

        [Fact]
        public void AnObjectWithoutTheNoteComesBackWithNoAutomaticBits()
        {
            Scene scene = new SceneHelpers().SetupScene();
            SceneObjectGroup sog = SceneHelpers.CreateSceneObject(1, UUID.Random(), "Plain", 0x33);
            TaskInventoryItem item = AddHeldScript(scene, sog.RootPart, UUID.Random(), GameControl, 0);

            SceneObjectGroup back = SceneObjectSerializer.FromXml2Format(SceneObjectSerializer.ToXml2Format(sog));

            Assert.Equal(0, back.RootPart.Inventory.GetInventoryItem(item.ItemID).SilentPermsMask);
            Assert.Equal(GameControl, back.RootPart.Inventory.GetInventoryItem(item.ItemID).PermsMask);
        }

        // ── detach: only the automatic grant ends ────────────────────────────────

        private static (Scene Scene, ScenePresence Avatar, SceneObjectGroup Worn) WornObject()
        {
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            Scene scene = new SceneHelpers().SetupScene("detach-test", TestHelpers.ParseTail(0x9), 1000, 1000, config);
            SceneHelpers.SetupSceneModules(scene, config, new AttachmentsModule(), new BasicInventoryAccessModule());
            UserAccount user = UserAccountHelpers.CreateUserWithInventory(scene, TestHelpers.ParseTail(0x1));
            ScenePresence sp = SceneHelpers.AddScenePresence(scene, user);
            SceneObjectGroup so = SceneHelpers.CreateSceneObject(1, sp.UUID, "worn", 0x40);
            InventoryItemBase invItem = UserInventoryHelpers.AddInventoryItem(scene, so, 0x100, 0x1000);
            var worn = (SceneObjectGroup)scene.AttachmentsModule.RezSingleAttachmentFromInventory(sp, invItem.ID, (uint)AttachmentPoint.Chest);
            Assert.True(worn.IsAttachment);
            Assert.NotEqual(UUID.Zero, worn.FromItemID);
            return (scene, sp, worn);
        }

        [Fact]
        public void DetachingAnAttachmentToInventoryEndsTheGameControlGrantItGaveAutomatically()
        {
            var (scene, sp, worn) = WornObject();
            TaskInventoryItem held = AddHeldScript(scene, worn.RootPart, sp.UUID, GameControl | TakeControls, GameControl | TakeControls);

            scene.AttachmentsModule.DetachSingleAttachmentToInv(sp, worn);

            Assert.Equal(0, held.PermsMask);
            Assert.Equal(UUID.Zero, held.PermsGranter);
        }

        [Fact]
        public void DetachingAnAttachmentToInventoryKeepsAGameControlGrantTheWearerAnsweredThroughTheDialog()
        {
            var (scene, sp, worn) = WornObject();
            TaskInventoryItem held = AddHeldScript(scene, worn.RootPart, sp.UUID, GameControl | TakeControls, TakeControls);

            scene.AttachmentsModule.DetachSingleAttachmentToInv(sp, worn);

            Assert.Equal(GameControl, held.PermsMask);      // the controls went with the detach (as before)
            Assert.Equal(sp.UUID, held.PermsGranter);
        }

        [Fact]
        public void DetachingAnAttachmentToTheGroundEndsTheGameControlGrantItGaveAutomatically()
        {
            var (scene, sp, worn) = WornObject();
            TaskInventoryItem held = AddHeldScript(scene, worn.RootPart, sp.UUID, GameControl, GameControl);

            scene.AttachmentsModule.DetachSingleAttachmentToGround(sp, worn.LocalId);

            Assert.Equal(0, held.PermsMask);
        }

        [Fact]
        public void DetachingAnAttachmentToTheGroundKeepsAGameControlGrantTheWearerAnsweredThroughTheDialog()
        {
            var (scene, sp, worn) = WornObject();
            TaskInventoryItem held = AddHeldScript(scene, worn.RootPart, sp.UUID, GameControl, 0);

            scene.AttachmentsModule.DetachSingleAttachmentToGround(sp, worn.LocalId);

            Assert.Equal(GameControl, held.PermsMask);
        }
    }
}
