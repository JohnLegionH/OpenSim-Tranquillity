/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ScriptScan;

/// <summary>
/// Follows list variables through simple building so a parameter list kept in a variable can be read like a list written
/// out in the call. A variable is followed only when every place that sets it is visible and is one of: an initialiser or
/// assignment from a list literal, from another followed list variable, or from a "+" of those. Anything else (a function
/// result such as llListReplaceList, a function or event parameter, an assignment inside an expression) makes the variable
/// unknown, with a reason. Scoping: a list declared in a function or event is local to it, otherwise the global of that name.
///
/// The position of a statement is ignored: a variable is the union of everything assigned to it anywhere, kept as separate
/// pieces. That covers branches and loops without working out which one ran, and it can only add rules to the list, never
/// hide one. The pieces are kept apart so that a piece which stops inside a rule can be refused.
/// </summary>
public sealed class LslListTracker
{
    private const int Global = -1;
    private const int MaxPieces = 500;

    private sealed class Term
    {
        public int Start;
        public int End;
        /// <summary>A declaration without an initialiser: the empty list.</summary>
        public bool Empty;
    }

    private sealed class Var
    {
        public string Name;
        public List<Term> Terms = new List<Term>();
        public string Reason;
    }

    private readonly IReadOnlyList<LslToken> m_t;
    private readonly int[] m_scope;
    private readonly Dictionary<(int, string), Var> m_vars = new Dictionary<(int, string), Var>();
    private readonly HashSet<Var> m_resolving = new HashSet<Var>();

    public LslListTracker(IReadOnlyList<LslToken> tokens)
    {
        m_t = tokens;
        m_scope = new int[tokens.Count];
        ComputeScopes();
        FindDeclarations();
        FindAssignments();
    }

    /// <summary>
    /// The value of the list variable named <paramref name="name"/> as used at token <paramref name="tokenIndex"/>: a list
    /// (kept as its pieces), an unknown with the reason it cannot be followed, or null when the name is not a list variable.
    /// </summary>
    public LslValue Resolve(string name, int tokenIndex)
    {
        Var v = Lookup(name, tokenIndex);
        return v == null ? null : ResolveVar(v);
    }

    private Var Lookup(string name, int tokenIndex)
    {
        if (m_vars.TryGetValue((m_scope[tokenIndex], name), out Var v))
            return v;
        return m_vars.TryGetValue((Global, name), out v) ? v : null;
    }

    // Each token gets the index of the "{" of the function or event body it is in, or -1 outside any body.
    private void ComputeScopes()
    {
        int depth = 0, body = -1, bodyOpenDepth = 0;
        bool inState = false;
        for (int i = 0; i < m_t.Count; i++)
        {
            LslToken t = m_t[i];
            if (t.Is("{"))
            {
                bool afterParen = i > 0 && m_t[i - 1].Is(")");
                if (depth == 0)
                {
                    if (afterParen) { body = i; bodyOpenDepth = 0; }
                    else inState = true;
                }
                else if (depth == 1 && inState && afterParen)
                {
                    body = i;
                    bodyOpenDepth = 1;
                }
                depth++;
            }
            m_scope[i] = body;
            if (t.Is("}") && depth > 0)
            {
                depth--;
                if (body >= 0 && depth == bodyOpenDepth) body = -1;
                if (depth == 0) inState = false;
            }
        }
    }

    private Var Declare(int scope, string name)
    {
        if (!m_vars.TryGetValue((scope, name), out Var v))
            m_vars[(scope, name)] = v = new Var { Name = name };
        return v;
    }

    private void FindDeclarations()
    {
        for (int i = 0; i + 2 < m_t.Count; i++)
        {
            if (!m_t[i].IsIdent("list") || m_t[i + 1].Kind != LslTokenKind.Identifier)
                continue;
            string name = m_t[i + 1].Text;
            LslToken after = m_t[i + 2];

            if (after.Is(")") || after.Is(","))
            {
                // A parameter: the body that follows the closing parenthesis owns it.
                int close = i + 2, depth = 0;
                while (close < m_t.Count && !(m_t[close].Is(")") && depth == 0))
                {
                    if (m_t[close].Is("(")) depth++;
                    else if (m_t[close].Is(")")) depth--;
                    close++;
                }
                if (close + 1 < m_t.Count && m_t[close + 1].Is("{"))
                    Declare(close + 1, name).Reason = "a parameter of the function it is in";
                continue;
            }

            Var v = Declare(m_scope[i], name);
            if (after.Is(";"))
                v.Terms.Add(new Term { Start = i + 2, End = i + 2, Empty = true });
            else if (after.Is("="))
                AddRightHandSide(v, i + 3);
            else
                v.Reason = "declared in a form the scanner does not read";
        }
    }

    private void FindAssignments()
    {
        for (int i = 1; i + 1 < m_t.Count; i++)
        {
            if (m_t[i].Kind != LslTokenKind.Identifier || !(m_t[i + 1].Is("=") || m_t[i + 1].Is("+=")))
                continue;
            if (m_t[i - 1].IsIdent("list"))
                continue;
            Var v = Lookup(m_t[i].Text, i);
            if (v == null)
                continue;

            LslToken prev = m_t[i - 1];
            bool statementStart = prev.Is(";") || prev.Is("{") || prev.Is("}") || prev.Is(")") || prev.IsIdent("else") || prev.IsIdent("do");
            if (!statementStart)
            {
                v.Reason = "set inside an expression";
                continue;
            }
            AddRightHandSide(v, i + 2);
        }
    }

    // Reads the tokens from start to the end of the statement and records each "+" separated term.
    private void AddRightHandSide(Var v, int start)
    {
        int depth = 0, end = start, termStart = start;
        List<Term> terms = new List<Term>();
        bool closed = false;
        for (; end < m_t.Count; end++)
        {
            LslToken t = m_t[end];
            if (t.Is("(") || t.Is("["))
                depth++;
            else if (t.Is(")") || t.Is("]"))
            {
                if (depth == 0) { closed = t.Is(")"); break; }
                depth--;
            }
            else if (depth == 0 && t.Is(";"))
            {
                closed = true;
                break;
            }
            else if (depth == 0 && t.Is(","))
            {
                v.Reason = "set inside an expression";
                return;
            }
            else if (depth == 0 && t.Is("+"))
            {
                terms.Add(new Term { Start = termStart, End = end });
                termStart = end + 1;
            }
        }
        if (!closed)
        {
            v.Reason = "set by a statement that does not end";
            return;
        }
        terms.Add(new Term { Start = termStart, End = end });
        v.Terms.AddRange(terms);
    }

    private LslValue ResolveVar(Var v)
    {
        if (v.Reason != null)
            return LslValue.UnknownBecause("list '" + v.Name + "' is " + v.Reason);
        if (!m_resolving.Add(v))
            return null;
        try
        {
            List<IReadOnlyList<LslValue>> pieces = new List<IReadOnlyList<LslValue>>();
            foreach (Term term in v.Terms)
            {
                if (term.Empty)
                {
                    pieces.Add(new List<LslValue>());
                    continue;
                }
                if (term.Start >= term.End)
                    return LslValue.UnknownBecause("list '" + v.Name + "' is set from an empty expression");

                if (term.End - term.Start == 1 && m_t[term.Start].Kind == LslTokenKind.Identifier)
                {
                    Var other = Lookup(m_t[term.Start].Text, term.Start);
                    if (other != null)
                    {
                        // A variable's own earlier value is made of the other pieces, which are counted anyway.
                        LslValue sub = m_resolving.Contains(other) ? null : ResolveVar(other);
                        if (sub == null)
                            continue;
                        if (sub.IsUnknown)
                            return LslValue.UnknownBecause("list '" + v.Name + "' is set from " + sub.Reason);
                        pieces.AddRange(sub.Segments);
                        continue;
                    }
                }

                LslExpressionParser p = new LslExpressionParser(m_t, term.Start);
                LslValue value = p.ParseExpression();
                if (p.Failed || p.Position != term.End || value.Kind != LslValueKind.List)
                    return LslValue.UnknownBecause("list '" + v.Name + "' is set from " + Describe(term));
                pieces.Add(value.Items);
            }
            if (pieces.Count > MaxPieces)
                return LslValue.UnknownBecause("list '" + v.Name + "' is built in too many places");
            return LslValue.OfSegments(pieces);
        }
        finally
        {
            m_resolving.Remove(v);
        }
    }

    private string Describe(Term term)
    {
        LslToken first = m_t[term.Start];
        if (first.Kind == LslTokenKind.Identifier && term.Start + 1 < term.End && m_t[term.Start + 1].Is("("))
            return "the result of " + first.Text + "()";
        if (term.End - term.Start == 1 && first.Kind == LslTokenKind.Identifier)
            return "'" + first.Text + "', which is not a list variable the scanner follows";
        return "an expression the scanner does not follow";
    }
}
