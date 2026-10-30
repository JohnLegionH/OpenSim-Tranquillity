/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nini.Config;
using Nwc.XmlRpc;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;
using OpenSim.Server.Base.Hosting;
using OpenSim.Server.GridServer;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace OpenSim.Server.Base.Tests.Hosting;

/// <summary>
/// The loopback-only, shutdown-only route of the grid server ([LocalShutdown]).
/// Every test uses its own port and its own secret file under a per-test folder.
/// Parallel-safe: no static or process-wide state is touched.
/// </summary>
public sealed class LocalShutdownServiceTests : IDisposable
{
    private const string Secret = "correct-horse-battery-staple-0001";

    private readonly string _folder = Path.Combine(
        AppContext.BaseDirectory, "local-shutdown-tests", Guid.NewGuid().ToString("N"));

    private readonly List<LocalShutdownService> _started = new();

    public LocalShutdownServiceTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        foreach (LocalShutdownService service in _started)
            service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---- off by default ----------------------------------------------------

    [Fact]
    public async Task NoSection_DoesNotListen_AndNothingAnswersOnTheConfiguredPort()
    {
        int port = FreePort();
        var server = new FakeServerBase(new IniConfigSource());
        LocalShutdownService service = await StartAsync(server);

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
        Assert.Equal(0, server.ShutdownCalls);
    }

    [Fact]
    public async Task SectionWithoutEnabledKey_WithPortAndSecret_DoesNotListen()
    {
        int port = FreePort();
        var config = new IniConfigSource();
        IConfig section = config.AddConfig("LocalShutdown");
        section.Set("Port", port.ToString());
        section.Set("SecretFile", WriteSecret(Secret));
        LocalShutdownService service = await StartAsync(new FakeServerBase(config));

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
    }

    [Fact]
    public async Task EnabledFalse_WithPortAndSecret_DoesNotListen()
    {
        int port = FreePort();
        string secretFile = WriteSecret(Secret);
        var server = new FakeServerBase(Config(enabled: "false", port: port, secretFile: secretFile));
        LocalShutdownService service = await StartAsync(server);

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
    }

    [Fact]
    public async Task EnabledWithSecretFile_Listens_OnTheConfiguredPort_OnLoopbackOnly()
    {
        int port = FreePort();
        string secretFile = WriteSecret(Secret);
        var server = new FakeServerBase(Config("true", port, secretFile));
        LocalShutdownService service = await StartAsync(server);

        Assert.True(service.Listening);
        Assert.Equal(port, service.Port);
        Assert.Equal(IPAddress.Loopback, service.ListenAddress);
        Assert.True(CanConnect(port));
    }

    [Fact]
    public async Task EnabledWithSecretFile_IsNotReachableOnAnyOtherLocalAddress()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        LocalShutdownService service = await StartAsync(server);
        Assert.True(service.Listening);

        var others = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToList();

        // A machine with no other IPv4 address has nothing to try; the bound address
        // asserted above is the proof there.
        foreach (IPAddress other in others)
            Assert.False(CanConnect(port, other), $"reachable on {other}");
    }

    // ---- the secret ----------------------------------------------------------

    [Fact]
    public async Task Enabled_WithNoSecretFile_DoesNotListen()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, secretFile: null));
        LocalShutdownService service = await StartAsync(server);

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
    }

    [Fact]
    public async Task Enabled_WithMissingSecretFile_DoesNotListen()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, Path.Combine(_folder, "absent.txt")));
        LocalShutdownService service = await StartAsync(server);

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("short-secret")]
    public async Task Enabled_WithEmptyOrShortSecret_DoesNotListen(string content)
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(content)));
        LocalShutdownService service = await StartAsync(server);

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("70000")]
    [InlineData("")]
    public async Task Enabled_WithoutAValidPort_DoesNotListen(string port)
    {
        var config = new IniConfigSource();
        IConfig section = config.AddConfig("LocalShutdown");
        section.Set("Enabled", "true");
        section.Set("Port", port);
        section.Set("SecretFile", WriteSecret(Secret));
        LocalShutdownService service = await StartAsync(new FakeServerBase(config));

        Assert.False(service.Listening);
    }

    [Fact]
    public async Task WrongSecret_IsRefused_AndNothingShutsDown()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string reply = await PostAsync(port, ShutdownBody("not-the-secret-not-the-secret-00"));

        Assert.False(Accepted(reply), reply);
        await NothingHappens(server);
    }

    [Fact]
    public async Task SecretThatIsOnlyAPrefixOfTheRealOne_IsRefused()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string reply = await PostAsync(port, ShutdownBody(Secret.Substring(0, Secret.Length - 1)));

        Assert.False(Accepted(reply), reply);
        await NothingHappens(server);
    }

    [Fact]
    public async Task NoSecretInTheRequest_IsRefused()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string reply = await PostAsync(port, ShutdownBody(password: null));

        Assert.False(Accepted(reply), reply);
        await NothingHappens(server);
    }

    [Fact]
    public async Task TheSecretFileIsReadWithItsLineEndingTrimmed()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret + "\r\n")));
        await StartAsync(server);

        string reply = await PostAsync(port, ShutdownBody(Secret));

        Assert.True(Accepted(reply), reply);
        await WaitForShutdown(server);
    }

    [Fact]
    public async Task TheSecretNeverAppearsInTheLog()
    {
        int port = FreePort();
        var logger = new ListLogger();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server, logger);

        await PostAsync(port, ShutdownBody("wrong-wrong-wrong-wrong-wrong-0"));
        await PostAsync(port, ShutdownBody(Secret));
        await WaitForShutdown(server);

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Secret));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("wrong-wrong"));
    }

    // ---- the peer --------------------------------------------------------------

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("10.0.0.5")]
    [InlineData("2001:db8::1")]
    public async Task APeerThatIsNotLoopback_IsRefused_EvenWithTheRightSecret(string peer)
    {
        var server = new FakeServerBase(Config("true", FreePort(), WriteSecret(Secret)));
        LocalShutdownService service = await StartAsync(server);
        Assert.True(service.Listening);

        XmlRpcResponse response = service.HandleShutdown(Request("admin_shutdown", Secret), new IPEndPoint(IPAddress.Parse(peer), 40000));

        Assert.False(Accepted(response));
        await NothingHappens(server);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ALoopbackPeer_WithTheRightSecret_IsAccepted(string peer)
    {
        var server = new FakeServerBase(Config("true", FreePort(), WriteSecret(Secret)));
        LocalShutdownService service = await StartAsync(server);

        XmlRpcResponse response = service.HandleShutdown(Request("admin_shutdown", Secret), new IPEndPoint(IPAddress.Parse(peer), 40000));

        Assert.True(Accepted(response));
        await WaitForShutdown(server);
    }

    // ---- shutdown and nothing else ---------------------------------------------

    [Theory]
    [InlineData("admin_console_command")]
    [InlineData("admin_restart")]
    [InlineData("admin_broadcast")]
    [InlineData("console_command")]
    public async Task AnyOtherCommand_IsRefused_EvenWithTheRightSecret(string method)
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string reply = await PostAsync(port, ShutdownBody(Secret, method));

        Assert.False(Accepted(reply), reply);
        await NothingHappens(server);
    }

    [Fact]
    public async Task OtherCommandReachingTheHandlerDirectly_IsRefused()
    {
        var server = new FakeServerBase(Config("true", FreePort(), WriteSecret(Secret)));
        LocalShutdownService service = await StartAsync(server);

        XmlRpcResponse response = service.HandleShutdown(Request("admin_console_command", Secret), new IPEndPoint(IPAddress.Loopback, 40000));

        Assert.False(Accepted(response));
        await NothingHappens(server);
    }

    // ---- malformed and hostile requests -------------------------------------------------

    [Fact]
    public async Task ADocumentTypeDeclaration_IsRefused_EvenWithTheRightSecret()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string body = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY e \"x\">]>" + ShutdownBody(Secret).Replace("<?xml version=\"1.0\"?>", string.Empty);
        string reply = await PostAsync(port, body);

        Assert.False(Accepted(reply), reply);
        await NothingHappens(server);
    }

    [Fact]
    public async Task ABodyOverTheLimit_IsRefused_AndTheRouteStillAnswersAfterwards()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        LocalShutdownService service = await StartAsync(server);

        string big = ShutdownBody(Secret) + new string(' ', 20000);
        string reply = await PostAsync(port, big);
        Assert.False(Accepted(reply), reply);
        Assert.Equal(0, server.ShutdownCalls);
        Assert.True(service.Listening);

        Assert.True(Accepted(await PostAsync(port, ShutdownBody(Secret))));
        await WaitForShutdown(server);
    }

    [Fact]
    public async Task MalformedXml_IsRefused_AndTheRouteStillAnswersAfterwards()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string reply = await PostAsync(port, "<methodCall><methodName>admin_shutdown");
        Assert.False(Accepted(reply), reply);
        Assert.Equal(0, server.ShutdownCalls);

        Assert.True(Accepted(await PostAsync(port, ShutdownBody(Secret))));
        await WaitForShutdown(server);
    }

    [Fact]
    public async Task AGetRequest_IsRefused()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{port}/?password={Secret}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        await NothingHappens(server);
    }

    // ---- the right request ---------------------------------------------------------

    [Fact]
    public async Task RightSecretFromLoopback_IsAnswered_ThenShutsDownThroughTheServerBase_Once()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        await StartAsync(server);

        string reply = await PostAsync(port, ShutdownBody(Secret));

        Assert.True(Accepted(reply), reply);
        await WaitForShutdown(server);
        await Task.Delay(300);
        Assert.Equal(1, server.ShutdownCalls);
    }

    [Fact]
    public async Task TheRoute_StopsTheHost_LikeTheConsoleShutdownCommand()
    {
        int port = FreePort();
        var config = Config("true", port, WriteSecret(Secret));
        var serverBase = new HostLifetimeShutdownBase(config);

        using IHost host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IServerBase>(serverBase);
                services.AddHostedService<LocalShutdownService>();
            })
            .Build();
        serverBase.HostLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();
        Task whenStopped = host.WaitForShutdownAsync();
        Assert.False(whenStopped.IsCompleted);

        string reply = await PostAsync(port, ShutdownBody(Secret));
        Assert.True(Accepted(reply), reply);

        Task done = await Task.WhenAny(whenStopped, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(whenStopped, done);
        Assert.Equal(1, serverBase.ShutdownCalls);
        await host.StopAsync();
        Assert.False(CanConnect(port));
    }

    [Fact]
    public async Task Stop_ClosesTheListener()
    {
        int port = FreePort();
        var server = new FakeServerBase(Config("true", port, WriteSecret(Secret)));
        LocalShutdownService service = await StartAsync(server);
        Assert.True(CanConnect(port));

        await service.StopAsync(CancellationToken.None);

        Assert.False(service.Listening);
        Assert.False(CanConnect(port));
    }

    // ---- helpers ---------------------------------------------------------------------

    private async Task<LocalShutdownService> StartAsync(FakeServerBase server, ILogger<LocalShutdownService>? logger = null)
    {
        var service = new LocalShutdownService(logger ?? new ListLogger(), server);
        _started.Add(service);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    private static IConfigSource Config(string enabled, int port, string? secretFile)
    {
        var config = new IniConfigSource();
        IConfig section = config.AddConfig("LocalShutdown");
        section.Set("Enabled", enabled);
        section.Set("Port", port.ToString());
        if (secretFile is not null)
            section.Set("SecretFile", secretFile);
        return config;
    }

    private string WriteSecret(string content)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".secret");
        File.WriteAllText(path, content);
        return path;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static bool CanConnect(int port, IPAddress? address = null)
    {
        using var client = new TcpClient();
        try
        {
            return client.ConnectAsync(address ?? IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(3)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static XmlRpcRequest Request(string method, string? password)
    {
        var data = new Hashtable { ["shutdown"] = "delayed" };
        if (password is not null)
            data["password"] = password;
        return new XmlRpcRequest(method, new ArrayList { data });
    }

    private static string ShutdownBody(string? password, string method = "admin_shutdown")
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><methodCall><methodName>").Append(method).Append("</methodName><params><param><value><struct>");
        if (password is not null)
            sb.Append("<member><name>password</name><value><string>").Append(password).Append("</string></value></member>");
        sb.Append("<member><name>shutdown</name><value><string>delayed</string></value></member>");
        sb.Append("</struct></value></param></params></methodCall>");
        return sb.ToString();
    }

    private static async Task<string> PostAsync(int port, string body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            // The server matches the content type exactly ("text/xml"), so no charset parameter.
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");
            using HttpResponseMessage response = await http.PostAsync($"http://127.0.0.1:{port}/", content);
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException e)
        {
            return "HTTP-ERROR " + e.Message;
        }
    }

    private static bool Accepted(string reply) =>
        Regex.IsMatch(reply, @"<name>accepted</name>\s*<value>\s*<boolean>\s*(1|true)", RegexOptions.IgnoreCase);

    private static bool Accepted(XmlRpcResponse response) =>
        response.Value is Hashtable h && h["accepted"] is bool b && b;

    private static async Task WaitForShutdown(FakeServerBase server)
    {
        for (int i = 0; i < 200 && server.ShutdownCalls == 0; i++)
            await Task.Delay(50);
        Assert.True(server.ShutdownCalls >= 1, "the server base was never asked to shut down");
    }

    // A "did NOT happen" window stays a real wait (no test clock exists for it).
    private static async Task NothingHappens(FakeServerBase server)
    {
        await Task.Delay(1500);
        Assert.Equal(0, server.ShutdownCalls);
    }

    private sealed class ListLogger : ILogger<LocalShutdownService>
    {
        private readonly object _lock = new();
        private readonly List<string> _messages = new();

        public List<string> Messages
        {
            get { lock (_lock) return new List<string>(_messages); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
            lock (_lock) _messages.Add(text);
        }
    }

    private class FakeServerBase : IServerBase
    {
        private int _shutdownCalls;

        public FakeServerBase(IConfigSource config) { Config = config; }

        public IConfigSource Config { get; set; }
        public ICommandConsole Console { get; set; } = new MockConsole();
        public DateTime StartupTime { get; set; }
        public string Version { get; set; } = "test-version";
        public int ShutdownCalls => Volatile.Read(ref _shutdownCalls);

        public virtual void Shutdown() => Interlocked.Increment(ref _shutdownCalls);

        public void RegisterCommonCommands() { }
        public void RegisterCommonComponents(IConfigSource configSource) { }
        public void CreatePIDFile(string path) { }
        public string GetThreadsReport() => string.Empty;
        public string GetUptimeReport() => string.Empty;
        public string GetVersionText() => string.Empty;
        public void HandleShow(string module, string[] cmd) { }
        public void HandleThreadsAbort(string module, string[] cmd) { }
        public void LogEnvironmentInformation() { }
        public void RegisterCommonAppenders(IConfig startupConfig) { }
        public void RemovePIDFile() { }
    }

    /// <summary>
    /// The production server base: Shutdown ends in ShutdownSpecific, which asks the host to stop.
    /// ServerBase.Shutdown also closes a stats collector that only exists after the real startup,
    /// so the test calls the part that the console command reaches next.
    /// </summary>
    private sealed class HostLifetimeShutdownBase : HostLifetimeServerBase
    {
        private int _shutdownCalls;

        public HostLifetimeShutdownBase(IConfigSource config) { Config = config; }

        public int ShutdownCalls => Volatile.Read(ref _shutdownCalls);

        public override void Shutdown()
        {
            Interlocked.Increment(ref _shutdownCalls);
            ShutdownSpecific();
        }
    }
}
