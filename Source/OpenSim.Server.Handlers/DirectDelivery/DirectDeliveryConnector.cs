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
using System.Reflection;
using Nini.Config;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.DirectDelivery
{
    // Direct Delivery — marketplace delivery adapter #1 (OPTIONAL grid module).
    //
    // A Robust ServiceConnector that exposes an authenticated POST /delivery on the private
    // service host. Given a shared secret + a buyer's local first/last name, it writes a
    // placeholder notecard straight into that buyer's inventory (server-side, no Scene, no
    // hypergrid). Loaded via config + ServerUtils.LoadPlugin (NOT Mono.Addins), so no addin-db
    // handling is required. Structured to be upstreamable to NGC/Tranquillity as an optional
    // module — enable it only if the operator opts into direct delivery.
    //
    // SECURITY: this handler enforces its OWN BasicHttpAuthentication (shared secret) and MUST
    // NOT rely on the surrounding private-port (8003) posture being open/unauthenticated.
    // 8003 hardening is a separate future task; this endpoint is fail-closed and stays correct
    // after it — it refuses to start unless a shared secret is configured.
    public class DirectDeliveryConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string m_ConfigName = "DirectDeliveryService";

        // Default non-zero CreatorID so a viewer accepts the delivered item (overridable in config).
        private const string DefaultCreatorID = "10000000-0000-0000-0000-000000000001";

        public DirectDeliveryConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            if (!string.IsNullOrEmpty(configName))
                m_ConfigName = configName;

            IConfig cfg = config.Configs[m_ConfigName];
            if (cfg == null)
                throw new Exception(string.Format("No section '{0}' in config file", m_ConfigName));

            if (!cfg.GetBoolean("Enabled", false))
            {
                m_log.InfoFormat("[DirectDelivery]: disabled (set Enabled=true in [{0}] to enable)", m_ConfigName);
                return;
            }

            // Load the grid services we deliver through. Each is read with the section name it
            // expects so it picks up the existing [InventoryService]/[AssetService]/[UserAccountService]
            // + [DatabaseService] config — we do not duplicate DB settings.
            string invModule = cfg.GetString("InventoryService", "OpenSim.Services.InventoryService.dll:XInventoryService");
            string assetModule = cfg.GetString("AssetService", "OpenSim.Services.AssetService.dll:AssetService");
            string userModule = cfg.GetString("UserAccountService", "OpenSim.Services.UserAccountService.dll:UserAccountService");

            IInventoryService inventory = ServerUtils.LoadPlugin<IInventoryService>(invModule, new object[] { config, "InventoryService" });
            IAssetService asset = ServerUtils.LoadPlugin<IAssetService>(assetModule, new object[] { config, "AssetService" });
            IUserAccountService users = ServerUtils.LoadPlugin<IUserAccountService>(userModule, new object[] { config });

            if (inventory == null || asset == null || users == null)
                throw new Exception("[DirectDelivery]: failed to load InventoryService / AssetService / UserAccountService");

            // Optional live-notification service (Option A). Loaded by DLL-name at runtime (no compile-time
            // reference to the Hypergrid assembly). Defensive: if it can't load, log and pass null — the
            // handler then files the item silently (it appears on the buyer's relog, as before).
            string imModule = cfg.GetString("NotifyIMService", "OpenSim.Services.HypergridService.dll:HGInstantMessageService");
            IInstantMessage im = null;
            try { im = ServerUtils.LoadPlugin<IInstantMessage>(imModule, new object[] { config }); }
            catch (Exception e) { m_log.Warn("[DirectDelivery]: live-notify IM service failed to load; deliveries will file silently", e); }
            if (im == null)
                m_log.Warn("[DirectDelivery]: no IM service — online buyers see deliveries only after relog");

            // Fail-closed authentication. ServiceAuth.Create alone can return a non-null auth that
            // only blocks LL-viewer requests (DisallowLlHttpRequest) without requiring the secret,
            // so we explicitly require AuthType=BasicHttpAuthentication + credentials here.
            string authType = Util.GetConfigVarFromSections<string>(config, "AuthType", new string[] { "Network", m_ConfigName }, "None");
            string user = Util.GetConfigVarFromSections<string>(config, "HttpAuthUsername", new string[] { "Network", m_ConfigName }, string.Empty);
            string pass = Util.GetConfigVarFromSections<string>(config, "HttpAuthPassword", new string[] { "Network", m_ConfigName }, string.Empty);
            if (authType != "BasicHttpAuthentication" || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                throw new Exception("[DirectDelivery]: refusing to start without a shared secret — set AuthType=BasicHttpAuthentication and HttpAuthUsername/HttpAuthPassword in [" + m_ConfigName + "]");

            IServiceAuth auth = ServiceAuth.Create(config, m_ConfigName);

            if (!UUID.TryParse(cfg.GetString("CreatorID", DefaultCreatorID), out UUID creatorID) || creatorID.IsZero())
                creatorID = new UUID(DefaultCreatorID);

            server.AddStreamHandler(new DirectDeliveryPostHandler(users, asset, inventory, creatorID, im, auth));

            m_log.InfoFormat("[DirectDelivery]: enabled — POST /delivery (authenticated), CreatorID {0}", creatorID);
        }
    }
}
