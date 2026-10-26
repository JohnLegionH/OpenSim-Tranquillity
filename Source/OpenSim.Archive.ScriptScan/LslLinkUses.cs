/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

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
        "llBreakLink", "llSetLinkSitFlags", "llGetLinkSitFlags",
        "llAvatarOnLinkSitTarget", "llIsLinkGLTFMaterial",
        "osGetLinkSitActiveRange", "osSetLinkSitActiveRange", "osGetLinkStandTarget", "osSetLinkStandTarget", "osSetProjectionParams",
        "osGetLinkInventoryItemKey", "osGetLinkInventoryKey", "osGetLinkInventoryItemKeys", "osGetLinkInventoryKeys", "osGetLinkInventoryNames",
        "osGetLinkInventoryDesc", "osGetLinkInventoryName", "osGiveLinkInventory", "osGiveLinkInventoryList", "osRemoveLinkInventory",
        "osForceBreakLink", "osLinkParticleSystem", "osAdjustSoundVolume", "osLoopSound", "osLoopSoundMaster", "osLoopSoundSlave",
        "osPlaySound", "osPlaySoundSlave", "osPreloadSound", "osSetSoundRadius", "osStopSound", "osTriggerSound", "osTriggerSoundLimited"
    };

    // Functions whose link number is a later argument (the index of the argument).
    private static readonly Dictionary<string, int> s_linkArgLater = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["llSitOnLink"] = 1, ["llGetObjectLinkKey"] = 1, ["osNpcTouch"] = 2
    };

    // Functions that take a list of primitive rules (the index of the list argument). PRIM_LINK_TARGET in such a list switches the part the
    // rules after it apply to, so the list can address parts the call's own link argument does not name.
    private static readonly Dictionary<string, int> s_ruleLists = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["llSetPrimitiveParams"] = 0, ["llGetPrimitiveParams"] = 0, ["llSetLinkPrimitiveParams"] = 1, ["llSetLinkPrimitiveParamsFast"] = 1,
        ["llGetLinkPrimitiveParams"] = 1, ["osGetLinkPrimitiveParams"] = 1
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
        watched.UnionWith(s_linkArgLater.Keys);
        watched.UnionWith(s_ruleLists.Keys);
        LslListTracker lists = new LslListTracker(tokens);

        List<LinkUse> uses = new List<LinkUse>();
        foreach (LslCall call in LslCallFinder.Find(tokens, watched, lists))
        {
            if (s_enumerations.Contains(call.Function))
            {
                uses.Add(new LinkUse { Kind = LinkUseKind.Enumeration, Function = call.Function, Line = call.Line });
                continue;
            }
            if (s_linkArgFirst.Contains(call.Function) || call.Function == MessageLinked || s_linkArgLater.ContainsKey(call.Function))
            {
                int at = s_linkArgLater.TryGetValue(call.Function, out int later) ? later : 0;
                uses.Add(UseOf(call, call.Args != null && call.Args.Count > at ? LinkTarget.FromValue(call.Args[at]) : new LinkTarget(LinkKind.Unknown)));
            }
            if (s_ruleLists.TryGetValue(call.Function, out int listAt))
                FindListTargets(call, listAt, uses);
        }
        FindSenderReads(tokens, uses);
        uses.Sort((a, b) => a.Line.CompareTo(b.Line));
        return uses;
    }

    private static LinkUse UseOf(LslCall call, LinkTarget t)
    {
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
        return use;
    }

    /// <summary>
    /// PRIM_LINK_TARGET (34) followed by a link number in a list of primitive rules. A list the scan cannot read could hold one, so it counts
    /// as a link argument that cannot be worked out; so does a PRIM_LINK_TARGET with no readable number after it. The value 34 anywhere in a
    /// readable list counts too, because the scan does not follow the rule that comes before it (a false find only keeps a part).
    /// </summary>
    private static void FindListTargets(LslCall call, int listAt, List<LinkUse> uses)
    {
        if (call.Args == null || call.Args.Count <= listAt || call.Args[listAt].Kind != LslValueKind.List)
        {
            uses.Add(UseOf(call, new LinkTarget(LinkKind.Unknown)));
            return;
        }
        IReadOnlyList<LslValue> items = call.Args[listAt].Items;
        for (int i = 0; i < items.Count; i++)
            if (items[i].IsInt && items[i].Int == ScriptBaseClass.PRIM_LINK_TARGET)
                uses.Add(UseOf(call, i + 1 < items.Count ? LinkTarget.FromValue(items[i + 1]) : new LinkTarget(LinkKind.Unknown)));
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
