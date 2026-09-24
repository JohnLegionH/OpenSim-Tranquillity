import io, os, re, collections, json

ROOT = r"D:\tranq-ais\Source"

# ---------------- Phlox side: Defaults.cs
sig_re = re.compile(
    r'\{"(?P<key>[^"]+)",\s*new FunctionSig\s*\{(?P<body>.*?)\}\}',
    re.S)
def phlox():
    s = io.open(ROOT + r"\InWorldz.Phlox\Types\Defaults.cs", encoding="utf-8").read()
    out = {}
    for m in sig_re.finditer(s):
        body = m.group("body")
        name = re.search(r'FunctionName\s*=\s*"([^"]+)"', body)
        ret = re.search(r'ReturnType\s*=\s*VarType\.(\w+)', body)
        pt = re.search(r'ParamTypes\s*=\s*new VarType\[\]\s*\{([^}]*)\}', body)
        types = []
        if pt and pt.group(1).strip():
            types = [t.strip().replace("VarType.", "") for t in pt.group(1).split(",") if t.strip()]
        fn = name.group(1) if name else m.group("key")
        # PHLOX-20: the raw key is a SYMBOL name (name, name__arity, name__arity_types) and several
        # keys share one FUNCTION name - every one of them is a signature Phlox really has. Keying
        # the audit by the key made every overload past the first invisible.
        out.setdefault(fn, []).append({
            "name": fn,
            "ret": ret.group(1) if ret else "?",
            "params": types,
        })
    return out

# ---------------- OSSL / LSL side: public methods on the Api classes
MAP = {
    "void": "Void", "LSL_Integer": "Integer", "int": "Integer",
    "LSL_Float": "Float", "double": "Float", "float": "Float",
    "LSL_String": "String", "string": "String", "LSL_Key": "String",
    "key": "String",
    "LSL_Vector": "Vector", "Vector3": "Vector", "LSL_Types.Vector3": "Vector",
    "LSL_Rotation": "Rotation", "Quaternion": "Rotation", "LSL_Types.Quaternion": "Rotation",
    "LSL_List": "List", "LSL_Types.list": "List", "list": "List",
    "LSL_Types.LSLInteger": "Integer", "LSL_Types.LSLFloat": "Float",
    "LSL_Types.LSLString": "String",
}
def norm(t):
    t = t.strip().replace("LSL_Types.", "")
    return MAP.get(t, MAP.get("LSL_Types." + t, t))

meth_re = re.compile(
    r'^\s*public\s+(?:virtual\s+|override\s+|static\s+)*'
    r'(?P<ret>[\w\.<>\[\]]+)\s+(?P<name>(?:os|ll)\w+)\s*\((?P<args>[^)]*)\)',
    re.M)
def api(path):
    s = io.open(path, encoding="utf-8").read()
    out = collections.defaultdict(list)
    for m in meth_re.finditer(s):
        args = m.group("args").strip()
        types = []
        if args:
            for a in args.split(","):
                a = a.strip()
                if not a:
                    continue
                parts = a.split()
                if len(parts) >= 2:
                    types.append(norm(" ".join(parts[:-1])))
        out[m.group("name")].append({"ret": norm(m.group("ret")), "params": types})
    return out

P = phlox()
O = api(ROOT + r"\OpenSim.Region.ScriptEngine.Shared\Api\OSSL_Api.cs")
L = api(ROOT + r"\OpenSim.Region.ScriptEngine.Shared\Api\LSL_Api.cs")
A = collections.defaultdict(list)
for d in (O, L):
    for k, v in d.items():
        A[k].extend(v)
# de-duplicate identical signatures
for k in A:
    seen, keep = set(), []
    for sig in A[k]:
        t = (sig["ret"], tuple(sig["params"]))
        if t not in seen:
            seen.add(t); keep.append(sig)
    A[k] = keep

missing_fn, extra_overload, arity, types_bad = [], [], [], []
for name, sigs in sorted(A.items()):
    if name not in P:
        missing_fn.append((name, sigs))
        continue
    phlox_sigs = [tuple(x["params"]) for x in P[name]]
    unmatched = [s for s in sigs if tuple(s["params"]) not in phlox_sigs]
    if not unmatched:
        continue
    if len(sigs) > 1:
        extra_overload.append((name, phlox_sigs, [s["params"] for s in unmatched]))
    else:
        s, p0 = sigs[0], phlox_sigs[0]
        (arity if len(s["params"]) != len(p0) else types_bad).append((name, list(p0), s["params"]))

res = {
    "phlox_entries": len(P),
    "api_names": len(A),
    "missing_functions": missing_fn,
    "overloaded_in_api": extra_overload,
    "arity_mismatch": arity,
    "type_mismatch": types_bad,
}
# PHLOX-20: beside this script, not in one session's scratchpad
io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "audit.json"),
        "w", encoding="utf-8").write(json.dumps(res, indent=1))

print("Phlox table entries      :", len(P))
print("API function names found :", len(A))
print()
print("OVERLOADED IN THE API (Phlox can hold only one):", len(extra_overload))
for n, ph, sigs in extra_overload:
    print("  %-28s phlox=%-42s missing=%s" % (
        n, " | ".join(",".join(x) or "()" for x in ph), " | ".join(",".join(s) or "()" for s in sigs)))
print()
print("ARITY MISMATCH (single signature both sides):", len(arity))
for n, ph, ap in arity:
    print("  %-28s phlox=%-42s api=%s" % (n, ",".join(ph) or "()", ",".join(ap) or "()"))
print()
print("TYPE MISMATCH (same arity, different types):", len(types_bad))
for n, ph, ap in types_bad:
    print("  %-28s phlox=%-42s api=%s" % (n, ",".join(ph) or "()", ",".join(ap) or "()"))
print()
print("IN THE API, ABSENT FROM PHLOX:", len(missing_fn))
for n, sigs in missing_fn:
    print("  %-28s %s" % (n, " | ".join(",".join(s["params"]) or "()" for s in sigs)))
