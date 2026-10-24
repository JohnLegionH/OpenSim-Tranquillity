/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Buffers.Binary;
using System.Text;

namespace OpenSim.MeshAsset;

/// <summary>
/// Binary LLSD, the serialisation inside a mesh asset: '{' count (k len key value)* '}', '[' count value* ']',
/// 'i' int32, 'r' float64, 's' and 'l' int32 length + bytes, 'b' int32 length + bytes, '1' and '0' booleans, '!' undefined.
/// Integers and lengths are big endian. Values are plain .NET objects: null (undefined), bool, int, double, string,
/// byte[], List&lt;object?&gt;, SortedDictionary&lt;string, object?&gt; (ordinal key order, as a std::map writes it).
/// </summary>
public static class LlsdBinary
{
    const int MaxDepth = 64;

    public static SortedDictionary<string, object?> NewMap() => new(StringComparer.Ordinal);

    public static byte[] Write(object? value)
    {
        using var ms = new MemoryStream();
        WriteValue(ms, value);
        return ms.ToArray();
    }

    static void WriteInt(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, v);
        s.Write(b);
    }

    static void WriteString(Stream s, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        WriteInt(s, bytes.Length);
        s.Write(bytes);
    }

    static void WriteValue(Stream s, object? v)
    {
        switch (v)
        {
            case null: s.WriteByte((byte)'!'); break;
            case bool b: s.WriteByte((byte)(b ? '1' : '0')); break;
            case int i: s.WriteByte((byte)'i'); WriteInt(s, i); break;
            case double d:
                {
                    s.WriteByte((byte)'r');
                    Span<byte> b8 = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleBigEndian(b8, d);
                    s.Write(b8);
                    break;
                }
            case float f: WriteValue(s, (double)f); break;
            case string str: s.WriteByte((byte)'s'); WriteString(s, str); break;
            case byte[] bin: s.WriteByte((byte)'b'); WriteInt(s, bin.Length); s.Write(bin); break;
            case IDictionary<string, object?> map:
                {
                    s.WriteByte((byte)'{');
                    WriteInt(s, map.Count);
                    foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        s.WriteByte((byte)'k');
                        WriteString(s, kv.Key);
                        WriteValue(s, kv.Value);
                    }
                    s.WriteByte((byte)'}');
                    break;
                }
            case IList<object?> list:
                {
                    s.WriteByte((byte)'[');
                    WriteInt(s, list.Count);
                    foreach (var item in list) WriteValue(s, item);
                    s.WriteByte((byte)']');
                    break;
                }
            default: throw new ArgumentException($"LLSD cannot hold a {v.GetType().Name}");
        }
    }

    /// <summary>Reads one value from the start of <paramref name="data"/>; <paramref name="consumed"/> is the number of bytes used.</summary>
    public static object? Read(ReadOnlySpan<byte> data, out int consumed)
    {
        int pos = 0;
        var v = ReadValue(data, ref pos, 0);
        consumed = pos;
        return v;
    }

    static void Need(ReadOnlySpan<byte> d, int pos, long n)
    {
        if (n < 0 || pos + n > d.Length) throw new MeshFormatException("LLSD data ends early");
    }

    static int ReadInt(ReadOnlySpan<byte> d, ref int pos)
    {
        Need(d, pos, 4);
        int v = BinaryPrimitives.ReadInt32BigEndian(d.Slice(pos, 4));
        pos += 4;
        return v;
    }

    static string ReadString(ReadOnlySpan<byte> d, ref int pos)
    {
        int len = ReadInt(d, ref pos);
        Need(d, pos, len);
        var s = Encoding.UTF8.GetString(d.Slice(pos, len));
        pos += len;
        return s;
    }

    static object? ReadValue(ReadOnlySpan<byte> d, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new MeshFormatException("LLSD nested too deeply");
        Need(d, pos, 1);
        char c = (char)d[pos++];
        switch (c)
        {
            case '!': return null;
            case '0': return false;
            case '1': return true;
            case 'i': return ReadInt(d, ref pos);
            case 'r':
                Need(d, pos, 8);
                var r = BinaryPrimitives.ReadDoubleBigEndian(d.Slice(pos, 8));
                pos += 8;
                return r;
            case 's':
            case 'l': return ReadString(d, ref pos);
            case 'b':
                {
                    int len = ReadInt(d, ref pos);
                    Need(d, pos, len);
                    var b = d.Slice(pos, len).ToArray();
                    pos += len;
                    return b;
                }
            case '[':
                {
                    int n = ReadInt(d, ref pos);
                    if (n < 0 || n > d.Length - pos) throw new MeshFormatException("LLSD array count is larger than the data");
                    var list = new List<object?>(n);
                    for (int i = 0; i < n; i++) list.Add(ReadValue(d, ref pos, depth + 1));
                    Need(d, pos, 1);
                    if (d[pos++] != ']') throw new MeshFormatException("LLSD array is not closed");
                    return list;
                }
            case '{':
                {
                    int n = ReadInt(d, ref pos);
                    if (n < 0 || n > d.Length - pos) throw new MeshFormatException("LLSD map count is larger than the data");
                    var map = NewMap();
                    for (int i = 0; i < n; i++)
                    {
                        Need(d, pos, 1);
                        if (d[pos++] != 'k') throw new MeshFormatException("LLSD map key is not a 'k' string");
                        var key = ReadString(d, ref pos);
                        map[key] = ReadValue(d, ref pos, depth + 1);
                    }
                    Need(d, pos, 1);
                    if (d[pos++] != '}') throw new MeshFormatException("LLSD map is not closed");
                    return map;
                }
            default: throw new MeshFormatException($"LLSD type byte 0x{(int)c:x2} is not supported");
        }
    }
}

public sealed class MeshFormatException : Exception
{
    public MeshFormatException(string message) : base(message) { }
}
