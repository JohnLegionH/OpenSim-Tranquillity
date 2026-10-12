/*
 * Copyright (c) Legion Builds
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

using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Reflection;
using System.Text;

using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.LindenCaps;
using OpenSim.Region.CoreModules.World.Estate;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using RegionFlags = OpenMetaverse.RegionFlags;

namespace OpenSim.Region.ClientStack.Linden.Tests
{
    /// <summary>
    /// The EstateChangeInfo capability saves the estate's settings from the viewer's Region/Estate floater. The
    /// viewer does not put every setting in every request, and the region owns all of them, so a setting the request
    /// leaves out must stay as it is, while a setting the request carries as false must be saved as false.
    /// </summary>
    // Test grouping: each test builds its own scene and touches no process-wide state (the class does not derive
    // from OpenSimTestCase, which clears MainServer), so it runs in parallel.
    public class EstateChangeInfoCapTests
    {
        private static readonly UUID s_owner = new UUID("3b8e6f1a-92c4-4d07-a5e1-7c0d4b9f2e61");

        private sealed class Harness
        {
            public readonly Scene Scene;
            public readonly EstateManagementModule Estate = new EstateManagementModule();
            public readonly EstateChangeInfoCapModule Cap = new EstateChangeInfoCapModule();
            public readonly RecordingEstateStore Store;

            public EstateSettings Settings => Scene.RegionInfo.EstateSettings;

            public Harness(bool taxFree = false, bool publicAccess = true, bool allowVoice = true,
                bool denyAnonymous = false, bool denyMinors = false, bool allowDirectTeleport = true,
                bool allowEnvironmentOverride = true)
            {
                Scene = new SceneHelpers().SetupScene();
                Scene.RegisterModuleInterface<IEstateDataService>(RecordingEstateStore.Create(out Store));

                IConfigSource config = new IniConfigSource();
                config.AddConfig("ClientStack.LindenCaps").Set("Cap_EstateChangeInfo", "localhost");
                SceneHelpers.SetupSceneModules(Scene, config, Estate, Cap);

                Settings.EstateOwner = s_owner;
                Settings.TaxFree = taxFree;
                Settings.PublicAccess = publicAccess;
                Settings.AllowVoice = allowVoice;
                Settings.DenyAnonymous = denyAnonymous;
                Settings.DenyMinors = denyMinors;
                Settings.AllowDirectTeleport = allowDirectTeleport;
                Settings.AllowEnvironmentOverride = allowEnvironmentOverride;

                SceneHelpers.AddScenePresence(Scene, s_owner);
            }

            /// <summary>Posts the given keys to the capability as the estate owner and returns the HTTP status.</summary>
            public int Post(params (string key, bool value)[] carried)
            {
                OSDMap body = new OSDMap
                {
                    ["estate_name"] = "Test Estate",
                    ["invoice"] = UUID.Random(),
                };
                foreach ((string key, bool value) in carried)
                    body[key] = value;

                TestOSHttpResponse response = new TestOSHttpResponse();
                Cap.ProcessRequest(new PostRequest(OSDParser.SerializeLLSDXmlBytes(body)), response, s_owner, null);
                return response.StatusCode;
            }

            public uint Flags => Estate.GetEstateFlags();
        }

        /// <summary>An IEstateDataService that counts StoreEstateSettings and does nothing else.</summary>
        public class RecordingEstateStore : DispatchProxy
        {
            private int m_stores;

            public int Stores => Volatile.Read(ref m_stores);

            public static IEstateDataService Create(out RecordingEstateStore recorder)
            {
                IEstateDataService proxy = Create<IEstateDataService, RecordingEstateStore>();
                recorder = (RecordingEstateStore)(object)proxy;
                return proxy;
            }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == nameof(IEstateDataService.StoreEstateSettings))
                    Interlocked.Increment(ref m_stores);
                Type rt = method.ReturnType;
                return rt == typeof(void) || !rt.IsValueType ? null : Activator.CreateInstance(rt);
            }
        }

        private sealed class PostRequest : IOSHttpRequest
        {
            public PostRequest(byte[] body)
            {
                InputStream = new MemoryStream(body);
            }

            public string[] AcceptTypes => Array.Empty<string>();
            public Encoding ContentEncoding => Encoding.UTF8;
            public long ContentLength => InputStream.Length;
            public long ContentLength64 => InputStream.Length;
            public string ContentType => "application/llsd+xml";
            public bool HasEntityBody => true;
            public NameValueCollection Headers { get; } = new();
            public string HttpMethod => "POST";
            public Stream InputStream { get; }
            public bool IsSecured => false;
            public bool KeepAlive => false;
            public NameValueCollection QueryString { get; } = new();
            public Hashtable Query { get; } = new();
            public HashSet<string> QueryFlags { get; } = new();
            public Dictionary<string, string> QueryAsDictionary { get; } = new();
            public string RawUrl => string.Empty;
            public IPEndPoint RemoteIPEndPoint => new(IPAddress.Loopback, 9000);
            public IPEndPoint LocalIPEndPoint => new(IPAddress.Any, 0);
            public Uri Url => new("http://localhost/");
            public string UriPath => string.Empty;
            public string UserAgent => string.Empty;
            public double ArrivalTS => 0;
        }

        // The estate's "allow parcel access override" is stored negated, as TaxFree. A save that does not mention
        // override_public_access used to read the stored value, negate it, and store the result, so every such save
        // turned the setting over.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ASaveWithoutOverridePublicAccessLeavesTaxFreeAsItWas(bool taxFree)
        {
            Harness h = new Harness(taxFree: taxFree);

            Assert.Equal((int)HttpStatusCode.OK, h.Post(("deny_age_unverified", true)));

            Assert.Equal(taxFree, h.Settings.TaxFree);
            Assert.True(h.Store.Stores > 0, "the estate was not stored");
        }

        [Fact]
        public void ASaveWithoutAllowVoiceChatLeavesVoiceOn()
        {
            Harness h = new Harness(allowVoice: true);

            h.Post(("deny_anonymous", true));

            Assert.True(h.Settings.AllowVoice);
        }

        [Fact]
        public void ASaveWithoutAllowVoiceChatLeavesVoiceOff()
        {
            Harness h = new Harness(allowVoice: false);

            h.Post(("deny_anonymous", true));

            Assert.False(h.Settings.AllowVoice);
            Assert.Equal(0u, h.Flags & (uint)RegionFlags.AllowVoice);
        }

        [Fact]
        public void ASaveCarryingNoSettingsChangesNone()
        {
            Harness h = new Harness(taxFree: true, publicAccess: true, allowVoice: true, denyAnonymous: true,
                denyMinors: true, allowDirectTeleport: true, allowEnvironmentOverride: true);
            uint flagsBefore = h.Flags;

            Assert.Equal((int)HttpStatusCode.OK, h.Post());

            Assert.True(h.Settings.TaxFree);
            Assert.True(h.Settings.PublicAccess);
            Assert.True(h.Settings.AllowVoice);
            Assert.True(h.Settings.DenyAnonymous);
            Assert.True(h.Settings.DenyMinors);
            Assert.True(h.Settings.AllowDirectTeleport);
            Assert.True(h.Settings.AllowEnvironmentOverride);
            Assert.Equal(flagsBefore, h.Flags);
        }

        [Fact]
        public void ASaveCarryingFalseSavesFalse()
        {
            Harness h = new Harness(taxFree: false, publicAccess: true, allowVoice: true, denyAnonymous: true,
                denyMinors: true, allowDirectTeleport: true, allowEnvironmentOverride: true);

            h.Post(
                ("is_externally_visible", false),
                ("allow_direct_teleport", false),
                ("deny_anonymous", false),
                ("deny_age_unverified", false),
                ("allow_voice_chat", false),
                ("override_public_access", false),   // stored negated: TaxFree
                ("override_environment", false));

            Assert.False(h.Settings.PublicAccess);
            Assert.False(h.Settings.AllowDirectTeleport);
            Assert.False(h.Settings.DenyAnonymous);
            Assert.False(h.Settings.DenyMinors);
            Assert.False(h.Settings.AllowVoice);
            Assert.True(h.Settings.TaxFree);
            Assert.False(h.Settings.AllowEnvironmentOverride);
        }

        [Fact]
        public void ASaveCarryingTrueSavesTrue()
        {
            Harness h = new Harness(taxFree: true, publicAccess: false, allowVoice: false, denyAnonymous: false,
                denyMinors: false, allowDirectTeleport: false, allowEnvironmentOverride: false);

            h.Post(
                ("is_externally_visible", true),
                ("allow_direct_teleport", true),
                ("deny_anonymous", true),
                ("deny_age_unverified", true),
                ("allow_voice_chat", true),
                ("override_public_access", true),
                ("override_environment", true));

            Assert.True(h.Settings.PublicAccess);
            Assert.True(h.Settings.AllowDirectTeleport);
            Assert.True(h.Settings.DenyAnonymous);
            Assert.True(h.Settings.DenyMinors);
            Assert.True(h.Settings.AllowVoice);
            Assert.False(h.Settings.TaxFree);
            Assert.True(h.Settings.AllowEnvironmentOverride);
        }

        [Fact]
        public void ASaveCarryingSomeSettingsAppliesThoseAndHoldsTheRest()
        {
            Harness h = new Harness(taxFree: true, publicAccess: true, allowVoice: true, denyAnonymous: false,
                denyMinors: false, allowDirectTeleport: true, allowEnvironmentOverride: true);

            h.Post(("is_externally_visible", false), ("deny_age_unverified", true));

            Assert.False(h.Settings.PublicAccess);   // carried
            Assert.True(h.Settings.DenyMinors);      // carried
            Assert.True(h.Settings.TaxFree);         // held
            Assert.True(h.Settings.AllowVoice);      // held
            Assert.False(h.Settings.DenyAnonymous);  // held
            Assert.True(h.Settings.AllowDirectTeleport);      // held
            Assert.True(h.Settings.AllowEnvironmentOverride); // held
        }

        // The flags the region sends viewers are packed from the stored settings: a setting the owner turned off
        // must still read as off after a save that does not mention it.
        [Fact]
        public void ASettingTurnedOffStaysOffInTheRegionFlagsAfterAnUnrelatedSave()
        {
            Harness h = new Harness(taxFree: false, allowVoice: false, publicAccess: true);
            Assert.Equal(0u, h.Flags & (uint)RegionFlags.AllowVoice);
            Assert.NotEqual(0u, h.Flags & (uint)RegionFlags.AllowParcelAccessOverride);

            h.Post(("deny_age_unverified", true));

            uint flags = h.Flags;
            Assert.Equal(0u, flags & (uint)RegionFlags.AllowVoice);
            Assert.NotEqual(0u, flags & (uint)RegionFlags.DenyAgeUnverified);          // the carried change
            Assert.NotEqual(0u, flags & (uint)RegionFlags.AllowParcelAccessOverride);  // not turned over
            Assert.NotEqual(0u, flags & (uint)RegionFlags.PublicAllowed);
        }
    }
}
