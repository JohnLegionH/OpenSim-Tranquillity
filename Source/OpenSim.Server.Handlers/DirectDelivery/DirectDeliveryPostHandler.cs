/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.DirectDelivery
{
    // POST /delivery — JSON in/out (chosen for the external .NET marketplace caller; this diverges
    // from the sibling connectors' form-encoded/XML convention on purpose). BaseStreamHandler
    // authenticates the shared secret BEFORE this body runs (401 on failure).
    //
    // Two delivery paths, selected by the request:
    //   * NOTECARD (default): synthesizes a placeholder empty notecard (no seller assets).
    //   * OBJECT (additive): given "source_inventory_id", delivers a REFERENCE-IN-PLACE clone of an
    //     existing grid object — clones the source item's metadata/type-triple, points at the same
    //     object asset (no copy, same grid), preserves CreatorID, buyer owns it with full perms,
    //     files it into Objects. Verifies the object's dependency closure exists before filing.
    //
    // Request:  { "first_name":"...", "last_name":"...", "item_name":"..."(opt),
    //             "source_inventory_id":"<uuid>"(opt -> object path) }
    // Response: 200 { status:"delivered", principal_id, inventory_item_id, asset_id }
    //           404 { status:"not_found" }   400 { status:"error" }
    //           409 { status:"closure_incomplete", missing:[...] }   500 { status:"error" }
    //           401 handled by BaseStreamHandler (auth fails before this runs)
    public class DirectDeliveryPostHandler : BaseStreamHandler
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IUserAccountService m_Users;
        private readonly IAssetService m_Assets;
        private readonly IInventoryService m_Inventory;
        private readonly UUID m_CreatorID;
        private readonly IInstantMessage m_IM;   // optional; null => no live notification (still delivers)

        private const string PlaceholderDesc = "Legion Market placeholder";

        public DirectDeliveryPostHandler(
            IUserAccountService users, IAssetService assets, IInventoryService inventory,
            UUID creatorID, IInstantMessage im, IServiceAuth auth) :
                base("POST", "/delivery", auth)
        {
            m_Users = users;
            m_Assets = assets;
            m_Inventory = inventory;
            m_CreatorID = creatorID;
            m_IM = im;
        }

        protected override byte[] ProcessRequest(string path, Stream requestData,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string body;
            using (StreamReader sr = new StreamReader(requestData))
                body = sr.ReadToEnd();

            string first = null, last = null, itemNameOverride = null, sourceInvIdStr = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("first_name", out JsonElement f) && f.ValueKind == JsonValueKind.String)
                    first = f.GetString();
                if (root.TryGetProperty("last_name", out JsonElement l) && l.ValueKind == JsonValueKind.String)
                    last = l.GetString();
                if (root.TryGetProperty("item_name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(n.GetString()))
                    itemNameOverride = n.GetString();
                // Object-delivery selector (ADDITIVE): a source inventory id switches to the
                // reference-in-place real-object path; absent => the placeholder notecard path.
                if (root.TryGetProperty("source_inventory_id", out JsonElement s) && s.ValueKind == JsonValueKind.String)
                    sourceInvIdStr = s.GetString();
            }
            catch (Exception)
            {
                return Json(httpResponse, 400, "{\"status\":\"error\",\"reason\":\"invalid JSON body\"}");
            }

            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
                return Json(httpResponse, 400, "{\"status\":\"error\",\"reason\":\"first_name and last_name are required\"}");

            // Resolve name -> PrincipalID. Unknown name returns null and creates NOTHING.
            UserAccount account = m_Users.GetUserAccount(UUID.Zero, first, last);
            if (account == null)
                return Json(httpResponse, 404, "{\"status\":\"not_found\",\"reason\":\"no local account for that name\"}");

            UUID buyer = account.PrincipalID;

            // ADDITIVE object-delivery path: a source inventory id => deliver a reference-in-place
            // clone of that real object. Absent => fall through to the placeholder notecard path
            // (unchanged below).
            if (UUID.TryParse(sourceInvIdStr, out UUID sourceInvId) && sourceInvId.IsNotZero())
                return DeliverReferencedObject(buyer, first, last, sourceInvId, itemNameOverride, httpResponse);

            string itemName = itemNameOverride ?? "Legion Market Delivery";
            try
            {
                // Ensure the buyer's inventory skeleton exists (idempotent).
                m_Inventory.CreateUserInventory(buyer);

                // A notecard belongs in the Notecards folder (FolderType != AssetType).
                // Fall back to the root folder if the typed folder is missing.
                InventoryFolderBase folder = m_Inventory.GetFolderForType(buyer, FolderType.Notecard);
                if (folder == null)
                    folder = m_Inventory.GetRootFolder(buyer);
                if (folder == null)
                    return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"buyer has no inventory root\"}");

                // Synthesize the placeholder notecard asset (ready-made empty-notecard body).
                UUID assetId = UUID.Random();
                AssetBase asset = new AssetBase(assetId, itemName, (sbyte)AssetType.Notecard, m_CreatorID.ToString())
                {
                    Description = PlaceholderDesc,
                    Data = Constants.EmptyNotecardData
                };
                string storedId = m_Assets.Store(asset);
                if (string.IsNullOrEmpty(storedId) || storedId == UUID.Zero.ToString())
                    return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"asset store failed\"}");

                // File the inventory item — Folder set EXPLICITLY (XInventoryService.AddItem does not
                // auto-file a UUID.Zero folder), correct type-triple, non-zero CreatorId, full owner perms.
                uint perms = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Modify
                    | OpenSim.Framework.PermissionMask.Transfer | OpenSim.Framework.PermissionMask.Move);
                InventoryItemBase item = new InventoryItemBase
                {
                    ID = UUID.Random(),
                    Owner = buyer,
                    Folder = folder.ID,
                    CreatorId = m_CreatorID.ToString(),
                    AssetID = new UUID(storedId),
                    AssetType = (int)AssetType.Notecard,
                    InvType = (int)InventoryType.Notecard,
                    Name = itemName,
                    Description = PlaceholderDesc,
                    BasePermissions = perms,
                    CurrentPermissions = perms,
                    NextPermissions = perms,
                    EveryOnePermissions = 0,
                    GroupPermissions = 0,
                    GroupID = UUID.Zero,
                    SalePrice = 0,
                    SaleType = 0,
                    Flags = 0,
                    CreationDate = Util.UnixTimeSinceEpoch()
                };

                if (!m_Inventory.AddItem(item))
                    return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"inventory add failed\"}");

                m_log.InfoFormat("[DirectDelivery]: delivered notecard item {0} (asset {1}) to {2} {3} [{4}] in folder {5}",
                    item.ID, item.AssetID, first, last, buyer, folder.ID);

                // Additive live notification — must never affect the delivery result (item is already filed).
                NotifyOnline(buyer, item.ID, itemName, (sbyte)AssetType.Notecard);

                string ok = "{\"status\":\"delivered\",\"principal_id\":\"" + buyer +
                    "\",\"inventory_item_id\":\"" + item.ID +
                    "\",\"asset_id\":\"" + item.AssetID + "\"}";
                return Json(httpResponse, 200, ok);
            }
            catch (Exception e)
            {
                m_log.Error("[DirectDelivery]: delivery failed", e);
                return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"internal error\"}");
            }
        }

        // ADDITIVE object-delivery path. Delivers a REFERENCE-IN-PLACE clone of an existing grid
        // object: clones the source inventory item's metadata + type-triple, points the delivered
        // item at the SAME object asset (no copy — same grid), PRESERVES the original CreatorID
        // (provenance), sets the buyer as owner with full perms, and files it into the buyer's
        // Objects folder. Before filing it walks the object's one-level dependency closure (face
        // textures, sculpt/mesh, task-inventory scripts) and confirms every referenced asset already
        // exists in the store; if any are missing it refuses to deliver a broken item and reports
        // them. Copies nothing. (Deep/cross-grid gathering + copy-to-vault is the deferred build.)
        private byte[] DeliverReferencedObject(UUID buyer, string first, string last,
                UUID sourceInvId, string itemNameOverride, IOSHttpResponse httpResponse)
        {
            try
            {
                // Ensure the buyer's inventory skeleton exists (idempotent).
                m_Inventory.CreateUserInventory(buyer);

                // Resolve the source item. XInventoryService.GetItem keys on the item id; the
                // principal arg is not used for the lookup, so no source owner is required.
                InventoryItemBase src = m_Inventory.GetItem(UUID.Zero, sourceInvId);
                if (src == null)
                    return Json(httpResponse, 404, "{\"status\":\"not_found\",\"reason\":\"source inventory item not found\"}");
                if (src.AssetType != (int)AssetType.Object || src.InvType != (int)InventoryType.Object)
                    return Json(httpResponse, 400, "{\"status\":\"error\",\"reason\":\"source item is not an object\"}");

                // Load the object asset (SceneObjectGroup XML) for closure verification.
                AssetBase objAsset = m_Assets.Get(src.AssetID.ToString());
                if (objAsset == null || objAsset.Data == null || objAsset.Data.Length == 0)
                    return Json(httpResponse, 409, "{\"status\":\"error\",\"reason\":\"object asset missing from store\"}");

                // READ-ONLY closure verification — confirm every referenced asset exists. No copy.
                List<UUID> refs = GatherClosureRefs(objAsset);
                List<string> missing = new List<string>();
                if (refs.Count > 0)
                {
                    string[] ids = refs.Select(u => u.ToString()).ToArray();
                    bool[] exist = m_Assets.AssetsExist(ids);
                    for (int i = 0; i < ids.Length; i++)
                        if (exist == null || i >= exist.Length || !exist[i])
                            missing.Add(ids[i]);
                }
                if (missing.Count > 0)
                {
                    m_log.WarnFormat("[DirectDelivery]: closure INCOMPLETE for object asset {0} — {1} of {2} refs missing: {3}",
                        src.AssetID, missing.Count, refs.Count, string.Join(",", missing));
                    string mbody = "{\"status\":\"closure_incomplete\",\"reason\":\"referenced assets missing from store\",\"missing\":[\""
                        + string.Join("\",\"", missing) + "\"]}";
                    return Json(httpResponse, 409, mbody);
                }

                // Objects folder (FolderType.Object != AssetType.Object); fall back to root.
                InventoryFolderBase folder = m_Inventory.GetFolderForType(buyer, FolderType.Object);
                if (folder == null)
                    folder = m_Inventory.GetRootFolder(buyer);
                if (folder == null)
                    return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"buyer has no inventory root\"}");

                string name = string.IsNullOrWhiteSpace(itemNameOverride) ? src.Name : itemNameOverride;

                // Full owner perms for the spike (freely rezzable/testable). Owner = buyer;
                // CreatorID preserved from the source (provenance). Reference-in-place: the item
                // points at the EXISTING object asset — no AssetService.Store.
                uint perms = (uint)(OpenSim.Framework.PermissionMask.Copy | OpenSim.Framework.PermissionMask.Modify
                    | OpenSim.Framework.PermissionMask.Transfer | OpenSim.Framework.PermissionMask.Move);
                InventoryItemBase item = new InventoryItemBase
                {
                    ID = UUID.Random(),
                    Owner = buyer,
                    Folder = folder.ID,
                    CreatorId = src.CreatorId,          // preserve original creator (provenance)
                    CreatorData = src.CreatorData,
                    AssetID = src.AssetID,              // reference-in-place: existing object asset
                    AssetType = (int)AssetType.Object,
                    InvType = (int)InventoryType.Object,
                    Name = name,
                    Description = src.Description,
                    BasePermissions = perms,
                    CurrentPermissions = perms,
                    NextPermissions = perms,
                    EveryOnePermissions = 0,
                    GroupPermissions = 0,
                    GroupID = UUID.Zero,
                    SalePrice = 0,
                    SaleType = 0,
                    Flags = src.Flags,
                    CreationDate = Util.UnixTimeSinceEpoch()
                };

                if (!m_Inventory.AddItem(item))
                    return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"inventory add failed\"}");

                m_log.InfoFormat("[DirectDelivery]: delivered OBJECT item {0} (asset {1}, closure {2} refs verified) to {3} {4} [{5}] in folder {6}",
                    item.ID, item.AssetID, refs.Count, first, last, buyer, folder.ID);

                // Live notification — asset type Object so the region fetches the right kind.
                NotifyOnline(buyer, item.ID, name, (sbyte)AssetType.Object);

                string ok = "{\"status\":\"delivered\",\"principal_id\":\"" + buyer +
                    "\",\"inventory_item_id\":\"" + item.ID +
                    "\",\"asset_id\":\"" + item.AssetID + "\"}";
                return Json(httpResponse, 200, ok);
            }
            catch (Exception e)
            {
                m_log.Error("[DirectDelivery]: object delivery failed", e);
                return Json(httpResponse, 500, "{\"status\":\"error\",\"reason\":\"internal error\"}");
            }
        }

        // Regexes over the SceneObjectGroup XML for the one-level closure walk. Same-grid presence
        // check only — NOT a substitute for UuidGatherer's deep/cross-grid gathering in the vault build.
        private static readonly Regex s_texEntry = new Regex("<TextureEntry>([A-Za-z0-9+/=]+)</TextureEntry>", RegexOptions.Compiled);
        private static readonly Regex s_sculpt = new Regex("<SculptTexture><UUID>([0-9a-fA-F-]{36})</UUID></SculptTexture>", RegexOptions.Compiled);
        private static readonly Regex s_assetId = new Regex("<AssetID><UUID>([0-9a-fA-F-]{36})</UUID></AssetID>", RegexOptions.Compiled);

        // Return the distinct non-zero asset UUIDs an object asset references: face textures
        // (decoded from the packed TextureEntry via OpenMetaverse), sculpt/mesh textures, and
        // task-inventory item assets (scripts etc.). The object asset id itself is excluded.
        private List<UUID> GatherClosureRefs(AssetBase objAsset)
        {
            HashSet<UUID> found = new HashSet<UUID>();
            string xml = Util.UTF8NoBomEncoding.GetString(objAsset.Data);

            // Face textures — decode each packed TextureEntry.
            foreach (Match m in s_texEntry.Matches(xml))
            {
                try
                {
                    byte[] teBytes = Convert.FromBase64String(m.Groups[1].Value);
                    Primitive.TextureEntry te = new Primitive.TextureEntry(teBytes, 0, teBytes.Length);
                    if (te.DefaultTexture != null)
                        AddIf(found, te.DefaultTexture.TextureID);
                    if (te.FaceTextures != null)
                        foreach (Primitive.TextureEntryFace face in te.FaceTextures)
                            if (face != null)
                                AddIf(found, face.TextureID);
                }
                catch (Exception e)
                {
                    m_log.Warn("[DirectDelivery]: could not parse a TextureEntry during closure walk", e);
                }
            }

            // Sculpt / mesh textures.
            foreach (Match m in s_sculpt.Matches(xml))
                if (UUID.TryParse(m.Groups[1].Value, out UUID sc))
                    AddIf(found, sc);

            // Task-inventory item assets (scripts, embedded notecards, sub-object assets, ...).
            // In a SceneObjectGroup, <AssetID> appears only inside task-inventory items.
            foreach (Match m in s_assetId.Matches(xml))
                if (UUID.TryParse(m.Groups[1].Value, out UUID a))
                    AddIf(found, a);

            found.Remove(objAsset.FullID);   // the object asset itself is already present
            return found.ToList();
        }

        private static void AddIf(HashSet<UUID> set, UUID id)
        {
            if (id.IsNotZero())
                set.Add(id);
        }

        // Live inventory notification (ADDITIVE). After the item is filed, hand an InventoryOffered IM
        // to the in-process messaging service. For an ONLINE buyer the region's InventoryTransferModule
        // fetches the item and calls SendBulkUpdateInventory (item appears live) and shows the
        // keep/discard toast; an OFFLINE buyer's IM is queued by OfflineIM (delivered next login). Any
        // failure here is swallowed — the item is already in inventory, so delivery still succeeds.
        //
        // LIMITATION (Option A — in-process HGInstantMessageService): this instance sends with an EMPTY
        // messageKey, because HGInstantMessageService.m_messageKey is a PER-INSTANCE field set only in
        // the first (InstantMessageServerConnector-owned) instance's ctor; the static m_Initialized guard
        // makes our second instance skip that init. This is correct ONLY while [Messaging] MessageKey is
        // empty (it is on this grid). If a MessageKey is ever configured, the region will reject this
        // forward and live-notify silently degrades to "appears on relog" (item still filed, no crash).
        // The fix then is Option C: read [Messaging] MessageKey from config and call the static
        // InstantMessageServiceConnector.SendInstantMessage(regionURI, gim, key) — which adds an
        // OpenSim.Services.Connectors assembly reference.
        private void NotifyOnline(UUID buyer, UUID itemID, string itemName, sbyte assetType)
        {
            if (m_IM == null)
                return;
            try
            {
                // InventoryOffered bucket: [0] = asset type, [1..17] = item id (see
                // InventoryTransferModule.OnGridInstantMessage, which reads exactly this).
                byte[] bucket = new byte[17];
                bucket[0] = (byte)assetType;
                Array.Copy(itemID.GetBytes(), 0, bucket, 1, 16);

                GridInstantMessage gim = new GridInstantMessage
                {
                    fromAgentID = m_CreatorID.Guid,
                    fromAgentName = "Legion Market",
                    toAgentID = buyer.Guid,
                    dialog = (byte)InstantMessageDialog.InventoryOffered,
                    fromGroup = false,
                    message = itemName,
                    imSessionID = UUID.Random().Guid,
                    offline = 0,
                    Position = Vector3.Zero,
                    binaryBucket = bucket,
                    ParentEstateID = 0,
                    RegionID = Guid.Empty,
                    timestamp = (uint)Util.UnixTimeSinceEpoch()
                };
                m_IM.IncomingInstantMessage(gim);
            }
            catch (Exception e)
            {
                // Non-fatal: item is already filed; it appears on relog even if this notification fails.
                m_log.Warn("[DirectDelivery]: live-notify IM failed (item already delivered)", e);
            }
        }

        private static byte[] Json(IOSHttpResponse response, int status, string payload)
        {
            response.StatusCode = status;
            response.ContentType = "application/json";
            return Util.UTF8NoBomEncoding.GetBytes(payload);
        }
    }
}
