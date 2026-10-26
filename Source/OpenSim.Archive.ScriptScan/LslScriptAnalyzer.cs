/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace OpenSim.Archive.ScriptScan;

/// <summary>How serious a call is for a part that is turned into a mesh.</summary>
public enum HitLevel { Note = 0, Warn = 1, Uncertain = 2, Unsafe = 3 }

/// <summary>Which part of the build a call reaches.</summary>
public enum LinkKind
{
    /// <summary>The part the script is in.</summary>
    Self,
    /// <summary>The root part (link number 0 or 1; the server treats both as the root).</summary>
    Root,
    /// <summary>A literal link number of 2 or more, or a negative number that names no part.</summary>
    Literal,
    /// <summary>LINK_SET</summary>
    Set,
    /// <summary>LINK_ALL_OTHERS</summary>
    AllOthers,
    /// <summary>LINK_ALL_CHILDREN</summary>
    AllChildren,
    /// <summary>The link number could not be worked out.</summary>
    Unknown,
    /// <summary>Another object, named by key.</summary>
    OtherObject
}

public readonly struct LinkTarget : IEquatable<LinkTarget>
{
    public LinkKind Kind { get; }
    public int Number { get; }

    public LinkTarget(LinkKind kind, int number = 0)
    {
        Kind = kind;
        Number = number;
    }

    public static readonly LinkTarget Self = new LinkTarget(LinkKind.Self);

    /// <summary>
    /// Link argument rules follow the server: LINK_SET, LINK_ALL_OTHERS, LINK_ALL_CHILDREN and LINK_THIS are the negative
    /// constants; 0 and 1 are the root; a larger number is a part's link number (LSL_Api.cs GetLinkParts, SceneObjectGroup.cs GetLinkNumPart).
    /// </summary>
    public static LinkTarget FromValue(LslValue v)
    {
        if (!v.IsInt)
            return new LinkTarget(LinkKind.Unknown);
        int n = (int)v.Int;
        if (n == ScriptBaseClass.LINK_SET) return new LinkTarget(LinkKind.Set);
        if (n == ScriptBaseClass.LINK_ALL_OTHERS) return new LinkTarget(LinkKind.AllOthers);
        if (n == ScriptBaseClass.LINK_ALL_CHILDREN) return new LinkTarget(LinkKind.AllChildren);
        if (n == ScriptBaseClass.LINK_THIS) return Self;
        if (n <= 1 && n >= 0) return new LinkTarget(LinkKind.Root, n);
        return new LinkTarget(LinkKind.Literal, n);
    }

    public bool Equals(LinkTarget o) => Kind == o.Kind && Number == o.Number;
    public override bool Equals(object obj) => obj is LinkTarget o && Equals(o);
    public override int GetHashCode() => ((int)Kind * 397) ^ Number;

    public override string ToString()
    {
        switch (Kind)
        {
            case LinkKind.Self: return "this part";
            case LinkKind.Root: return "root";
            case LinkKind.Literal: return "link " + Number;
            case LinkKind.Set: return "LINK_SET";
            case LinkKind.AllOthers: return "LINK_ALL_OTHERS";
            case LinkKind.AllChildren: return "LINK_ALL_CHILDREN";
            case LinkKind.OtherObject: return "another object";
            default: return "unknown link";
        }
    }
}

/// <summary>One finding in one script: a call (or a rule inside a call) and what it means for the part it reaches.</summary>
public sealed class ScriptHit
{
    public string Code { get; set; }
    public HitLevel Level { get; set; }
    public string Function { get; set; }
    /// <summary>Line of the first occurrence.</summary>
    public int Line { get; set; }
    /// <summary>How many times the same finding occurs in the script.</summary>
    public int Count { get; set; } = 1;
    public string Detail { get; set; }
    public LinkTarget Target { get; set; }
}

/// <summary>The result of reading one script.</summary>
public sealed class ScriptAnalysis
{
    public List<ScriptHit> Hits { get; } = new List<ScriptHit>();
    public int Lines { get; set; }
    public int CallsSeen { get; set; }
    /// <summary>True when the hit list was cut at the cap; the script is then treated as not fully read.</summary>
    public bool Truncated { get; set; }
    /// <summary>Where the script addresses other parts of its linkset, or depends on the link numbering.</summary>
    public List<LinkUse> LinkUses { get; } = new List<LinkUse>();
}

/// <summary>
/// The rules of the script-safety scan. Each rule cites the server code it comes from (LSL_Api.cs in
/// OpenSim.Region.ScriptEngine.Shared/Api). The scan only reads text, so a script that builds its calls at run time is
/// reported as uncertain, never as safe.
/// </summary>
public static class LslScriptAnalyzer
{
    /// <summary>Most distinct findings kept for one script.</summary>
    public const int MaxHitsPerScript = 2000;

    // Reason codes.
    public const string ShapeSet = "SHAPE_SET";             // PRIM_TYPE (LSL_Api.cs SetPrimParams, case PRIM_TYPE at line 9635)
    public const string SculptSwap = "SCULPT_SWAP";         // PRIM_TYPE + PRIM_TYPE_SCULPT (LSL_Api.cs 10223)
    public const string SliceSet = "SLICE_SET";             // PRIM_SLICE (LSL_Api.cs 10866)
    public const string FlexiSet = "FLEXI_SET";             // PRIM_FLEXIBLE (LSL_Api.cs 10324)
    public const string FaceGt7 = "FACE_GT7";               // a literal face of 8 or more
    public const string FaceVariable = "FACE_VARIABLE";     // note: the face number is not a literal
    public const string FaceIndexed = "FACE_INDEXED";       // note: a literal face below 8
    public const string SidesQuery = "SIDES_QUERY";         // note: llGetNumberOfSides (LSL_Api.cs 5992) / llGetLinkNumberOfSides (5977)
    public const string ParamsUnknown = "PARAMS_UNKNOWN";   // a set-params list that cannot be read
    public const string LinkUnknown = "LINK_UNKNOWN";       // a shape call whose link target cannot be worked out
    public const string Readback = "READBACK";              // llGetPrimitiveParams reading PRIM_TYPE or PRIM_SLICE
    public const string PhysicsCall = "PHYSICS_CALL";       // vehicle calls, STATUS_PHYSICS, PRIM_PHYSICS, PRIM_PHYSICS_SHAPE_TYPE
    public const string ScriptUnscanned = "SCRIPT_UNSCANNED";
    public const string ScriptTruncated = "SCRIPT_TRUNCATED";

    private sealed class FaceFunction
    {
        public int FaceArg;
        public int LinkArg = -1;
    }

    private static readonly Dictionary<string, FaceFunction> s_faceFunctions = new Dictionary<string, FaceFunction>(StringComparer.Ordinal)
    {
        ["llSetTexture"] = new FaceFunction { FaceArg = 1 },
        ["llSetColor"] = new FaceFunction { FaceArg = 1 },
        ["llSetAlpha"] = new FaceFunction { FaceArg = 1 },
        ["llScaleTexture"] = new FaceFunction { FaceArg = 2 },
        ["llOffsetTexture"] = new FaceFunction { FaceArg = 2 },
        ["llRotateTexture"] = new FaceFunction { FaceArg = 1 },
        ["llGetTexture"] = new FaceFunction { FaceArg = 0 },
        ["llGetColor"] = new FaceFunction { FaceArg = 0 },
        ["llGetAlpha"] = new FaceFunction { FaceArg = 0 },
        ["llGetTextureOffset"] = new FaceFunction { FaceArg = 0 },
        ["llGetTextureScale"] = new FaceFunction { FaceArg = 0 },
        ["llGetTextureRot"] = new FaceFunction { FaceArg = 0 },
        ["llSetTextureAnim"] = new FaceFunction { FaceArg = 1 },
        ["llSetRenderMaterial"] = new FaceFunction { FaceArg = 1 },
        ["llSetPrimMediaParams"] = new FaceFunction { FaceArg = 0 },
        ["llSetLinkTexture"] = new FaceFunction { LinkArg = 0, FaceArg = 2 },
        ["llSetLinkColor"] = new FaceFunction { LinkArg = 0, FaceArg = 2 },
        ["llSetLinkAlpha"] = new FaceFunction { LinkArg = 0, FaceArg = 2 },
        ["llSetLinkTextureAnim"] = new FaceFunction { LinkArg = 0, FaceArg = 2 },
        ["llSetLinkMedia"] = new FaceFunction { LinkArg = 0, FaceArg = 1 },
        ["osGetLinkColor"] = new FaceFunction { LinkArg = 0, FaceArg = 1 },
        ["osSetDynamicTextureDataFace"] = new FaceFunction { FaceArg = 5 },
        ["osSetDynamicTextureDataBlendFace"] = new FaceFunction { FaceArg = 8 },
        ["osSetDynamicTextureURLBlendFace"] = new FaceFunction { FaceArg = 8 },
    };

    private static readonly HashSet<string> s_physicsCalls = new HashSet<string>(StringComparer.Ordinal)
    {
        "llSetVehicleType", "llSetVehicleFloatParam", "llSetVehicleVectorParam", "llSetVehicleRotationParam",
        "llSetVehicleFlags", "llRemoveVehicleFlags"
    };

    private static readonly HashSet<string> s_watched = BuildWatched();

    private static HashSet<string> BuildWatched()
    {
        HashSet<string> w = new HashSet<string>(s_faceFunctions.Keys, StringComparer.Ordinal);
        w.UnionWith(s_physicsCalls);
        foreach (string f in new[]
        {
            "llSetPrimitiveParams", "llSetLinkPrimitiveParams", "llSetLinkPrimitiveParamsFast", "osSetPrimitiveParams",
            "llGetPrimitiveParams", "llGetLinkPrimitiveParams", "osGetLinkPrimitiveParams",
            "llGetNumberOfSides", "llGetLinkNumberOfSides", "llSetStatus"
        })
            w.Add(f);
        return w;
    }

    // Number of values each set-params rule takes after its rule code (LSL_Api.cs SetPrimParams, 9548-11230). PRIM_TYPE is handled apart.
    private static readonly Dictionary<int, int> s_ruleArity = new Dictionary<int, int>
    {
        [ScriptBaseClass.PRIM_MATERIAL] = 1,
        [ScriptBaseClass.PRIM_PHYSICS] = 1,
        [ScriptBaseClass.PRIM_TEMP_ON_REZ] = 1,
        [ScriptBaseClass.PRIM_PHANTOM] = 1,
        [ScriptBaseClass.PRIM_POSITION] = 1,
        [ScriptBaseClass.PRIM_SIZE] = 1,
        [ScriptBaseClass.PRIM_ROTATION] = 1,
        [ScriptBaseClass.PRIM_TEXTURE] = 5,
        [ScriptBaseClass.PRIM_COLOR] = 3,
        [ScriptBaseClass.PRIM_BUMP_SHINY] = 3,
        [ScriptBaseClass.PRIM_FULLBRIGHT] = 2,
        [ScriptBaseClass.PRIM_FLEXIBLE] = 7,
        [ScriptBaseClass.PRIM_TEXGEN] = 2,
        [ScriptBaseClass.PRIM_POINT_LIGHT] = 5,
        [ScriptBaseClass.PRIM_GLOW] = 2,
        [ScriptBaseClass.PRIM_TEXT] = 3,
        [ScriptBaseClass.PRIM_NAME] = 1,
        [ScriptBaseClass.PRIM_DESC] = 1,
        [ScriptBaseClass.PRIM_ROT_LOCAL] = 1,
        [ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE] = 1,
        [ScriptBaseClass.PRIM_PHYSICS_MATERIAL] = 5,
        [ScriptBaseClass.PRIM_OMEGA] = 3,
        [ScriptBaseClass.PRIM_POS_LOCAL] = 1,
        [ScriptBaseClass.PRIM_LINK_TARGET] = 1,
        [ScriptBaseClass.PRIM_SLICE] = 1,
        [ScriptBaseClass.PRIM_SPECULAR] = 8,
        [ScriptBaseClass.PRIM_NORMAL] = 5,
        [ScriptBaseClass.PRIM_ALPHA_MODE] = 3,
        [ScriptBaseClass.PRIM_ALLOW_UNSIT] = 1,
        [ScriptBaseClass.PRIM_SCRIPTED_SIT_ONLY] = 1,
        [ScriptBaseClass.PRIM_SIT_TARGET] = 3,
        [ScriptBaseClass.PRIM_PROJECTOR] = 4,
        [ScriptBaseClass.PRIM_REFLECTION_PROBE] = 4,
    };

    // Values after PRIM_TYPE and its type code (LSL_Api.cs 9662-10243).
    private static int TypeArity(int primType)
    {
        switch (primType)
        {
            case ScriptBaseClass.PRIM_TYPE_BOX:
            case ScriptBaseClass.PRIM_TYPE_CYLINDER:
            case ScriptBaseClass.PRIM_TYPE_PRISM: return 6;
            case ScriptBaseClass.PRIM_TYPE_SPHERE: return 5;
            case ScriptBaseClass.PRIM_TYPE_TORUS:
            case ScriptBaseClass.PRIM_TYPE_TUBE:
            case ScriptBaseClass.PRIM_TYPE_RING: return 11;
            case ScriptBaseClass.PRIM_TYPE_SCULPT: return 2;
            default: return -1;
        }
    }

    private static bool IsFaceRule(int code)
    {
        return code == ScriptBaseClass.PRIM_TEXTURE || code == ScriptBaseClass.PRIM_COLOR || code == ScriptBaseClass.PRIM_BUMP_SHINY
            || code == ScriptBaseClass.PRIM_FULLBRIGHT || code == ScriptBaseClass.PRIM_TEXGEN || code == ScriptBaseClass.PRIM_GLOW
            || code == ScriptBaseClass.PRIM_NORMAL || code == ScriptBaseClass.PRIM_SPECULAR || code == ScriptBaseClass.PRIM_ALPHA_MODE;
    }

    public static ScriptAnalysis Analyze(string source)
    {
        List<LslToken> tokens = LslLexer.Tokenize(source);
        ScriptAnalysis result = new ScriptAnalysis { Lines = tokens.Count == 0 ? 0 : tokens[tokens.Count - 1].Line };
        Collector collector = new Collector(result);

        LslListTracker lists = new LslListTracker(tokens);

        foreach (LslCall call in LslCallFinder.Find(tokens, s_watched, lists))
        {
            result.CallsSeen++;
            Evaluate(call, collector);
        }
        return result;
    }

    private sealed class Collector
    {
        private readonly ScriptAnalysis m_a;
        private readonly Dictionary<string, ScriptHit> m_index = new Dictionary<string, ScriptHit>(StringComparer.Ordinal);

        /// <summary>When set, a finding that is already recorded is not counted again (the same piece read under another link target).</summary>
        public bool Repeat { get; set; }

        public Collector(ScriptAnalysis a) { m_a = a; }

        public void Add(string code, HitLevel level, LslCall call, LinkTarget target, string detail)
        {
            string key = code + "|" + call.Function + "|" + (int)target.Kind + "|" + target.Number + "|" + detail;
            if (m_index.TryGetValue(key, out ScriptHit h))
            {
                if (!Repeat)
                    h.Count++;
                return;
            }
            if (m_a.Hits.Count >= MaxHitsPerScript)
            {
                m_a.Truncated = true;
                return;
            }
            h = new ScriptHit { Code = code, Level = level, Function = call.Function, Line = call.Line, Detail = detail, Target = target };
            m_index[key] = h;
            m_a.Hits.Add(h);
        }
    }

    private static void Evaluate(LslCall call, Collector c)
    {
        string f = call.Function;
        List<LslValue> a = call.Args;

        switch (f)
        {
            case "llSetPrimitiveParams":
                SetParams(call, c, LinkTarget.Self, a != null && a.Count > 0 ? a[0] : LslValue.Unknown);
                return;
            case "llSetLinkPrimitiveParams":
            case "llSetLinkPrimitiveParamsFast":
                if (a == null || a.Count < 2)
                    SetParams(call, c, new LinkTarget(LinkKind.Unknown), LslValue.Unknown);
                else
                    SetParams(call, c, LinkTarget.FromValue(a[0]), a[1]);
                return;
            case "osSetPrimitiveParams":
                SetParams(call, c, new LinkTarget(LinkKind.OtherObject), a != null && a.Count > 1 ? a[1] : LslValue.Unknown);
                return;
            case "llGetPrimitiveParams":
                GetParams(call, c, LinkTarget.Self, a != null && a.Count > 0 ? a[0] : LslValue.Unknown);
                return;
            case "llGetLinkPrimitiveParams":
            case "osGetLinkPrimitiveParams":
                if (a != null && a.Count > 1)
                    GetParams(call, c, LinkTarget.FromValue(a[0]), a[1]);
                return;
            case "llGetNumberOfSides":
                c.Add(SidesQuery, HitLevel.Note, call, LinkTarget.Self, null);
                return;
            case "llGetLinkNumberOfSides":
                c.Add(SidesQuery, HitLevel.Note, call, a != null && a.Count > 0 ? LinkTarget.FromValue(a[0]) : new LinkTarget(LinkKind.Unknown), null);
                return;
            case "llSetStatus":
                if (a == null || a.Count == 0 || !a[0].IsInt || (a[0].Int & ScriptBaseClass.STATUS_PHYSICS) != 0)
                    c.Add(PhysicsCall, HitLevel.Warn, call, LinkTarget.Self, "STATUS_PHYSICS");
                return;
        }

        if (s_physicsCalls.Contains(f))
        {
            c.Add(PhysicsCall, HitLevel.Warn, call, LinkTarget.Self, "vehicle");
            return;
        }

        if (s_faceFunctions.TryGetValue(f, out FaceFunction ff))
        {
            LinkTarget target = LinkTarget.Self;
            if (ff.LinkArg >= 0)
                target = a != null && a.Count > ff.LinkArg ? LinkTarget.FromValue(a[ff.LinkArg]) : new LinkTarget(LinkKind.Unknown);
            Face(call, c, target, a != null && a.Count > ff.FaceArg ? a[ff.FaceArg] : LslValue.Unknown);
        }
    }

    private static void Face(LslCall call, Collector c, LinkTarget target, LslValue face)
    {
        if (face.IsInt && face.Int >= 8)
            c.Add(FaceGt7, HitLevel.Unsafe, call, target, "face " + face.Int);
        else if (face.IsInt)
            c.Add(FaceIndexed, HitLevel.Note, call, target, null);
        else
            c.Add(FaceVariable, HitLevel.Note, call, target, null);
    }

    /// <summary>A rule that changes the shape of its target; the scanner downgrades it to uncertain when the target is not known.</summary>
    private static void Shape(LslCall call, Collector c, string code, LinkTarget target, string detail)
    {
        c.Add(code, HitLevel.Unsafe, call, target, detail);
    }

    private static void SetParams(LslCall call, Collector c, LinkTarget target, LslValue rules)
    {
        if (rules.Kind != LslValueKind.List)
        {
            string why = call.Context == "link_message" ? "in link_message" : null;
            if (rules.Reason != null)
                why = why == null ? rules.Reason : why + "; " + rules.Reason;
            c.Add(ParamsUnknown, HitLevel.Uncertain, call, target, why);
            return;
        }

        if (rules.Segments == null)
        {
            ReadRules(call, c, target, rules.Items, false, out _);
            return;
        }

        // A list kept in a variable: each piece the script adds to it is read on its own and must end between rules. Any piece
        // may follow any other (branches, loops), so each is read under every link target the call or a piece can leave in force.
        HashSet<LinkTarget> targets = new HashSet<LinkTarget> { target };
        Collector scratch = new Collector(new ScriptAnalysis());
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (IReadOnlyList<LslValue> piece in rules.Segments)
                foreach (LinkTarget t in targets.ToList())
                    if (ReadRules(call, scratch, t, piece, true, out LinkTarget end) && targets.Add(end))
                        grew = true;
        }

        foreach (IReadOnlyList<LslValue> piece in rules.Segments)
        {
            c.Repeat = false;
            foreach (LinkTarget t in targets)
            {
                if (!ReadRules(call, c, t, piece, true, out _))
                    break;
                c.Repeat = true;
            }
        }
        c.Repeat = false;
    }

    /// <summary>
    /// Reads one run of rules. Returns false when it stops at a finding it cannot classify. With <paramref name="whole"/>,
    /// a run whose last rule is cut short is also refused (a followed list is built in pieces, each of which must be complete).
    /// </summary>
    private static bool ReadRules(LslCall call, Collector c, LinkTarget target, IReadOnlyList<LslValue> r, bool whole, out LinkTarget end)
    {
        int i = 0;
        end = target;
        while (i < r.Count)
        {
            LslValue codeValue = r[i];
            if (!codeValue.IsInt)
            {
                c.Add(ParamsUnknown, HitLevel.Uncertain, call, target, "rule code at position " + i);
                end = new LinkTarget(LinkKind.Unknown);
                return false;
            }
            int code = (int)codeValue.Int;
            i++;

            if (code == ScriptBaseClass.PRIM_TYPE)
            {
                LslValue t = i < r.Count ? r[i] : LslValue.Unknown;
                if (t.IsInt && t.Int == ScriptBaseClass.PRIM_TYPE_SCULPT)
                    Shape(call, c, SculptSwap, target, null);
                else
                    Shape(call, c, ShapeSet, target, null);
                int arity = t.IsInt ? TypeArity((int)t.Int) : -1;
                if (arity < 0)
                {
                    c.Add(ParamsUnknown, HitLevel.Uncertain, call, target, "shape type not a known constant");
                    end = new LinkTarget(LinkKind.Unknown);
                    return false;
                }
                i += 1 + arity;
                if (whole && i > r.Count)
                    return CutShort(call, c, target, out end);
                continue;
            }

            if (!s_ruleArity.TryGetValue(code, out int n))
            {
                c.Add(ParamsUnknown, HitLevel.Uncertain, call, target, "rule " + code + " not in the table");
                end = new LinkTarget(LinkKind.Unknown);
                return false;
            }

            if (code == ScriptBaseClass.PRIM_SLICE)
                Shape(call, c, SliceSet, target, null);
            else if (code == ScriptBaseClass.PRIM_FLEXIBLE)
                Shape(call, c, FlexiSet, target, null);
            else if (code == ScriptBaseClass.PRIM_PHYSICS || code == ScriptBaseClass.PRIM_PHYSICS_SHAPE_TYPE)
                c.Add(PhysicsCall, HitLevel.Warn, call, target, code == ScriptBaseClass.PRIM_PHYSICS ? "PRIM_PHYSICS" : "PRIM_PHYSICS_SHAPE_TYPE");
            else if (IsFaceRule(code))
                Face(call, c, target, i < r.Count ? r[i] : LslValue.Unknown);
            else if (code == ScriptBaseClass.PRIM_LINK_TARGET)
                target = i < r.Count ? LinkTarget.FromValue(r[i]) : new LinkTarget(LinkKind.Unknown);

            i += n;
            if (whole && i > r.Count)
                return CutShort(call, c, target, out end);
        }
        end = target;
        return true;
    }

    private static bool CutShort(LslCall call, Collector c, LinkTarget target, out LinkTarget end)
    {
        c.Add(ParamsUnknown, HitLevel.Uncertain, call, target, "a piece of a list variable ends inside a rule");
        end = new LinkTarget(LinkKind.Unknown);
        return false;
    }

    private static void GetParams(LslCall call, Collector c, LinkTarget target, LslValue rules)
    {
        if (rules.Kind != LslValueKind.List)
            return;
        // A read-back list holds rule codes and their face numbers; a 9 or 35 may be a face, which only costs a warning.
        foreach (LslValue v in rules.Items)
            if (v.IsInt && (v.Int == ScriptBaseClass.PRIM_TYPE || v.Int == ScriptBaseClass.PRIM_SLICE))
            {
                c.Add(Readback, HitLevel.Warn, call, target, v.Int == ScriptBaseClass.PRIM_TYPE ? "PRIM_TYPE" : "PRIM_SLICE");
                return;
            }
    }
}
