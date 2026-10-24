/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using Microsoft.Extensions.Logging;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.AssetService;
using OpenSim.Services.AuthenticationService;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using OpenSim.Services.InventoryService;

namespace OpenSim.CharacterPublish.Services.Tests;

[CollectionDefinition(Name)]
public sealed class ScratchGridCollection : ICollectionFixture<ScratchGrid>
{
    // Serial on purpose: the SQLite data plugin keeps one static connection per table type for the whole process, so there is
    // one inventory database per test run and every test shares it. Tests keep apart by using a resident of their own.
    public const string Name = "scratch grid";
}

/// <summary>
/// The real XInventoryService and AssetService over SQLite scratch databases. The folder comes from PUB_SCRATCH_DB, or the
/// test output folder; each run makes its own sub-folder and nothing is ever deleted. No other database is opened.
/// </summary>
public sealed class ScratchGrid
{
    public string Folder { get; }
    public IConfigSource Config { get; }

    /// <summary>Unwrapped services for seeding a resident and for reading back; they record nothing.</summary>
    public XInventoryService Plain { get; }
    public AssetService PlainAssets { get; }
    public UserAccountService Accounts { get; }
    public PasswordAuthenticationService Auth { get; }

    /// <summary>An account with a password, as the grid stores them (the password is hashed and salted by the authentication service).</summary>
    public void AddAccount(UUID id, string first, string last, string password, int userLevel = 0, int userFlags = 0)
    {
        Assert.True(Accounts.StoreUserAccount(new UserAccount(UUID.Zero, id, first, last, "test@example.org") { UserLevel = userLevel, UserFlags = userFlags }));
        Assert.True(Auth.SetPassword(id, password));
    }

    public ScratchGrid()
    {
        var baseDir = Environment.GetEnvironmentVariable("PUB_SCRATCH_DB");
        if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine(AppContext.BaseDirectory, "scratch-db");
        Folder = Path.Combine(baseDir, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Folder);

        var ini = new IniConfigSource();
        var inv = ini.AddConfig("InventoryService");
        inv.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        // Durability is worth nothing on a scratch database, and a synchronous commit per row makes a test take seconds.
        const string Fast = ";Version=3;Synchronous=Off;Journal Mode=Memory";
        inv.Set("ConnectionString", "Data Source=" + Path.Combine(Folder, "inventory.db").Replace('\\', '/') + Fast);
        var ast = ini.AddConfig("AssetService");
        ast.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        ast.Set("ConnectionString", "Data Source=" + Path.Combine(Folder, "assets.db").Replace('\\', '/') + Fast);
        // the grid's own authentication and account services, for the publish endpoint's login
        var authc = ini.AddConfig("AuthenticationService");
        authc.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        authc.Set("ConnectionString", "Data Source=" + Path.Combine(Folder, "auth.db").Replace('\\', '/') + Fast);
        var userc = ini.AddConfig("UserAccountService");
        userc.Set("StorageProvider", "OpenSim.Data.SQLite.dll");
        userc.Set("ConnectionString", "Data Source=" + Path.Combine(Folder, "useraccounts.db").Replace('\\', '/') + Fast);
        Config = ini;
        LoggerProvider.LoggerFactory = LogCapture.Factory; // every log line of every service in this process is kept, so a test can look for a secret in them

        Plain = new XInventoryService(Config);
        PlainAssets = new AssetService(Config);
        Accounts = new UserAccountService(Config);
        // The SQLite plugin's UserAccounts migrations stop before the DisplayName and NameChanged columns the account service reads and
        // writes (the MySQL ones have them), so the service cannot store an account in a fresh SQLite database. The scratch database gets
        // the two columns the MySQL migration adds; nothing but this scratch file is touched.
        using (var db = new System.Data.SQLite.SQLiteConnection("Data Source=" + Path.Combine(Folder, "useraccounts.db")))
        {
            db.Open();
            foreach (var sql in new[] { "ALTER TABLE UserAccounts ADD COLUMN DisplayName VARCHAR(31) NULL", "ALTER TABLE UserAccounts ADD COLUMN NameChanged INT NULL DEFAULT 0" })
            {
                using var cmd = new System.Data.SQLite.SQLiteCommand(sql, db);
                cmd.ExecuteNonQuery();
            }
        }
        Auth = new PasswordAuthenticationService(Config, Accounts);

        // The textures the fixture wearables name are on a real grid, and a missing texture is a refusal, so the scratch grid holds them.
        // 5748decc-... is the grid's blank texture, which the Max bundle names in every slot
        foreach (var id in new[] { BundleFixtures.SkinHead, BundleFixtures.SkinUpper, BundleFixtures.SkinLower, BundleFixtures.Iris, new UUID("5748decc-f629-461c-9a36-a35a221fe21f") })
            AddTexture(id);
    }

    /// <summary>Stores a texture asset under <paramref name="id"/> (the bytes do not matter to the publish service).</summary>
    public void AddTexture(UUID id)
    {
        var a = new AssetBase(id, "texture " + id, (sbyte)AssetType.Texture, BundleFixtures.Creator.ToString()) { Data = new byte[] { 1, 2, 3, 4 } };
        Assert.False(string.IsNullOrEmpty(PlainAssets.Store(a)));
    }

    public RecordingInventory NewInventory() => new(Config);
    public RecordingAssets NewAssets() => new(Config);

    // ---- a resident with a default inventory and a worn look ------------------------------------------------------

    public sealed record Resident(UUID Id, UUID Root, UUID Cof, List<InventoryItemBase> Targets, List<InventoryItemBase> Links);

    /// <summary>
    /// A resident with the inventory the service makes (no My Outfits: the service never makes one) and a Current Outfit holding
    /// links to a shape, a skin, eyes and one attachment, as the viewer leaves it.
    /// </summary>
    public Resident NewResident(bool wornLook = true, UUID? withId = null)
    {
        var id = withId ?? UUID.Random();
        Assert.True(Plain.CreateUserInventory(id));
        var root = Plain.GetRootFolder(id);
        var cof = Plain.GetFolderForType(id, FolderType.CurrentOutfit);
        var bodyParts = Plain.GetFolderForType(id, FolderType.BodyPart);
        var objects = Plain.GetFolderForType(id, FolderType.Object);
        var targets = new List<InventoryItemBase>();
        var links = new List<InventoryItemBase>();
        if (wornLook)
        {
            targets.Add(Item(id, bodyParts.ID, "Worn Shape", AssetType.Bodypart, InventoryType.Wearable, 0));
            targets.Add(Item(id, bodyParts.ID, "Worn Skin", AssetType.Bodypart, InventoryType.Wearable, 1));
            targets.Add(Item(id, bodyParts.ID, "Worn Eyes", AssetType.Bodypart, InventoryType.Wearable, 3));
            targets.Add(Item(id, objects.ID, "Worn Attachment", AssetType.Object, InventoryType.Attachment, 0));
            foreach (var t in targets) Assert.True(Plain.AddItem(t));
            for (var i = 0; i < targets.Count; i++)
            {
                var link = Link(id, cof.ID, targets[i], "@" + (targets[i].Flags * 100));
                Assert.True(Plain.AddItem(link));
                links.Add(link);
            }
        }
        return new Resident(id, root.ID, cof.ID, targets, links);
    }

    public static InventoryItemBase Item(UUID owner, UUID folder, string name, AssetType assetType, InventoryType invType, uint flags) => new(UUID.Random(), owner)
    {
        Folder = folder, Name = name, Description = "worn", AssetID = UUID.Random(), AssetType = (int)assetType, InvType = (int)invType, Flags = flags,
        BasePermissions = 0x7FFFFFFF, CurrentPermissions = 0x7FFFFFFF, NextPermissions = 0x82000, EveryOnePermissions = 0, GroupPermissions = 0,
        CreatorId = owner.ToString(), CreationDate = 1_700_000_000, SalePrice = 0, SaleType = 0,
    };

    public static InventoryItemBase Link(UUID owner, UUID folder, InventoryItemBase target, string description) => new(UUID.Random(), owner)
    {
        Folder = folder, Name = target.Name, Description = description, AssetID = target.ID, AssetType = (int)AssetType.Link, InvType = target.InvType,
        Flags = target.Flags, BasePermissions = 0x7FFFFFFF, CurrentPermissions = 0x7FFFFFFF, NextPermissions = 0x7FFFFFFF, EveryOnePermissions = 0,
        GroupPermissions = 0, CreatorId = owner.ToString(), CreationDate = 1_700_000_000, SalePrice = 0, SaleType = 0,
    };

    public InventoryFolderBase AddFolder(UUID owner, UUID parent, string name, FolderType type)
    {
        var f = new InventoryFolderBase(UUID.Random(), name, owner, parent) { Type = (short)type, Version = 1 };
        Assert.True(Plain.AddFolder(f));
        return f;
    }

    // ---- reading everything back ----------------------------------------------------------------------------------

    /// <summary>Every folder and item the resident has, as comparable text. Folders carry their version; items carry every field.</summary>
    public Dictionary<string, string> Snapshot(UUID owner)
    {
        var all = new Dictionary<string, string>();
        foreach (var f in Plain.GetInventorySkeleton(owner) ?? new List<InventoryFolderBase>())
        {
            all["F:" + f.ID] = $"{f.Name}|{f.Type}|{f.ParentID}|{f.Owner}|v{f.Version}";
            foreach (var i in Plain.GetFolderContent(owner, f.ID).Items) all["I:" + i.ID] = Dump(i);
        }
        return all;
    }

    public static string Dump(InventoryItemBase i) =>
        $"{i.Name}|{i.Description}|{i.AssetID}|{i.AssetType}|{i.InvType}|{i.Folder}|{i.Owner}|{i.CreatorIdentification}|{i.Flags}|{i.BasePermissions:x}|{i.CurrentPermissions:x}|{i.NextPermissions:x}|{i.GroupPermissions:x}|{i.EveryOnePermissions:x}|{i.SalePrice}|{i.SaleType}|{i.CreationDate}|{i.GroupID}|{i.GroupOwned}";

    static string WithoutVersion(string folderRow) => folderRow[..folderRow.LastIndexOf("|v", StringComparison.Ordinal)];

    /// <summary>
    /// Everything that was there is still there, row for row. Only the folders in <paramref name="parents"/> (the ones a new child
    /// was added to) may differ, and only in their version: the data layer bumps a folder's version when a child is stored in it.
    /// Returns the keys that were added.
    /// </summary>
    public static List<string> AssertOnlyAdditions(Dictionary<string, string> before, Dictionary<string, string> after, params UUID[] parents)
    {
        var bump = parents.Select(p => "F:" + p).ToHashSet();
        foreach (var (key, row) in before)
        {
            Assert.True(after.TryGetValue(key, out var now), $"{key} ({row}) is gone");
            if (bump.Contains(key))
            {
                Assert.Equal(WithoutVersion(row), WithoutVersion(now!));
                Assert.True(ushort.Parse(now![(now.LastIndexOf("|v", StringComparison.Ordinal) + 2)..]) >= ushort.Parse(row[(row.LastIndexOf("|v", StringComparison.Ordinal) + 2)..]));
            }
            else Assert.Equal(row, now);
        }
        return after.Keys.Where(k => !before.ContainsKey(k)).ToList();
    }

    /// <summary>Writes a folder straight to the data layer, past the service's own checks (to make a data fault an older bug left).</summary>
    public void ForceFolder(InventoryFolderBase folder) => Assert.True(new RawInventory(Config).StoreRaw(folder));

    sealed class RawInventory : XInventoryService
    {
        public RawInventory(IConfigSource config) : base(config) { }
        public bool StoreRaw(InventoryFolderBase folder) => m_Database.StoreFolder(ConvertFromOpenSim(folder));
    }

    public InventoryFolderBase? MyOutfitsOf(UUID owner) =>
        Plain.GetFolderContent(owner, Plain.GetRootFolder(owner).ID).Folders.SingleOrDefault(f => f.Type == BundleTypes.FolderMyOutfits);
}

/// <summary>
/// The real inventory service, recording every write call and able to fail one on demand. The data layer underneath is the
/// same SQLite database the unwrapped service uses.
/// </summary>
public sealed class RecordingInventory : XInventoryService
{
    public RecordingInventory(IConfigSource config) : base(config) { }

    public List<string> Calls { get; } = new();
    /// <summary>Calls the publish service must never make: anything that changes or removes a row that is already there.</summary>
    public List<string> Forbidden { get; } = new();
    public List<InventoryItemBase> ItemsAdded { get; } = new();
    public List<InventoryFolderBase> FoldersAdded { get; } = new();

    /// <summary>Fail the AddItem calls for which this returns true (by 1-based call number); the call is not forwarded.</summary>
    public Func<int, InventoryItemBase, bool>? FailAddItem { get; set; }
    public bool FailByException { get; set; }
    public Func<int, InventoryFolderBase, bool>? FailAddFolder { get; set; }
    int _itemCalls, _folderCalls;

    public override bool AddItem(InventoryItemBase item)
    {
        Calls.Add("AddItem " + item.Name);
        if (FailAddItem?.Invoke(++_itemCalls, item) == true)
        {
            if (FailByException) throw new InvalidOperationException("injected: the database went away");
            return false;
        }
        ItemsAdded.Add(item);
        return base.AddItem(item);
    }

    public override bool AddFolder(InventoryFolderBase folder)
    {
        Calls.Add("AddFolder " + folder.Name);
        if (FailAddFolder?.Invoke(++_folderCalls, folder) == true) return false;
        FoldersAdded.Add(folder);
        return base.AddFolder(folder);
    }

    bool Forbid(string what) { Forbidden.Add(what); Calls.Add(what); return false; }
    public override bool UpdateItem(InventoryItemBase item) => Forbid("UpdateItem " + item.Name);
    public override bool UpdateFolder(InventoryFolderBase folder) => Forbid("UpdateFolder " + folder.Name);
    public override bool MoveFolder(InventoryFolderBase folder) => Forbid("MoveFolder " + folder.Name);
    public override bool MoveItems(UUID principalID, List<InventoryItemBase> items) => Forbid("MoveItems");
    public override bool DeleteItems(UUID principalID, List<UUID> itemIDs) => Forbid("DeleteItems");
    public override bool DeleteFolders(UUID principalID, List<UUID> folderIDs) => Forbid("DeleteFolders");
    public override bool DeleteFolders(UUID principalID, List<UUID> folderIDs, bool onlyIfTrash) => Forbid("DeleteFolders");
    public override bool PurgeFolder(InventoryFolderBase folder) => Forbid("PurgeFolder");
    public override bool PurgeFolder(InventoryFolderBase folder, bool onlyIfTrash) => Forbid("PurgeFolder");
    public override bool CreateUserInventory(UUID principalID) => Forbid("CreateUserInventory") ;
}

public sealed class RecordingAssets : AssetService
{
    public RecordingAssets(IConfigSource config) : base(config) { }

    public List<UUID> Stored { get; } = new();
    public List<string> Forbidden { get; } = new();
    public Func<int, AssetBase, bool>? FailStore { get; set; }
    int _calls;

    public override string Store(AssetBase asset)
    {
        if (FailStore?.Invoke(++_calls, asset) == true) return UUID.Zero.ToString();
        Stored.Add(asset.FullID);
        return base.Store(asset);
    }

    public override bool Delete(string id) { Forbidden.Add("Delete " + id); return false; }
}

/// <summary>Keeps every line any logger writes, formatted, so tests can prove what never reaches a log.</summary>
public static class LogCapture
{
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
    public static readonly ILoggerFactory Factory = new CaptureFactory();

    sealed class CaptureFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    public sealed class CaptureLogger : ILogger
    {
        readonly string _category;
        public CaptureLogger(string category) => _category = category;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue($"{logLevel} {_category}: {formatter(state, exception)}{(exception == null ? "" : " | " + exception)}");
    }
}
