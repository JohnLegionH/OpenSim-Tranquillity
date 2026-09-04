using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>An in-memory IAssetService: whatever was Put or Stored, keyed by id string.</summary>
public sealed class FakeAssetService : IAssetService
{
    public readonly Dictionary<string, AssetBase> Assets = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<AssetBase> Stored = new();

    public void Put(AssetBase asset) => Assets[asset.ID] = asset;
    public bool Remove(string id) => Assets.Remove(id);

    public AssetBase Get(string id) => Assets.TryGetValue(id, out var a) ? a : null;
    public AssetBase Get(string id, string foreignAssetService, bool storeOnLocalGrid) => Get(id);
    public AssetMetadata GetMetadata(string id) => Get(id)?.Metadata;
    public byte[] GetData(string id) => Get(id)?.Data;
    public AssetBase GetCached(string id) => Get(id);
    public bool Get(string id, object sender, AssetRetrieved handler) { handler(id, sender, Get(id)); return true; }
    public void Get(string id, string foreignAssetService, bool storeOnLocalGrid, SimpleAssetRetrieved callBack) => callBack(Get(id));
    public bool[] AssetsExist(string[] ids) => ids.Select(i => Assets.ContainsKey(i)).ToArray();
    public string Store(AssetBase asset) { Assets[asset.ID] = asset; Stored.Add(asset); return asset.ID; }
    public bool UpdateContent(string id, byte[] data) { if (!Assets.TryGetValue(id, out var a)) return false; a.Data = data; return true; }
    public bool Delete(string id) => Assets.Remove(id);
}
