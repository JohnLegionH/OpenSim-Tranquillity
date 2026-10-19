/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * The handler and the region connector over real HTTP, and the way the service is protected. A listener
 * on an ephemeral loopback port stands in for Robust's HTTP server: it hands each POST to the real
 * VoiceSessionPostHandler.Handle (the method that checks the IServiceAuth before anything else) and writes
 * back what it returns.
 */
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nini.Config;
using NUnit.Framework;
using OpenMetaverse;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Tests.Common;
using osWebRtcVoice.NonSpatial;
using static osWebRtcVoice.Tests.NonSpatialIdentityTests;

namespace osWebRtcVoice.Tests
{
    [TestFixture]
    public class VoiceSessionWireTests
    {
        private static readonly UUID GroupId = NonSpatialIdentityTests.Group;
        private const string User = "region-server";
        private const string Password = "0123456789abcdef0123456789abcdef-example";

        private sealed class TestServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly VoiceSessionPostHandler _handler;
            private readonly Task _loop;
            public string BaseUri { get; }
            public int Requests;

            public TestServer(VoiceSessionPostHandler handler)
            {
                _handler = handler;
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                BaseUri = $"http://127.0.0.1:{port}";
                _listener.Prefixes.Add(BaseUri + "/");
                _listener.Start();
                _loop = Task.Run(Serve);
            }

            private async Task Serve()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    try
                    {
                        System.Threading.Interlocked.Increment(ref Requests);
                        byte[] body;
                        using (MemoryStream ms = new MemoryStream())
                        {
                            ctx.Request.InputStream.CopyTo(ms);
                            body = ms.ToArray();
                        }
                        TestOSHttpRequest request = new TestOSHttpRequest { Headers = new NameValueCollection(ctx.Request.Headers) };
                        TestOSHttpResponse response = new TestOSHttpResponse();
                        byte[] reply = _handler.Handle(ctx.Request.Url.AbsolutePath, new MemoryStream(body), request, response);
                        ctx.Response.StatusCode = response.StatusCode != 0 ? response.StatusCode : 200;
                        ctx.Response.ContentLength64 = reply.Length;
                        ctx.Response.OutputStream.Write(reply, 0, reply.Length);
                    }
                    catch { /* a broken exchange is the client's to notice */ }
                    finally { ctx.Response.Close(); }
                }
            }

            public void Dispose()
            {
                _listener.Close();
                try { _loop.Wait(2000); } catch { }
            }
        }

        private static IniConfigSource Config(string password = Password, string user = User)
        {
            IniConfigSource config = new IniConfigSource();
            foreach (string section in new[] { "WebRtcVoice", VoiceSessionServerConnector.SectionName })
            {
                IConfig c = config.AddConfig(section);
                c.Set("AuthType", "BasicHttpAuthentication");
                c.Set("HttpAuthUsername", user);
                c.Set("HttpAuthPassword", password);
            }
            return config;
        }

        private static VoiceSessionPostHandler NewHandler(VoiceSessionService service)
            => new VoiceSessionPostHandler(service, ServiceAuth.Create(Config(), VoiceSessionServerConnector.SectionName));

        private static VoiceSessionServicesConnector NewClient(TestServer server, IniConfigSource config = null, int timeoutMs = 3000)
            => new VoiceSessionServicesConnector(config ?? Config(), "WebRtcVoice", server.BaseUri, timeoutMs);

        // ---- a region over HTTP ----------------------------------------------------------------------

        [Test]
        public void ARegionRunsACallThroughTheHandler()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));

            SessionOutcome started = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);
            SessionOutcome joined = region.Accept(GroupId, Bob, RegionB, "viewer-one");
            string token = region.IssueToken(GroupId, Bob);
            NonSpatialVoiceSession s = region.Get(GroupId);

            Assert.That(started.Ok, Is.True, started.Decision);
            Assert.That(started.Created, Is.True);
            Assert.That(started.StartedRinging, Is.True);
            Assert.That(joined.Ok, Is.True, joined.Decision);
            Assert.That(joined.StartedRinging, Is.False);
            Assert.That(token, Is.Not.Null);
            Assert.That(s.SeatsHeld, Is.EqualTo(2));
            Assert.That(s.Find(Bob).ViewerSession, Is.EqualTo("viewer-one"));
            Assert.That(s.Find(Bob).OriginRegion, Is.EqualTo(RegionB));
            Assert.That(s.Token, Is.EqualTo(token));
            Assert.That(service.SessionCount, Is.EqualTo(1));
        }

        [Test]
        public void EveryOperationRoundTrips()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server));

            SessionOutcome conf = region.StartAdhoc(Alice, UUID.Random(), RegionA, new List<UUID> { Bob, Carol });
            UUID id = conf.Session.SessionId;
            Assert.That(conf.Ok, Is.True, conf.Decision);
            Assert.That(region.CheckInviteCount(id, Alice, new List<UUID> { UUID.Random() }).Ok, Is.True);
            UUID dave = UUID.Random();
            Assert.That(region.Invite(id, Alice, dave).Decision, Is.EqualTo("invited"));
            Assert.That(region.Accept(id, Bob, RegionA).Ok, Is.True);
            Assert.That(region.MarkPresent(id, Bob, RegionA, "v").Ok, Is.True);
            Assert.That(region.Decline(id, Carol).Decision, Is.EqualTo("declined"));
            Assert.That(region.LeaveChat(id, Bob).Decision, Is.EqualTo("chat-left"));
            region.MarkInvited(id, new List<UUID> { dave });
            Assert.That(region.Get(id).WasInvited(dave), Is.True);
            Assert.That(region.TryTakeConferenceRequest(Alice), Is.True);
            Assert.That(region.DepartByViewerSession(Bob, "v", DepartureReason.VoiceTeardown), Has.Count.EqualTo(1));
            Assert.That(region.Depart(id, Alice, DepartureReason.VoiceTeardown).Ok, Is.True);
            Assert.That(region.DepartAll(dave, DepartureReason.PresenceLost), Has.Count.EqualTo(1), "the invited member leaves everything");
            Assert.That(region.DepartRegion(RegionA), Is.GreaterThanOrEqualTo(0));
            Assert.That(region.RenewSeats(new List<UUID> { Alice }), Is.True);
            Assert.That(region.GetByRoomKey(region.Get(id).RoomKey)?.SessionId, Is.EqualTo(id));
        }

        // ---- protection ---------------------------------------------------------------------------------

        [Test]
        public void ARegionWithTheWrongPasswordIsRefusedAndNothingChanges()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            using TestServer server = new TestServer(NewHandler(service));
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(NewClient(server, Config(password: "not-the-service-password-not-the-service")));

            SessionOutcome o = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA);

            Assert.That(o.Ok, Is.False);
            Assert.That(o.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ARequestWithNoCredentialIsRefusedBeforeItIsRead()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpResponse response = new TestOSHttpResponse();
            string body = "METHOD=start&type=2&creator=" + Alice + "&other=" + GroupId + "&temp=" + GroupId + "&origin=" + RegionA + "&cap=0";

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          new TestOSHttpRequest(), response);

            Assert.That(response.StatusCode, Is.EqualTo((int)HttpStatusCode.Unauthorized));
            Assert.That(reply, Is.Empty);
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void AnEmptyConfigurationIsNotAcceptableAuthentication()
        {
            Assert.That(VoiceSessionServerConnector.CheckAuthConfig(new IniConfigSource()), Is.Not.Null);
        }

        [TestCase("None", User, Password)]
        [TestCase("BasicHttpAuthentication", "", Password)]
        [TestCase("BasicHttpAuthentication", User, "")]
        [TestCase("BasicHttpAuthentication", User, "short-password")]
        [TestCase("BasicHttpAuthentication", User, "0123456789abcdef0123456789abcde")]   // 31 characters
        public void AnEnabledServiceRefusesToLoadWithoutAcceptableAuthentication(string authType, string user, string password)
        {
            IniConfigSource config = new IniConfigSource();
            IConfig c = config.AddConfig(VoiceSessionServerConnector.SectionName);
            c.Set("Enabled", "true");
            c.Set("AuthType", authType);
            c.Set("HttpAuthUsername", user);
            c.Set("HttpAuthPassword", password);

            Assert.That(VoiceSessionServerConnector.CheckAuthConfig(config), Is.Not.Null);
            Assert.Throws<Exception>(() => new VoiceSessionServerConnector(config, null, VoiceSessionServerConnector.SectionName));
        }

        [Test]
        public void AnAcceptablePasswordPassesTheCheck_AndThe32CharacterBoundaryIsInclusive()
        {
            IniConfigSource config = Config("0123456789abcdef0123456789abcdef");   // exactly 32
            Assert.That(VoiceSessionServerConnector.CheckAuthConfig(config), Is.Null);
        }

        [Test]
        public void ADisabledServiceServesNothing()
        {
            IniConfigSource none = new IniConfigSource();
            Assert.DoesNotThrow(() => new VoiceSessionServerConnector(none, null, VoiceSessionServerConnector.SectionName));
            IniConfigSource off = new IniConfigSource();
            off.AddConfig(VoiceSessionServerConnector.SectionName).Set("Enabled", "false");
            Assert.DoesNotThrow(() => new VoiceSessionServerConnector(off, null, VoiceSessionServerConnector.SectionName));
        }

        // ---- malformed requests change nothing -----------------------------------------------------------

        [TestCase("")]
        [TestCase("not a form")]
        [TestCase("type=2&creator=x")]
        [TestCase("METHOD=nosuchmethod")]
        [TestCase("METHOD=start")]
        [TestCase("METHOD=start&type=two&creator=11111111-1111-1111-1111-111111111111&other=x&temp=x&origin=x&cap=0")]
        [TestCase("METHOD=start&type=2&creator=not-a-uuid&other=99999999-9999-9999-9999-999999999999&temp=99999999-9999-9999-9999-999999999999&origin=99999999-9999-9999-9999-999999999999&cap=0")]
        [TestCase("METHOD=accept&session=99999999-9999-9999-9999-999999999999")]
        [TestCase("METHOD=startadhoc&creator=11111111-1111-1111-1111-111111111111&temp=22222222-2222-2222-2222-222222222222&origin=33333333-3333-3333-3333-333333333333&cap=0&invitees=11111111-1111-1111-1111-111111111111,garbage")]
        [TestCase("METHOD=renewseats&agents=garbage")]
        [TestCase("METHOD=depart&session=99999999-9999-9999-9999-999999999999&agent=11111111-1111-1111-1111-111111111111&reason=banana")]
        [TestCase("METHOD=getbyroomkey&roomkey=")]
        public void AMalformedRequestIsAFailureAndChangesNothing(string body)
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpRequest request = new TestOSHttpRequest();
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          request, new TestOSHttpResponse());

            string text = Encoding.UTF8.GetString(reply);
            Assert.That(text, Does.Contain("Failure"), text);
            Assert.That(service.SessionCount, Is.EqualTo(0));
        }

        [Test]
        public void ARequestOverTheSizeLimitIsAFailure()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            VoiceSessionPostHandler handler = NewHandler(service);
            TestOSHttpRequest request = new TestOSHttpRequest();
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password));
            string body = "METHOD=renewseats&agents=" + new string('a', VoiceSessionPostHandler.MaxBodyChars + 1);

            byte[] reply = handler.Handle(VoiceSessionPostHandler.HandlerPath, new MemoryStream(Encoding.UTF8.GetBytes(body)),
                                          request, new TestOSHttpResponse());

            Assert.That(Encoding.UTF8.GetString(reply), Does.Contain("Failure"));
        }

        // ---- the service is not reachable ------------------------------------------------------------------

        [Test]
        public void WithNothingListening_ARegionIsRefusedCleanly()
        {
            VoiceSessionService service = VoiceSessionServiceTests.NewService();
            TestServer first = new TestServer(NewHandler(service));
            string uri = first.BaseUri;
            first.Dispose();   // Robust is down: the port is closed
            VoiceSessionServicesConnector client = new VoiceSessionServicesConnector(Config(), "WebRtcVoice", uri, 1000);
            GridNonSpatialSessions region = VoiceSessionServiceTests.NewRegion(client);

            SessionOutcome refused = default;
            Assert.DoesNotThrow(() => refused = region.Start(NonSpatialSessionType.Group, Alice, GroupId, GroupId, RegionA));
            Assert.That(refused.Ok, Is.False);
            Assert.That(refused.Decision, Is.EqualTo(SessionOutcome.StoreUnavailable));
            Assert.That(region.Get(GroupId), Is.Null);
            Assert.That(service.SessionCount, Is.EqualTo(0), "no session anywhere");
        }
    }
}
