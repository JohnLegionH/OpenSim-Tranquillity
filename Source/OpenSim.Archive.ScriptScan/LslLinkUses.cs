/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace OpenSim.Archive.ScriptScan;

/// <summary>How a script reaches, or depends on, the other parts of its linkset.</summary>
public enum LinkUseKind
{
    /// <summary>The part the script is in.</summary>
    Self,
    /// <summary>LINK_ROOT (or 0 or 1): the root part.</summary>
    Root,
    /// <summary>A literal link number of 2 or more.</summary>
    Literal,
    /// <summary>LINK_SET</summary>
    Set,
    /// <summary>LINK_ALL_OTHERS</summary>
    AllOthers,
    /// <summary>LINK_ALL_CHILDREN</summary>
    AllChildren,
    /// <summary>A link argument the scan cannot work out (a variable, a computed value).</summary>
    Unknown,
    /// <summary>The script asks how many parts there are, which part it is, or which part was touched: it depends on the numbering.</summary>
    Enumeration,
    /// <summary>A link_message handler that reads the sender's link number.</summary>
    LinkMessageSender
}

/// <summary>One place a script addresses linkset parts.</summary>
public sealed class LinkUse
{
    public LinkUseKind Kind { get; set; }
    /// <summary>The link number for <see cref="LinkUseKind.Literal"/>.</summary>
    public int Number { get; set; }
    public string Function { get; set; }
    public int Line { get; set; }
    /// <summary>True for llMessageLinked: the message goes to the scripts in the target parts wherever they sit, so LINK_SET and the like address no part by number.</summary>
    public bool MessageOnly { get; set; }
}

/// <summary>
/// Reads every place a script addresses other parts of its linkset by link number, or depends on the link numbering.
/// The rules follow the server's link functions (LSL_Api.cs, the llSetLink*, llGetLink* and llMessageLinked families, and
/// llGetNumberOfPrims, llGetLinkNumber, llDetectedLinkNumber). The scan only reads text: a link argument it cannot work out is
/// reported as <see cref="LinkUseKind.Unknown"/>, never as harmless.
/// </summary>
public static class LslLinkUses
{
    // Functions whose first argument is a link number.
    private static readonly HashSet<string> s_linkArgFirst = new HashSet<string>(StringComparer.Ordinal)
    {
        "llSetLinkPrimitiveParams", "llSetLinkPrimitiveParamsFast", "llGetLinkPrimitiveParams", "osGetLinkPrimitiveParams",
        "llSetLinkColor", "llSetLinkTexture", "llSetLinkAlpha", "llSetLinkTextureAnim", "llSetLinkMedia", "llGetLinkMedia",
        "llClearLinkMedia", "llSetLinkRenderMaterial", "llSetLinkCamera", "osGetLinkColor",
        "llLinkParticleSystem", "llLinkSitTarget", "llGetLinkNumberOfSides", "llGetLinkName", "llGetLinkKey",
        "llLinkPlaySound", "llLinkStopSound", "llLinkAdjustSoundVolume", "llLinkSetSoundQueueing", "llLinkSetSoundRadius",
        "llBreakLink", "llSetLinkSitFlags", "llGetLinkSitFlags"
    };

    // Calls that depend on how many parts there are, or which one the script is in or was touched in.
    private static readonly HashSet<string> s_enumerations = new HashSet<string>(StringComparer.Ordinal)
    {
        "llGetNumberOfPrims", "llGetObjectPrimCount", "llGetLinkNumber", "llDetectedLinkNumber"
    };

    private const string MessageLinked = "llMessageLinked";
    private const string LinkMessageEvent = "link_message";

    public static List<LinkUse> Find(string source)
    {
        List<LslToken> tokens = LslLexer.Tokenize(source);
        HashSet<string> watched = new HashSet<string>(s_linkArgFirst, StringComparer.Ordinal) { MessageLinked };
        watched.UnionWith(s_enumerations);
        LslListTracker lists = new LslListTracker(tokens);

        List<LinkUse> uses = new List<LinkUse>();
        foreach (LslCall call in LslCallFinder.Find(tokens, watched, lists))
        {
            if (s_enumerations.Contains(call.Function))
            {
                uses.Add(new LinkUse { Kind = LinkUseKind.Enumeration, Function = call.Function, Line = call.Line });
                continue;
            }
            LinkTarget t = call.Args != null && call.Args.Count > 0 ? LinkTarget.FromValue(call.Args[0]) : new LinkTarget(LinkKind.Unknown);
            LinkUse use = new LinkUse { Function = call.Function, Line = call.Line, MessageOnly = call.Function == MessageLinked };
            switch (t.Kind)
            {
                case LinkKind.Self: use.Kind = LinkUseKind.Self; break;
                case LinkKind.Root: use.Kind = LinkUseKind.Root; break;
                case LinkKind.Literal: use.Kind = LinkUseKind.Literal; use.Number = t.Number; break;
                case LinkKind.Set: use.Kind = LinkUseKind.Set; break;
                case LinkKind.AllOthers: use.Kind = LinkUseKind.AllOthers; break;
                case LinkKind.AllChildren: use.Kind = LinkUseKind.AllChildren; break;
                default: use.Kind = LinkUseKind.Unknown; break;
            }
            uses.Add(use);
        }
        FindSenderReads(tokens, uses);
        uses.Sort((a, b) => a.Line.CompareTo(b.Line));
        return uses;
    }

    /// <summary>link_message(integer sender, ...) { ... }: reported when the body names the sender parameter.</summary>
    private static void FindSenderReads(List<LslToken> tokens, List<LinkUse> uses)
    {
        for (int i = 0; i + 3 < tokens.Count; i++)
        {
            if (tokens[i].Kind != LslTokenKind.Identifier || tokens[i].Text != LinkMessageEvent || !tokens[i + 1].Is("(")
                || tokens[i + 2].Text != "integer" || tokens[i + 3].Kind != LslTokenKind.Identifier)
                continue;
            string sender = tokens[i + 3].Text;
            int j = i + 4;
            while (j < tokens.Count && !tokens[j].Is("{"))
                j++;
            int depth = 0;
            for (; j < tokens.Count; j++)
            {
                if (tokens[j].Is("{")) depth++;
                else if (tokens[j].Is("}") && --depth == 0) break;
                else if (tokens[j].Kind == LslTokenKind.Identifier && tokens[j].Text == sender)
                {
                    uses.Add(new LinkUse { Kind = LinkUseKind.LinkMessageSender, Function = LinkMessageEvent, Line = tokens[i].Line });
                    break;
                }
            }
        }
    }
}
