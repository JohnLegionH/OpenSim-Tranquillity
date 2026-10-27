/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Concurrent;
using OpenMetaverse;
using OpenSim.Region.Framework.Interfaces;

namespace SLua.ScriptEngine.Tests;

/// <summary>
/// The region's HTTP request module with no network: a request is answered at once with status 200 and a fixed body, and the
/// outbound filter refuses one host name.
/// </summary>
internal sealed class FakeHttpModule : IHttpRequestModule
{
    public const string RefusedHost = "blocked.example.org";
    public const string Body = "hello from the fake";

    private sealed class Completed : IHttpServiceRequest
    {
        public int Status { get; init; }
        public string ResponseBody { get; init; }
        public bool Finished => true;
        public UUID ItemID { get; set; }
        public uint LocalID { get; set; }
        public UUID ReqID { get; set; }
        public void Process() { }
        public void SendRequest() { }
        public void Stop() { }
    }

    private readonly ConcurrentQueue<IHttpServiceRequest> m_done = new();
    public readonly ConcurrentQueue<string> Urls = new();

    public UUID MakeHttpRequest(string url, string parameters, string body) => UUID.Zero;

    public UUID StartHttpRequest(uint localID, UUID itemID, string url, List<string> parameters, Dictionary<string, string> headers, string body)
    {
        Urls.Enqueue(url);
        UUID id = UUID.Random();
        m_done.Enqueue(new Completed { Status = 200, ResponseBody = Body, ItemID = itemID, LocalID = localID, ReqID = id });
        return id;
    }

    public void StopHttpRequest(uint localID, UUID itemID) { }
    public IHttpServiceRequest GetNextCompletedRequest() => m_done.TryDequeue(out IHttpServiceRequest r) ? r : null;
    public void RemoveCompletedRequest(UUID id) { }
    public bool CheckThrottle(uint localID, UUID ownerID) => true;
    public bool CheckAllowed(Uri url) => url.Host != RefusedHost;
}
