/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using InWorldz.Phlox.VM;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using Phlox.ScriptEngine;

namespace SLua.ScriptEngine
{
    /// <summary>
    /// The engine's chat: the region's listen registry (Phlox's <see cref="PhloxListenManager"/>, with its filters, ranges and
    /// limits of listens per script and per region) and the three ways chat reaches it: chat spoken in the region, chat the
    /// region's other modules broadcast, and messages another script engine sent through the region's IWorldComm. It also
    /// offers this engine's chat to the other engines' listens through IWorldComm. The routes follow PhloxEngine's, so a
    /// region running both engines delivers every message to each engine's listens once.
    /// </summary>
    internal sealed class EngineChat
    {
        private readonly Scene m_scene;
        private IWorldComm m_worldComm;

        [ThreadStatic] private static bool t_sendingToWorldComm;

        public PhloxListenManager Listens { get; }

        public EngineChat(Scene scene, IConfigSource config, SLuaSettings settings, Func<UUID, EventParams, bool> post, Action<UUID, string> tellOwner)
        {
            m_scene = scene;
            int whisper = PhloxListenManager.DefaultWhisperDistance, say = PhloxListenManager.DefaultSayDistance, shout = PhloxListenManager.DefaultShoutDistance;
            // The region's [Chat] distances, read with the same keys and defaults as the chat module, WorldComm and Phlox.
            IConfig chat = config?.Configs["Chat"];
            if (chat != null)
            {
                whisper = chat.GetInt("whisper_distance", whisper);
                say = chat.GetInt("say_distance", say);
                shout = chat.GetInt("shout_distance", shout);
            }
            // The limits of listens per script and per region, from [LL-Functions] as the core WorldComm reads them.
            (int perScript, int perRegion) = config != null
                ? PhloxListenManager.ReadListenCaps(config, out _)
                : (PhloxListenManager.DefaultMaxListensPerScript, PhloxListenManager.DefaultMaxListensPerRegion);
            var sink = new Sink(config?.Configs[EngineIdentity.ConfigSection], post, tellOwner);
            Listens = new PhloxListenManager(sink, scene, whisper, say, shout, perScript, perRegion);
        }

        public void Subscribe()
        {
            m_scene.EventManager.OnChatFromWorld += OnChatFromWorld;
            m_scene.EventManager.OnChatFromClient += OnChatFromClient;
            m_scene.EventManager.OnChatBroadcast += OnChatBroadcast;
        }

        /// <summary>The region's world communication is resolved once every module has loaded (it may not be registered when the engine is added).</summary>
        public void Connect(IWorldComm worldComm)
        {
            if (worldComm == null || ReferenceEquals(worldComm, m_worldComm))
                return;
            m_worldComm = worldComm;
            worldComm.OnMessageDelivered += OnWorldCommMessage;
        }

        public void Unsubscribe()
        {
            m_scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
            m_scene.EventManager.OnChatFromClient -= OnChatFromClient;
            m_scene.EventManager.OnChatBroadcast -= OnChatBroadcast;
            if (m_worldComm != null)
                m_worldComm.OnMessageDelivered -= OnWorldCommMessage;
            m_worldComm = null;
        }

        /// <summary>
        /// Offers this engine's chat to the listens the region's world communication holds (the other engines'), as their own
        /// llSay, llRegionSay and llRegionSayTo do. This engine's listens have already had it, so the copy the world
        /// communication raises for it on this thread is not delivered again.
        /// </summary>
        public void SendToWorldComm(Action<IWorldComm> send)
        {
            IWorldComm worldComm = m_worldComm;
            if (worldComm == null)
                return;
            t_sendingToWorldComm = true;
            try { send(worldComm); }
            finally { t_sendingToWorldComm = false; }
        }

        private void OnChatFromWorld(object sender, OSChatMessage chat) =>
            Listens.DeliverChat(chat.Type, chat.Channel, chat.From, chat.SenderUUID, chat.Message, chat.Position, chat.Destination);

        private void OnChatFromClient(object sender, OSChatMessage chat) => DeliverSceneChat(chat, UUID.Zero);

        /// <summary>Broadcast chat (Scene.SimChatBroadcast): region modules send it (bots, the concierge, bridges); nothing else carries it.</summary>
        private void OnChatBroadcast(object sender, OSChatMessage chat) => DeliverSceneChat(chat, chat.Destination);

        private void DeliverSceneChat(OSChatMessage chat, UUID destination)
        {
            // A dialog-button reply sets chat.Sender but leaves chat.SenderUUID empty; a key-filtered listen would never match it.
            UUID speakerKey = chat.SenderUUID;
            if (speakerKey == UUID.Zero && chat.Sender != null)
                speakerKey = chat.Sender.AgentId;
            string speakerName = chat.From;
            if (string.IsNullOrEmpty(speakerName) && chat.Sender != null)
                speakerName = chat.Sender.Name;
            Listens.DeliverChat(chat.Type, chat.Channel, speakerName, speakerKey, chat.Message, chat.Position, destination);
        }

        /// <summary>
        /// A message another script engine sent through the world communication (llRegionSay and llRegionSayTo). Whisper, say
        /// and shout also go out as scene chat, which the handlers above bring here, so taking them here too would deliver them twice.
        /// </summary>
        private void OnWorldCommMessage(OSChatMessage chat)
        {
            if (t_sendingToWorldComm)
                return;
            if (chat.Type != ChatTypeEnum.Region && chat.Type != ChatTypeEnum.Direct)
                return;
            Listens.DeliverChat(chat.Type, chat.Channel, chat.From, chat.SenderUUID, chat.Message, chat.Position, chat.Destination);
        }

        /// <summary>What the listen manager asks of this engine.</summary>
        private sealed class Sink : IListenEventSink
        {
            private readonly Func<UUID, EventParams, bool> m_post;
            private readonly Action<UUID, string> m_tellOwner;

            public Sink(IConfig config, Func<UUID, EventParams, bool> post, Action<UUID, string> tellOwner)
            {
                EngineConfig = config;
                m_post = post;
                m_tellOwner = tellOwner;
            }

            public IConfig EngineConfig { get; }

            /// <summary>A heard message becomes the script's listen event: channel, name, key, message.</summary>
            public void PostEvent(UUID itemId, PostedEvent evt) => m_post(itemId, new EventParams("listen", evt.Args, null));

            /// <summary>A listen is not part of the state this engine saves yet, so there is nothing to mark.</summary>
            public void ScriptChangedOutsideARun(UUID itemId) { }

            public void ShoutError(UUID itemId, string message) => m_tellOwner(itemId, message);

            /// <summary>The engine has no pass of its own for the listen manager to ask for.</summary>
            public void Wake() { }
        }
    }
}
