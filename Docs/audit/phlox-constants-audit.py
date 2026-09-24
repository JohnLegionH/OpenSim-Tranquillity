"""PHLOX-8. Phlox's LSL constant table against upstream's, by name and by value.

Re-runnable: python Docs/audit/phlox-constants-audit.py  [--json out.json]

  A  name in upstream, absent from Phlox   -> an SL script naming it fails to compile
  B  same name, different value            -> compiles, misbehaves (the dangerous one)
  C  Phlox-only names                       -> InWorldz heritage is fine; ll-looking ones are flagged

Phlox side : Source/InWorldz.Phlox/Compiler/DefaultConstants.cs   {"NAME", new ConstantSymbol("NAME", SymbolTable.T, "value")}
Upstream   : Source/OpenSim.Region.ScriptEngine.Shared/Api/ScriptBase/LSL_Constants.cs
             public const int|double|string NAME = expr;  public static readonly LSLInteger|vector|rotation NAME = ...;
Upstream int expressions may reference other constants (A | B, 1 << n, ~X); they are evaluated in
declaration order with the names seen so far. Vector/rotation aliases (TOUCH_INVALID_VECTOR = ZERO_VECTOR)
resolve the same way.

The Phlox value is what a SCRIPT sees, not the table text: a STRING/KEY ConstValue is emitted as an
`sconst "..."` operand and the assembler unescapes \\n \\r \\t \\" \\\\ (BytecodeGenerator.UnescapeStringChars),
so the table's EOF = "\\n\\n\\n" is three newlines at runtime. The C# literal is decoded first, then that pass.

Where SL sides with Phlox against upstream, the name is listed in SL_SIDES_WITH_PHLOX with its wiki page
and reported separately from B - it is not a Phlox defect.
"""
import io, re, sys, json, collections

ROOT = r"D:\tranq-ais\Source"
PHLOX = ROOT + r"\InWorldz.Phlox\Compiler\DefaultConstants.cs"
UP = ROOT + r"\OpenSim.Region.ScriptEngine.Shared\Api\ScriptBase\LSL_Constants.cs"

# InWorldz / Phlox-native families, by prefix. IWERR_ is InWorldz by name (not in the Halcyon reference
# LSL_Constants.cs either; it came in with the Phlox import, 02cf1370df).
HERITAGE_PREFIXES = ("IW_", "BOT_", "PHLOX_", "IWTIMER", "TRAVELMODE", "IWERR_")

# Phlox-only names that the Halcyon reference LSL_Constants.cs declares (D:\halcyon-reference\OpenSim\Region\
# ScriptEngine\Shared\Api\Runtime\LSL_Constants.cs, word-boundary grep 2026-09-09): heritage without the prefix.
HALCYON_NATIVE = {
    "DATA_ACCOUNT_TYPE",
    "ESTATE_ACCESS_QUERY_ALLOWED_AGENT", "ESTATE_ACCESS_QUERY_ALLOWED_GROUP",
    "ESTATE_ACCESS_QUERY_BANNED_AGENT", "ESTATE_ACCESS_QUERY_CAN_MANAGE",
    "VEHICLE_TYPE_MOTORCYCLE", "VEHICLE_TYPE_SAILBOAT",
    "WIND_SPEED_DEFAULT", "WIND_SPEED_FIXED", "WIND_SPEED_TERRAIN_TURBULENCE",
}

# Phlox-only names that ARE Second Life constants - upstream simply lacks them. Each family checked on
# the wiki page named, 2026-09-09.
SL_HAS_IT = {
    # https://wiki.secondlife.com/wiki/Path_update
    "PU_SLOWDOWN_DISTANCE_REACHED", "PU_GOAL_REACHED", "PU_FAILURE_INVALID_START", "PU_FAILURE_INVALID_GOAL",
    "PU_FAILURE_UNREACHABLE", "PU_FAILURE_TARGET_GONE", "PU_FAILURE_NO_VALID_DESTINATION", "PU_EVADE_HIDDEN",
    "PU_EVADE_SPOTTED", "PU_FAILURE_NO_NAVMESH", "PU_FAILURE_DYNAMIC_PATHFINDING_DISABLED",
    "PU_FAILURE_PARCEL_UNREACHABLE", "PU_FAILURE_OTHER",
    # https://wiki.secondlife.com/wiki/LlSetPrimMediaParams
    "STATUS_OK", "STATUS_MALFORMED_PARAMS", "STATUS_TYPE_MISMATCH", "STATUS_BOUNDS_ERROR", "STATUS_NOT_FOUND",
    "STATUS_NOT_SUPPORTED", "STATUS_INTERNAL_ERROR", "STATUS_WHITELIST_FAILED",
    # https://wiki.secondlife.com/wiki/LlReturnObjectsByOwner
    "OBJECT_RETURN_PARCEL", "OBJECT_RETURN_PARCEL_OWNER", "OBJECT_RETURN_REGION",
    # https://wiki.secondlife.com/wiki/LlDerezObject
    "DEREZ_DIE", "DEREZ_MAKE_TEMP", "DEREZ_TO_INVENTORY",
}

# Same name, different value, and the SL wiki says Phlox is the right one.
SL_SIDES_WITH_PHLOX = {
    # wiki: "integer JSON_APPEND = -1"; upstream declares it `public const string JSON_APPEND = "-1"`.
    "JSON_APPEND": "https://wiki.secondlife.com/wiki/JSON_APPEND",
}

# ---------------------------------------------------------------- Phlox
def phlox_table():
    t = {}
    rx = re.compile(r'new ConstantSymbol\("([A-Za-z_0-9]+)",\s*SymbolTable\.([A-Z]+),\s*"((?:[^"\\]|\\.)*)"\)')
    for m in rx.finditer(io.open(PHLOX, encoding="utf-8").read()):
        name, typ, raw = m.group(1), m.group(2), m.group(3)
        t[name] = (typ, raw)
    return t

def assembler_unescape(txt):
    """BytecodeGenerator.UnescapeStringChars, exactly: \\n \\r \\t(->4 spaces) \\" \\\\; anything else keeps the backslash."""
    out, ix = [], 0
    while ix < len(txt):
        jx = txt.find("\\", ix)
        if jx < 0 or jx == len(txt) - 1:
            out.append(txt[ix:]); break
        out.append(txt[ix:jx])
        c = txt[jx + 1]
        out.append({"n": "\n", "r": "\r", "t": "    ", '"': '"', "\\": "\\"}.get(c, "\\" + c))
        ix = jx + 2
    return "".join(out)

def norm_phlox(typ, raw):
    if typ == "INT":
        r = raw.strip()
        return ("int", int(r, 16) if r.lower().startswith("0x") else int(r))
    if typ == "FLOAT":
        return ("float", round(float(raw.rstrip("fF")), 6))
    if typ in ("STRING", "KEY"):
        return ("string", assembler_unescape(raw.encode("utf-8").decode("unicode_escape")))
    if typ in ("VECTOR", "ROTATION"):
        nums = tuple(round(float(x), 6) for x in raw.strip("<>").split(","))
        return ("vec", nums)
    return (typ.lower(), raw)

# ---------------------------------------------------------------- upstream
def upstream_table():
    t = collections.OrderedDict()
    src = io.open(UP, encoding="utf-8").read()
    rx = re.compile(
        r'public\s+(?:const|static\s+readonly)\s+(int|double|string|LSLInteger|LSLFloat|vector|rotation)\s+'
        r'([A-Za-z_0-9]+)\s*=\s*(.+?);', re.S)
    for m in rx.finditer(src):
        typ, name, expr = m.group(1), m.group(2), m.group(3).strip()
        expr = re.sub(r'//.*', '', expr).strip()
        t[name] = (typ, expr)
    return t

def eval_int(expr, known):
    e = expr
    e = re.sub(r'\bunchecked\s*\(', '(', e)
    e = re.sub(r'\(int\)', '', e)
    e = re.sub(r'\bnew\s+LSLInteger\s*\(', '(', e)
    e = re.sub(r'\bScriptBaseClass\.', '', e)
    e = re.sub(r'([0-9A-Fa-f])[uUlL]+\b', r'\1', e)
    def sub_name(m):
        n = m.group(0)
        if n in known and known[n][0] == "int":
            return str(known[n][1])
        return n
    e = re.sub(r'\b[A-Za-z_][A-Za-z_0-9]*\b', sub_name, e)
    # hex literals are digits, not names
    probe = re.sub(r'0[xX][0-9A-Fa-f]+', '0', e)
    if re.search(r'[A-Za-z_]', probe):
        raise ValueError("unresolved: " + expr)
    return int(eval(e, {"__builtins__": {}}, {}))

def norm_upstream(typ, expr, known):
    if typ in ("int", "LSLInteger"):
        return ("int", eval_int(expr, known))
    if typ in ("double", "LSLFloat"):
        e = re.sub(r'\bnew\s+LSLFloat\s*\(', '(', expr).rstrip("fFdD")
        e = re.sub(r'([0-9.])[fFdD]\b', r'\1', e)
        return ("float", round(float(eval(e, {"__builtins__": {}}, {"Math": None})), 6))
    if typ == "string":
        s = expr.strip()
        if s.startswith('"') and s.endswith('"'):
            return ("string", s[1:-1].encode("utf-8").decode("unicode_escape"))
        raise ValueError("string expr: " + expr)
    if typ in ("vector", "rotation"):
        alias = expr.strip()
        if alias in known and known[alias][0] == "vec":          # TOUCH_INVALID_VECTOR = ZERO_VECTOR
            return known[alias]
        nums = re.findall(r'[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?', expr)
        if not nums:
            raise ValueError("vector expr: " + expr)
        return ("vec", tuple(round(float(x), 6) for x in nums))
    raise ValueError(typ)

# ---------------------------------------------------------------- classify
def main(json_out=None):
    ph = phlox_table()
    up_raw = upstream_table()
    up, unresolved = collections.OrderedDict(), []
    for name, (typ, expr) in up_raw.items():
        try:
            up[name] = norm_upstream(typ, expr, up)
        except Exception as ex:
            unresolved.append((name, expr, str(ex)))

    phn = {n: norm_phlox(t, r) for n, (t, r) in ph.items()}

    A = [n for n in up if n not in phn]
    B, B_sl_phlox = [], []
    for n in up:
        if n in phn and phn[n] != up[n]:
            (B_sl_phlox if n in SL_SIDES_WITH_PHLOX else B).append((n, phn[n], up[n]))
    C_all = [n for n in phn if n not in up]
    C_heritage = [n for n in C_all if n.startswith(HERITAGE_PREFIXES) or n in HALCYON_NATIVE]
    C_sl = [n for n in C_all if n in SL_HAS_IT]
    C_flag = [n for n in C_all if n not in C_heritage and n not in C_sl]

    print(f"Phlox constants   : {len(phn)}")
    print(f"upstream constants: {len(up)}  (+{len(unresolved)} unresolved expressions)")
    for n, e, why in unresolved:
        print(f"   unresolved: {n} = {e}   [{why}]")
    print()
    print(f"A  upstream-only (absent from Phlox): {len(A)}")
    for n in A:
        print(f"   {n} = {up[n][1]!r} ({up[n][0]})")
    print()
    print(f"B  same name, DIFFERENT value: {len(B)}")
    for n, p, u in B:
        print(f"   {n:40s} phlox={p[1]!r}  upstream={u[1]!r}")
    print(f"   ... and {len(B_sl_phlox)} where the SL wiki sides with Phlox (not defects):")
    for n, p, u in B_sl_phlox:
        print(f"   {n:40s} phlox={p[1]!r}  upstream={u[1]!r}   {SL_SIDES_WITH_PHLOX[n]}")
    print()
    print(f"C  Phlox-only: {len(C_all)}  = heritage {len(C_heritage)} + SL-has-it {len(C_sl)} + flagged {len(C_flag)}")
    for n in C_flag:
        print(f"   FLAG {n} = {phn[n][1]!r}")
    print(f"   heritage = prefixes {HERITAGE_PREFIXES} + {len(HALCYON_NATIVE)} Halcyon-native names")

    if json_out:
        io.open(json_out, "w", encoding="utf-8").write(json.dumps({
            "A": {n: up[n] for n in A},
            "B": {n: {"phlox": p, "upstream": u} for n, p, u in B},
            "B_sl_sides_with_phlox": {n: {"phlox": p, "upstream": u} for n, p, u in B_sl_phlox},
            "C_flagged": {n: phn[n] for n in C_flag},
            "C_sl_has_it": C_sl,
            "C_heritage": C_heritage,
            "unresolved": unresolved,
        }, indent=1, default=str))
        print(f"\nwritten {json_out}")

if __name__ == "__main__":
    out = None
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
    main(out)
