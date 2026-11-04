/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Servers.HttpServer;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    /// <summary>
    /// The inventory type and asset type the NewFileAgentInventory upload route stores for each pair of type names a
    /// client sends. The names are the ones the official viewer sends (LLInventoryType / LLAssetType lookup tables:
    /// "texture", "sound", "animatn"/"animation", "notecard", "gesture", "lsltext"/"script", "mesh"/"object",
    /// "snapshot", "bodypart"/"clothing"/"wearable") and the ones LibreMetaverse's Utils.AssetTypeToString /
    /// InventoryTypeToString produce, which are the same table. A pair the route does not store is refused with an
    /// error and nothing is stored: an item is never stored with an inventory type that does not match its asset type.
    /// </summary>
    // Each test builds its own scene and an unstarted HTTP server and touches nothing process-wide, so the class runs
    // in parallel.
    public class UploadRouteTypeTests
    {
        private sealed class Outcome
        {
            public string Error;
            public InventoryItemBase Item;
            public AssetBase Asset;
        }

        private static BunchOfCaps NewBunch(out Outcome outcome)
        {
            Scene scene = new SceneHelpers().SetupScene();
            UUID agent = UUID.Random();
            var caps = new Caps(new BaseHttpServer(0), "localhost", 9000, "/CAPS/" + UUID.Random(), agent, "Test");
            var options = new BunchOfCapsConfigOptions { ModelCost = new ModelCost(scene) };
            var bunch = new BunchOfCaps(scene, agent, caps, options);

            var o = new Outcome();
            bunch.AddNewAsset = a => o.Asset = a;
            bunch.AddNewInventoryItem = (id, item, cost) => o.Item = item;
            outcome = o;
            return bunch;
        }

        private static Outcome Upload(string inventoryType, string assetType)
        {
            BunchOfCaps bunch = NewBunch(out Outcome outcome);

            string error = string.Empty;
            int next = 0, group = 0, everyone = 0;
            bunch.UploadCompleteHandler("an item", "test", UUID.Random(), UUID.Random(), UUID.Random(),
                Encoding.UTF8.GetBytes("payload"),
                inventoryType, assetType, 0, UUID.Zero, 0, 0, 0, false, ref error,
                ref next, ref group, ref everyone, null);
            outcome.Error = error;
            return outcome;
        }

        private static LLSDAssetUploadResponse Request(string inventoryType, string assetType)
        {
            BunchOfCaps bunch = NewBunch(out _);
            return bunch.NewAgentInventoryRequest(new LLSDAssetUploadRequest
            {
                name = "an item",
                inventory_type = inventoryType,
                asset_type = assetType,
            });
        }

        // Pairs the route stored correctly before, and must keep storing exactly as before.
        [Theory]
        [InlineData("texture", "texture", InventoryType.Texture, AssetType.Texture)]
        [InlineData("texture", "", InventoryType.Texture, AssetType.Texture)]
        [InlineData("image", "texture", InventoryType.Texture, AssetType.Texture)]
        [InlineData("snapshot", "texture", InventoryType.Snapshot, AssetType.Texture)]
        [InlineData("sound", "sound", InventoryType.Sound, AssetType.Sound)]
        [InlineData("animation", "animatn", InventoryType.Animation, AssetType.Animation)]
        [InlineData("wearable", "bodypart", InventoryType.Wearable, AssetType.Bodypart)]
        [InlineData("wearable", "clothing", InventoryType.Wearable, AssetType.Clothing)]
        public void AWorkingTypeIsStoredAsBefore(string inv, string asset, InventoryType expInv, AssetType expAsset)
        {
            Outcome o = Upload(inv, asset);

            Assert.Equal(string.Empty, o.Error);
            Assert.NotNull(o.Item);
            Assert.NotNull(o.Asset);
            Assert.Equal((int)expInv, o.Item.InvType);
            Assert.Equal((int)expAsset, o.Item.AssetType);
            Assert.Equal((sbyte)expAsset, o.Asset.Type);
        }

        [Fact]
        public void AnAnimationSetIsStoredAsBefore()
        {
            Outcome o = Upload("animset", "animset");

            Assert.Equal(string.Empty, o.Error);
            Assert.NotNull(o.Item);
            Assert.Equal((int)CustomInventoryType.AnimationSet, o.Item.InvType);
            Assert.Equal((int)CustomAssetType.AnimationSet, o.Item.AssetType);
        }

        // Pairs a client sends that the route used to store as a texture.
        [Theory]
        [InlineData("notecard", "notecard", InventoryType.Notecard, AssetType.Notecard)]
        [InlineData("gesture", "gesture", InventoryType.Gesture, AssetType.Gesture)]
        [InlineData("script", "lsltext", InventoryType.LSL, AssetType.LSLText)]
        public void ATypeThatWasStoredAsATextureIsStoredWithItsOwnTypes(string inv, string asset, InventoryType expInv, AssetType expAsset)
        {
            Outcome o = Upload(inv, asset);

            Assert.Equal(string.Empty, o.Error);
            Assert.NotNull(o.Item);
            Assert.NotNull(o.Asset);
            Assert.Equal((int)expInv, o.Item.InvType);
            Assert.Equal((int)expAsset, o.Item.AssetType);
            Assert.Equal((sbyte)expAsset, o.Asset.Type);
        }

        // A client that sends no inventory type gets the one that goes with the asset type, as the official viewer does
        // for a file upload (it sends the default inventory type for the asset type).
        [Theory]
        [InlineData("texture", InventoryType.Texture, AssetType.Texture)]
        [InlineData("animatn", InventoryType.Animation, AssetType.Animation)]
        [InlineData("notecard", InventoryType.Notecard, AssetType.Notecard)]
        public void ANamelessInventoryTypeTakesTheOneThatGoesWithTheAssetType(string asset, InventoryType expInv, AssetType expAsset)
        {
            Outcome o = Upload(string.Empty, asset);

            Assert.Equal(string.Empty, o.Error);
            Assert.NotNull(o.Item);
            Assert.Equal((int)expInv, o.Item.InvType);
            Assert.Equal((int)expAsset, o.Item.AssetType);
        }

        // Pairs the route does not store: no client sends them here, or the two names do not belong together.
        [Theory]
        [InlineData("landmark", "landmark")]
        [InlineData("callcard", "callcard")]
        [InlineData("attach", "object")]
        [InlineData("settings", "settings")]
        [InlineData("material", "material")]
        [InlineData("gltf", "gltf")]
        [InlineData("glbin", "glbin")]
        [InlineData("category", "category")]
        [InlineData("widget", "widget")]
        [InlineData("nonsense", "texture")]
        [InlineData("", "")]
        [InlineData("", "nonsense")]
        [InlineData("wearable", "")]
        [InlineData("wearable", "notecard")]
        [InlineData("script", "lslbyte")]
        [InlineData("script", "script")]
        [InlineData("script", "texture")]
        [InlineData("object", "texture")]
        [InlineData("texture", "sound")]
        [InlineData("texture", "notecard")]
        [InlineData("notecard", "texture")]
        [InlineData("notecard", "gesture")]
        [InlineData("gesture", "notecard")]
        public void AnUnsupportedPairIsRefusedAtTheRequest(string inv, string asset)
        {
            LLSDAssetUploadResponse response = Request(inv, asset);

            Assert.Equal("error", response.state);
            Assert.Equal(string.Empty, response.uploader);
            Assert.False(string.IsNullOrEmpty(response.error?.message));
        }

        // The upload handler checks again, so data posted to an uploader that was issued for a pair is never stored
        // with types that do not match.
        [Theory]
        [InlineData("landmark", "landmark")]
        [InlineData("attach", "object")]
        [InlineData("wearable", "")]
        [InlineData("script", "lslbyte")]
        [InlineData("object", "texture")]
        [InlineData("texture", "sound")]
        [InlineData("notecard", "texture")]
        public void AnUnsupportedPairStoresNothing(string inv, string asset)
        {
            Outcome o = Upload(inv, asset);

            Assert.False(string.IsNullOrEmpty(o.Error));
            Assert.Null(o.Item);
            Assert.Null(o.Asset);
        }

        [Fact]
        public void ARefusedUploadDoesNotLeaveTheUploaderBusy()
        {
            BunchOfCaps bunch = NewBunch(out _);
            var refused = bunch.NewAgentInventoryRequest(new LLSDAssetUploadRequest
            {
                name = "x", inventory_type = "landmark", asset_type = "landmark",
            });
            Assert.Equal("error", refused.state);

            var next = bunch.NewAgentInventoryRequest(new LLSDAssetUploadRequest
            {
                name = "x", inventory_type = "notecard", asset_type = "notecard",
            });

            Assert.Equal("upload", next.state);
        }

        [Theory]
        [InlineData("texture", "texture")]
        [InlineData("snapshot", "texture")]
        [InlineData("sound", "sound")]
        [InlineData("animation", "animatn")]
        [InlineData("notecard", "notecard")]
        [InlineData("gesture", "gesture")]
        [InlineData("script", "lsltext")]
        [InlineData("wearable", "clothing")]
        [InlineData("wearable", "bodypart")]
        [InlineData("object", "mesh")]
        public void ASupportedPairGetsAnUploader(string inv, string asset)
        {
            LLSDAssetUploadResponse response = Request(inv, asset);

            Assert.Equal("upload", response.state);
            Assert.False(string.IsNullOrEmpty(response.uploader));
        }
    }
}
