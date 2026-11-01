/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.ClientStack.LindenCaps.AIS;
using OpenSim.Tests.Common;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS.Tests;

/// <summary>
/// A viewer decides that a folder belongs to the Library by comparing its owner with Linden Lab's own library
/// owner, which is built into the viewer. On a grid whose Library has another owner that test fails, so the viewer
/// sends every Library folder read to the agent's InventoryAPIv3 instead of LibraryAPIv3. These tests pin the
/// server side of that: InventoryAPIv3 answers, read-only, for an id that is not the agent's but is in the Library,
/// with exactly the bytes LibraryAPIv3 gives for it; a mutation on such an id is refused exactly as LibraryAPIv3
/// refuses it and changes nothing; and an id that is neither the agent's nor the Library's answers as before.
///
/// <para>The agent's side is the real <see cref="AISv3Module.InventoryServiceBackend"/> over
/// <see cref="PrincipalIgnoringInventoryService"/> (so the AIS-SEC-1 scoping is genuinely in play), the library
/// side is <see cref="FakeAisBackend"/> behind a real library-mode <see cref="AisHandler"/>.</para>
/// </summary>
[TestFixture]
public class AisLibraryViaInventoryHttpTests
{
    private const string Cap = "/CAP/5ec0de00-0000-4000-8000-0000000000aa";

    private static readonly UUID Alice = new("a1000000-0000-4000-8000-000000000001");
    private static readonly UUID AliceRoot = new("a1000000-0000-4000-8000-000000000002");
    private static readonly UUID AliceCof = new("a1000000-0000-4000-8000-000000000003");
    private static readonly UUID AliceShirt = new("a1000000-0000-4000-8000-000000000004");

    private static readonly UUID Bob = new("b1000000-0000-4000-8000-000000000001");
    private static readonly UUID BobRoot = new("b1000000-0000-4000-8000-000000000002");
    private static readonly UUID BobFolder = new("b1000000-0000-4000-8000-000000000003");
    private static readonly UUID BobShirt = new("b1000000-0000-4000-8000-000000000004");

    private static readonly UUID LibraryOwner = new("c1000000-0000-4000-8000-000000000001");
    private static readonly UUID LibRoot = new("c1000000-0000-4000-8000-000000000002");
    private static readonly UUID LibFolder = new("c1000000-0000-4000-8000-000000000003");
    private static readonly UUID LibSub = new("c1000000-0000-4000-8000-000000000004");
    private static readonly UUID LibItem = new("c1000000-0000-4000-8000-000000000005");
    private static readonly UUID LibSubItem = new("c1000000-0000-4000-8000-000000000006");
    private static readonly UUID LibLink = new("c1000000-0000-4000-8000-000000000007");

    private static readonly UUID Unknown = new("d1000000-0000-4000-8000-000000000001");

    private sealed class Request : OpenSim.Framework.Servers.HttpServer.IOSHttpRequest
    {
        public Request(string verb, string url, OSD body)
        {
            HttpMethod = verb; Url = new Uri("http://sim.test" + url); RawUrl = url;
            InputStream = body is null ? new MemoryStream() : new MemoryStream(OSDParser.SerializeLLSDXmlBytes(body));
        }
        public string HttpMethod { get; }
        public Uri Url { get; }
        public string RawUrl { get; }
        public string UriPath => Url.AbsolutePath;
        public Stream InputStream { get; set; }
        public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
        public bool HasEntityBody => InputStream.Length > 0;
        public long ContentLength => InputStream.Length;
        public long ContentLength64 => InputStream.Length;
        public string ContentType => "application/llsd+xml";
        public string[] AcceptTypes => Array.Empty<string>();
        public System.Text.Encoding ContentEncoding => System.Text.Encoding.UTF8;
        public bool IsSecured => false;
        public bool KeepAlive => false;
        public System.Collections.Specialized.NameValueCollection QueryString => throw new NotImplementedException();
        public System.Collections.Hashtable Query => throw new NotImplementedException();
        public HashSet<string> QueryFlags => throw new NotImplementedException();
        public Dictionary<string, string> QueryAsDictionary => throw new NotImplementedException();
        public IPEndPoint RemoteIPEndPoint => new(IPAddress.Loopback, 1);
        public IPEndPoint LocalIPEndPoint => new(IPAddress.Loopback, 2);
        public string UserAgent => "test";
        public double ArrivalTS => 0;
    }

    private sealed class World
    {
        public PrincipalIgnoringInventoryService Inventory;
        public FakeAisBackend Library;
        public AisHandler LibraryHandler;
        public AisHandler InventoryWithLibrary;
        public AisHandler InventoryWithoutLibrary;
    }

    private static World Build()
    {
        var inv = new PrincipalIgnoringInventoryService();
        inv.Seed(AliceRoot, Alice, UUID.Zero, "My Inventory", (short)FolderType.Root, 3);
        inv.Seed(AliceCof, Alice, AliceRoot, "Current Outfit", (short)FolderType.CurrentOutfit, 5);
        inv.SeedItem(AliceShirt, Alice, AliceRoot, "a shirt");
        inv.Seed(BobRoot, Bob, UUID.Zero, "My Inventory", (short)FolderType.Root, 3);
        inv.Seed(BobFolder, Bob, BobRoot, "Bob's folder", -1, 2);
        inv.SeedItem(BobShirt, Bob, BobFolder, "bob's shirt");

        var lib = new FakeAisBackend(LibraryOwner);
        lib.AddFolder(LibRoot, UUID.Zero, "Example Library", 1, (short)FolderType.Root);
        lib.AddFolder(LibFolder, LibRoot, "Clothing Library", 4, -1);
        lib.AddFolder(LibSub, LibFolder, "Sub folder", 2, -1);
        lib.AddItem(LibItem, LibFolder, "Library Shirt");
        lib.AddItem(LibSubItem, LibSub, "Library Shoes");
        lib.AddLink(LibLink, LibFolder, "Link to shirt", LibItem);

        var libraryHandler = new AisHandler(Cap, LibraryOwner, lib, AisMode.Library);
        IAisInventoryBackend Agent() => new AISv3Module.InventoryServiceBackend(inv, Alice);
        return new World
        {
            Inventory = inv,
            Library = lib,
            LibraryHandler = libraryHandler,
            InventoryWithLibrary = new AisHandler(Cap, Alice, Agent(), AisMode.Inventory, null, default, libraryHandler),
            InventoryWithoutLibrary = new AisHandler(Cap, Alice, Agent()),
        };
    }

    private static (int Status, byte[] Raw) Send(AisHandler handler, string verb, string path, OSD body = null)
    {
        var response = new TestOSHttpResponse();
        handler.Handle(new Request(verb, Cap + path, body), response);
        return (response.StatusCode, response.RawBuffer ?? Array.Empty<byte>());
    }

    private static OSDMap Parse(byte[] raw) => (OSDMap)OSDParser.DeserializeLLSDXml(raw);

    /// <summary>Every read route the viewer can send for a Library folder or item (indexed by ids that exist in the Library).</summary>
    public static IEnumerable<TestCaseData> LibraryReadRoutes()
    {
        yield return new TestCaseData($"/category/{LibFolder}/children?depth=50").SetName("children_depth50");
        yield return new TestCaseData($"/category/{LibFolder}/children?depth=0").SetName("children_depth0");
        yield return new TestCaseData($"/category/{LibRoot}/children?depth=1").SetName("children_of_library_root");
        yield return new TestCaseData($"/category/{LibFolder}/children?depth=0&children={LibSub},{LibItem}").SetName("subset");
        yield return new TestCaseData($"/category/{LibFolder}/categories?depth=1").SetName("categories");
        yield return new TestCaseData($"/category/{LibFolder}/links").SetName("links");
        yield return new TestCaseData($"/item/{LibItem}").SetName("item");
        yield return new TestCaseData($"/item/{LibLink}").SetName("link_item");
    }

    [TestCaseSource(nameof(LibraryReadRoutes))]
    public void Library_read_through_the_inventory_cap_is_byte_identical_to_the_library_cap(string path)
    {
        var w = Build();
        var viaLibrary = Send(w.LibraryHandler, "GET", path);
        var viaInventory = Send(w.InventoryWithLibrary, "GET", path);

        Assert.That(viaLibrary.Status, Is.EqualTo((int)HttpStatusCode.OK), "the library cap itself answers this route");
        Assert.That(viaInventory.Status, Is.EqualTo((int)HttpStatusCode.OK));
        Assert.That(viaInventory.Raw, Is.EqualTo(viaLibrary.Raw), "exactly what LibraryAPIv3 returns");
    }

    [Test]
    public void Library_folder_children_carry_the_library_owner_and_the_folders_content()
    {
        var w = Build();
        var (status, raw) = Send(w.InventoryWithLibrary, "GET", $"/category/{LibFolder}/children?depth=50");
        var body = Parse(raw);

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
        Assert.That(body["category_id"].AsUUID(), Is.EqualTo(LibFolder));
        Assert.That(body["agent_id"].AsUUID(), Is.EqualTo(LibraryOwner), "owner = the Library owner, not the agent");
        var embedded = (OSDMap)body["_embedded"];
        Assert.That(((OSDMap)embedded["categories"]).ContainsKey(LibSub.ToString()), Is.True);
        Assert.That(((OSDMap)embedded["items"]).ContainsKey(LibItem.ToString()), Is.True);
        Assert.That(((OSDMap)embedded["links"]).ContainsKey(LibLink.ToString()), Is.True);
    }

    [Test]
    public void Without_a_library_the_same_request_is_404_as_before()
    {
        var w = Build();
        var (status, _) = Send(w.InventoryWithoutLibrary, "GET", $"/category/{LibFolder}/children?depth=50");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
    }

    [Test]
    public void The_agents_own_folder_is_still_served_as_the_agents()
    {
        var w = Build();
        var withLib = Send(w.InventoryWithLibrary, "GET", $"/category/{AliceRoot}/children?depth=1");
        var without = Send(w.InventoryWithoutLibrary, "GET", $"/category/{AliceRoot}/children?depth=1");

        Assert.That(withLib.Status, Is.EqualTo((int)HttpStatusCode.OK));
        Assert.That(withLib.Raw, Is.EqualTo(without.Raw));
        Assert.That(Parse(withLib.Raw)["agent_id"].AsUUID(), Is.EqualTo(Alice));
    }

    [Test]
    public void The_current_outfit_alias_is_still_the_agents()
    {
        var w = Build();
        var withLib = Send(w.InventoryWithLibrary, "GET", "/category/current/links");
        var without = Send(w.InventoryWithoutLibrary, "GET", "/category/current/links");

        Assert.That(withLib.Status, Is.EqualTo((int)HttpStatusCode.OK));
        Assert.That(withLib.Raw, Is.EqualTo(without.Raw));
    }

    [Test]
    public void An_id_that_is_in_both_stores_answers_as_the_agents()
    {
        var w = Build();
        // an agent folder that reuses a Library folder's id: the agent's inventory wins
        w.Inventory.Seed(LibFolder, Alice, AliceRoot, "My own", -1, 9);
        var (status, raw) = Send(w.InventoryWithLibrary, "GET", $"/category/{LibFolder}/children?depth=0");

        Assert.That(status, Is.EqualTo((int)HttpStatusCode.OK));
        Assert.That(Parse(raw)["agent_id"].AsUUID(), Is.EqualTo(Alice));
        Assert.That(Parse(raw)["name"].AsString(), Is.EqualTo("My own"));
    }

    // ------------------------------------------------------------------ mutations on a Library id

    public static IEnumerable<TestCaseData> LibraryMutations()
    {
        yield return new TestCaseData("PATCH", $"/item/{LibItem}", new OSDMap { ["name"] = "renamed" }).SetName("patch_item");
        yield return new TestCaseData("PATCH", $"/category/{LibFolder}", new OSDMap { ["name"] = "renamed" }).SetName("patch_category");
        yield return new TestCaseData("DELETE", $"/item/{LibItem}", null).SetName("delete_item");
        yield return new TestCaseData("DELETE", $"/category/{LibFolder}", null).SetName("delete_category");
        yield return new TestCaseData("DELETE", $"/category/{LibFolder}/children", null).SetName("purge_children");
        yield return new TestCaseData("POST", $"/category/{LibFolder}?tid={UUID.Random()}",
            new OSDMap { ["categories"] = new OSDArray { new OSDMap { ["name"] = "new", ["type_default"] = -1 } } }).SetName("create_under_library_folder");
        yield return new TestCaseData("PUT", $"/category/{LibFolder}/links?tid={UUID.Random()}", new OSDArray()).SetName("slam_links");
    }

    [TestCaseSource(nameof(LibraryMutations))]
    public void A_mutation_on_a_library_id_through_the_inventory_cap_is_answered_as_the_library_cap_answers_it_and_changes_nothing(
        string verb, string path, OSD body)
    {
        var w = Build();
        var folderCount = w.Inventory.Folders.Count;
        var itemCount = w.Inventory.Items.Count;
        var libFolders = w.Library.Folders.Count;
        var libItems = w.Library.Items.Count;
        var libItemName = w.Library.Items[LibItem].Name;
        var libFolderName = w.Library.Folders[LibFolder].Name;

        var viaLibrary = Send(w.LibraryHandler, verb, path, body);
        var viaInventory = Send(w.InventoryWithLibrary, verb, path, body);

        Assert.That(viaLibrary.Status, Is.EqualTo((int)HttpStatusCode.MethodNotAllowed), "the library cap refuses it");
        Assert.That(viaInventory.Status, Is.EqualTo(viaLibrary.Status));
        Assert.That(viaInventory.Raw, Is.EqualTo(viaLibrary.Raw), "the same body, too");
        Assert.That(w.Library.Writes, Is.Empty, "no write reached the library");
        Assert.That(w.Inventory.Folders.Count, Is.EqualTo(folderCount));
        Assert.That(w.Inventory.Items.Count, Is.EqualTo(itemCount));
        Assert.That(w.Library.Folders.Count, Is.EqualTo(libFolders));
        Assert.That(w.Library.Items.Count, Is.EqualTo(libItems));
        Assert.That(w.Library.Items[LibItem].Name, Is.EqualTo(libItemName));
        Assert.That(w.Library.Folders[LibFolder].Name, Is.EqualTo(libFolderName));
    }

    // ------------------------------------------------------------------ the fallthrough cannot reach anyone else

    public static IEnumerable<TestCaseData> ForeignAndUnknownRoutes()
    {
        foreach (var id in new[] { BobFolder, BobRoot, Unknown })
        {
            yield return new TestCaseData("GET", $"/category/{id}/children?depth=50", null).SetName($"children_{id.ToString()[..2]}");
            yield return new TestCaseData("GET", $"/category/{id}/categories?depth=1", null).SetName($"categories_{id.ToString()[..2]}");
            yield return new TestCaseData("GET", $"/category/{id}/links", null).SetName($"links_{id.ToString()[..2]}");
            yield return new TestCaseData("GET", $"/category/{id}/children?depth=0&children={BobShirt}", null).SetName($"subset_{id.ToString()[..2]}");
            yield return new TestCaseData("PATCH", $"/category/{id}", new OSDMap { ["name"] = "stolen" }).SetName($"patch_category_{id.ToString()[..2]}");
            yield return new TestCaseData("DELETE", $"/category/{id}", null).SetName($"delete_category_{id.ToString()[..2]}");
            yield return new TestCaseData("DELETE", $"/category/{id}/children", null).SetName($"purge_{id.ToString()[..2]}");
        }
        foreach (var id in new[] { BobShirt, Unknown })
        {
            yield return new TestCaseData("GET", $"/item/{id}", null).SetName($"item_{id.ToString()[..2]}");
            yield return new TestCaseData("PATCH", $"/item/{id}", new OSDMap { ["name"] = "stolen" }).SetName($"patch_item_{id.ToString()[..2]}");
            yield return new TestCaseData("DELETE", $"/item/{id}", null).SetName($"delete_item_{id.ToString()[..2]}");
        }
    }

    [TestCaseSource(nameof(ForeignAndUnknownRoutes))]
    public void An_id_that_is_neither_the_agents_nor_the_librarys_answers_exactly_as_without_the_library(
        string verb, string path, OSD body)
    {
        var w = Build();
        var bobFolderName = w.Inventory.Folders[BobFolder].Name;
        var bobItemName = w.Inventory.Items[BobShirt].Name;
        var counts = (w.Inventory.Folders.Count, w.Inventory.Items.Count);

        var without = Send(w.InventoryWithoutLibrary, verb, path, body);
        var withLib = Send(w.InventoryWithLibrary, verb, path, body);

        Assert.That(without.Status, Is.EqualTo((int)HttpStatusCode.NotFound), "today's answer for a foreign or unknown id");
        Assert.That(withLib.Status, Is.EqualTo(without.Status));
        Assert.That(withLib.Raw, Is.EqualTo(without.Raw));
        Assert.That(w.Inventory.Folders[BobFolder].Name, Is.EqualTo(bobFolderName));
        Assert.That(w.Inventory.Items[BobShirt].Name, Is.EqualTo(bobItemName));
        Assert.That((w.Inventory.Folders.Count, w.Inventory.Items.Count), Is.EqualTo(counts));
        Assert.That(w.Library.Writes, Is.Empty);
    }

    [Test]
    public void Another_residents_folder_is_not_served_even_when_the_library_is_asked_about_it()
    {
        var w = Build();
        var (status, raw) = Send(w.InventoryWithLibrary, "GET", $"/category/{BobFolder}/children?depth=50");
        Assert.That(status, Is.EqualTo((int)HttpStatusCode.NotFound));
        Assert.That(Parse(raw).ContainsKey("_embedded"), Is.False);
        Assert.That(Parse(raw).ContainsKey("category_id"), Is.False);
    }
}
