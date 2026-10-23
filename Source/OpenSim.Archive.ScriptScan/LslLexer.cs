/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;

namespace OpenSim.Archive.ScriptScan;

public enum LslTokenKind { Identifier, Integer, Float, String, Punct }

/// <summary>One lexical token of an LSL script, with the 1-based line it starts on.</summary>
public readonly struct LslToken
{
    public LslTokenKind Kind { get; }
    /// <summary>Identifier or punctuation text; for a string the unescaped contents; for a number its source text.</summary>
    public string Text { get; }
    public int Line { get; }
    public long IntValue { get; }
    public double FloatValue { get; }

    public LslToken(LslTokenKind kind, string text, int line, long intValue = 0, double floatValue = 0)
    {
        Kind = kind;
        Text = text;
        Line = line;
        IntValue = intValue;
        FloatValue = floatValue;
    }

    public bool Is(string punct) => Kind == LslTokenKind.Punct && Text == punct;
    public bool IsIdent(string name) => Kind == LslTokenKind.Identifier && Text == name;
    public override string ToString() => Kind + ":" + Text;
}

/// <summary>
/// Splits LSL source into tokens. Comments are dropped, string literals are kept as single tokens (so a call named inside
/// a string or a comment is never seen), and preprocessor lines are left as ordinary tokens. It does not check that the
/// script is valid; an unterminated string or comment ends at the end of the text.
/// </summary>
public static class LslLexer
{
    private static readonly string[] s_threeChar = { "<<=", ">>=" };
    private static readonly string[] s_twoChar = { "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=" };

    public static List<LslToken> Tokenize(string src)
    {
        List<LslToken> tokens = new List<LslToken>();
        int i = 0, line = 1, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                i += 2;
                while (i < n && !(src[i] == '*' && i + 1 < n && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                i = Math.Min(n, i + 2);
                continue;
            }

            if (c == '"')
            {
                int startLine = line;
                StringBuilder sb = new StringBuilder();
                i++;
                while (i < n && src[i] != '"')
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        char e = src[i + 1];
                        if (e == '\n') line++;
                        sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e);
                        i += 2;
                        continue;
                    }
                    if (src[i] == '\n') line++;
                    sb.Append(src[i]);
                    i++;
                }
                i = Math.Min(n, i + 1);
                tokens.Add(new LslToken(LslTokenKind.String, sb.ToString(), startLine));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
            {
                tokens.Add(Number(src, ref i, line));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                tokens.Add(new LslToken(LslTokenKind.Identifier, src.Substring(s, i - s), line));
                continue;
            }

            string punct = null;
            foreach (string p in s_threeChar)
                if (string.CompareOrdinal(src, i, p, 0, 3) == 0) { punct = p; break; }
            if (punct == null)
                foreach (string p in s_twoChar)
                    if (string.CompareOrdinal(src, i, p, 0, 2) == 0) { punct = p; break; }
            punct ??= c.ToString();
            tokens.Add(new LslToken(LslTokenKind.Punct, punct, line));
            i += punct.Length;
        }
        return tokens;
    }

    private static LslToken Number(string src, ref int i, int line)
    {
        int s = i, n = src.Length;
        if (src[i] == '0' && i + 1 < n && (src[i + 1] == 'x' || src[i + 1] == 'X'))
        {
            i += 2;
            while (i < n && Uri.IsHexDigit(src[i])) i++;
            string hex = src.Substring(s, i - s);
            long.TryParse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv);
            // LSL integers are 32 bit and wrap.
            return new LslToken(LslTokenKind.Integer, hex, line, (int)(uint)hv);
        }

        bool isFloat = false;
        while (i < n && char.IsDigit(src[i])) i++;
        if (i < n && src[i] == '.')
        {
            isFloat = true;
            i++;
            while (i < n && char.IsDigit(src[i])) i++;
        }
        if (i < n && (src[i] == 'e' || src[i] == 'E'))
        {
            int save = i;
            i++;
            if (i < n && (src[i] == '+' || src[i] == '-')) i++;
            if (i < n && char.IsDigit(src[i]))
            {
                isFloat = true;
                while (i < n && char.IsDigit(src[i])) i++;
            }
            else
                i = save;
        }
        string text = src.Substring(s, i - s);
        if (i < n && (src[i] == 'f' || src[i] == 'F') && isFloat)
            i++;
        if (isFloat)
        {
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d);
            return new LslToken(LslTokenKind.Float, text, line, 0, d);
        }
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long v);
        return new LslToken(LslTokenKind.Integer, text, line, (int)v);
    }
}
