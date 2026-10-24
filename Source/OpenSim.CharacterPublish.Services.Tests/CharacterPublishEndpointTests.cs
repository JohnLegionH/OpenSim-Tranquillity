/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Collections;
using System.Collections.Specialized;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.CharacterPublish.Objects;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.CharacterPublish;
using OpenSim.Services.AuthenticationService;

namespace OpenSim.CharacterPublish.Services.Tests;

// ---- an in-process request and response: the handler is called directly, no port is opened -------------------------

sealed class FakeRequest : IOSHttpRequest
{
    public string HttpMethod { get; init; } = "POST";
    public string UriPath { get; init; } = "/characterpublish/login";
    public NameValueCollection Headers { get; } = new();
    public Stream InputStream { get; init; } = new MemoryStream();
    public bool IsSecured { get; init; }
    public IPEndPoint RemoteIPEndPoint { get; init; } = new(IPAddress.Parse("192.0.2.10"), 50000);
    public long ContentLength64 { get; init; } = -1;

    public string[] AcceptTypes => Array.Empty<string>();
    public Encoding ContentEncoding => Encoding.UTF8;
    public long ContentLength => ContentLength64;
    public string ContentType => Headers["Content-Type"] ?? "";
    public bool HasEntityBody => true;
    public bool KeepAlive => false;
    public NameValueCollection QueryString => new();
    public Hashtable Query => new();
    public HashSet<string> QueryFlags => new();
    public Dictionary<string, string> QueryAsDictionary => new();
    public string RawUrl => UriPath;
    public IPEndPoint LocalIPEndPoint => new(IPAddress.Loopback, 8002);
    public Uri Url => new("https://example.org" + UriPath);
    public string UserAgent => "tests";
    public double ArrivalTS => 0;
}

sealed class FakeResponse : IOSHttpResponse
{
    public string ContentType { get; set; } = "";
    public long ContentLength { get; set; }
    public long ContentLength64 { get; set; }
    public Encoding ContentEncoding { get; set; } = Encoding.UTF8;
    public bool KeepAlive { get; set; }
    public int KeepAliveTimeout { get; set; }
    public Stream OutputStream { get; } = new MemoryStream();
    public string ProtocolVersion { get; set; } = "1.1";
    public int Priority { get; set; }
    public byte[] RawBuffer { get; set; } = Array.Empty<byte>();
    public int RawBufferStart { get; set; }
    public int RawBufferLen { get; set; }
    public Stream Body => OutputStream;
    public bool SendChunked { get; set; }
    public int StatusCode { get; set; } = 200;
    public string StatusDescription { get; set; } = "";
    public double RequestTS => 0;
    public Dictionary<string, string> Added { get; } = new();
    public void Redirect(string url, HttpStatusCode redirStatusCode = HttpStatusCode.Redirect) => throw new NotSupportedException();
    public void AddHeader(string key, string value) => Added[key] = value;
}

/// <summary>What the endpoint answered.</summary>
sealed record Answer(int Status, JsonElement Json, Dictionary<string, string> Headers)
{
    public string Code => Json.GetProperty("code").GetString()!;
    public string Message => Json.GetProperty("message").GetString()!;
    public string Text => Json.ToString();
}

/// <summary>The endpoint started through the connector's own startup path, over the scratch grid's real services, with a clock the test moves.</summary>
sealed class Endpoint
{
    public const string Key = "test-service-key-0123456789";
    public CharacterPublishHandler Handler { get; }
    public DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    public Endpoint(ScratchGrid g, Action<IConfig>? tweak = null)
    {
        var cfg = new IniConfigSource();
        cfg.Merge(g.Config);
        var c = cfg.AddConfig("CharacterPublish");
        c.Set("ServiceKey", Key);
        c.Set("CreatorAccount", BundleFixtures.Creator.ToString());
        c.Set("AuthenticationServiceModule", "OpenSim.Services.AuthenticationService.dll:PasswordAuthenticationService");
        c.Set("UserAccountServiceModule", "OpenSim.Services.UserAccountService.dll:UserAccountService");
        c.Set("AssetServiceModule", "OpenSim.Services.AssetService.dll:AssetService");
        c.Set("InventoryServiceModule", "OpenSim.Services.InventoryService.dll:XInventoryService");
        tweak?.Invoke(c);
        CharacterPublishHandler? handler = null;
        CharacterPublishServerConnector.Start(cfg, "CharacterPublish", LogCapture.Factory.CreateLogger("CharacterPublish"), h => handler = (CharacterPublishHandler)h, () => Now);
        Handler = handler!;
    }

    public Endpoint(CharacterPublishHandler handler) => Handler = handler;

    public Answer Call(string method, string route, byte[]? body = null, Action<NameValueCollection>? headers = null, bool secure = true,
        string remote = "192.0.2.10", bool key = true)
    {
        var req = new FakeRequest { HttpMethod = method, UriPath = "/characterpublish/" + route, InputStream = new MemoryStream(body ?? Array.Empty<byte>()),
            IsSecured = secure, RemoteIPEndPoint = new IPEndPoint(IPAddress.Parse(remote), 50000), ContentLength64 = body?.Length ?? 0 };
        if (key) req.Headers["X-Publish-Key"] = Key;
        headers?.Invoke(req.Headers);
        var res = new FakeResponse();
        Handler.Handle(req, res);
        var json = res.RawBuffer.Length == 0 ? JsonDocument.Parse("{\"code\":\"(none)\",\"message\":\"\"}").RootElement : JsonDocument.Parse(res.RawBuffer).RootElement.Clone();
        return new Answer(res.StatusCode, json, res.Added);
    }

    public Answer Login(string first, string last, string password, bool secure = true, string remote = "192.0.2.10", bool key = true) =>
        Call("POST", "login", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { firstName = first, lastName = last, password })), null, secure, remote, key);

    public Answer Publish(UUID resident, string token, byte[] zip, bool key = true, string remote = "192.0.2.10") =>
        Call("POST", "publish", zip, h => { h["X-Publish-Resident"] = resident.ToString(); h["X-Publish-Token"] = token; }, true, remote, key);

    public Answer Status(UUID resident, string token) =>
        Call("GET", "status", null, h => { h["X-Publish-Resident"] = resident.ToString(); h["X-Publish-Token"] = token; });
}

[Collection(ScratchGridCollection.Name)]
public sealed class CharacterPublishEndpointTests
{
    readonly ScratchGrid g;
    public CharacterPublishEndpointTests(ScratchGrid grid) => g = grid;

    /// <summary>Passwords, tokens and keys these tests used; the log test looks for every one of them.</summary>
    static readonly System.Collections.Concurrent.ConcurrentBag<string> Secrets = new() { Endpoint.Key };

    (ScratchGrid.Resident R, string First, string Last, string Password) NewLoginResident(string caller)
    {
        var r = g.NewResident();
        var first = "Pub";
        var last = "R" + ContentId.Derive("name", caller, r.Id.ToString()).ToString()[..8];
        var password = "pw-" + Guid.NewGuid().ToString("N");
        g.AddAccount(r.Id, first, last, password);
        Secrets.Add(password); Secrets.Add(Util.Md5Hash(password));
        return (r, first, last, password);
    }

    string LoginToken(Endpoint e, (ScratchGrid.Resident R, string First, string Last, string Password) u)
    {
        var a = e.Login(u.First, u.Last, u.Password);
        Assert.Equal(200, a.Status);
        var token = a.Json.GetProperty("token").GetString()!;
        Secrets.Add(token);
        return token;
    }

    static byte[] Zip(Bundle bundle, IReadOnlyDictionary<UUID, byte[]> content)
    {
        var files = bundle.Assets.Select(a => (a.File, content[a.Id])).Append((BundleFiles.ManifestName, BundleFiles.ToJson(bundle)));
        return Zip(files);
    }

    static byte[] Zip(IEnumerable<(string Name, byte[] Data)> files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in files)
            {
                var entry = z.CreateEntry(name, CompressionLevel.Fastest);
                using var s = entry.Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    // ================================================================================================================
    // C1 login against the real authentication service

    [Fact]
    public void LoginWithTheRightPasswordReturnsATokenTheAuthenticationServiceVerifies()
    {
        var u = NewLoginResident(nameof(LoginWithTheRightPasswordReturnsATokenTheAuthenticationServiceVerifies));
        var e = new Endpoint(g);

        var a = e.Login(u.First, u.Last, u.Password);

        Assert.Equal(200, a.Status);
        Assert.Equal(u.R.Id.ToString(), a.Json.GetProperty("principalId").GetString());
        Assert.Equal(30, a.Json.GetProperty("expiresInMinutes").GetInt32());
        var token = a.Json.GetProperty("token").GetString()!;
        Secrets.Add(token);
        Assert.True(UUID.TryParse(token, out _));
        Assert.True(g.Auth.Verify(u.R.Id, token, 30), "the grid's own service accepts the token for this resident");
        Assert.False(g.Auth.Verify(UUID.Random(), token, 30), "and for nobody else");
    }

    [Fact]
    public void LoginWithAWrongPasswordOrAnUnknownNameIsRefusedWithTheSameAnswer()
    {
        var u = NewLoginResident(nameof(LoginWithAWrongPasswordOrAnUnknownNameIsRefusedWithTheSameAnswer));
        var e = new Endpoint(g);
        var wrong = "not-" + Guid.NewGuid().ToString("N"); Secrets.Add(wrong);

        var badPassword = e.Login(u.First, u.Last, wrong);
        var unknownName = e.Login("Nobody", "Here" + Guid.NewGuid().ToString("N")[..6], u.Password);

        Assert.Equal(401, badPassword.Status); Assert.Equal("login-failed", badPassword.Code);
        Assert.Equal(401, unknownName.Status); Assert.Equal("login-failed", unknownName.Code);
        Assert.Equal(badPassword.Message, unknownName.Message); // a caller cannot tell which names exist
        Assert.DoesNotContain("token", badPassword.Text);
    }

    [Fact]
    public void LoginOverPlainHttpIsRefusedBeforeThePasswordIsLookedAt()
    {
        var u = NewLoginResident(nameof(LoginOverPlainHttpIsRefusedBeforeThePasswordIsLookedAt));
        var e = new Endpoint(g);

        var a = e.Login(u.First, u.Last, u.Password, secure: false);

        Assert.Equal(403, a.Status); Assert.Equal("https-required", a.Code);
        Assert.DoesNotContain("token", a.Text);
        // the same login over HTTPS works, so the refusal was the scheme and nothing else
        Assert.Equal(200, e.Login(u.First, u.Last, u.Password).Status);
    }

    [Fact]
    public void AProxyTheOperatorListedMakesAPlainConnectionHttpsAndOneThatIsNotListedDoesNot()
    {
        var u = NewLoginResident(nameof(AProxyTheOperatorListedMakesAPlainConnectionHttpsAndOneThatIsNotListedDoesNot));
        var e = new Endpoint(g, c => c.Set("TrustedProxies", "10.0.0.0/24, 192.0.2.99"));
        Answer ViaProxy(string proxy, string proto) => e.Call("POST", "login",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { firstName = u.First, lastName = u.Last, password = u.Password })),
            h => h["X-Forwarded-Proto"] = proto, secure: false, remote: proxy);

        Assert.Equal(200, ViaProxy("10.0.0.7", "https").Status);          // a range member, TLS ended at the proxy
        Assert.Equal(200, ViaProxy("192.0.2.99", "HTTPS").Status);        // a single listed address
        Assert.Equal("https-required", ViaProxy("10.0.0.7", "http").Code); // the proxy says the caller used plain HTTP
        Assert.Equal("https-required", ViaProxy("203.0.113.5", "https").Code); // not listed: its header is just a header any caller can write
    }

    /// <summary>
    /// The real authentication service, except that it answers a chosen principal the way the service does when an administrator's
    /// password is used to log in as someone else: a token for the principal, and the administrator's id as the real one. The SQLite
    /// account data cannot run the service's own impersonation query (it looks accounts up by level), so the answer is staged here.
    /// </summary>
    sealed class ImpersonatingAuth : OpenSim.Services.Interfaces.IAuthenticationService
    {
        readonly PasswordAuthenticationService _real; readonly UUID _victim; readonly UUID _admin;
        public List<(UUID, string)> Released { get; } = new();
        public ImpersonatingAuth(PasswordAuthenticationService real, UUID victim, UUID admin) { _real = real; _victim = victim; _admin = admin; }
        public string Authenticate(UUID principalID, string password, int lifetime) => _real.Authenticate(principalID, password, lifetime);
        public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID)
        {
            var token = _real.Authenticate(principalID, password, lifetime, out realID);
            if (principalID == _victim && !string.IsNullOrEmpty(token)) realID = _admin;
            return token;
        }
        public bool Verify(UUID principalID, string token, int lifetime) => _real.Verify(principalID, token, lifetime);
        public bool Release(UUID principalID, string token) { Released.Add((principalID, token)); return _real.Release(principalID, token); }
        public bool SetPassword(UUID principalID, string passwd) => _real.SetPassword(principalID, passwd);
        public OpenSim.Services.Interfaces.AuthInfo GetAuthInfo(UUID principalID) => _real.GetAuthInfo(principalID);
        public bool SetAuthInfo(OpenSim.Services.Interfaces.AuthInfo info) => _real.SetAuthInfo(info);
    }

    [Fact]
    public void ALoginThatTheServiceAnswersWithAnAdministratorsIdentityIsRefusedAndItsTokenEnded()
    {
        var victim = NewLoginResident(nameof(ALoginThatTheServiceAnswersWithAnAdministratorsIdentityIsRefusedAndItsTokenEnded));
        var auth = new ImpersonatingAuth(g.Auth, victim.R.Id, UUID.Random());
        var e = new Endpoint(new CharacterPublishHandler(CharacterPublishSettings.Read(Cfg(), "CharacterPublish", out _), auth, g.Accounts, g.PlainAssets, g.Plain,
            LogCapture.Factory.CreateLogger("CharacterPublish")));

        var a = e.Login(victim.First, victim.Last, victim.Password);

        Assert.Equal(401, a.Status); Assert.Equal("login-failed", a.Code);
        Assert.DoesNotContain("token", a.Text);
        var (who, issued) = Assert.Single(auth.Released);
        Assert.Equal(victim.R.Id, who);
        Assert.False(g.Auth.Verify(victim.R.Id, issued, 30), "the token the service issued was ended");
    }

    IConfigSource Cfg()
    {
        var cfg = new IniConfigSource(); cfg.Merge(g.Config);
        var c = cfg.AddConfig("CharacterPublish");
        c.Set("ServiceKey", Endpoint.Key); c.Set("CreatorAccount", BundleFixtures.Creator.ToString());
        foreach (var k in new[] { "AuthenticationServiceModule", "UserAccountServiceModule", "AssetServiceModule", "InventoryServiceModule" }) c.Set(k, "x");
        return cfg;
    }

    // ================================================================================================================
    // C2 publish

    [Fact]
    public void PublishWithAValidTokenAndKeyWritesTheOutfitForTheLoggedInResident()
    {
        var u = NewLoginResident(nameof(PublishWithAValidTokenAndKeyWritesTheOutfitForTheLoggedInResident));
        var a = BundleFixtures.Assemble(u.R.Id);
        var e = new Endpoint(g);
        var token = LoginToken(e, u);
        var before = g.Snapshot(u.R.Id);

        var answer = e.Publish(u.R.Id, token, Zip(a.Bundle, a.Content));

        Assert.Equal(200, answer.Status);
        Assert.Equal("ok", answer.Code);
        Assert.Equal(a.Bundle.OutfitFolder.Id.ToString(), answer.Json.GetProperty("outfitFolderId").GetString());
        var outfit = g.Plain.GetFolderContent(u.R.Id, a.Bundle.OutfitFolder.Id);
        Assert.Equal(a.Bundle.Items.Count, outfit.Items.Count);
        Assert.All(outfit.Items, i => Assert.Equal(u.R.Id, i.Owner));
        var look = g.Plain.GetFolderContent(u.R.Id, a.Bundle.CurrentLook!.FolderId);
        Assert.Equal(4, look.Items.Count); // the way back
        Assert.All(look.Items, i => Assert.Equal(24, i.AssetType));
        ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(u.R.Id), u.R.Root, g.MyOutfitsOf(u.R.Id)!.ID);
        foreach (var asset in a.Bundle.Assets) Assert.Equal(a.Content[asset.Id], g.PlainAssets.GetData(asset.Id.ToString()));
        // the status request now lists it
        var status = e.Status(u.R.Id, token);
        Assert.Equal(200, status.Status);
        Assert.Equal(1, status.Json.GetProperty("count").GetInt32());
        Assert.Equal(20, status.Json.GetProperty("limit").GetInt32());
        Assert.Equal(a.Bundle.OutfitFolder.Id.ToString(), status.Json.GetProperty("outfits")[0].GetProperty("folderId").GetString());
        // and the same publish again is already published, with nothing new written
        var again = e.Publish(u.R.Id, token, Zip(a.Bundle, a.Content));
        Assert.Equal(200, again.Status);
        Assert.True(again.Json.GetProperty("alreadyPublished").GetBoolean());
    }

    [Fact]
    public void TheMaxBundleLandsOverTheEndpointWithThePreviousLookWrittenFirst()
    {
        var description = Environment.GetEnvironmentVariable("PUB_MAX_DESCRIPTION");
        if (string.IsNullOrEmpty(description)) return; // the character's files are private; the tests above carry the same path with a synthetic bundle
        var u = NewLoginResident(nameof(TheMaxBundleLandsOverTheEndpointWithThePreviousLookWrittenFirst));
        var made = OutfitAssembler.AssembleFromFile(description, new PublishContext(u.R.Id, BundleFixtures.Creator, 1, BundleFixtures.Date));
        Assert.True(made.Validation.Ok, made.Validation.ToString());
        // the real login and account services, with the inventory and asset services wrapped to record the order of writes
        var inv = g.NewInventory(); var ast = g.NewAssets();
        var e = new Endpoint(new CharacterPublishHandler(CharacterPublishSettings.Read(Cfg(), "CharacterPublish", out _), g.Auth, g.Accounts, ast, inv,
            LogCapture.Factory.CreateLogger("CharacterPublish")));
        var token = LoginToken(e, u);
        var before = g.Snapshot(u.R.Id);

        var answer = e.Publish(u.R.Id, token, Zip(made.Bundle, made.Content));

        Assert.Equal(200, answer.Status);
        Assert.Equal(made.Bundle.Items.Count, g.Plain.GetFolderContent(u.R.Id, made.Bundle.OutfitFolder.Id).Items.Count);
        Assert.Equal(4, g.Plain.GetFolderContent(u.R.Id, made.Bundle.CurrentLook!.FolderId).Items.Count);
        ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(u.R.Id), u.R.Root, g.MyOutfitsOf(u.R.Id)!.ID);
        var folders = inv.FoldersAdded.Select(f => f.ID).ToList();
        Assert.True(folders.IndexOf(made.Bundle.CurrentLook.FolderId) < folders.IndexOf(made.Bundle.OutfitFolder.Id), "the previous-look folder is written first");
        Assert.True(inv.ItemsAdded.FindLastIndex(i => i.AssetType == 24) < inv.ItemsAdded.FindIndex(i => i.Folder == made.Bundle.OutfitFolder.Id), "every link before the first outfit item");
        Assert.Empty(inv.Forbidden); Assert.Empty(ast.Forbidden);
    }

    // ================================================================================================================
    // C3 refusals

    [Fact]
    public void ARequestWithNoKeyIsRefused()
    {
        var u = NewLoginResident(nameof(ARequestWithNoKeyIsRefused));
        var e = new Endpoint(g);
        var a = e.Login(u.First, u.Last, u.Password, key: false);
        Assert.Equal(401, a.Status); Assert.Equal("service-key-missing", a.Code); Assert.Contains("X-Publish-Key", a.Message);
        Assert.DoesNotContain("token", a.Text);
    }

    [Fact]
    public void ARequestWithTheWrongKeyIsRefused()
    {
        var u = NewLoginResident(nameof(ARequestWithTheWrongKeyIsRefused));
        var e = new Endpoint(g);
        var wrong = "wrong-key-" + Guid.NewGuid().ToString("N"); Secrets.Add(wrong);
        var req = new FakeRequest { UriPath = "/characterpublish/login", IsSecured = true,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { firstName = u.First, lastName = u.Last, password = u.Password }))) };
        req.Headers["X-Publish-Key"] = wrong;
        var res = new FakeResponse();
        e.Handler.Handle(req, res);
        Assert.Equal(401, res.StatusCode);
        Assert.Contains("service-key-invalid", Encoding.UTF8.GetString(res.RawBuffer));
    }

    [Fact]
    public void AnExpiredTokenIsRefused()
    {
        var u = NewLoginResident(nameof(AnExpiredTokenIsRefused));
        var a = BundleFixtures.Assemble(u.R.Id);
        var e = new Endpoint(g);
        // a token the real service minted with a lifetime of zero minutes is over the moment it is made
        var expired = g.Auth.Authenticate(u.R.Id, Util.Md5Hash(u.Password), 0);
        Assert.False(string.IsNullOrEmpty(expired)); Secrets.Add(expired);

        var answer = e.Publish(u.R.Id, expired, Zip(a.Bundle, a.Content));

        Assert.Equal(401, answer.Status); Assert.Equal("token-invalid", answer.Code); Assert.Contains("expired", answer.Message);
        Assert.Null(g.Plain.GetFolder(u.R.Id, a.Bundle.OutfitFolder.Id));
    }

    [Fact]
    public void AnotherResidentsTokenCannotPublishToThisResident()
    {
        var alice = NewLoginResident(nameof(AnotherResidentsTokenCannotPublishToThisResident) + "A");
        var bob = NewLoginResident(nameof(AnotherResidentsTokenCannotPublishToThisResident) + "B");
        var e = new Endpoint(g);
        var aliceToken = LoginToken(e, alice);
        var forBob = BundleFixtures.Assemble(bob.R.Id);
        var beforeBob = g.Snapshot(bob.R.Id);

        var answer = e.Publish(bob.R.Id, aliceToken, Zip(forBob.Bundle, forBob.Content)); // Alice's token, Bob named as the resident

        Assert.Equal(401, answer.Status); Assert.Equal("token-invalid", answer.Code);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(beforeBob, g.Snapshot(bob.R.Id)));

        // and with her own token, a bundle built for Bob is refused: the owner is the token's resident, never a field of the request
        var wrongOwner = e.Publish(alice.R.Id, aliceToken, Zip(forBob.Bundle, forBob.Content));
        Assert.Equal(422, wrongOwner.Status); Assert.Equal("wrong-owner", wrongOwner.Code);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(beforeBob, g.Snapshot(bob.R.Id)));
    }

    [Fact]
    public void AnOversizedBundleIsRefusedBeforeItIsRead()
    {
        var u = NewLoginResident(nameof(AnOversizedBundleIsRefusedBeforeItIsRead));
        var e = new Endpoint(g, c => c.Set("MaxBundleBytes", "4096"));
        var token = LoginToken(e, u);
        var big = new byte[10_000];

        var declared = e.Publish(u.R.Id, token, big);
        Assert.Equal(413, declared.Status); Assert.Equal("bundle-too-large", declared.Code); Assert.Contains("4096", declared.Message);

        // a body whose length is not declared is cut off while it is read
        var req = new FakeRequest { UriPath = "/characterpublish/publish", IsSecured = true, InputStream = new MemoryStream(big) };
        req.Headers["X-Publish-Key"] = Endpoint.Key; req.Headers["X-Publish-Resident"] = u.R.Id.ToString(); req.Headers["X-Publish-Token"] = token;
        var res = new FakeResponse();
        e.Handler.Handle(req, res);
        Assert.Equal(413, res.StatusCode);
    }

    [Fact]
    public void TooManyFilesAndAnOversizedAssetAreRefused()
    {
        var u = NewLoginResident(nameof(TooManyFilesAndAnOversizedAssetAreRefused));
        var a = BundleFixtures.Assemble(u.R.Id);
        var e = new Endpoint(g);
        var token = LoginToken(e, u);

        var many = Enumerable.Range(0, 66).Select(i => ("f" + i + ".bin", new byte[] { 1 }));
        var tooMany = e.Publish(u.R.Id, token, Zip(many));
        Assert.Equal(413, tooMany.Status); Assert.Equal("too-many-files", tooMany.Code); Assert.Contains("66", tooMany.Message); Assert.Contains("65", tooMany.Message);

        var small = new Endpoint(g, c => c.Set("MaxAssetBytes", "2048"));
        var smallToken = LoginToken(small, u);
        var oversized = small.Publish(u.R.Id, smallToken, Zip(new[] { (BundleFiles.ManifestName, BundleFiles.ToJson(a.Bundle)), ("huge.bin", new byte[100_000]) }));
        Assert.Equal(413, oversized.Status); Assert.Equal("asset-too-large", oversized.Code); Assert.Contains("huge.bin", oversized.Message);
    }

    [Fact]
    public void ABodyThatIsNotAZipOrHoldsAPathIsRefused()
    {
        var u = NewLoginResident(nameof(ABodyThatIsNotAZipOrHoldsAPathIsRefused));
        var e = new Endpoint(g);
        var token = LoginToken(e, u);

        Assert.Equal("bundle-not-a-zip", e.Publish(u.R.Id, token, Encoding.UTF8.GetBytes("this is not a zip")).Code);
        Assert.Equal("bundle-bad-entry", e.Publish(u.R.Id, token, Zip(new[] { ("../escape.bin", new byte[] { 1 }) })).Code);
        Assert.Equal("bundle-no-manifest", e.Publish(u.R.Id, token, Zip(new[] { ("a.bin", new byte[] { 1 }) })).Code);
        Assert.Equal("bundle-bad-manifest", e.Publish(u.R.Id, token, Zip(new[] { (BundleFiles.ManifestName, Encoding.UTF8.GetBytes("{ nope")) })).Code);
    }

    [Fact]
    public void ThePublishRateLimitIsPerResidentAndLiftsAfterAnHour()
    {
        var u = NewLoginResident(nameof(ThePublishRateLimitIsPerResidentAndLiftsAfterAnHour));
        var other = NewLoginResident(nameof(ThePublishRateLimitIsPerResidentAndLiftsAfterAnHour) + "2");
        var e = new Endpoint(g, c => c.Set("PublishesPerResidentPerHour", "2"));
        var token = LoginToken(e, u); var otherToken = LoginToken(e, other);
        var a = BundleFixtures.Assemble(u.R.Id);
        var zip = Zip(a.Bundle, a.Content);

        Assert.Equal(200, e.Publish(u.R.Id, token, zip).Status);
        Assert.Equal(200, e.Publish(u.R.Id, token, zip).Status);
        var third = e.Publish(u.R.Id, token, zip);

        Assert.Equal(429, third.Status); Assert.Equal("rate-limited", third.Code);
        Assert.Contains("2 times an hour", third.Message);
        Assert.True(int.Parse(third.Headers["Retry-After"]) > 0);
        // another resident is not held back by it
        var b = BundleFixtures.Assemble(other.R.Id);
        Assert.Equal(200, e.Publish(other.R.Id, otherToken, Zip(b.Bundle, b.Content)).Status);
        // an hour later the first resident may publish again
        e.Now += TimeSpan.FromMinutes(61);
        var later = e.Login(u.First, u.Last, u.Password); // the clock moved, the token's own life is the authentication service's and is not tested here
        Assert.Equal(200, later.Status);
        Assert.Equal(200, e.Publish(u.R.Id, later.Json.GetProperty("token").GetString()!, zip).Status);
    }

    [Fact]
    public void LoginAttemptsAreLimitedPerNameAndPerAddress()
    {
        var u = NewLoginResident(nameof(LoginAttemptsAreLimitedPerNameAndPerAddress));
        var e = new Endpoint(g, c => { c.Set("LoginAttemptsPerNamePerMinute", "3"); c.Set("LoginAttemptsPerAddressPerMinute", "6"); });

        for (var i = 0; i < 3; i++) Assert.Equal(401, e.Login(u.First, u.Last, "guess" + i).Status);
        var fourth = e.Login(u.First, u.Last, u.Password); // even the right password: the name is locked for the minute
        Assert.Equal(429, fourth.Status); Assert.Contains("this name", fourth.Message);

        // other names from the same address run into the address limit (two attempts left of six: three failed and the locked one count too)
        Assert.Equal(401, e.Login("Pub", "Other1", "x").Status);
        Assert.Equal(401, e.Login("Pub", "Other2", "x").Status);
        var sixth = e.Login("Pub", "Other3", "x");
        Assert.Equal(429, sixth.Status); Assert.Contains("this address", sixth.Message);
        // a different address is not held back
        Assert.Equal(401, e.Login("Pub", "Other4", "x", remote: "192.0.2.77").Status);
        // a minute later the name works again
        e.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(200, e.Login(u.First, u.Last, u.Password).Status);
    }

    [Fact]
    public void TheClientAddressBehindATrustedProxyIsTheForwardedOneAndAnUntrustedOnesHeaderIsIgnored()
    {
        var e = new Endpoint(g, c => { c.Set("TrustedProxies", "10.0.0.1"); c.Set("RequestsPerAddressPerMinute", "2"); });
        Answer Hit(string remote, string? xff) => e.Call("GET", "status", null, h => { if (xff != null) h["X-Forwarded-For"] = xff; }, remote: remote);

        // two callers behind the proxy have their own counts
        Assert.NotEqual(429, Hit("10.0.0.1", "203.0.113.1").Status); Assert.NotEqual(429, Hit("10.0.0.1", "203.0.113.1").Status);
        Assert.Equal(429, Hit("10.0.0.1", "203.0.113.1").Status);
        Assert.NotEqual(429, Hit("10.0.0.1", "203.0.113.2").Status);
        // a caller that is not a listed proxy cannot choose its address by writing the header
        Assert.NotEqual(429, Hit("198.51.100.9", "203.0.113.50").Status); Assert.NotEqual(429, Hit("198.51.100.9", "203.0.113.51").Status);
        Assert.Equal(429, Hit("198.51.100.9", "203.0.113.52").Status);
    }

    [Fact]
    public void ATextureTheGridLacksIsRefusedOverTheEndpointWithItsMessage()
    {
        var u = NewLoginResident(nameof(ATextureTheGridLacksIsRefusedOverTheEndpointWithItsMessage));
        var missing = UUID.Random();
        var a = BundleFixtures.Assemble(u.R.Id, skinHead: missing);
        var e = new Endpoint(g);
        var token = LoginToken(e, u);
        var before = g.Snapshot(u.R.Id);

        var answer = e.Publish(u.R.Id, token, Zip(a.Bundle, a.Content));

        Assert.Equal(422, answer.Status); Assert.Equal("texture-missing", answer.Code);
        Assert.Contains(missing.ToString(), answer.Message); Assert.Contains("Nothing was written", answer.Message);
        Assert.Empty(ScratchGrid.AssertOnlyAdditions(before, g.Snapshot(u.R.Id)));

        // the operator setting turns it into a warning
        var lenient = new Endpoint(g, c => c.Set("MissingTextureIsWarning", "true"));
        var ok = lenient.Publish(u.R.Id, LoginToken(lenient, u), Zip(a.Bundle, a.Content));
        Assert.Equal(200, ok.Status);
        Assert.Contains(ok.Json.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains(missing.ToString()));
    }

    [Fact]
    public void TheOutfitCapIsRefusedWithAConflictAndItsMessage()
    {
        var u = NewLoginResident(nameof(TheOutfitCapIsRefusedWithAConflictAndItsMessage));
        var e = new Endpoint(g, c => c.Set("MaxOutfitsPerResident", "1"));
        var token = LoginToken(e, u);
        var one = BundleFixtures.Assemble(u.R.Id, 1, "CapOne" + u.Last[1..]);
        var two = BundleFixtures.Assemble(u.R.Id, 1, "CapTwo" + u.Last[1..]);

        Assert.Equal(200, e.Publish(u.R.Id, token, Zip(one.Bundle, one.Content)).Status);
        var refused = e.Publish(u.R.Id, token, Zip(two.Bundle, two.Content));

        Assert.Equal(409, refused.Status); Assert.Equal("cap-reached", refused.Code); Assert.Contains("limit on this grid is 1", refused.Message);
    }

    [Fact]
    public void LogoutEndsTheToken()
    {
        var u = NewLoginResident(nameof(LogoutEndsTheToken));
        var e = new Endpoint(g);
        var token = LoginToken(e, u);
        Assert.Equal(200, e.Status(u.R.Id, token).Status);

        Assert.Equal(200, e.Call("POST", "logout", null, h => { h["X-Publish-Resident"] = u.R.Id.ToString(); h["X-Publish-Token"] = token; }).Status);

        Assert.Equal(401, e.Status(u.R.Id, token).Status);
    }

    [Fact]
    public void ATokenThatIsNotAGuidNeverReachesTheAuthenticationDataLayer()
    {
        var u = NewLoginResident(nameof(ATokenThatIsNotAGuidNeverReachesTheAuthenticationDataLayer));
        var e = new Endpoint(g);
        // the SQLite authentication data builds its token query by joining strings, so an injection attempt must be stopped before it
        var a = e.Status(u.R.Id, "' or 1=1 --");
        Assert.Equal(401, a.Status); Assert.Equal("token-missing", a.Code);
    }

    [Fact]
    public void UnknownRoutesAndWrongMethodsAreAnswered()
    {
        var e = new Endpoint(g);
        Assert.Equal(404, e.Call("GET", "nothing").Status);
        var wrong = e.Call("GET", "publish");
        Assert.Equal(405, wrong.Status); Assert.Equal("POST", wrong.Headers["Allow"]);
    }

    // ================================================================================================================
    // C4 startup

    sealed class Capture : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<TState>(LogLevel l, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f) => Lines.Add($"{l}: {f(state, ex)}");
    }

    [Fact]
    public void WithNoCharacterPublishSectionTheConnectorDoesNotStartAndRegistersNothing()
    {
        var log = new Capture(); var registered = new List<object>();
        var cfg = new IniConfigSource(); cfg.Merge(g.Config); // the grid's services are configured; the publish section is not

        var thrown = Assert.Throws<InvalidOperationException>(() => CharacterPublishServerConnector.Start(cfg, "CharacterPublish", log, registered.Add));

        Assert.Empty(registered);
        Assert.Contains("There is no [CharacterPublish] section", thrown.Message);
        Assert.Contains(log.Lines, l => l.StartsWith("Error") && l.Contains("There is no [CharacterPublish] section"));
    }

    [Fact]
    public void WithTheSectionButNoKeyOrAuthenticationSettingsItRefusesToStartAndSaysWhatIsMissing()
    {
        var log = new Capture(); var registered = new List<object>();
        var cfg = new IniConfigSource(); cfg.Merge(g.Config);
        cfg.AddConfig("CharacterPublish").Set("MaxOutfitsPerResident", "5"); // present, but nothing that matters

        var thrown = Assert.Throws<InvalidOperationException>(() => CharacterPublishServerConnector.Start(cfg, "CharacterPublish", log, registered.Add));

        Assert.Empty(registered);
        foreach (var missing in new[] { "ServiceKey is not set", "CreatorAccount is not set", "AuthenticationServiceModule is not set", "UserAccountServiceModule is not set", "AssetServiceModule is not set", "InventoryServiceModule is not set" })
        {
            Assert.Contains(log.Lines, l => l.StartsWith("Error") && l.Contains("not started") && l.Contains(missing));
        }
    }

    [Fact]
    public void AModuleThatDoesNotLoadStopsTheStartWithTheSettingNamed()
    {
        var log = new Capture(); var registered = new List<object>();
        var cfg = new IniConfigSource(); cfg.Merge(g.Config);
        var c = cfg.AddConfig("CharacterPublish");
        c.Set("ServiceKey", Endpoint.Key); c.Set("CreatorAccount", BundleFixtures.Creator.ToString());
        c.Set("AuthenticationServiceModule", "OpenSim.Services.AuthenticationService.dll:NoSuchService");
        c.Set("UserAccountServiceModule", "OpenSim.Services.UserAccountService.dll:UserAccountService");
        c.Set("AssetServiceModule", "OpenSim.Services.AssetService.dll:AssetService");
        c.Set("InventoryServiceModule", "OpenSim.Services.InventoryService.dll:XInventoryService");

        Assert.Throws<InvalidOperationException>(() => CharacterPublishServerConnector.Start(cfg, "CharacterPublish", log, registered.Add));

        Assert.Empty(registered);
        Assert.Contains(log.Lines, l => l.StartsWith("Error") && l.Contains("AuthenticationServiceModule") && l.Contains("did not load"));
    }

    [Fact]
    public void WithEverythingSetItStartsAndTurningTheKeyOffIsLoggedLoudly()
    {
        var log = new Capture(); var registered = new List<object>();
        var cfg = new IniConfigSource(); cfg.Merge(g.Config);
        var c = cfg.AddConfig("CharacterPublish");
        c.Set("RequireServiceKey", "false"); c.Set("CreatorAccount", BundleFixtures.Creator.ToString());
        c.Set("AuthenticationServiceModule", "OpenSim.Services.AuthenticationService.dll:PasswordAuthenticationService");
        c.Set("UserAccountServiceModule", "OpenSim.Services.UserAccountService.dll:UserAccountService");
        c.Set("AssetServiceModule", "OpenSim.Services.AssetService.dll:AssetService");
        c.Set("InventoryServiceModule", "OpenSim.Services.InventoryService.dll:XInventoryService");

        CharacterPublishServerConnector.Start(cfg, "CharacterPublish", log, registered.Add);

        Assert.Single(registered);
        Assert.Contains(log.Lines, l => l.StartsWith("Warning") && l.Contains("*** WARNING ***") && l.Contains("RequireServiceKey is off"));
        Assert.Contains(log.Lines, l => l.StartsWith("Information") && l.Contains("ready on /characterpublish") && l.Contains("HTTPS required True"));
    }

    [Fact]
    public void TheDefaultsAreTheSafeOnes()
    {
        var cfg = new IniConfigSource();
        var c = cfg.AddConfig("CharacterPublish");
        c.Set("ServiceKey", Endpoint.Key); c.Set("CreatorAccount", BundleFixtures.Creator.ToString());
        foreach (var k in new[] { "AuthenticationServiceModule", "UserAccountServiceModule", "AssetServiceModule", "InventoryServiceModule" }) c.Set(k, "m");

        var s = CharacterPublishSettings.Read(cfg, "CharacterPublish", out var problems);

        Assert.Empty(problems);
        Assert.True(s.RequireServiceKey); Assert.False(s.AllowInsecureHttp); Assert.Empty(s.TrustedProxies);
        Assert.Equal(30, s.TokenMinutes); Assert.Equal(64L * 1024 * 1024, s.MaxBundleBytes); Assert.Equal(16 * 1024 * 1024, s.MaxAssetBytes); Assert.Equal(65, s.MaxFiles);
        Assert.Equal(20, s.MaxOutfitsPerResident); Assert.True(s.SaveCurrentLook); Assert.False(s.MissingTextureIsWarning);
        Assert.Equal((120, 10, 5, 10), (s.RequestsPerAddressPerMinute, s.LoginAttemptsPerAddressPerMinute, s.LoginAttemptsPerNamePerMinute, s.PublishesPerResidentPerHour));
    }

    // ================================================================================================================
    // C5 logs

    [Fact]
    public void NoPasswordTokenKeyOrPasswordHashAppearsInAnyLogLine()
    {
        var u = NewLoginResident(nameof(NoPasswordTokenKeyOrPasswordHashAppearsInAnyLogLine));
        var e = new Endpoint(g);
        var a = BundleFixtures.Assemble(u.R.Id);
        var bad = "wrong-pw-" + Guid.NewGuid().ToString("N"); Secrets.Add(bad); Secrets.Add(Util.Md5Hash(bad));
        var badKey = "bad-key-" + Guid.NewGuid().ToString("N"); Secrets.Add(badKey);

        e.Login(u.First, u.Last, bad);                                   // failed login
        var token = LoginToken(e, u);                                    // good login
        e.Publish(u.R.Id, token, Zip(a.Bundle, a.Content));              // good publish
        e.Publish(u.R.Id, token.Replace(token[0], token[0] == 'a' ? 'b' : 'a'), Zip(a.Bundle, a.Content)); // a token that is not hers
        var req = new FakeRequest { UriPath = "/characterpublish/login", IsSecured = true };
        req.Headers["X-Publish-Key"] = badKey;
        e.Handler.Handle(req, new FakeResponse());                       // wrong key
        e.Login(u.First, u.Last, u.Password, secure: false);             // plain HTTP login
        e.Call("POST", "logout", null, h => { h["X-Publish-Resident"] = u.R.Id.ToString(); h["X-Publish-Token"] = token; });

        var lines = LogCapture.Lines.ToArray();
        Assert.Contains(lines, l => l.Contains("[CHARACTER PUBLISH]") && l.Contains("login ok"));   // the logging is on, so an empty search means something
        Assert.Contains(lines, l => l.Contains("[CHARACTER PUBLISH]") && l.Contains("publish for"));
        var secrets = Secrets.Distinct().ToList();
        Assert.True(secrets.Count >= 10);
        foreach (var secret in secrets)
        {
            var hit = lines.FirstOrDefault(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.True(hit == null, $"a secret of {secret.Length} characters (starting '{secret[..Math.Min(4, secret.Length)]}') is in a log line: {hit}");
        }
    }
}
