/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

public enum ArchiveKind { Unknown, Oar, Iar }

public enum InspectionStatus
{
    /// <summary>The whole file was read.</summary>
    Complete,
    /// <summary>Reading stopped early or an entry could not be read; the report holds what was read.</summary>
    Partial,
    /// <summary>The file is not a gzip or plain tar archive of the OAR/IAR family, or could not be opened.</summary>
    NotAnArchive
}

public enum RecordContext
{
    /// <summary>Found directly in the archive (an OAR object file or an IAR item file).</summary>
    Archive,
    /// <summary>Found inside an object asset (a boxed object, coalesced objects included).</summary>
    Boxed
}

public enum ItemSource { PrimInventory, InventoryArchive }

public enum ReferenceKind
{
    FaceTexture, Sculpt, ProjectionTexture, ParticleTexture, Sound, CollisionSound, Animation,
    Material, MaterialOverride, TerrainTexture, TerrainPbr, ParcelMedia, InventoryItem, ScriptText, NotecardText, WearableTexture, GestureAsset, MaterialTexture, EmbeddedText
}

public enum Certainty
{
    /// <summary>Named by a field whose job is to name an asset.</summary>
    Certain,
    /// <summary>A UUID found in script, notecard or similar text; it may not be an asset.</summary>
    Uncertain
}

/// <summary>Base of every record handed to checks, in file order.</summary>
public abstract class ArchiveRecord
{
    public string Entry { get; set; }
    public RecordContext Context { get; set; }
    /// <summary>Object asset this record was read from when Context is Boxed.</summary>
    public UUID? BoxedAssetId { get; set; }
}

/// <summary>Creator named by a record, in the forms archives use.</summary>
public class CreatorRef
{
    public UUID Id { get; set; }
    /// <summary>Raw creator data text ("home;First Last" or "home/uuid;First Last"), empty when absent.</summary>
    public string Data { get; set; } = string.Empty;
    /// <summary>Home grid URL split out of the creator data.</summary>
    public string Home { get; set; } = string.Empty;
    /// <summary>Name from the creator data or from an OSPA string.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Raw text of the creator field when it was not a UUID (an OSPA string).</summary>
    public string Raw { get; set; } = string.Empty;
    public bool HasData => Data.Length > 0;
    public bool IsBlank => Id == UUID.Zero && Name.Length == 0;
    public bool IsLibrary => Id == UUID.Parse(OpenSim.Framework.Constants.m_MrOpenSimIDString);
}

public class PermissionMasks
{
    public uint? Base { get; set; }
    public uint? Owner { get; set; }
    public uint? Group { get; set; }
    public uint? Everyone { get; set; }
    public uint? NextOwner { get; set; }
}

public class LinksetRecord : ArchiveRecord
{
    public string RegionDir { get; set; } = string.Empty;
    public UUID? RegionId { get; set; }
    public UUID LinksetId { get; set; }
    public string Name { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int PrimCount { get; set; }
}

public class PartRecord : ArchiveRecord
{
    public string RegionDir { get; set; } = string.Empty;
    public UUID? RegionId { get; set; }
    public UUID LinksetId { get; set; }
    public string LinksetName { get; set; }
    public UUID PartId { get; set; }
    public string Name { get; set; }
    public bool IsRoot { get; set; }
    public int LinkNum { get; set; }
    public CreatorRef Creator { get; set; } = new CreatorRef();
    public UUID OwnerId { get; set; }
    public UUID LastOwnerId { get; set; }
    public UUID GroupId { get; set; }
    public UUID RezzerId { get; set; }
    public PermissionMasks Masks { get; set; } = new PermissionMasks();
    public int? CreationDate { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int Depth { get; set; }
}

/// <summary>A prim inventory item (in an OAR part or in a boxed object) or an IAR item.</summary>
public class ItemRecord : ArchiveRecord
{
    public ItemSource Source { get; set; }
    public UUID ItemId { get; set; }
    public string Name { get; set; }
    public UUID AssetId { get; set; }
    public int? AssetType { get; set; }
    public int? InvType { get; set; }
    public CreatorRef Creator { get; set; } = new CreatorRef();
    public UUID OwnerId { get; set; }
    /// <summary>Only prim inventory items carry a last owner; IAR items leave it zero.</summary>
    public UUID LastOwnerId { get; set; }
    public bool HasLastOwner { get; set; }
    public UUID GroupId { get; set; }
    public bool GroupOwned { get; set; }
    public PermissionMasks Masks { get; set; } = new PermissionMasks();
    public int? CreationDate { get; set; }
    public UUID FolderId { get; set; }
    public string FolderPath { get; set; }
    public string RegionDir { get; set; } = string.Empty;
    public UUID PartId { get; set; }
    public UUID LinksetId { get; set; }
    public string PartName { get; set; }
    public int Depth { get; set; }
}

public class FolderRecord : ArchiveRecord
{
    public string Path { get; set; }
    public string Name { get; set; }
    public UUID FolderId { get; set; }
    public int ItemCount { get; set; }
}

public class ParcelRecord : ArchiveRecord
{
    public string RegionDir { get; set; } = string.Empty;
    public string Name { get; set; }
    public UUID OwnerId { get; set; }
    public UUID GroupId { get; set; }
    public bool IsGroupOwned { get; set; }
    public UUID MediaId { get; set; }
}

public class RegionSettingsRecord : ArchiveRecord
{
    public string RegionDir { get; set; } = string.Empty;
    public string RegionName { get; set; }
    public List<UUID> TerrainTextures { get; set; } = new List<UUID>();
    public List<UUID> TerrainPbr { get; set; } = new List<UUID>();
}

public class AssetRecord : ArchiveRecord
{
    public UUID AssetId { get; set; }
    public int AssetType { get; set; }
    public string TypeName { get; set; }
    public long Size { get; set; }
    /// <summary>False when the file name has no known type extension.</summary>
    public bool Recognised { get; set; }
    public bool BodyRead { get; set; }
    public string Note { get; set; }
}

public class ReferrerInfo
{
    public string RecordKind { get; set; }
    public string Name { get; set; }
    public UUID Id { get; set; }
    public CreatorRef Creator { get; set; }
    public UUID OwnerId { get; set; }
}

public class ReferenceRecord : ArchiveRecord
{
    public UUID AssetId { get; set; }
    public int? AssetType { get; set; }
    public ReferenceKind Kind { get; set; }
    public Certainty Certainty { get; set; }
    public ReferrerInfo Referrer { get; set; }
}

public class BoxedObjectRecord : ArchiveRecord
{
    public UUID AssetId { get; set; }
    public string Format { get; set; }
    public int LinksetCount { get; set; }
    public int PartCount { get; set; }
    public int ItemCount { get; set; }
    public CreatorRef RootCreator { get; set; }
    /// <summary>Filled when the pass ends: how deep the object is held (1 = held by an archive item or part).</summary>
    public int Depth { get; set; }
    public int HolderCount { get; set; }
}

public class LegacyPayloadRecord : ArchiveRecord
{
    public string Kind { get; set; }
    public UUID? AssetId { get; set; }
    public long Bytes { get; set; }
}

public class ProblemRecord : ArchiveRecord
{
    public string Message { get; set; }
}

/// <summary>Body of a text-type asset handed to checks (script, notecard, gesture, wearable, material, settings).</summary>
public class TextAssetBody : ArchiveRecord
{
    public UUID AssetId { get; set; }
    public int AssetType { get; set; }
    public ReadOnlyMemory<byte> Bytes { get; set; }
}

public class RegionInfo
{
    public string Dir { get; set; } = string.Empty;
    public UUID? Id { get; set; }
    public float? SizeX { get; set; }
    public float? SizeY { get; set; }
}

public class ArchiveInfo
{
    public ArchiveKind Kind { get; set; }
    public int? MajorVersion { get; set; }
    public int? MinorVersion { get; set; }
    public string Version => MajorVersion.HasValue ? MajorVersion + "." + MinorVersion : null;
    public long? CreationTime { get; set; }
    public bool? AssetsIncluded { get; set; }
    public bool MultiRegion { get; set; }
    public List<RegionInfo> Regions { get; set; } = new List<RegionInfo>();
    public bool ControlFileFound { get; set; }
    public bool ControlFileFirst { get; set; }
    public long CompressedBytes { get; set; }
    public long UncompressedBytes { get; set; }
    public bool Compressed { get; set; }
    public long EntryCount { get; set; }
    public long FileBytes { get; set; }
}
