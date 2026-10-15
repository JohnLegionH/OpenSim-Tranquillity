/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Text;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Archive.Inspection;

/// <summary>
/// Finds the asset ids named in the text-type assets the savers open (scripts, notecards, gestures, wearables,
/// materials, settings). The rules follow UuidGatherer so the reference sets can be compared.
/// </summary>
internal static class TextScan
{
    public static bool IsScannedType(int assetType)
    {
        switch ((AssetType)assetType)
        {
            case AssetType.Bodypart:
            case AssetType.Clothing:
            case AssetType.Gesture:
            case AssetType.Notecard:
            case AssetType.LSLText:
            case AssetType.OSMaterial:
            case AssetType.Settings:
            case AssetType.Material:
                return true;
            default:
                return false;
        }
    }

    private static bool IsHex(byte b) => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

    /// <summary>Strict 36 character 8-4-4-4-12 form.</summary>
    public static bool TryParseUuid(ReadOnlySpan<byte> s, out UUID id)
    {
        id = UUID.Zero;
        if (s.Length != 36)
            return false;
        for (int i = 0; i < 36; i++)
        {
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (s[i] != (byte)'-')
                    return false;
            }
            else if (!IsHex(s[i]))
                return false;
        }
        return UUID.TryParse(Encoding.ASCII.GetString(s), out id);
    }

    /// <summary>Every non-zero id written out in full anywhere in the text (UuidGatherer.RecordEmbeddedAssetDataUuids).</summary>
    public static IEnumerable<UUID> EmbeddedUuids(byte[] data)
    {
        List<UUID> found = new List<UUID>();
        int indx = 8;
        while (indx < data.Length - 28)
        {
            if (data[indx] == (byte)'-')
            {
                if (TryParseUuid(data.AsSpan(indx - 8, 36), out UUID id))
                {
                    if (id != UUID.Zero)
                        found.Add(id);
                    indx += 37;
                }
                else
                    indx += 9;
            }
            else
                indx++;
        }
        return found;
    }

    public static List<RefInfo> Scan(int assetType, byte[] data)
    {
        List<RefInfo> refs = new List<RefInfo>();
        if (data == null || data.Length < 36)
            return refs;

        switch ((AssetType)assetType)
        {
            case AssetType.LSLText:
                foreach (UUID id in EmbeddedUuids(data))
                    refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.ScriptText, Certainty = Certainty.Uncertain });
                break;
            case AssetType.Settings:
            case AssetType.Material:
                foreach (UUID id in EmbeddedUuids(data))
                    refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.EmbeddedText, Certainty = Certainty.Uncertain });
                break;
            case AssetType.Notecard:
            {
                List<UUID> ids = SLUtil.GetEmbeddedAssetIDs(data);
                if (ids != null)
                    foreach (UUID id in ids)
                        if (id != UUID.Zero)
                            refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.NotecardText, Certainty = Certainty.Uncertain });
                break;
            }
            case AssetType.Bodypart:
            case AssetType.Clothing:
                ScanWearable(data, refs);
                break;
            case AssetType.Gesture:
                ScanGesture(data, refs);
                break;
            case AssetType.OSMaterial:
                ScanOsMaterial(data, refs);
                break;
        }
        return refs;
    }

    private static List<string> Lines(byte[] data)
    {
        return new List<string>(Encoding.UTF8.GetString(data).Split('\n'));
    }

    // Linden wearable text: four header lines, then "parameters N" followed by N lines and "textures N" followed by N "slot uuid" lines.
    private static void ScanWearable(byte[] data, List<RefInfo> refs)
    {
        if (data.Length < 64)
            return;
        List<string> lines = Lines(data);
        int i = 4;
        while (i < lines.Count)
        {
            string[] parts = lines[i++].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            if (parts[0] == "parameters")
            {
                if (!int.TryParse(parts[1], out int n))
                    return;
                i += n;
            }
            else if (parts[0] == "textures")
            {
                if (!int.TryParse(parts[1], out int n) || n == 0)
                    return;
                for (int k = 0; k < n && i < lines.Count; k++)
                {
                    string[] tex = lines[i++].Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tex.Length >= 2 && tex[1].Length >= 36 && UUID.TryParse(tex[1], out UUID id) && id != UUID.Zero)
                        refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.WearableTexture, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
                }
            }
        }
    }

    // Gesture text: version, key, mask, trigger, replace, step count, then per step: type, [name, uuid, flags] or [text, flags].
    private static void ScanGesture(byte[] data, List<RefInfo> refs)
    {
        List<string> lines = Lines(data);
        if (lines.Count < 7)
            return;
        if (!int.TryParse(lines[5].Trim(), out int steps) || steps == 0)
            return;
        int i = 6;
        for (int s = 0; s < steps; s++)
        {
            if (i >= lines.Count || !int.TryParse(lines[i++].Trim(), out int type))
                return;
            switch (type)
            {
                case 0:
                case 1:
                    i++;
                    if (i >= lines.Count)
                        return;
                    if (UUID.TryParse(lines[i++].Trim(), out UUID id) && id != UUID.Zero)
                        refs.Add(new RefInfo
                        {
                            Id = id,
                            Kind = ReferenceKind.GestureAsset,
                            Certainty = Certainty.Certain,
                            AssetType = type == 0 ? (int)AssetType.Animation : (int)AssetType.Sound
                        });
                    i++;
                    break;
                case 2:
                case 3:
                    i += 2;
                    break;
                default:
                    return;
            }
        }
    }

    // Material XML: the text between <uuid> and the next '<' is a texture id.
    private static void ScanOsMaterial(byte[] data, List<RefInfo> refs)
    {
        string text = Encoding.UTF8.GetString(data);
        int pos = 0;
        while ((pos = text.IndexOf("<uuid", pos, StringComparison.Ordinal)) >= 0)
        {
            int close = text.IndexOf('>', pos);
            if (close < 0)
                break;
            int end = text.IndexOf('<', close);
            if (end < 0)
                break;
            if (text[close - 1] != '/' && UUID.TryParse(text.Substring(close + 1, end - close - 1).Trim(), out UUID id) && id != UUID.Zero)
                refs.Add(new RefInfo { Id = id, Kind = ReferenceKind.MaterialTexture, Certainty = Certainty.Certain, AssetType = (int)AssetType.Texture });
            pos = end;
        }
    }
}
