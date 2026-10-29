#!/usr/bin/env python3
# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Writes the ll function bindings of the SLua engine: LlBindings.g.cs and LlCoverage.g.cs.

usage: generate-bindings.py <builtins.txt> <ILSLSystemAPI.cs> <LSLSystemAPI.cs> <SyscallShim.cs> <output folder> [<Defaults.cs>]

Inputs
  builtins.txt      the SLua runtime's function list: the ll functions a script can call and their LSL types
                    (key, list, integer ...), which the C# signatures cannot tell apart (a key is a string).
  ILSLSystemAPI.cs  the interface a binding calls through: one method per script function.
  LSLSystemAPI.cs   the implementation Phlox runs. Each public ll method is read to tell whether it asks for a
                    forced delay (ScriptSleep); the engine takes the delay itself from the call.
  SyscallShim.cs    Phlox's call shims. A shim that runs its call with RunAsync is a long-running call; one that goes
                    through Defer may be handed to the service lane. Both can leave the script's thread.

An ll function is bound when the runtime's list and the interface agree on its name and every type. The others are
listed in LlCoverage.g.cs: missing from the interface, only in the interface, or the signatures differ.

With Defaults.cs (Phlox's compiler table) the generator also writes OsBindings.g.cs: every os, iw and bot function of that
table, with the LSL types and parameter names the table has (a key stays a key), one entry per signature and one function per
name. Each signature is matched to the interface method of the same name and types. The call kind, the delay and the host
members a function needs are read from the Phlox source the same way as for the ll functions; the table index of a signature
finds its call shim, so an overload with a shim of its own (osNpcCreate with five arguments) is read from that shim. The
list the library's LSL compiler is given is the same table written as `type name( type parameter, ... )` lines.

Call kinds are read from the Phlox source by pattern, and so is whether a function asks for a delay; the delay itself is not
copied into the table, because the engine takes it from LSLSystemAPI when the call runs. What the patterns cannot settle (a
delay that depends on a setting, a computed delay, several different delays on one function) is listed as unplaced in
LlCoverage.g.cs. The output is deterministic: sorted, no dates, no paths.
"""
import os
import re
import sys

LSL_TO_CLR = {
    "integer": "int", "float": "float", "string": "string", "key": "string",
    "vector": "Vector3", "rotation": "Quaternion", "list": "LSLList", "void": "void",
}
MARSHAL_IN = {
    "integer": "Int", "float": "Float", "string": "String", "key": "Key",
    "vector": "Vector", "rotation": "Rotation", "list": "List",
}
MARSHAL_OUT = {
    "integer": "Of", "float": "Of", "string": "OfString", "key": "OfKey",
    "vector": "Of", "rotation": "Of", "list": "Of",
}
FUNCTION_CLASSES = ("NonBlocking", "Delayed", "Async", "Service", "Unplaced")

BUILTIN = re.compile(r"^(void|integer|float|string|key|vector|rotation|list)\s+(ll\w+)\(\s*(.*?)\s*\)\s*$")
MEMBER = re.compile(r"^\s{8}([A-Za-z0-9_<>\[\]]+)\s+(\w+)\((.*)\);\s*$")
PREFIXES = ("ll", "os", "iw", "bot")
HOST_PREFIXES = ("os", "iw", "bot")
TABLE_OF = {"os": "ossl", "iw": "iw", "bot": "bot"}


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read().replace("\r\n", "\n")


# ---- the runtime's list ----

def read_builtins(path):
    out = {}
    for line in read(path).split("\n"):
        m = BUILTIN.match(line)
        if not m:
            continue
        params = [p.strip().split()[0] for p in m.group(3).split(",")] if m.group(3) else []
        out[m.group(2)] = (m.group(1), params)
    if not out:
        sys.exit("no ll functions found in " + path)
    return out


def read_builtin_parameters(path):
    """name -> (return type, [(type, parameter name)]) for every function in the runtime's list."""
    out = {}
    for line in read(path).split("\n"):
        m = BUILTIN.match(line)
        if not m:
            continue
        params = [tuple(p.strip().split()[:2]) for p in m.group(3).split(",")] if m.group(3) else []
        out[m.group(2)] = (m.group(1), params)
    return out


# ---- the interface ----

def read_interface(path):
    members = []
    for line in read(path).split("\n"):
        m = MEMBER.match(line)
        if not m or not m.group(2).startswith(PREFIXES):
            continue
        params = []
        names = []
        if m.group(3).strip():
            for p in m.group(3).split(","):
                p = p.split("=")[0].strip()
                t, n = p.rsplit(None, 1)
                params.append(t)
                names.append(n)
        members.append((m.group(1), m.group(2), params, names))
    if not members:
        sys.exit("no members found in " + path)
    return members


# ---- delays, from the implementation ----

METHOD = re.compile(
    r"(?m)^[ \t]*(public|private|protected|internal)?[ \t]*(?:static[ \t]+)?(?:virtual[ \t]+|override[ \t]+)?"
    r"([A-Za-z0-9_<>\[\]]+)[ \t]+(\w+)\(([^()]*)\)[ \t\r\n]*(=>|\{)")
SLEEP = re.compile(r"\bScriptSleep\(\s*(.*?)\s*\)\s*;")
EARLY_RETURN = re.compile(r"\bScriptSleep\([^;]*\)\s*;\s*return\b[^;]*;")
KEYWORDS = {"else", "new", "return", "throw", "case", "using", "lock", "if", "while", "for", "foreach", "switch", "catch"}


def blank_literals(text):
    """The text with comments and the insides of string and char literals replaced by spaces, positions kept."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            for k in range(i, j):
                out[k] = " "
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            for k in range(i, j):
                if text[k] != "\n":
                    out[k] = " "
            i = j
        elif c == '"' or c == "'":
            verbatim = c == '"' and i > 0 and text[i - 1] == "@"
            j = i + 1
            while j < n:
                if verbatim:
                    if text[j] == '"':
                        if j + 1 < n and text[j + 1] == '"':
                            j += 2
                            continue
                        break
                else:
                    if text[j] == "\\":
                        j += 2
                        continue
                    if text[j] == c or text[j] == "\n":
                        break
                j += 1
            for k in range(i + 1, min(j, n)):
                if text[k] != "\n":
                    out[k] = " "
            i = j + 1
        else:
            i += 1
    return "".join(out)


def body_end(code, start, expression):
    """The index just past the method body that begins at start (after => or at the opening brace)."""
    depth = 0
    i = start
    while i < len(code):
        c = code[i]
        if expression:
            if c in "({[":
                depth += 1
            elif c in ")}]":
                depth -= 1
            elif c == ";" and depth <= 0:
                return i + 1
        else:
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    return i + 1
        i += 1
    return len(code)


def split_methods(text):
    """Yields (name, parameter types, parameter names, access modifier, is public, body code) for each method."""
    code = blank_literals(text)
    for m in METHOD.finditer(code):
        access, ret, name, raw_params, arrow = m.groups()
        if ret in KEYWORDS or name in KEYWORDS:
            continue
        expression = arrow == "=>"
        start = m.end() if expression else m.end() - 1
        end = body_end(code, start, expression)
        types, names = [], []
        if raw_params.strip():
            for p in raw_params.split(","):
                p = p.split("=")[0].strip()
                if " " not in p:
                    continue
                t, n = p.rsplit(None, 1)
                types.append(t)
                names.append(n)
        yield name, types, names, access, access == "public", code[m.start():end]


def read_constants(text):
    consts = {}
    for m in re.finditer(r"\b(?:const|static readonly)\s+int\s+(\w+)\s*=\s*(-?\d+)\s*;", text):
        consts[m.group(1)] = int(m.group(2))
    return consts


def delay_of(body, param_names, consts, helpers):
    """Returns (delay ms, argument index or -1, reason or None). A reason means the delay is not placed."""
    for h in helpers:
        if re.search(r"\b%s\(" % re.escape(h), body):
            return 0, -1, "its delay is applied inside %s, which depends on a setting or a condition" % h
    early = {m.start() for m in EARLY_RETURN.finditer(body)}
    values = set()
    arg_index = set()
    for m in SLEEP.finditer(body):
        if m.start() in early:  # an error path that sleeps and leaves; the main path's delay is the function's
            continue
        arg = m.group(1)
        if re.fullmatch(r"-?\d+", arg):
            values.add(int(arg))
            continue
        if arg in consts:
            values.add(consts[arg])
            continue
        am = re.fullmatch(r"\(int\)\((\w+)\s*\*\s*1000(?:\.0f?)?\)", arg)
        if am and am.group(1) in param_names:
            arg_index.add(param_names.index(am.group(1)))
            continue
        return 0, -1, "its delay (%s) is not a number the generator can read" % arg
    if arg_index and values:
        return 0, -1, "it asks for a delay from an argument and a fixed delay"
    if len(arg_index) > 1:
        return 0, -1, "it asks for a delay from more than one argument"
    if len(values) > 1:
        return 0, -1, "it asks for different delays on different paths (%s ms)" % ", ".join(str(v) for v in sorted(values))
    if arg_index:
        return 0, arg_index.pop(), None
    if values:
        return values.pop(), -1, None
    return 0, -1, None


def read_delays(path):
    text = read(path)
    consts = read_constants(text)
    methods = list(split_methods(text))
    helpers = sorted({n for n, _, _, access, public, body in methods
                      if access and not public and n != "ScriptSleep" and SLEEP.search(body)})
    delays = {}
    for name, types, names, access, public, body in methods:
        if public and name.startswith(PREFIXES):
            delays[(name, tuple(types))] = delay_of(body, names, consts, helpers)
    return delays


# ---- host members, from the implementation ----

# The members of the engine's host services (IScriptHostServices) that the SLua engine does not provide yet. A script function
# that reaches one, itself or through a method it calls, cannot run on the real LSLSystemAPI with the SLua engine's host.
LACKING_HOST_MEMBERS = (
    "ExperienceGrantRestored", "ExperienceCannotRun",
)
HOST_MEMBER = re.compile(r"m_ScriptEngine\s*\??\s*\.\s*(%s)\b" % "|".join(LACKING_HOST_MEMBERS))
IDENTIFIER = re.compile(r"\b[A-Za-z_]\w*\b")


def read_host_needs(path):
    """Maps each method name of LSLSystemAPI to the lacking host members it reaches, directly or through the methods it calls."""
    methods = {}
    for name, types, names, access, public, body in split_methods(read(path)):
        methods.setdefault(name, []).append(body)
    direct = {n: {m.group(1) for b in bodies for m in HOST_MEMBER.finditer(b)} for n, bodies in methods.items()}
    callees = {n: {t for b in bodies for t in IDENTIFIER.findall(b) if t in methods and t != n} for n, bodies in methods.items()}
    needs = {}

    def reach(name):
        # Everything reachable from the method; a cycle is walked once.
        seen, stack, found = set(), [name], set()
        while stack:
            n = stack.pop()
            if n in seen:
                continue
            seen.add(n)
            found |= direct[n]
            stack.extend(callees[n])
        return found

    for n in methods:
        needs[n] = reach(n)
    return needs


# ---- call kinds, from the shims ----

def read_shim_kinds(path):
    text = read(path)
    kinds = {}
    parts = re.split(r"\n(?=\s*static private void Shim_)", text)
    for part in parts:
        m = re.match(r"\s*static private void Shim_(ll\w+)\(", part)
        if not m:
            continue
        body = part.split("\n        }\n")[0]
        if re.search(r"\bRunAsync\(self", body):
            kinds[m.group(1)] = "Async"
        elif re.search(r"\bDefer\(self", body):
            kinds[m.group(1)] = "Service"
    return kinds


def read_shim_table(path):
    """Table index -> the name of the shim that serves it, from the shim array (`Shim_osTeleportAgent, //672`)."""
    return {int(m.group(2)): m.group(1) for m in re.finditer(r"\bShim_(\w+),\s*//\s*(\d+)", read(path))}


DEFER_ANY = re.compile(r'\bDefer\(self, "((?:ll|os|iw|bot)\w+)", [^\n]*, (null|"[^"]*"|-?\d+|new LSLList\(\)), (?:true|false)\);')


def read_shim_calls(path):
    """Shim name -> ("Async", None, None) or ("Service", the name the shim defers under, its failure value), for os, iw and bot."""
    text = read(path)
    calls = {}
    for part in re.split(r"\n(?=\s*static private void Shim_)", text):
        m = re.match(r"\s*static private void Shim_((?:os|iw|bot)\w*)\(", part)
        if not m:
            continue
        body = part.split("\n        }\n")[0]
        if re.search(r"\bRunAsync\(self", body):
            calls[m.group(1)] = ("Async", None, None)
        else:
            d = DEFER_ANY.search(body)
            if d:
                calls[m.group(1)] = ("Service", d.group(1), d.group(2))
            elif re.search(r"\bDefer\(self", body):
                sys.exit("the shim of %s defers its call in a form the generator does not read" % m.group(1))
    return calls


# ---- Phlox's compiler table ----

VARTYPE = {"Integer": "integer", "Float": "float", "String": "string", "Key": "key",
           "Vector": "vector", "Rotation": "rotation", "List": "list", "Void": "void"}
SIGNATURE = re.compile(
    r'FunctionName\s*=\s*"((?:os|iw|bot)[A-Z]\w*)",\s*ReturnType\s*=\s*VarType\.(\w+),\s*'
    r'ParamTypes\s*=\s*new VarType\[\]\s*\{([^}]*)\},\s*ParamNames\s*=\s*new string\[\]\s*\{([^}]*)\},\s*TableIndex\s*=\s*(\d+)')


def read_compiler_table(path):
    """[(name, return type, [parameter types], [parameter names], table index)] for every os, iw and bot signature, in table order."""
    out = []
    for m in SIGNATURE.finditer(read(path)):
        types = [VARTYPE[x.strip().replace("VarType.", "")] for x in m.group(3).split(",") if x.strip()]
        names = [x.strip().strip('"') for x in m.group(4).split(",") if x.strip()]
        if len(types) != len(names):
            sys.exit("%s has %d parameter types and %d names" % (m.group(1), len(types), len(names)))
        out.append((m.group(1), VARTYPE[m.group(2)], types, names, int(m.group(5))))
    if not out:
        sys.exit("no os, iw or bot functions found in " + path)
    out.sort(key=lambda s: s[4])
    return out


def os_functions(defaults_path, shim_path, members, delays, host_needs):
    """One row per os, iw and bot signature of Phlox's compiler table, matched to the interface method of the same types."""
    table = read_compiler_table(defaults_path)
    shim_of = read_shim_table(shim_path)
    calls = read_shim_calls(shim_path)
    by_name = {}
    for ret, name, params, names in members:
        if name.startswith(HOST_PREFIXES):
            by_name.setdefault(name, []).append((ret, params))
    rows = []
    seen = set()
    for name, ret, types, names, index in table:
        want = [LSL_TO_CLR[x] for x in types]
        member = next((m for m in by_name.get(name, []) if m[1] == want and m[0] == LSL_TO_CLR[ret]), None)
        sysret = False
        if member is None:
            # Phlox's method is void and gives the script's result with SysReturn (its shim runs it as a long-running call).
            member = next((m for m in by_name.get(name, []) if m[1] == want and m[0] == "void"), None)
            sysret = member is not None and ret != "void"
        if member is None:
            sys.exit("%s (table index %d) has no interface method of the same parameter types and result" % (name, index))
        seen.add((name, tuple(member[1])))
        delay_ms, delay_arg, reason = delays.get((name, tuple(member[1])), (0, -1, "the implementation's method was not found by the generator"))
        shim = shim_of.get(index)
        call = calls.get(shim)
        if call and call[0] == "Async":
            cls, defer, failure = "Async", None, None
        elif call:
            cls, defer, failure = "Service", call[1], call[2]
        elif reason:
            cls, defer, failure = "Unplaced", None, None
        elif delay_ms > 0 or delay_arg >= 0:
            cls, defer, failure = "Delayed", None, None
        else:
            cls, defer, failure = "NonBlocking", None, None
        if sysret and cls != "Async":
            sys.exit("%s returns %s in the compiler table and void in the interface, but its shim is not a long-running call" % (name, ret))
        rows.append({"sysret": sysret, "name": name, "ret": ret, "types": types, "names": names, "index": index, "cls": cls, "failure": failure,
                     "reason": reason if cls == "Unplaced" else None, "needs": sorted(host_needs.get(name, ())),
                     "prefix": next(p for p in HOST_PREFIXES if name.startswith(p))})
    for name, sigs in by_name.items():
        for ret, params in sigs:
            if (name, tuple(params)) not in seen:
                sys.exit("%s(%s) is in the interface but not in Phlox's compiler table" % (name, ", ".join(params)))
    return rows


# What the script receives when a deferred call passes its deadline: the failure argument of the shim's Defer call.
FAILURE = re.compile(r'\bDefer\(self, "(ll\w+)", [^\n]*, (null|"[^"]*"|-?\d+|new LSLList\(\)), (?:true|false)\);')


def read_failures(path):
    return {m.group(1): m.group(2) for m in FAILURE.finditer(read(path))}


# ---- adapters and gaps ----

# Runtime functions whose signature differs from ILSLSystemAPI's but that Phlox implements: they are bound through an adapter
# on this side that converts between the runtime's signature (secondlife/lsl-definitions) and Phlox's.
#   result-by-sysreturn  Phlox's method is void and gives the runtime's result with SysReturn (its shim runs it as a
#                        long-running call); the call is made and the answer is the one the lane records.
#   drop-return          Phlox's method returns a value the runtime's function does not.
#   hand                 the conversion is written by hand in LlAdapters.cs.
ADAPTERS = {
    "llManageEstateAccess": ("hand", "the runtime's ESTATE_ACCESS_* actions (4, 8, 16, 32, 64, 128) are turned into Phlox's numbers (0 to 5; LSLSystemAPI EstateAllowedAgentAdd and the others); the TRUE/FALSE result is Phlox's SysReturn"),
    "llOpenFloater": ("hand", "Phlox's llOpenFloater has the runtime's arguments and no result; the adapter calls it and gives the runtime's success code 0 (lsl-definitions: returns an error code, 0 if successful)"),
    "llRequestAgentData": ("result-by-sysreturn", "the runtime returns the query key (lsl-definitions); Phlox returns it with SysReturn (LSLSystemAPI.ReturnQueryKey)"),
    "llRequestDisplayName": ("result-by-sysreturn", "the runtime returns the query key (lsl-definitions); Phlox returns it with SysReturn (LSLSystemAPI.llRequestUsername)"),
    "llRequestUsername": ("result-by-sysreturn", "the runtime returns the query key (lsl-definitions); Phlox returns it with SysReturn (LSLSystemAPI.llRequestUsername)"),
    "llSetGroundTexture": ("hand", "the runtime takes a list of TERRAIN_* changes (wiki LlSetGroundTexture); each TERRAIN_DETAIL_1 to 4 pair is one Phlox llSetGroundTexture(texture, corner) call, the other changes are skipped, and the result is 0 as the wiki says"),
    "llSetLinkGLTFOverrides": ("drop-return", "the runtime's function returns nothing (lsl-definitions); Phlox's returns an integer the adapter drops"),
}

# Runtime functions Phlox does not implement as the runtime defines them. They answer a script with "not available".
PHLOX_GAPS = {
}

# Runtime functions that Second Life's definitions mark private and deprecated (lsl-definitions: "private: true",
# "deprecated: true", no tooltip, and no page on the SL wiki). Second Life keeps them out of its documentation and its script
# editor; there is no documented behaviour to build, so they are intentionally absent and answer a script with "not available".
def _private(func_id):
    return "private in SL, intentionally absent (lsl-definitions func-id %d: private and deprecated, no description)" % func_id


PRIVATE_IN_SL = {
    "llClearExperience": _private(411),
    "llClearExperiencePermissions": _private(385),
    "llGetExperienceList": _private(410),
    "llSetExperienceKey": _private(386),
}

# ---- matching ----

def matches(builtin, member):
    ret, params = builtin
    mret, mparams, _ = member
    return mret == LSL_TO_CLR[ret] and mparams == [LSL_TO_CLR[p] for p in params]


def lsl_sig(name, builtin):
    ret, params = builtin
    return "%s %s(%s)" % (ret, name, ", ".join(params))


def main(builtins_path, interface_path, impl_path, shim_path, out_dir, defaults_path=None):
    builtins = read_builtins(builtins_path)
    members = read_interface(interface_path)
    delays = read_delays(impl_path)
    kinds = read_shim_kinds(shim_path)
    failures = read_failures(shim_path)
    host_needs = read_host_needs(impl_path)

    by_name = {}
    for ret, name, params, names in members:
        by_name.setdefault(name, []).append((ret, params, names))

    bound = []            # (name, builtin, member)
    missing = []          # names
    mismatch = []         # (name, text)
    bound_members = set()
    for name in sorted(builtins):
        cands = by_name.get(name)
        if name in ADAPTERS:
            if not cands:
                sys.exit("%s has an adapter but the interface has no method of that name" % name)
            bound.append((name, builtins[name], cands[0]))
            bound_members.add((name, tuple(cands[0][1])))
            continue
        if not cands:
            missing.append(name)
            continue
        hit = next((c for c in cands if matches(builtins[name], c)), None)
        if hit is None:
            seam = "; ".join("%s %s(%s)" % (c[0], name, ", ".join(c[1])) for c in cands)
            mismatch.append((name, "runtime: %s; seam: %s" % (lsl_sig(name, builtins[name]), seam)))
            continue
        bound.append((name, builtins[name], hit))
        bound_members.add((name, tuple(hit[1])))

    for name in missing + [n for n, _ in mismatch]:
        if name not in PHLOX_GAPS and name not in PRIVATE_IN_SL:
            sys.exit("%s is neither bound, adapted nor a named Phlox gap" % name)
    for name in list(PHLOX_GAPS) + list(PRIVATE_IN_SL):
        if name not in missing and name not in {n for n, _ in mismatch}:
            sys.exit("%s is listed as a Phlox gap but is bound" % name)
    mismatch_names = {n for n, _ in mismatch}
    seam_only = []
    for ret, name, params, names in members:
        if not name.startswith("ll") or (name, tuple(params)) in bound_members:
            continue
        if name not in builtins:
            reason = "not in the runtime list"
        elif name in mismatch_names:
            reason = "the runtime list's signature differs (see the mismatch list)"
        else:
            reason = "another overload of this name is bound"
        seam_only.append("%s %s(%s): %s" % (ret, name, ", ".join(params), reason))
    seam_only.sort()

    rows = []
    unplaced = []
    for name, builtin, member in bound:
        key = (name, tuple(member[1]))
        if key in delays:
            delay_ms, delay_arg, reason = delays[key]
        else:
            delay_ms, delay_arg, reason = 0, -1, "the implementation's method was not found by the generator"
        kind = kinds.get(name)
        if kind:
            cls = kind
        elif reason:
            cls = "Unplaced"
        elif delay_ms > 0 or delay_arg >= 0:
            cls = "Delayed"
        else:
            cls = "NonBlocking"
        if reason:
            unplaced.append("%s: %s (%s)" % (name, reason, cls))
        rows.append((name, builtin, cls, delay_ms, delay_arg))

    for name, builtin, cls, _, _ in rows:
        if cls == "Service" and name not in failures:
            sys.exit("%s is a service call but the shim has no failure value for it" % name)
    os_rows = os_functions(defaults_path, shim_path, members, delays, host_needs) if defaults_path else None
    if os_rows is not None:
        write_os_bindings(out_dir, os_rows)
    write_bindings(out_dir, rows, failures)
    write_served(out_dir, [r[0] for r in rows], host_needs)
    write_coverage(out_dir, [r[0] for r in rows], missing, seam_only, mismatch, unplaced)
    write_indexes(out_dir, read_builtin_parameters(builtins_path), read_marks(os.path.join(os.path.dirname(builtins_path), "lsl-definition-marks.txt")))
    counts = {c: sum(1 for r in rows if r[2] == c) for c in FUNCTION_CLASSES}
    print("bound %d (%s); missing from the seam %d; seam only %d; signature mismatch %d; unplaced %d"
          % (len(rows), ", ".join("%s %d" % (c, counts[c]) for c in FUNCTION_CLASSES), len(missing), len(seam_only),
             len(mismatch), len(unplaced)))
    if os_rows is not None:
        oc = {c: sum(1 for r in os_rows if r["cls"] == c) for c in FUNCTION_CLASSES}
        print("os/iw/bot: %d signatures, %d names (%s); not served %d"
              % (len(os_rows), len({r["name"] for r in os_rows}), ", ".join("%s %d" % (c, oc[c]) for c in FUNCTION_CLASSES),
                 sum(1 for r in os_rows if r["needs"])))


HEADER = """/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// <auto-generated>
// Written by tools/slua-bindings/generate-bindings.py from the runtime's builtins.txt, ILSLSystemAPI, and the Phlox
// implementation and call shims. Do not edit by hand.
// </auto-generated>
"""


def esc(s):
    return s.replace("\\", "\\\\").replace('"', '\\"')


def write_bindings(out_dir, rows, failures):
    out = [HEADER, """
// The long-running and service-lane call kinds below follow Halcyon's script functions as Phlox carries them (SyscallShim
// RunAsync and Defer), and a function is classed as delayed when LSLSystemAPI asks for a ScriptSleep. The table holds no delay
// figures: the delay of a call is whatever LSLSystemAPI asks for when it runs. The function names and types are the runtime's.

using System;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    internal static partial class LlBindings
    {
"""]
    for name, (ret, params), cls, delay_ms, delay_arg in rows:
        short = name[2:]
        args = ", ".join('LslMarshal.%s(a, %d, "%s")' % (MARSHAL_IN[p], i, short) for i, p in enumerate(params))
        call = "c.Api.%s(%s)" % (name, args)
        out.append("        private static Value[] %s(ScriptContext c, ReadOnlySpan<Value> a)\n        {\n" % short)
        out.append('            LslMarshal.Arity(a, %d, "%s");\n' % (len(params), short))
        adapter = ADAPTERS.get(name)
        if adapter and adapter[0] == "hand":
            out.append("            return LlAdapters.%s(c, a);\n" % short)
        elif adapter or ret == "void":
            # result-by-sysreturn: the answer is the one the call records; drop-return: the value is not the runtime's
            out.append("            %s;\n            return null;\n" % call)
        else:
            out.append("            return new[] { LslMarshal.%s(%s) };\n" % (MARSHAL_OUT[ret], call))
        out.append("        }\n\n")
        if cls == "Service":
            # The arguments as the shim passes them to the deferral question (ISyscallDeferralAdvisor.NeedsService).
            out.append("        private static object[] %sArgs(ReadOnlySpan<Value> a)\n        {\n" % short)
            out.append('            LslMarshal.Arity(a, %d, "%s");\n' % (len(params), short))
            out.append("            return new object[] { %s };\n        }\n\n" % args)
    out.append("        internal static readonly BoundFunction[] All = new BoundFunction[]\n        {\n")
    for name, (ret, params), cls, delay_ms, delay_arg in rows:
        short = name[2:]
        types = "new string[0]" if not params else "new[] { %s }" % ", ".join('"%s"' % p for p in params)
        extra = ""
        if cls == "Service":
            extra = ", %s, %sArgs" % (failure_expr(failures[name]), short)
        out.append('            new BoundFunction("%s", "%s", FunctionClass.%s, "%s", %s, %s%s),\n'
                   % (short, name, cls, ret, types, short, extra))
    out.append("        };\n    }\n}\n")
    write(out_dir, "LlBindings.g.cs", "".join(out))


def sig_text(row):
    return "%s %s(%s)" % (row["ret"], row["name"], ", ".join(row["types"]))


def host_line(row):
    """The signature as the library's LSL compiler takes it, with Phlox's parameter names."""
    if not row["types"]:
        return "%s %s()" % (row["ret"], row["name"])
    return "%s %s( %s )" % (row["ret"], row["name"], ", ".join("%s %s" % (t, n) for t, n in zip(row["types"], row["names"])))


def write_os_bindings(out_dir, rows):
    """OsBindings.g.cs: the call methods, the table of functions and the host list, for the os, iw and bot signatures."""
    out = [HEADER, """
// The os, iw and bot functions of Phlox's compiler table, one call method per signature. The long-running and service-lane
// call kinds follow the call shims of Phlox (RunAsync and Defer) and a function is classed as delayed when LSLSystemAPI asks for
// a ScriptSleep, as for the ll functions; the delay of a call is whatever LSLSystemAPI asks for when it runs.

using System;
using SLua.ScriptEngine.Bindings;

namespace SLua.ScriptEngine
{
    internal static partial class OsBindings
    {
"""]
    method = lambda r: "%s_%d" % (r["name"][len(r["prefix"]):], r["index"])
    for r in rows:
        args = ", ".join('LslMarshal.%s(a, %d, "%s")' % (MARSHAL_IN[t], i, r["name"]) for i, t in enumerate(r["types"]))
        call = "c.Api.%s(%s)" % (r["name"], args)
        out.append("        private static Value[] %s(ScriptContext c, ReadOnlySpan<Value> a)\n        {\n" % method(r))
        out.append('            LslMarshal.Arity(a, %d, "%s");\n' % (len(r["types"]), r["name"]))
        if r["ret"] == "void" or r["sysret"]:
            out.append("            %s;\n            return null;\n" % call)
        else:
            out.append("            return new[] { LslMarshal.%s(%s) };\n" % (MARSHAL_OUT[r["ret"]], call))
        out.append("        }\n\n")
        if r["cls"] == "Service":
            out.append("        private static object[] %sArgs(ReadOnlySpan<Value> a)\n        {\n" % method(r))
            out.append('            LslMarshal.Arity(a, %d, "%s");\n' % (len(r["types"]), r["name"]))
            out.append("            return new object[] { %s };\n        }\n\n" % args)
    names = sorted({r["name"] for r in rows})
    out.append("        /// <summary>One function per name, in the table of its prefix; the signatures of a name in the order of Phlox's compiler table.</summary>\n")
    out.append("        internal static readonly OsFunction[] All = new OsFunction[]\n        {\n")
    for name in names:
        sigs = [r for r in rows if r["name"] == name]
        prefix = sigs[0]["prefix"]
        short = name[len(prefix):]
        out.append('            new OsFunction("%s", "%s", "%s", new BoundFunction[]\n            {\n' % (TABLE_OF[prefix], short, name))
        for r in sigs:
            types = "new string[0]" if not r["types"] else "new[] { %s }" % ", ".join('"%s"' % t for t in r["types"])
            extra = ""
            if r["cls"] == "Service":
                extra = ", %s, %sArgs" % (failure_expr(r["failure"]), method(r))
            out.append('                new BoundFunction("%s", "%s", FunctionClass.%s, "%s", %s, %s%s),\n'
                       % (short, name, r["cls"], r["ret"], types, method(r), extra))
        out.append("            }),\n")
    out.append("        };\n\n")
    out.append("        /// <summary>The same signatures as lines for the library's LSL compiler (`type name( type parameter, ... )`).</summary>\n")
    out.append("        internal static readonly string[] HostLines = new string[]\n        {\n")
    for r in rows:
        out.append('            "%s",\n' % esc(host_line(r)))
    out.append("        };\n\n")
    unplaced = ["%s: %s" % (sig_text(r), r["reason"]) for r in rows if r["cls"] == "Unplaced"]
    needs = ["%s: %s" % (sig_text(r), ", ".join(r["needs"])) for r in rows if r["needs"]]
    out.append(string_array("Unplaced", "Signatures whose delay the generator could not read, with the reason; they were given the class Unplaced.", unplaced))
    out.append("\n")
    out.append(string_array("NotServed", "Signatures that reach a host member the engine does not provide, with the members; they answer a script with \"not available\".", needs))
    out.append("    }\n}\n")
    write(out_dir, "OsBindings.g.cs", "".join(out))


def write_served(out_dir, bound, host_needs):
    served = [n for n in bound if not host_needs.get(n)]
    not_served = ["%s: %s" % (n, ", ".join(sorted(host_needs[n]))) for n in bound if host_needs.get(n)]
    out = [HEADER, """
namespace SLua.ScriptEngine
{
    /// <summary>
    /// Which bound functions can run on the real LSLSystemAPI with the SLua engine's host services: those that, with the methods
    /// they call, reach none of the host members the engine does not provide yet. The others are listed with the members they
    /// need and run on the fallback API until the engine provides them.
    /// </summary>
    internal static class LlServed
    {
"""]
    out.append(string_array("Served", "Bound functions that run on the real API.", served))
    out.append("\n")
    out.append(string_array("NotServed", "Bound functions that do not, each with the host members it needs.", not_served))
    out.append("    }\n}\n")
    write(out_dir, "LlServed.g.cs", "".join(out))


def failure_expr(literal):
    if literal == "new LSLList()":
        return "new InWorldz.Phlox.Types.LSLList()"
    return literal


def string_array(name, doc, items):
    lines = ["        /// <summary>%s</summary>\n" % doc,
             "        internal static readonly string[] %s = new string[]\n        {\n" % name]
    for item in items:
        lines.append('            "%s",\n' % esc(item))
    lines.append("        };\n")
    return "".join(lines)


def write_coverage(out_dir, bound, missing, seam_only, mismatch, unplaced):
    adapted = ["%s: %s" % (n, ADAPTERS[n][1]) for n in sorted(ADAPTERS)]
    gaps = ["%s: %s" % (n, PHLOX_GAPS[n]) for n in sorted(PHLOX_GAPS)]
    private = ["%s: %s" % (n, PRIVATE_IN_SL[n]) for n in sorted(PRIVATE_IN_SL)]
    out = [HEADER, """
namespace SLua.ScriptEngine
{
    /// <summary>
    /// Every runtime ll function against ILSLSystemAPI, in groups, as the generator found them. Each runtime function is in
    /// exactly one of Bound, MissingFromSeam and SignatureMismatch; each ll member of the interface is bound or in SeamOnly.
    /// </summary>
    internal static class LlCoverage
    {
"""]
    out.append(string_array("Bound", "The ll functions bound to the interface method of the same name.", bound))
    out.append("\n")
    out.append(string_array("MissingFromSeam", "In the runtime list; ILSLSystemAPI has no method of that name.", missing))
    out.append("\n")
    out.append(string_array("SeamOnly", "ll members of the interface that are not bound, with the reason.", seam_only))
    out.append("\n")
    out.append(string_array("SignatureMismatch", "In both, with different types or parameter counts: the two signatures.", ["%s: %s" % m for m in mismatch]))
    out.append("\n")
    out.append(string_array("Unplaced", "Bound functions whose delay the generator could not read, with the class they were given.", unplaced))
    out.append("\n")
    out.append(string_array("Adapted", "Runtime functions bound through an adapter, each with the source of the conversion.", adapted))
    out.append("\n")
    out.append(string_array("PhloxGaps", "Runtime functions Phlox does not implement as the runtime defines them, each with the reason.", gaps))
    out.append("\n")
    out.append(string_array("PrivateInSl", "Runtime functions that Second Life's definitions mark private: intentionally absent, each with the reason.", private))
    out.append("    }\n}\n")
    write(out_dir, "LlCoverage.g.cs", "".join(out))


# Which arguments and results are positions or truth values, read from Second Life's definitions (secondlife/lsl-definitions,
# index-semantics and bool-semantics), as written into lsl-definition-marks.txt by extract-marks.py. A position is zero-based in
# LSL and counts from 1 for an SLua script; a truth value is an integer in LSL and a boolean for an SLua script. Integer
# parameters whose name suggests a position that the definitions do not mark are listed, not shifted.
# List results the definitions mark as holding positions (index-semantics on a list return) without saying which elements are:
# llFindNotecardTextSync answers a strided [line, column, length, ...] (its tooltip, lsl-definitions func-id 508). Line and
# column are positions in the notecard and count from 1 for an SLua script; the length is a count. This is our reading of the
# definitions, which only say "there are list indices in the return value"; docs/SLUA.md says so too.
LIST_POSITIONS = {
    "llFindNotecardTextSync": (3, [0, 1]),
}

POSITION_LIKE = re.compile(r"^(number|item|line|face|num)$")


def read_marks(path):
    """name -> list of marks ("return:bool", "arg1:index" ...) from the marks file."""
    marks = {}
    for line in read(path).split("\n"):
        if line and not line.startswith("#"):
            fields = line.split("\t")
            marks[fields[0]] = fields[1:]
    if not marks:
        sys.exit("no marks found in " + path)
    return marks


def write_indexes(out_dir, definitions, marks):
    arguments = {}
    returns = []
    not_derived = []
    for name in sorted(definitions):
        ret, params = definitions[name]
        given = marks.get(name, [])
        at = sorted(int(m[3:m.index(":")]) for m in given if m.startswith("arg") and m.endswith(":index"))
        if at:
            arguments[name] = at
        if ret == "integer" and "return:index" in given:
            returns.append(name)
        for i, p in enumerate(params):
            if len(p) == 2 and p[0] == "integer" and POSITION_LIKE.match(p[1]) and i not in at:
                not_derived.append("%s(%s)" % (name, p[1]))
    out = [HEADER, """
using System.Collections.Generic;

namespace SLua.ScriptEngine
{
    /// <summary>Which arguments and results of the ll functions are positions (see tools/slua-bindings/generate-bindings.py).</summary>
    internal static class LlIndexes
    {
        /// <summary>The ll functions with integer parameters that are positions, and the zero-based places of those parameters.</summary>
        internal static readonly IReadOnlyDictionary<string, int[]> Arguments = new Dictionary<string, int[]>
        {
"""]
    for name in sorted(arguments):
        out.append('            ["%s"] = new[] { %s },\n' % (name, ", ".join(str(i) for i in arguments[name])))
    out.append("        };\n\n")
    out.append(string_array("Returns", "The ll functions whose integer result is a position (-1 for no match).", returns))
    out.append("\n")
    out.append(string_array("NotDerived", "Integer parameters whose name suggests a position that the definitions do not mark; they are not shifted.", not_derived))
    out.append("\n        /// <summary>The ll functions whose list result holds positions at fixed places of each stride (see LIST_POSITIONS in tools/slua-bindings/generate-bindings.py).</summary>\n")
    out.append("        internal static readonly IReadOnlyDictionary<string, ListPositions> ListStrides = new Dictionary<string, ListPositions>\n        {\n")
    for name in sorted(LIST_POSITIONS):
        stride, places = LIST_POSITIONS[name]
        if "return:index" not in marks.get(name, []):
            sys.exit("%s is in LIST_POSITIONS but the definitions do not mark its result as positions" % name)
        out.append('            ["%s"] = new ListPositions(%d, new[] { %s }),\n' % (name, stride, ", ".join(str(i) for i in places)))
    out.append("        };\n    }\n\n")
    out.append('    /// <summary>A list result made of strides of <see cref="Stride"/> elements, of which the elements at <see cref="Positions"/> (zero-based, within a stride) are positions.</summary>\n')
    out.append("    internal sealed record ListPositions(int Stride, int[] Positions);\n}\n")
    write(out_dir, "LlIndexes.g.cs", "".join(out))

    booleans = sorted(n for n in definitions if definitions[n][0] == "integer" and "return:bool" in marks.get(n, []))
    lists = sorted(n for n in definitions if definitions[n][0] == "list" and "return:bool" in marks.get(n, []))
    out = [HEADER, """
namespace SLua.ScriptEngine
{
    /// <summary>Which ll results are truth values (see tools/slua-bindings/generate-bindings.py).</summary>
    internal static class LlBooleans
    {
"""]
    out.append(string_array("Returns", "The ll functions whose integer result is a truth value: true or false for an SLua script.", booleans))
    out.append("\n")
    out.append(string_array("ListsNotConverted", "The ll functions whose list result holds truth values at places that depend on the request; left as integers. The open runtime pushes a truth value only for a single result (Luau VM/src/llsl.cpp luaSL_pushboollike); the list conversion, if any, is in the server-side call_lib_func_lua, which is not in the open source (the registration code generated by lsl-definitions generators/lscript.py only passes bool_semantics to it, and lsl_definitions.yaml says of llGetObjectDetails that there may be bool-like entries in the list to convert).", lists))
    out.append("    }\n}\n")
    write(out_dir, "LlBooleans.g.cs", "".join(out))


def write(out_dir, name, text):
    with open("%s/%s" % (out_dir, name), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


if __name__ == "__main__":
    if len(sys.argv) not in (6, 7):
        sys.exit(__doc__)
    main(*sys.argv[1:])
