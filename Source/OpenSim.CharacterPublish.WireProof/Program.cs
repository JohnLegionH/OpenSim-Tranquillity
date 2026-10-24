/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMetaverse;
using OpenSim.CharacterPublish;
using OpenSim.CharacterPublish.Objects;
using OpenSim.Framework;
using OpenSim.Services.Connectors;
using OpenSim.Services.Interfaces;

// Checks a running Robust that has the character publish endpoint loaded.
//   --public <url>      the endpoint's base address (no trailing slash), for example http://host:port
//   --private <url>     the base address of the inventory, asset and user account connectors
//   --key-file <file>   the endpoint's service key
//   --name1, --name2    "First Last" of the two test accounts; --pw1-file, --pw2-file hold their passwords
//   --description <file>   the character description to assemble and publish
//   --creator <uuid>    the CreatorAccount the endpoint is configured with
//   --report <file>     where the Markdown record goes
// The two accounts must exist, with their default inventories. Both are given a worn look, the first is published to, and the
// second must be unchanged at the end.
return await WireProof.Run(args);

static class WireProof
{
    static readonly List<(string Part, string Step, bool Ok, string Detail)> Results = new();
    static string Part = "";

    static void Check(string step, bool ok, string detail)
    {
        Results.Add((Part, step, ok, detail));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  [{Part}] {step}: {detail}");
    }

    static string Arg(string[] a, string name) =>
        Array.IndexOf(a, name) is var i && i >= 0 && i + 1 < a.Length ? a[i + 1] : throw new ArgumentException("missing " + name);

    sealed record Answer(int Status, JsonElement Json, string Raw)
    {
        public string Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
        public string Message => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("message", out var c) ? c.GetString() ?? "" : "";
        public override string ToString() => $"HTTP {Status} {Code}" + (Message.Length > 0 ? $" ({Message})" : "") + (Status < 0 ? " " + Raw : "");
    }

    sealed class Endpoint
    {
        readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };
        readonly string _base, _key;
        public Endpoint(string baseUrl, string key) { _base = baseUrl.TrimEnd('/'); _key = key; }

        /// <param name="key">null sends no key; "" sends the configured one.</param>
        public async Task<Answer> Call(string method, string route, byte[]? body = null, string? key = "", bool https = true,
            string? resident = null, string? token = null)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), _base + "/characterpublish/" + route);
            var useKey = key == "" ? _key : key;
            if (useKey != null) req.Headers.Add("X-Publish-Key", useKey);
            if (https) req.Headers.Add("X-Forwarded-Proto", "https");
            if (resident != null) req.Headers.Add("X-Publish-Resident", resident);
            if (token != null) req.Headers.Add("X-Publish-Token", token);
            if (body != null) { req.Content = new ByteArrayContent(body); req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); }
            try
            {
                using var res = await _http.SendAsync(req);
                var raw = await res.Content.ReadAsStringAsync();
                JsonElement json;
                try { json = JsonDocument.Parse(raw).RootElement.Clone(); } catch (JsonException) { json = default; }
                return new Answer((int)res.StatusCode, json, raw);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                return new Answer(-1, default, e.GetType().Name + ": " + (e.InnerException?.Message ?? e.Message));
            }
        }

        public Task<Answer> Login(string first, string last, string password) =>
            Call("POST", "login", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { firstName = first, lastName = last, password })));
    }

    static byte[] Zip(Bundle bundle, IReadOnlyDictionary<UUID, byte[]> content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] data) { using var s = z.CreateEntry(name, CompressionLevel.Fastest).Open(); s.Write(data); }
            Add(BundleFiles.ManifestName, BundleFiles.ToJson(bundle));
            foreach (var a in bundle.Assets) Add(a.File, content[a.Id]);
        }
        return ms.ToArray();
    }

    // ---- a resident as the inventory connector sees one ----------------------------------------------------------

    sealed record Worn(List<InventoryItemBase> Targets, UUID Root, UUID Cof, List<InventoryItemBase> CofEntries);

    static InventoryItemBase Item(UUID owner, UUID folder, string name, AssetType t, InventoryType it, uint flags) => new(UUID.Random(), owner)
    {
        Folder = folder, Name = name, Description = "worn", AssetID = UUID.Random(), AssetType = (int)t, InvType = (int)it, Flags = flags,
        BasePermissions = 0x7FFFFFFF, CurrentPermissions = 0x7FFFFFFF, NextPermissions = 0x82000, CreatorId = owner.ToString(), CreationDate = 1_700_000_000,
    };

    static Worn GiveWornLook(IInventoryService inv, UUID id)
    {
        var root = inv.GetRootFolder(id);
        var cof = inv.GetFolderForType(id, FolderType.CurrentOutfit);
        var body = inv.GetFolderForType(id, FolderType.BodyPart);
        var objects = inv.GetFolderForType(id, FolderType.Object);
        var targets = new List<InventoryItemBase>
        {
            Item(id, body.ID, "Worn Shape", AssetType.Bodypart, InventoryType.Wearable, 0),
            Item(id, body.ID, "Worn Skin", AssetType.Bodypart, InventoryType.Wearable, 1),
            Item(id, body.ID, "Worn Eyes", AssetType.Bodypart, InventoryType.Wearable, 3),
            Item(id, objects.ID, "Worn Attachment", AssetType.Object, InventoryType.Attachment, 0),
        };
        foreach (var t in targets) if (!inv.AddItem(t)) throw new InvalidOperationException("could not add " + t.Name);
        foreach (var t in targets)
        {
            var link = new InventoryItemBase(UUID.Random(), id)
            {
                Folder = cof.ID, Name = t.Name, Description = "@" + (t.Flags * 100), AssetID = t.ID, AssetType = (int)AssetType.Link, InvType = t.InvType,
                Flags = t.Flags, BasePermissions = 0x7FFFFFFF, CurrentPermissions = 0x7FFFFFFF, NextPermissions = 0x7FFFFFFF, CreatorId = id.ToString(), CreationDate = 1_700_000_000,
            };
            if (!inv.AddItem(link)) throw new InvalidOperationException("could not link " + t.Name);
        }
        return new Worn(targets, root.ID, cof.ID, inv.GetFolderContent(id, cof.ID).Items);
    }

    static Dictionary<string, string> Snapshot(IInventoryService inv, UUID owner)
    {
        var all = new Dictionary<string, string>();
        foreach (var f in inv.GetInventorySkeleton(owner) ?? new List<InventoryFolderBase>())
        {
            all["F:" + f.ID] = $"{f.Name}|{f.Type}|{f.ParentID}|{f.Owner}|v{f.Version}";
            foreach (var i in inv.GetFolderContent(owner, f.ID).Items)
                all["I:" + i.ID] = $"{i.Name}|{i.Description}|{i.AssetID}|{i.AssetType}|{i.InvType}|{i.Folder}|{i.Owner}|{i.CreatorIdentification}|{i.Flags}|{i.BasePermissions:x}|{i.CurrentPermissions:x}|{i.NextPermissions:x}|{i.GroupPermissions:x}|{i.EveryOnePermissions:x}|{i.SalePrice}|{i.SaleType}|{i.CreationDate}";
        }
        return all;
    }

    /// <summary>Rows that were there before and are gone or different now; the folders named may differ in version only (a child was added).</summary>
    static List<string> Differences(Dictionary<string, string> before, Dictionary<string, string> after, params UUID[] parentsMayGainVersion)
    {
        var bump = parentsMayGainVersion.Select(p => "F:" + p).ToHashSet();
        static string NoVer(string r) => r[..r.LastIndexOf("|v", StringComparison.Ordinal)];
        var diffs = new List<string>();
        foreach (var (k, row) in before)
        {
            if (!after.TryGetValue(k, out var now)) diffs.Add(k + " is gone");
            else if (bump.Contains(k) ? NoVer(row) != NoVer(now) : row != now) diffs.Add(k + " changed");
        }
        return diffs;
    }

    // ---- the run -------------------------------------------------------------------------------------------------

    public static async Task<int> Run(string[] a)
    {
        var started = DateTime.UtcNow;
        var key = File.ReadAllText(Arg(a, "--key-file")).Trim();
        var pw1 = File.ReadAllText(Arg(a, "--pw1-file")).Trim(); var pw2 = File.ReadAllText(Arg(a, "--pw2-file")).Trim();
        var n1 = Arg(a, "--name1").Split(' '); var n2 = Arg(a, "--name2").Split(' ');
        var creator = UUID.Parse(Arg(a, "--creator"));
        var priv = Arg(a, "--private");
        var ep = new Endpoint(Arg(a, "--public"), key);

        var accounts = new UserAccountServicesConnector(priv);
        var inv = new XInventoryServicesConnector(priv);
        var assets = new AssetServicesConnector(priv);

        // ---- setup
        Part = "A";
        var u1 = accounts.GetUserAccount(UUID.Zero, n1[0], n1[1]); var u2 = accounts.GetUserAccount(UUID.Zero, n2[0], n2[1]);
        Check("both test accounts exist", u1 != null && u2 != null, $"{n1[0]} {n1[1]} = {u1?.PrincipalID}, {n2[0]} {n2[1]} = {u2?.PrincipalID}");
        if (u1 == null || u2 == null) return Finish(started, a);
        var worn1 = GiveWornLook(inv, u1.PrincipalID); var worn2 = GiveWornLook(inv, u2.PrincipalID);
        Check("a worn look (Current Outfit links to a shape, a skin, eyes and an attachment) added to both through the inventory connector",
            worn1.Targets.All(t => worn1.CofEntries.Any(c => c.AssetID == t.ID)) && worn2.Targets.All(t => worn2.CofEntries.Any(c => c.AssetID == t.ID)),
            $"Current Outfit holds {worn1.CofEntries.Count} and {worn2.CofEntries.Count} links (4 added to each; the rest came with the model inventory)");
        var before1 = Snapshot(inv, u1.PrincipalID); var before2 = Snapshot(inv, u2.PrincipalID);

        var made = OutfitAssembler.AssembleFromFile(Arg(a, "--description"), new PublishContext(u1.PrincipalID, creator, 1, 1_790_000_000));
        Check("the Max bundle for account 1 assembles and validates", made.Validation.Ok, $"{made.Bundle.Items.Count} items, {made.Bundle.Assets.Count} assets, {made.Content.Values.Sum(c => c.Length)} bytes");
        var zip = Zip(made.Bundle, made.Content);

        // ---- B: publish over HTTP
        Part = "B";
        var login = await ep.Login(n1[0], n1[1], pw1);
        var token1 = login.Status == 200 ? login.Json.GetProperty("token").GetString()! : "";
        Check("B1 login as account 1", login.Status == 200 && Guid.TryParse(token1, out _),
            login.Status == 200 ? $"HTTP 200, principal {login.Json.GetProperty("principalId")}, token length {token1.Length}, expires in {login.Json.GetProperty("expiresInMinutes")} min" : login.ToString());
        var res = u1.PrincipalID.ToString();
        var first = await ep.Call("POST", "publish", zip, resident: res, token: token1);
        var firstOk = first.Status == 200 && !first.Json.GetProperty("alreadyPublished").GetBoolean();
        Check("B1 publish the Max bundle", firstOk, first.Status == 200
            ? $"HTTP 200, {first.Json.GetProperty("itemIds").GetArrayLength()} items, alreadyPublished=false, warnings={first.Json.GetProperty("warnings").GetArrayLength()}, outfit folder {first.Json.GetProperty("outfitFolderId")}, previous look {first.Json.GetProperty("previousLookFolderId")}"
            : first + " " + first.Raw);
        var status = await ep.Call("GET", "status", resident: res, token: token1);
        var listed = status.Status == 200 ? status.Json.GetProperty("outfits").EnumerateArray().Select(o => o.GetProperty("folderId").GetString()).ToList() : new List<string?>();
        Check("B1 status lists the outfit", status.Status == 200 && status.Json.GetProperty("count").GetInt32() == 1 && listed.Contains(made.Bundle.OutfitFolder.Id.ToString()),
            status.Status == 200 ? $"count {status.Json.GetProperty("count")} of limit {status.Json.GetProperty("limit")}, folder {string.Join(",", listed)}" : status.ToString());
        var afterFirst = Snapshot(inv, u1.PrincipalID);

        var again = await ep.Call("POST", "publish", zip, resident: res, token: token1);
        Check("B2 the same publish again is 'already published'", again.Status == 200 && again.Json.GetProperty("alreadyPublished").GetBoolean(),
            again.Status == 200 ? "HTTP 200, alreadyPublished=true" : again.ToString());
        var afterAgain = Snapshot(inv, u1.PrincipalID);
        Check("B2 the repeat changed nothing in the inventory", afterFirst.Count == afterAgain.Count && Differences(afterFirst, afterAgain).Count == 0, $"{afterAgain.Count} rows before and after");

        async Task Refusal(string name, Task<Answer> call, int status, string code)
        {
            var r = await call;
            Check(name, r.Status == status && (code == "" || r.Code == code), r.ToString());
        }
        await Refusal("B3 no service key", ep.Call("POST", "publish", zip, key: null, resident: res, token: token1), 401, "service-key-missing");
        await Refusal("B3 wrong service key", ep.Call("POST", "publish", zip, key: key + "x", resident: res, token: token1), 401, "service-key-invalid");
        await Refusal("B3 plain HTTP (no X-Forwarded-Proto)", ep.Call("POST", "publish", zip, https: false, resident: res, token: token1), 403, "https-required");
        await Refusal("B3 plain HTTP login (no X-Forwarded-Proto)", ep.Call("POST", "login", Encoding.UTF8.GetBytes("{}"), https: false), 403, "https-required");
        var login2 = await ep.Login(n2[0], n2[1], pw2);
        var token2 = login2.Status == 200 ? login2.Json.GetProperty("token").GetString()! : "";
        Check("B3 login as account 2 (to try its token on account 1)", login2.Status == 200, login2.Status == 200 ? "HTTP 200" : login2.ToString());
        await Refusal("B3 account 2's token used for account 1", ep.Call("POST", "publish", zip, resident: res, token: token2), 401, "token-invalid");
        await Refusal("B3 wrong password", ep.Login(n1[0], n1[1], pw1 + "x"), 401, "login-failed");
        await Refusal("B3 oversized bundle (64 MiB + 1 byte)", ep.Call("POST", "publish", new byte[64 * 1024 * 1024 + 1], resident: res, token: token1), 413, "");
        var afterRefusals = Snapshot(inv, u1.PrincipalID);
        Check("B3 no refusal wrote anything", afterRefusals.Count == afterFirst.Count && Differences(afterFirst, afterRefusals).Count == 0, $"{afterRefusals.Count} rows");

        var out1 = await ep.Call("POST", "logout", resident: res, token: token1);
        Check("B4 logout account 1", out1.Status == 200, out1.ToString());
        var out2 = await ep.Call("POST", "logout", resident: u2.PrincipalID.ToString(), token: token2);
        Check("B4 logout account 2", out2.Status == 200, out2.ToString());
        await Refusal("B4 the ended token no longer works", ep.Call("GET", "status", resident: res, token: token1), 401, "token-invalid");

        // ---- C: read back as a region does
        Part = "C";
        var bundle = made.Bundle; var me = u1.PrincipalID;
        var mo = inv.GetFolderContent(me, worn1.Root).Folders.Where(f => f.Type == BundleTypes.FolderMyOutfits).ToList();
        Check("C1 one My Outfits folder (type 48) under the root", mo.Count == 1, mo.Count == 1 ? $"{mo[0].Name} {mo[0].ID}" : mo.Count.ToString());
        var viaType = inv.GetFolderForType(me, (FolderType)BundleTypes.FolderMyOutfits);
        Check("C1 GetFolderForType(MyOutfits) finds the same folder", mo.Count == 1 && viaType?.ID == mo[0].ID, viaType?.ID.ToString() ?? "null");
        if (mo.Count != 1) return Finish(started, a);

        var outfit = inv.GetFolder(me, bundle.OutfitFolder.Id);
        Check("C2 outfit folder: type 47, parent My Outfits, bundle name, owner",
            outfit != null && outfit.Type == BundleTypes.FolderOutfit && outfit.ParentID == mo[0].ID && outfit.Name == bundle.OutfitFolder.Name && outfit.Owner == me,
            outfit == null ? "missing" : $"'{outfit.Name}' type {outfit.Type}");
        var contents = inv.GetFolderContent(me, bundle.OutfitFolder.Id);
        Check("C2 outfit folder holds exactly the bundle's items and no sub-folders",
            contents.Folders.Count == 0 && bundle.Items.Select(i => i.Id).Order().SequenceEqual(contents.Items.Select(i => i.ID).Order()), $"{contents.Items.Count} items");
        var itemProblems = new List<string>();
        foreach (var want in bundle.Items)
        {
            var have = contents.Items.SingleOrDefault(i => i.ID == want.Id);
            if (have == null) { itemProblems.Add(want.Name + " missing"); continue; }
            void Eq(string field, object? w, object? h) { if (!Equals(w, h)) itemProblems.Add($"{want.Name}.{field}: want {w} have {h}"); }
            Eq("owner", me, have.Owner); Eq("folder", outfit!.ID, have.Folder); Eq("name", want.Name, have.Name); Eq("description", want.Description, have.Description);
            Eq("asset", want.AssetId, have.AssetID); Eq("assetType", want.AssetType, have.AssetType); Eq("invType", want.InvType, have.InvType); Eq("flags", want.Flags, have.Flags);
            Eq("base", want.BasePermissions, have.BasePermissions); Eq("current", want.CurrentPermissions, have.CurrentPermissions); Eq("next", want.NextPermissions, have.NextPermissions);
            Eq("group", want.GroupPermissions, have.GroupPermissions); Eq("everyone", want.EveryonePermissions, have.EveryOnePermissions);
            Eq("creator", bundle.CreatorId, UUID.Parse(have.CreatorId)); Eq("created", want.CreationDate, have.CreationDate);
        }
        Check("C2 every item field equals the bundle (owner, names, assets, types, flags, five permission masks, creator, date)", itemProblems.Count == 0,
            itemProblems.Count == 0 ? $"{bundle.Items.Count} items" : string.Join("; ", itemProblems));

        var assetProblems = new List<string>();
        foreach (var asset in bundle.Assets)
        {
            var got = assets.Get(asset.Id.ToString());
            if (got == null) assetProblems.Add(asset.Name + " not found");
            else if (!made.Content[asset.Id].AsSpan().SequenceEqual(got.Data)) assetProblems.Add(asset.Name + " bytes differ");
            else if (got.Type != (sbyte)asset.AssetType) assetProblems.Add($"{asset.Name} type {got.Type}");
        }
        Check("C3 every asset fetched through the asset connector, bytes and type equal to the bundle", assetProblems.Count == 0,
            assetProblems.Count == 0 ? string.Join(", ", bundle.Assets.Select(x => $"{x.Name} {x.Length}")) : string.Join("; ", assetProblems));
        foreach (var ext in bundle.ExternalAssets)
            Check("C3 the grid texture the skin and eyes name is present", assets.Get(ext.ToString()) != null, ext.ToString());

        var look = bundle.CurrentLook!;
        var lf = inv.GetFolder(me, look.FolderId);
        Check("C4 previous-look folder: type 47 under My Outfits, named 'Previous look <date> before outfit N'",
            lf != null && lf.Type == BundleTypes.FolderOutfit && lf.ParentID == mo[0].ID && lf.Name == look.FolderName
            && Regex.IsMatch(lf.Name, @"^Previous look \d{4}-\d{2}-\d{2} before outfit \d+$"), lf?.Name ?? "missing");
        var links = lf == null ? new List<InventoryItemBase>() : inv.GetFolderContent(me, lf.ID).Items;
        var linkProblems = new List<string>();
        if (!worn1.CofEntries.Select(c => c.AssetID).Order().SequenceEqual(links.Select(l => l.AssetID).Order())) linkProblems.Add("link targets differ from the Current Outfit's");
        foreach (var l in links)
        {
            var c = worn1.CofEntries.FirstOrDefault(x => x.AssetID == l.AssetID);
            if (c == null) continue;
            if (l.AssetType != BundleTypes.AssetLink) linkProblems.Add(l.Name + " is not a link");
            if (l.Owner != me || l.InvType != c.InvType || l.Name != c.Name || l.Flags != c.Flags) linkProblems.Add(l.Name + " fields differ");
        }
        foreach (var t in worn1.Targets)
            if (!links.Any(l => l.AssetID == t.ID)) linkProblems.Add(t.Name + " not linked");
        Check("C4 the previous look holds one link per Current Outfit entry, including the four worn items (target ids, owner, type, name, flags)", linkProblems.Count == 0 && links.Count == worn1.CofEntries.Count,
            linkProblems.Count == 0 ? $"{links.Count} links" : string.Join("; ", linkProblems));

        var finalSnap = Snapshot(inv, me);
        var diffs = Differences(before1, finalSnap, worn1.Root, mo[0].ID);
        Check("C5 nothing that was in account 1's inventory before changed (Current Outfit and every older row identical)", diffs.Count == 0,
            diffs.Count == 0 ? $"{before1.Count} old rows identical" : string.Join("; ", diffs));
        var added = finalSnap.Keys.Count(k => !before1.ContainsKey(k));
        var expected = 1 + 1 + bundle.Items.Count + 1 + links.Count;
        Check("C5 only additions: My Outfits, the outfit folder, its items, the previous-look folder and its links", added == expected, $"{added} rows added, expected {expected}");

        var snap2 = Snapshot(inv, u2.PrincipalID);
        Check("C6 account 2's inventory is unchanged (every folder and item row, including folder versions)", snap2.Count == before2.Count && Differences(before2, snap2).Count == 0, $"{snap2.Count} rows identical");
        return Finish(started, a);
    }

    static int Finish(DateTime started, string[] a)
    {
        var failed = Results.Count(r => !r.Ok);
        var sb = new StringBuilder();
        sb.AppendLine($"# Wire proof, {started:u}").AppendLine().AppendLine($"{Results.Count - failed} passed, {failed} failed.").AppendLine();
        sb.AppendLine("| Part | Step | Result | Detail |").AppendLine("|---|---|---|---|");
        foreach (var r in Results) sb.AppendLine($"| {r.Part} | {r.Step} | {(r.Ok ? "PASS" : "FAIL")} | {r.Detail.Replace("|", "/").Replace("\n", " ")} |");
        File.WriteAllText(Arg(a, "--report"), sb.ToString());
        Console.WriteLine($"{Results.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
