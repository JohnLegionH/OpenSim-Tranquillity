/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Xml;
using OpenMetaverse;

namespace OpenSim.Archive.Inspection;

internal sealed class ControlFile
{
    public int? Major, Minor;
    public long? CreationTime;
    public bool? AssetsIncluded;
    public bool HasCreationInfo, HasRegionInfo, HasRegions;
    public List<RegionInfo> Regions = new List<RegionInfo>();
}

/// <summary>Parsers for the small XML files of an archive: control file, IAR item, parcel, region settings.</summary>
internal static class MetadataParsers
{
    public static ControlFile ParseControl(byte[] body)
    {
        ControlFile c = new ControlFile();
        RegionInfo current = null;
        bool inRegions = false;
        bool inRegionInfo = false;
        bool inCreationInfo = false;
        RegionInfo single = null;

        using (XmlReader r = XmlRead.Create(body))
        {
            bool consumed = false;
            while (true)
            {
                if (consumed) { if (r.EOF) break; }
                else if (!r.Read()) break;
                consumed = false;

                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.Name == "region") current = null;
                    else if (r.Name == "regions") inRegions = false;
                    else if (r.Name == "region_info") inRegionInfo = false;
                    else if (r.Name == "creation_info") inCreationInfo = false;
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element)
                    continue;

                switch (r.Name)
                {
                    case "archive":
                        if (XmlRead.TryInt(r.GetAttribute("major_version") ?? "", out int ma)) c.Major = ma;
                        if (XmlRead.TryInt(r.GetAttribute("minor_version") ?? "", out int mi)) c.Minor = mi;
                        break;
                    case "creation_info": c.HasCreationInfo = true; inCreationInfo = !r.IsEmptyElement; break;
                    case "region_info": c.HasRegionInfo = true; inRegionInfo = !r.IsEmptyElement; single ??= new RegionInfo(); break;
                    case "regions": c.HasRegions = true; inRegions = !r.IsEmptyElement; break;
                    case "region":
                        if (inRegions)
                        {
                            current = r.IsEmptyElement ? null : new RegionInfo();
                            if (current != null)
                                c.Regions.Add(current);
                        }
                        break;
                    case "datetime":
                        if (long.TryParse(XmlRead.Text(r), NumberStyles.Integer, CultureInfo.InvariantCulture, out long t)) c.CreationTime = t;
                        consumed = true;
                        break;
                    case "assets_included":
                    {
                        string s = XmlRead.Text(r);
                        if (bool.TryParse(s, out bool b)) c.AssetsIncluded = b;
                        consumed = true;
                        break;
                    }
                    case "id":
                    {
                        string s = XmlRead.Text(r);
                        if (UUID.TryParse(s, out UUID id))
                        {
                            if (current != null) current.Id = id;
                            else if (inCreationInfo || !inRegions) { single ??= new RegionInfo(); single.Id = id; }
                        }
                        consumed = true;
                        break;
                    }
                    case "dir":
                    {
                        string s = XmlRead.Text(r);
                        if (current != null) current.Dir = s;
                        consumed = true;
                        break;
                    }
                    case "size_in_meters":
                    {
                        string s = XmlRead.Text(r);
                        string[] parts = s.Split(',');
                        if (parts.Length >= 2)
                        {
                            RegionInfo target = current ?? (inRegionInfo ? (single ??= new RegionInfo()) : null);
                            if (target != null)
                            {
                                target.SizeX = XmlRead.Float(parts[0]);
                                target.SizeY = XmlRead.Float(parts[1]);
                            }
                        }
                        consumed = true;
                        break;
                    }
                }
            }
        }

        if (!c.HasRegions && single != null)
            c.Regions.Add(single);
        return c;
    }

    public static ItemRecord ParseIarItem(byte[] body)
    {
        ItemRecord item = new ItemRecord { Source = ItemSource.InventoryArchive };
        string creatorUuid = null, creatorId = null, creatorData = null;

        using (XmlReader r = XmlRead.Create(body))
        {
            while (!r.EOF && !(r.NodeType == XmlNodeType.Element && r.Name == "InventoryItem"))
                r.Read();
            if (r.EOF)
                throw new XmlException("no InventoryItem element");

            if (r.IsEmptyElement)
                return item;

            int depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    r.Read();
                    continue;
                }

                switch (r.Name)
                {
                    case "Name": item.Name = XmlRead.Text(r); break;
                    case "ID": item.ItemId = XmlRead.Uuid(r); break;
                    case "InvType": { if (XmlRead.TryInt(XmlRead.Text(r), out int it)) item.InvType = it; break; }
                    case "CreatorUUID": creatorUuid = XmlRead.Text(r); break;
                    case "CreatorID": creatorId = XmlRead.Text(r); break;
                    case "CreatorData": creatorData = XmlRead.Text(r); break;
                    case "CreationDate": item.CreationDate = XmlRead.Date(XmlRead.Text(r)); break;
                    case "Owner": item.OwnerId = XmlRead.Uuid(r); break;
                    case "AssetType": { if (XmlRead.TryInt(XmlRead.Text(r), out int at)) item.AssetType = at; break; }
                    case "AssetID": item.AssetId = XmlRead.Uuid(r); break;
                    case "BasePermissions": item.Masks.Base = XmlRead.Mask(XmlRead.Text(r)); break;
                    case "CurrentPermissions": item.Masks.Owner = XmlRead.Mask(XmlRead.Text(r)); break;
                    case "EveryOnePermissions": item.Masks.Everyone = XmlRead.Mask(XmlRead.Text(r)); break;
                    case "NextPermissions": item.Masks.NextOwner = XmlRead.Mask(XmlRead.Text(r)); break;
                    case "GroupPermissions": item.Masks.Group = XmlRead.Mask(XmlRead.Text(r)); break;
                    case "GroupID": item.GroupId = XmlRead.Uuid(r); break;
                    case "GroupOwned": { bool.TryParse(XmlRead.Text(r), out bool go); item.GroupOwned = go; break; }
                    default: r.Skip(); break;
                }
            }
        }

        // CreatorID, when present, overrides CreatorUUID (UserInventoryItemSerializer reads them in that order).
        item.Creator = XmlRead.BuildCreator(creatorId, creatorUuid, creatorData);
        return item;
    }

    public static ParcelRecord ParseParcel(byte[] body)
    {
        ParcelRecord p = new ParcelRecord();
        using (XmlReader r = XmlRead.Create(body))
        {
            while (!r.EOF && !(r.NodeType == XmlNodeType.Element && r.Name == "LandData"))
                r.Read();
            if (r.EOF)
                throw new XmlException("no LandData element");
            if (r.IsEmptyElement)
                return p;

            int depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType != XmlNodeType.Element)
                {
                    r.Read();
                    continue;
                }
                switch (r.Name)
                {
                    case "Name": p.Name = XmlRead.Text(r); break;
                    case "OwnerID": p.OwnerId = XmlRead.Uuid(r); break;
                    case "GroupID": p.GroupId = XmlRead.Uuid(r); break;
                    case "MediaID": p.MediaId = XmlRead.Uuid(r); break;
                    case "IsGroupOwned": { bool.TryParse(XmlRead.Text(r), out bool g); p.IsGroupOwned = g; break; }
                    default: r.Skip(); break;
                }
            }
        }
        return p;
    }

    public static RegionSettingsRecord ParseRegionSettings(byte[] body)
    {
        RegionSettingsRecord s = new RegionSettingsRecord();
        using (XmlReader r = XmlRead.Create(body))
        {
            bool consumed = false;
            while (true)
            {
                if (consumed) { if (r.EOF) break; }
                else if (!r.Read()) break;
                consumed = false;

                if (r.NodeType != XmlNodeType.Element)
                    continue;
                string n = r.Name;
                if (n == "Texture1" || n == "Texture2" || n == "Texture3" || n == "Texture4")
                {
                    if (UUID.TryParse(XmlRead.Text(r), out UUID id)) s.TerrainTextures.Add(id);
                    consumed = true;
                }
                else if (n == "PBR1" || n == "PBR2" || n == "PBR3" || n == "PBR4")
                {
                    if (UUID.TryParse(XmlRead.Text(r), out UUID id)) s.TerrainPbr.Add(id);
                    consumed = true;
                }
            }
        }
        return s;
    }
}
