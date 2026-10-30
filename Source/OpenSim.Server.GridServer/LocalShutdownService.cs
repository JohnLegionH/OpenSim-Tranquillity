/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

#nullable enable

using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nini.Config;
using Nwc.XmlRpc;
using OpenSim.Framework.Servers;

namespace OpenSim.Server.GridServer;

/// <summary>
/// A shutdown-only route for the grid server, so a deploy can stop it without anyone at its console.
/// </summary>
/// <remarks>
/// Off unless <c>[LocalShutdown] Enabled = true</c>. When on it opens its own small HTTP listener on the loopback
/// address (not a handler on the public or private service port; it shares no state with the service HTTP
/// servers, whose static timeout manager would otherwise be stopped with it) and answers one XML-RPC method,
/// <c>admin_shutdown</c>, in the shape of the region server's RemoteAdmin method of the same name
/// (<c>password</c>, optional <c>shutdown=delayed</c> and <c>milliseconds</c>). The password is the content of
/// the file named by <c>SecretFile</c>, so it is never in the configuration itself. A request is acted on only
/// when it comes from a loopback peer, names that method and carries the secret; the action is
/// <see cref="IServerBase.Shutdown"/>, the call the console "shutdown" command makes, so the host stops its
/// services in order and the process exits as it does after a typed shutdown.
/// </remarks>
public sealed class LocalShutdownService : IHostedService
{
    public const string SectionName = "LocalShutdown";
    public const string MethodName = "admin_shutdown";

    /// <summary>A secret shorter than this is refused: the route would not be worth guarding.</summary>
    public const int MinimumSecretLength = 16;

    private const int DefaultDelayMilliseconds = 500;
    private const int MaximumDelayMilliseconds = 60000;

    private readonly ILogger<LocalShutdownService> _logger;
    private readonly IServerBase _serverBase;

    private const int MaximumHeadBytes = 8192;
    private const int MaximumBodyBytes = 8192;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;
    private Task? _acceptLoop;
    private byte[]? _secret;
    private int _shutdownRequested;

    public LocalShutdownService(ILogger<LocalShutdownService> logger, IServerBase serverBase)
    {
        _logger = logger;
        _serverBase = serverBase;
    }

    public bool Listening => _listener is not null;

    public int Port { get; private set; }

    public IPAddress? ListenAddress => (_listener?.LocalEndpoint as IPEndPoint)?.Address;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IConfig? section = _serverBase.Config?.Configs[SectionName];
        if (section is null || !section.GetBoolean("Enabled", false))
            return Task.CompletedTask;

        if (!TryReadPort(section, out int port))
        {
            _logger.LogError("[LOCAL SHUTDOWN]: Enabled, but Port is missing or not between 1 and 65535. The route is not started.");
            return Task.CompletedTask;
        }

        if (!TryReadSecret(section.GetString("SecretFile", string.Empty), out byte[]? secret))
            return Task.CompletedTask;

        // The secret is in place before the listener opens, so no request can meet an unarmed handler.
        _secret = secret;

        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start(8);
        }
        catch (SocketException e)
        {
            _secret = null;
            _logger.LogError("[LOCAL SHUTDOWN]: Could not listen on 127.0.0.1:{Port}: {Message}. The route is not started.", port, e.Message);
            return Task.CompletedTask;
        }

        _listener = listener;
        _stopping = new CancellationTokenSource();
        CancellationToken stopping = _stopping.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, stopping));
        Port = port;
        _logger.LogInformation("[LOCAL SHUTDOWN]: Shutdown route listening on 127.0.0.1:{Port}", port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        TcpListener? listener = _listener;
        _listener = null;
        _secret = null;

        if (listener is null)
            return;

        _stopping?.Cancel();
        listener.Stop();

        Task? loop = _acceptLoop;
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                // The listener is closed; a connection still being served ends at its own timeout.
            }
        }
    }

    /// <summary>
    /// The XML-RPC handler. Public so the checks that gate it can be exercised without a socket.
    /// </summary>
    public XmlRpcResponse HandleShutdown(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        var data = new Hashtable { ["accepted"] = false, ["success"] = false };
        var response = new XmlRpcResponse { Value = data };

        string? refusal = Check(request, remoteClient, out int delay);
        if (refusal is not null)
        {
            data["error"] = refusal;
            _logger.LogWarning("[LOCAL SHUTDOWN]: Refused a shutdown request from {Peer}: {Reason}", remoteClient?.Address, refusal);
            return response;
        }

        data["accepted"] = true;
        data["success"] = true;

        // Answer first; the shutdown follows after the delay so the reply is on the wire.
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
        {
            _logger.LogInformation("[LOCAL SHUTDOWN]: Shutdown accepted from {Peer}; stopping in {Delay} ms", remoteClient.Address, delay);
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    _serverBase.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "[LOCAL SHUTDOWN]: Shutdown failed");
                }
            });
        }

        return response;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, stopping);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            timeout.CancelAfter(ConnectionTimeout);
            try
            {
                NetworkStream stream = client.GetStream();
                var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                (int status, byte[] body) = await ReadAndAnswerAsync(stream, remote, timeout.Token).ConfigureAwait(false);
                await WriteResponseAsync(stream, status, body, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException
                                          or OperationCanceledException or InvalidOperationException)
            {
                // A client that goes away or stalls is dropped; nothing was acted on.
            }
        }
    }

    /// <summary>Reads one request (head and body, both size-capped) and returns the status and body to send.</summary>
    private async Task<(int Status, byte[] Body)> ReadAndAnswerAsync(NetworkStream stream, IPEndPoint remote, CancellationToken token)
    {
        var received = new byte[MaximumHeadBytes + MaximumBodyBytes];
        int count = 0;
        int headEnd = -1;

        while (headEnd < 0)
        {
            if (count >= MaximumHeadBytes)
                return (431, Array.Empty<byte>());

            int read = await stream.ReadAsync(received.AsMemory(count, MaximumHeadBytes - count), token).ConfigureAwait(false);
            if (read == 0)
                return (400, Array.Empty<byte>());

            count += read;
            headEnd = IndexOfHeadEnd(received, count);
        }

        string head = Encoding.ASCII.GetString(received, 0, headEnd);
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2 || !string.Equals(requestLine[0], "POST", StringComparison.Ordinal))
            return (405, Array.Empty<byte>());

        int contentLength = -1;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !int.TryParse(line.AsSpan(colon + 1).Trim(), out contentLength))
                return (400, Array.Empty<byte>());
        }

        if (contentLength < 0)
            return (411, Array.Empty<byte>());
        if (contentLength > MaximumBodyBytes)
            return (413, Array.Empty<byte>());

        int bodyStart = headEnd + 4;
        int bodyEnd = bodyStart + contentLength;
        while (count < bodyEnd)
        {
            int read = await stream.ReadAsync(received.AsMemory(count, bodyEnd - count), token).ConfigureAwait(false);
            if (read == 0)
                return (400, Array.Empty<byte>());
            count += read;
        }

        string xml = Encoding.UTF8.GetString(received, bodyStart, contentLength);
        XmlRpcRequest? request = ParseRequest(xml);
        if (request is null)
            return (400, Array.Empty<byte>());

        XmlRpcResponse response;
        if (string.Equals(request.MethodName, MethodName, StringComparison.Ordinal))
        {
            response = HandleShutdown(request, remote);
        }
        else
        {
            // Nothing but the one method exists on this listener.
            response = new XmlRpcResponse();
            response.SetFault(-32601, "Requested method not found");
            _logger.LogWarning("[LOCAL SHUTDOWN]: Refused a request for another method from {Peer}", remote.Address);
        }

        using var buffer = new MemoryStream();
        using (var writer = new XmlTextWriter(buffer, new UTF8Encoding(false)) { Formatting = Formatting.None })
        {
            new XmlRpcResponseSerializer().Serialize(writer, response);
            writer.Flush();
        }

        return (200, buffer.ToArray());
    }

    private XmlRpcRequest? ParseRequest(string xml)
    {
        // The route has no use for a document type declaration; refusing one up front keeps entity
        // expansion out of a parser that is reached before the secret is checked.
        if (xml.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || xml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var reader = new StringReader(xml);
            return new XmlRpcRequestDeserializer().Deserialize(reader) as XmlRpcRequest;
        }
        catch (Exception e)
        {
            _logger.LogWarning("[LOCAL SHUTDOWN]: A request could not be decoded: {Message}", e.Message);
            return null;
        }
    }

    private static int IndexOfHeadEnd(byte[] data, int count)
    {
        for (int i = 0; i + 3 < count; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, byte[] body, CancellationToken token)
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            405 => "Method Not Allowed",
            411 => "Length Required",
            413 => "Payload Too Large",
            431 => "Request Header Fields Too Large",
            _ => "Error",
        };

        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/xml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private string? Check(XmlRpcRequest request, IPEndPoint remoteClient, out int delay)
    {
        delay = DefaultDelayMilliseconds;

        byte[]? secret = _secret;
        if (secret is null)
            return "route not enabled";

        if (remoteClient?.Address is null || !IsLoopback(remoteClient.Address))
            return "peer is not loopback";

        if (!string.Equals(request.MethodName, MethodName, StringComparison.Ordinal))
            return "method not available";

        if (request.Params.Count == 0 || request.Params[0] is not Hashtable parameters)
            return "no parameters";

        if (parameters["password"] is not string password
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), secret))
            return "wrong or missing secret";

        if (parameters["milliseconds"] is not null
            && int.TryParse(parameters["milliseconds"]!.ToString(), out int requested)
            && requested >= 0)
            delay = Math.Min(requested, MaximumDelayMilliseconds);

        return null;
    }

    private static bool IsLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private static bool TryReadPort(IConfig section, out int port)
    {
        port = 0;
        try
        {
            port = section.GetInt("Port", 0);
        }
        catch (FormatException)
        {
            return false;
        }

        return port is >= 1 and <= 65535;
    }

    private bool TryReadSecret(string path, out byte[]? secret)
    {
        secret = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogError("[LOCAL SHUTDOWN]: Enabled, but SecretFile is not set. The route is not started.");
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("[LOCAL SHUTDOWN]: Enabled, but SecretFile {Path} could not be read ({Message}). The route is not started.", path, e.Message);
            return false;
        }

        string firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length < MinimumSecretLength)
        {
            _logger.LogError("[LOCAL SHUTDOWN]: Enabled, but the secret in {Path} is empty or shorter than {Minimum} characters. The route is not started.", path, MinimumSecretLength);
            return false;
        }

        secret = Encoding.UTF8.GetBytes(firstLine);
        return true;
    }
}
