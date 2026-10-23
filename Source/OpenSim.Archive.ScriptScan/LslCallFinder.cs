/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ScriptScan;

/// <summary>One call of a function the scan cares about.</summary>
public sealed class LslCall
{
    public string Function { get; set; }
    public int Line { get; set; }
    /// <summary>The argument values in order, or null when the argument text could not be read.</summary>
    public List<LslValue> Args { get; set; }
    /// <summary>The event handler or user function the call is in (for example "link_message"), or null at top level.</summary>
    public string Context { get; set; }
}

/// <summary>Finds every call of a set of functions in a token list and evaluates its arguments where it can.</summary>
public static class LslCallFinder
{
    public static List<LslCall> Find(IReadOnlyList<LslToken> tokens, ISet<string> watched)
    {
        List<LslCall> calls = new List<LslCall>();
        Stack<string> contexts = new Stack<string>();
        Stack<int> parens = new Stack<int>();
        int lastClosedOpen = -1;
        int lastClosed = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            LslToken t = tokens[i];
            if (t.Kind == LslTokenKind.Punct)
            {
                if (t.Text == "(") parens.Push(i);
                else if (t.Text == ")" && parens.Count > 0)
                {
                    lastClosedOpen = parens.Pop();
                    lastClosed = i;
                }
                else if (t.Text == "{")
                {
                    // A block that follows "name(...)" at the top of a state or a function names the context.
                    string name = contexts.Count > 0 ? contexts.Peek() : null;
                    if (contexts.Count <= 1 && lastClosed == i - 1 && lastClosedOpen > 0 && tokens[lastClosedOpen - 1].Kind == LslTokenKind.Identifier)
                        name = tokens[lastClosedOpen - 1].Text;
                    contexts.Push(name);
                }
                else if (t.Text == "}" && contexts.Count > 0)
                    contexts.Pop();
                continue;
            }

            if (t.Kind == LslTokenKind.Identifier && i + 1 < tokens.Count && tokens[i + 1].Is("(") && watched.Contains(t.Text))
            {
                LslExpressionParser p = new LslExpressionParser(tokens, i + 2);
                List<LslValue> args = p.ParseArguments();
                calls.Add(new LslCall { Function = t.Text, Line = t.Line, Args = args, Context = contexts.Count > 0 ? contexts.Peek() : null });
                // Carry on from the next token so a watched call inside an argument is found too.
            }
        }
        return calls;
    }
}
