/*
 * Copyright (c) InWorldz Halcyon Developers
 * Copyright (c) Contributors, http://opensimulator.org/
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
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

// Ported from Halcyon/InWorldz to Legion Grid (dotnet10-modernization)
// Adaptations:
//   - HttpRequestObject replaced with IHttpServiceRequest interface (no concrete cast)
//   - Uses PostObjectEvent by LocalID; PHLOX-55: another engine's response goes through the region's other engines

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ScriptEngine.Shared.Api.Plugins
{
    public class HttpRequest
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public AsyncCommandManager m_CmdManager;

        public HttpRequest(AsyncCommandManager CmdManager)
        {
            m_CmdManager = CmdManager;
        }

        public void CheckHttpRequests()
        {
            if (m_CmdManager.m_ScriptEngine.World == null)
                return;

            IHttpRequestModule iHttpReq =
                m_CmdManager.m_ScriptEngine.World.RequestModuleInterface<IHttpRequestModule>();

            if (iHttpReq == null)
                return;

            // Use the IHttpServiceRequest interface only: HttpRequestClass is defined in
            // OpenSim.Region.CoreModules, which develop's McMaster-based plugin loader may
            // load into a different AssemblyLoadContext than this assembly, making a direct
            // cast to the concrete type fail with an InvalidCastException even though the
            // type name matches. The interface exposes Status/ResponseBody safely.
            IHttpServiceRequest req = iHttpReq.GetNextCompletedRequest();
            while (req != null)
            {
                iHttpReq.RemoveCompletedRequest(req.ReqID);

                switch (Complete(req))
                {
                    case Owner.Dropped:
                        // PHLOX-46: the script that asked is gone or was reset since - dropped here, never queued.
                        System.Threading.Interlocked.Increment(ref m_Dropped);
                        if (m_log.IsEnabled(LogLevel.Debug))
                            m_log.LogDebug("[Phlox HTTP]: late http_response {0} for {1} dropped (script reset or gone)", req.ReqID, req.ItemID);
                        break;

                    case Owner.OtherEngine:
                        // PHLOX-55: the core's one completed queue is drained by every engine's pump, so this can be a
                        // YEngine script's response. It goes out exactly as YEngine's own pump sends it.
                        PostAsYEngine(req);
                        break;

                    default:
                    {
                        object[] resobj = new object[]
                        {
                            req.ReqID.ToString(),
                            req.Status,
                            new object[0],   // metadata — HTTP_BODY_TRUNCATED not implemented
                            req.ResponseBody
                        };

                        bool posted = m_CmdManager.m_ScriptEngine.PostObjectEvent(req.LocalID,
                            new EventParams("http_response", resobj, Array.Empty<DetectParams>()));

                        if (m_log.IsEnabled(LogLevel.Debug))
                            m_log.LogDebug("[Phlox HTTP]: http_response {0} status {1} -> prim {2} (accepted={3})",
                                req.ReqID, resobj[1], req.LocalID, posted);
                        break;
                    }
                }

                req = iHttpReq.GetNextCompletedRequest();
            }
        }

        /// <summary>
        /// PHLOX-55: a response this pump took for a script another engine runs. YEngine's pump
        /// (OpenSim.Region.ScriptEngine.Shared/Api/Plugins/HttpRequest.cs) builds these arguments and offers the event to
        /// each engine's PostObjectEvent in turn, stopping at the first that takes it. The same is done here through the
        /// region's other script engines. Phlox is left out: its PostObjectEvent accepts any prim that exists, and it
        /// does not run this script.
        /// </summary>
        private void PostAsYEngine(IHttpServiceRequest req)
        {
            object[] resobj = new object[]
            {
                new LSL_Types.LSLString(req.ReqID.ToString()),
                new LSL_Types.LSLInteger(req.Status),
                new LSL_Types.list(),
                new LSL_Types.LSLString(req.ResponseBody)
            };

            bool posted = false;
            IScriptModule[] engines = m_CmdManager.m_ScriptEngine.World?.RequestModuleInterfaces<IScriptModule>() ?? Array.Empty<IScriptModule>();
            foreach (IScriptModule m in engines)
            {
                if (ReferenceEquals(m, m_CmdManager.m_ScriptEngine) || m is not IScriptEngine e)
                    continue;
                if (e.PostObjectEvent(req.LocalID, new EventParams("http_response", resobj, new DetectParams[0])))
                {
                    posted = true;
                    break;
                }
            }

            if (m_log.IsEnabled(LogLevel.Debug))
                m_log.LogDebug("[Phlox HTTP]: http_response {0} status {1} for another engine's script {2} -> prim {3} (accepted={4})",
                    req.ReqID, req.Status, req.ItemID, req.LocalID, posted);
        }

        // ── PHLOX-46: requests belong to the script that made them (HALCYON-DIFF S12) ──
        //
        // The core stops a script's PENDING requests (HttpRequestModule.StopHttpRequest), but one that has already
        // completed stays in its completed queue and would still be posted. So Phlox keeps the ids of its scripts'
        // outstanding requests; a reset or removal forgets them, and a response whose id is no longer here is dropped
        // when its script is a Phlox script (it was reset) or is no longer in the prim (it was deleted). Anything else
        // is another engine's request that this pump happened to take; PHLOX-55 posts it through that engine.

        private readonly object m_TrackLock = new object();
        private readonly Dictionary<UUID, UUID> m_Outstanding = new();   // request id -> script item id
        private long m_Dropped;

        /// <summary>
        /// llHTTPRequest: start the request and record it in one step. The core can complete a request before
        /// StartHttpRequest returns (a filtered URL), and the pump must not see it untracked.
        /// </summary>
        internal UUID Start(UUID itemID, Func<UUID> start)
        {
            lock (m_TrackLock)
            {
                UUID reqID = start();
                if (!reqID.IsZero()) m_Outstanding[reqID] = itemID;
                return reqID;
            }
        }

        private enum Owner { Phlox, OtherEngine, Dropped }

        /// <summary>
        /// Whose response this is: a live request of a Phlox script, a request of a script another engine runs (PHLOX-55),
        /// or one to drop (a Phlox script reset since it asked, or a script or prim that is gone).
        /// </summary>
        private Owner Complete(IHttpServiceRequest req)
        {
            lock (m_TrackLock)
                if (m_Outstanding.Remove(req.ReqID)) return Owner.Phlox;

            if (m_CmdManager.m_ScriptEngine is global::Phlox.ScriptEngine.PhloxEngine phlox && phlox.HasOrIsLoading(req.ItemID))
                return Owner.Dropped;   // a Phlox script that has been reset since it asked
            SceneObjectPart part = m_CmdManager.m_ScriptEngine.World?.GetSceneObjectPart(req.LocalID);
            return part?.Inventory?.GetInventoryItem(req.ItemID) != null
                ? Owner.OtherEngine
                : Owner.Dropped;   // gone with its script or prim
        }

        /// <summary>
        /// PHLOX-46: the script is reset or removed. Its requests are forgotten and the core stops the ones still in flight
        /// (Halcyon AsyncCommandManager.RemoveScript: iHttpReq.StopHttpRequest(localID, itemID)).
        /// </summary>
        public void RemoveEvents(uint localID, OpenMetaverse.UUID itemID)
        {
            lock (m_TrackLock)
            {
                List<UUID> mine = null;
                foreach (var kvp in m_Outstanding)
                    if (kvp.Value == itemID) (mine ??= new List<UUID>()).Add(kvp.Key);
                if (mine != null)
                    foreach (UUID id in mine) m_Outstanding.Remove(id);
            }
            m_CmdManager.m_ScriptEngine.World?.RequestModuleInterface<IHttpRequestModule>()?.StopHttpRequest(localID, itemID);
        }

        internal int OutstandingCount { get { lock (m_TrackLock) return m_Outstanding.Count; } }
        internal long DroppedResponses => System.Threading.Interlocked.Read(ref m_Dropped);
    }
}
