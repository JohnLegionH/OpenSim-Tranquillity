/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Reflection;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace OpenSim.Archive.ScriptScan;

public enum LslValueKind { Unknown, Integer, Float, String, Vector, Rotation, List }

/// <summary>
/// What an argument expression evaluates to without running the script. Anything that depends on a variable, a function
/// result or run-time list building is <see cref="LslValueKind.Unknown"/>.
/// </summary>
public sealed class LslValue
{
    public static readonly LslValue Unknown = new LslValue(LslValueKind.Unknown);

    public LslValueKind Kind { get; }
    public long Int { get; }
    public double Float { get; }
    public string Str { get; }
    public IReadOnlyList<LslValue> Items { get; }
    /// <summary>The constant's name when the value came straight from a named constant (PRIM_TYPE, LINK_SET, ...).</summary>
    public string ConstName { get; }

    private LslValue(LslValueKind kind, long i = 0, double f = 0, string s = null, IReadOnlyList<LslValue> items = null, string constName = null)
    {
        Kind = kind;
        Int = i;
        Float = f;
        Str = s;
        Items = items;
        ConstName = constName;
    }

    public static LslValue Of(long i, string constName = null) => new LslValue(LslValueKind.Integer, i: i, constName: constName);
    public static LslValue Of(double f) => new LslValue(LslValueKind.Float, f: f);
    public static LslValue Of(string s) => new LslValue(LslValueKind.String, s: s);
    public static LslValue OfVector() => new LslValue(LslValueKind.Vector);
    public static LslValue OfRotation() => new LslValue(LslValueKind.Rotation);
    public static LslValue OfList(IReadOnlyList<LslValue> items) => new LslValue(LslValueKind.List, items: items);

    public bool IsUnknown => Kind == LslValueKind.Unknown;
    public bool IsInt => Kind == LslValueKind.Integer;

    public override string ToString()
    {
        switch (Kind)
        {
            case LslValueKind.Integer: return ConstName ?? Int.ToString(CultureInfo.InvariantCulture);
            case LslValueKind.Float: return Float.ToString("R", CultureInfo.InvariantCulture);
            case LslValueKind.String: return "string";
            case LslValueKind.List: return "[" + string.Join(", ", Items) + "]";
            case LslValueKind.Vector: return "vector";
            case LslValueKind.Rotation: return "rotation";
            default: return "?";
        }
    }
}

/// <summary>
/// The LSL and OSSL constants, read by reflection from the server's script base class (LSL_Constants.cs), so the table is
/// always the one the region server compiles against.
/// </summary>
public static class LslConstants
{
    private static readonly Dictionary<string, LslValue> s_table = Build();

    public static bool TryGet(string name, out LslValue value) => s_table.TryGetValue(name, out value);

    public static int Count => s_table.Count;

    private static Dictionary<string, LslValue> Build()
    {
        Dictionary<string, LslValue> t = new Dictionary<string, LslValue>(StringComparer.Ordinal);
        foreach (FieldInfo f in typeof(ScriptBaseClass).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object v = f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null);
            if (v == null)
                continue;
            if (v is int i) { t[f.Name] = LslValue.Of(i, f.Name); continue; }
            if (v is double d) { t[f.Name] = LslValue.Of(d); continue; }
            if (v is string s) { t[f.Name] = LslValue.Of(s); continue; }

            string type = f.FieldType.Name;
            if (type == "LSLInteger" && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long li))
                t[f.Name] = LslValue.Of(li, f.Name);
            else if (type == "LSLFloat" && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ld))
                t[f.Name] = LslValue.Of(ld);
            else if (type == "LSLString")
                t[f.Name] = LslValue.Of(v.ToString());
            else if (type == "Vector3")
                t[f.Name] = LslValue.OfVector();
            else if (type == "Quaternion")
                t[f.Name] = LslValue.OfRotation();
        }
        return t;
    }
}

/// <summary>
/// Reads an expression from a token list. It understands literals, named constants, parentheses, unary minus, casts of
/// literals, vector and rotation literals, list literals, and the arithmetic, shift and bit operators on known values. Calls,
/// variables and member access are consumed (so the position stays right) and evaluate to Unknown.
/// </summary>
public sealed class LslExpressionParser
{
    private readonly IReadOnlyList<LslToken> m_t;
    private int m_pos;

    public LslExpressionParser(IReadOnlyList<LslToken> tokens, int start)
    {
        m_t = tokens;
        m_pos = start;
    }

    public int Position => m_pos;
    public bool Failed { get; private set; }

    private sealed class ParseFail : Exception { }

    private bool PeekPunct(string p) => m_pos < m_t.Count && m_t[m_pos].Is(p);

    /// <summary>Reads one expression. On a token it cannot place, sets <see cref="Failed"/> and returns Unknown.</summary>
    public LslValue ParseExpression()
    {
        try { return Binary(0); }
        catch (ParseFail) { Failed = true; return LslValue.Unknown; }
    }

    /// <summary>
    /// Reads a comma separated argument list after "(" and consumes the ")". Returns null when the arguments cannot be
    /// read reliably; the position is then after the matching ")" found by bracket counting.
    /// </summary>
    public List<LslValue> ParseArguments()
    {
        int open = m_pos;
        List<LslValue> args = new List<LslValue>();
        if (PeekPunct(")")) { m_pos++; return args; }
        while (true)
        {
            LslValue v = ParseExpression();
            if (Failed) break;
            args.Add(v);
            if (PeekPunct(",")) { m_pos++; continue; }
            if (PeekPunct(")")) { m_pos++; return args; }
            Failed = true;
            break;
        }
        // Fall back to bracket counting so the caller carries on after the call.
        m_pos = open;
        SkipToCloseParen();
        return null;
    }

    private bool SkipToCloseParen()
    {
        int depth = 1;
        while (m_pos < m_t.Count)
        {
            LslToken t = m_t[m_pos++];
            if (t.Is("(") || t.Is("[")) depth++;
            else if (t.Is(")") || t.Is("]"))
            {
                depth--;
                if (depth == 0) return true;
            }
        }
        return false;
    }

    private static int Prec(string op)
    {
        switch (op)
        {
            case "|": return 1;
            case "^": return 2;
            case "&": return 3;
            case "<<":
            case ">>": return 4;
            case "+":
            case "-": return 5;
            case "*":
            case "/":
            case "%": return 6;
            default: return -1;
        }
    }

    private LslValue Binary(int minPrec)
    {
        LslValue left = Unary();
        while (m_pos < m_t.Count && m_t[m_pos].Kind == LslTokenKind.Punct)
        {
            string op = m_t[m_pos].Text;
            int p = Prec(op);
            if (p < 0 || p < minPrec)
                break;
            m_pos++;
            LslValue right = Binary(p + 1);
            left = Apply(op, left, right);
        }
        return left;
    }

    private static LslValue Apply(string op, LslValue a, LslValue b)
    {
        if (a.IsUnknown || b.IsUnknown)
            return LslValue.Unknown;

        if (op == "+")
        {
            if (a.Kind == LslValueKind.List && b.Kind == LslValueKind.List)
                return LslValue.OfList(a.Items.Concat(b.Items).ToList());
            if (a.Kind == LslValueKind.List)
                return LslValue.OfList(a.Items.Append(b).ToList());
            if (b.Kind == LslValueKind.List)
                return LslValue.OfList(b.Items.Prepend(a).ToList());
            if (a.Kind == LslValueKind.String && b.Kind == LslValueKind.String)
                return LslValue.Of(a.Str + b.Str);
        }

        if (a.IsInt && b.IsInt)
        {
            int x = (int)a.Int, y = (int)b.Int;
            switch (op)
            {
                case "+": return LslValue.Of(unchecked(x + y));
                case "-": return LslValue.Of(unchecked(x - y));
                case "*": return LslValue.Of(unchecked(x * y));
                case "/": return y == 0 || (x == int.MinValue && y == -1) ? LslValue.Unknown : LslValue.Of(x / y);
                case "%": return y == 0 || (x == int.MinValue && y == -1) ? LslValue.Unknown : LslValue.Of(x % y);
                case "|": return LslValue.Of(x | y);
                case "&": return LslValue.Of(x & y);
                case "^": return LslValue.Of(x ^ y);
                case "<<": return LslValue.Of(x << (y & 31));
                case ">>": return LslValue.Of(x >> (y & 31));
            }
        }

        bool aNum = a.IsInt || a.Kind == LslValueKind.Float, bNum = b.IsInt || b.Kind == LslValueKind.Float;
        if (aNum && bNum)
        {
            double x = a.IsInt ? a.Int : a.Float, y = b.IsInt ? b.Int : b.Float;
            switch (op)
            {
                case "+": return LslValue.Of(x + y);
                case "-": return LslValue.Of(x - y);
                case "*": return LslValue.Of(x * y);
                case "/": return y == 0 ? LslValue.Unknown : LslValue.Of(x / y);
            }
        }
        return LslValue.Unknown;
    }

    private static readonly HashSet<string> s_castTypes = new HashSet<string> { "integer", "float", "string", "key", "vector", "rotation", "list" };

    private LslValue Unary()
    {
        if (m_pos >= m_t.Count)
            throw new ParseFail();
        LslToken t = m_t[m_pos];

        if (t.Kind == LslTokenKind.Punct && (t.Text == "-" || t.Text == "+" || t.Text == "~" || t.Text == "!"))
        {
            m_pos++;
            LslValue v = Unary();
            if (v.IsUnknown || t.Text == "+") return v;
            if (t.Text == "-")
                return v.IsInt ? LslValue.Of(-(int)v.Int) : v.Kind == LslValueKind.Float ? LslValue.Of(-v.Float) : LslValue.Unknown;
            if (t.Text == "~" && v.IsInt) return LslValue.Of(~(int)v.Int);
            if (t.Text == "!" && v.IsInt) return LslValue.Of(v.Int == 0 ? 1 : 0);
            return LslValue.Unknown;
        }

        // A cast: "(" type ")" operand.
        if (t.Is("(") && m_pos + 2 < m_t.Count && m_t[m_pos + 1].Kind == LslTokenKind.Identifier
            && s_castTypes.Contains(m_t[m_pos + 1].Text) && m_t[m_pos + 2].Is(")"))
        {
            string type = m_t[m_pos + 1].Text;
            m_pos += 3;
            LslValue v = Unary();
            if (v.IsUnknown) return v;
            switch (type)
            {
                case "integer":
                    if (v.IsInt) return v;
                    if (v.Kind == LslValueKind.Float) return LslValue.Of((int)v.Float);
                    if (v.Kind == LslValueKind.String && int.TryParse(v.Str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pi)) return LslValue.Of(pi);
                    return LslValue.Unknown;
                case "float":
                    if (v.IsInt) return LslValue.Of((double)v.Int);
                    return v.Kind == LslValueKind.Float ? v : LslValue.Unknown;
                case "string":
                case "key":
                    return v.Kind == LslValueKind.String ? v : v.IsInt ? LslValue.Of(v.Int.ToString(CultureInfo.InvariantCulture)) : LslValue.Unknown;
                case "list":
                    return v.Kind == LslValueKind.List ? v : LslValue.OfList(new[] { v });
                default:
                    return LslValue.Unknown;
            }
        }

        if (t.Is("("))
        {
            m_pos++;
            LslValue v = Binary(0);
            if (!PeekPunct(")")) throw new ParseFail();
            m_pos++;
            return v;
        }

        if (t.Is("["))
        {
            m_pos++;
            List<LslValue> items = new List<LslValue>();
            if (PeekPunct("]")) { m_pos++; return LslValue.OfList(items); }
            while (true)
            {
                items.Add(Binary(0));
                if (PeekPunct(",")) { m_pos++; continue; }
                if (PeekPunct("]")) { m_pos++; return LslValue.OfList(items); }
                throw new ParseFail();
            }
        }

        if (t.Is("<"))
        {
            m_pos++;
            int count = 0;
            while (true)
            {
                Binary(0);
                count++;
                if (PeekPunct(",")) { m_pos++; continue; }
                if (PeekPunct(">")) { m_pos++; break; }
                throw new ParseFail();
            }
            return count == 3 ? LslValue.OfVector() : count == 4 ? LslValue.OfRotation() : LslValue.Unknown;
        }

        if (t.Kind == LslTokenKind.Integer) { m_pos++; return LslValue.Of(t.IntValue); }
        if (t.Kind == LslTokenKind.Float) { m_pos++; return LslValue.Of(t.FloatValue); }
        if (t.Kind == LslTokenKind.String) { m_pos++; return LslValue.Of(t.Text); }

        if (t.Kind == LslTokenKind.Identifier)
        {
            m_pos++;
            // A function call: skip its arguments; the result is not known.
            if (PeekPunct("("))
            {
                m_pos++;
                if (!SkipToCloseParen()) throw new ParseFail();
                return LslValue.Unknown;
            }
            LslValue result = LslConstants.TryGet(t.Text, out LslValue c) ? c : LslValue.Unknown;
            // Member access (v.x) and postfix ++/-- on a variable.
            while (PeekPunct(".") && m_pos + 1 < m_t.Count && m_t[m_pos + 1].Kind == LslTokenKind.Identifier) { m_pos += 2; result = LslValue.Unknown; }
            if (PeekPunct("++") || PeekPunct("--")) { m_pos++; result = LslValue.Unknown; }
            return result;
        }

        throw new ParseFail();
    }
}
