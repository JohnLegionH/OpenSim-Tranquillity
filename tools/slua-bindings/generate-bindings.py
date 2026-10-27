#!/usr/bin/env python3
# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Writes the ll function bindings of the SLua engine: LlBindings.g.cs and LlCoverage.g.cs.

usage: generate-bindings.py <builtins.txt> <ILSLSystemAPI.cs> <LSLSystemAPI.cs> <SyscallShim.cs> <output folder>

Inputs
  builtins.txt      the SLua runtime's function list: the ll functions a script can call and their LSL types
                    (key, list, integer ...), which the C# signatures cannot tell apart (a key is a string).
  ILSLSystemAPI.cs  the interface a binding calls through: one method per script function.
  LSLSystemAPI.cs   the implementation Phlox runs. Each public ll method is read to tell whether it asks for a
                    forced delay (ScriptSleep); the engine takes the delay itself from the call.
  SyscallShim.cs    Phlox's call shims. A shim that runs its call with RunAsync is a long-running call; one that goes
                    through Defer may be handed to the service lane. Both can leave the script's thread.

An ll function is bound when the runtime's list and the interface agree on its name and every type. The others are
listed in LlCoverage.g.cs: missing from the interface, only in the interface, or the signatures differ. Every os, iw and
bot function of the interface is listed apart: the compiler does not know them yet.

Call kinds are read from the Phlox source by pattern, and so is whether a function asks for a delay; the delay itself is not
copied into the table, because the engine takes it from LSLSystemAPI when the call runs. What the patterns cannot settle (a
delay that depends on a setting, a computed delay, several different delays on one function) is listed as unplaced in
LlCoverage.g.cs. The output is deterministic: sorted, no dates, no paths.
"""
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
        if public and name.startswith("ll"):
            delays[(name, tuple(types))] = delay_of(body, names, consts, helpers)
    return delays


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


# What the script receives when a deferred call passes its deadline: the failure argument of the shim's Defer call.
FAILURE = re.compile(r'\bDefer\(self, "(ll\w+)", [^\n]*, (null|"[^"]*"|-?\d+|new LSLList\(\)), (?:true|false)\);')


def read_failures(path):
    return {m.group(1): m.group(2) for m in FAILURE.finditer(read(path))}


# ---- matching ----

def matches(builtin, member):
    ret, params = builtin
    mret, mparams, _ = member
    return mret == LSL_TO_CLR[ret] and mparams == [LSL_TO_CLR[p] for p in params]


def lsl_sig(name, builtin):
    ret, params = builtin
    return "%s %s(%s)" % (ret, name, ", ".join(params))


def main(builtins_path, interface_path, impl_path, shim_path, out_dir):
    builtins = read_builtins(builtins_path)
    members = read_interface(interface_path)
    delays = read_delays(impl_path)
    kinds = read_shim_kinds(shim_path)
    failures = read_failures(shim_path)

    by_name = {}
    for ret, name, params, names in members:
        by_name.setdefault(name, []).append((ret, params, names))

    bound = []            # (name, builtin, member)
    missing = []          # names
    mismatch = []         # (name, text)
    bound_members = set()
    for name in sorted(builtins):
        cands = by_name.get(name)
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

    compiler = sorted("%s %s(%s)" % (ret, name, ", ".join(params))
                      for ret, name, params, names in members if not name.startswith("ll"))

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
    write_bindings(out_dir, rows, failures)
    write_coverage(out_dir, [r[0] for r in rows], missing, seam_only, mismatch, compiler, unplaced)
    counts = {c: sum(1 for r in rows if r[2] == c) for c in FUNCTION_CLASSES}
    print("bound %d (%s); missing from the seam %d; seam only %d; signature mismatch %d; os/iw/bot %d; unplaced %d"
          % (len(rows), ", ".join("%s %d" % (c, counts[c]) for c in FUNCTION_CLASSES), len(missing), len(seam_only),
             len(mismatch), len(compiler), len(unplaced)))


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
        if ret == "void":
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


def write_coverage(out_dir, bound, missing, seam_only, mismatch, compiler, unplaced):
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
    out.append(string_array("NeedsCompilerChange", "The os, iw and bot members of the interface. Not bound: the compiler does not know them yet.", compiler))
    out.append("\n")
    out.append(string_array("Unplaced", "Bound functions whose delay the generator could not read, with the class they were given.", unplaced))
    out.append("    }\n}\n")
    write(out_dir, "LlCoverage.g.cs", "".join(out))


def write(out_dir, name, text):
    with open("%s/%s" % (out_dir, name), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


if __name__ == "__main__":
    if len(sys.argv) != 6:
        sys.exit(__doc__)
    main(*sys.argv[1:])
