#!/usr/bin/env python3
# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Writes lsl-definition-marks.txt: which arguments and results of the ll functions Second Life's definitions mark as
positions (index-semantics) or truth values (bool-semantics).

usage: extract-marks.py <lsl_definitions.yaml> <commit of the definitions> <output file> [<list marks output file>]

The input is secondlife/lsl-definitions (MIT, Copyright (c) 2025 Linden Research, Inc.; see ThirdPartyLicenses). Each function
is one line, in name order: the name, then tab-separated marks, "return:bool", "return:index", "arg<N>:bool" or "arg<N>:index"
(N counts from 0). Needs PyYAML; the generator reads the file this writes and does not need PyYAML.

With a list marks file the script also writes, for each ll function whose list result is marked bool-semantics, the set of
request tags that result is read with (the function's param-get-semantics, such as PrimParamGet) and for each tag of that set
the layout of the values it returns: "b" for a value-type boolean, "x" for any other value, "?" when the definitions do not say
how many values the tag returns. A tag whose first value is the face argument (access: arg) repeats once per face when the
face is ALL_SIDES. The definitions' schema gives value-type as "the type of the value that follows this constant in the list".
"""
import sys

import yaml


# The definitions give PRIM_TYPE one integer value, but its tooltip says it gets or sets the shape "and its associated
# parameters": the number of values depends on the shape, so the definitions do not say it.
OPEN_ENDED = {"PRIM_TYPE"}


def layout_of(name, constant):
    """(per_face, cells) of the values a request tag returns, or (False, "?") when the definitions do not say."""
    if name in OPEN_ENDED:
        return False, "?"
    values = constant.get("values")
    if not values:
        kind = constant.get("value-type")
        return False, "?" if kind is None else ("b" if kind == "boolean" else "x")
    per_face = False
    cells = ""
    for i, value in enumerate(values):
        access = value.get("access")
        if access == "write":
            continue
        if access == "arg":
            if i == 0 and value.get("name") == "face":
                per_face = True
                continue
            return False, "?"
        kind = value.get("value-type")
        if kind is None:
            return False, "?"
        cells += "b" if kind == "boolean" else "x"
    return per_face, cells


def write_list_marks(document, commit, out):
    functions = document["functions"]
    constants = document["constants"]
    lines = [
        "# The request tags of the list results marked bool-semantics, from secondlife/lsl-definitions lsl_definitions.yaml at " + commit + ".",
        "# MIT, Copyright (c) 2025 Linden Research, Inc. Written by tools/slua-bindings/extract-marks.py.",
        "# function<TAB>name<TAB>request set, then tag<TAB>request set<TAB>value<TAB>name<TAB>cells<TAB>face or - (cells: b boolean, x other, ? not given).",
    ]
    rules = {}
    for name in sorted(functions):
        fn = functions[name]
        if fn.get("bool-semantics") and fn.get("return") == "list":
            rules[name] = fn["param-get-semantics"]
            lines.append("function\t" + name + "\t" + rules[name])
    for rule in sorted(set(rules.values())):
        tags = sorted((c["value"], n) for n, c in constants.items()
                      if rule in c.get("member-of", []) and c.get("access") != "write" and isinstance(c.get("value"), int))
        for value, tag in tags:
            per_face, cells = layout_of(tag, constants[tag])
            lines.append("tag\t%s\t%d\t%s\t%s\t%s" % (rule, value, tag, cells, "face" if per_face else "-"))
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def main(path, commit, out, list_out=None):
    with open(path, encoding="utf-8") as f:
        document = yaml.safe_load(f)
    functions = document["functions"]
    lines = [
        "# The ll functions' bool and index marks, from secondlife/lsl-definitions lsl_definitions.yaml at " + commit + ".",
        "# MIT, Copyright (c) 2025 Linden Research, Inc. Written by tools/slua-bindings/extract-marks.py.",
        "# name<TAB>mark<TAB>mark ...; marks are return:bool, return:index, arg<N>:bool, arg<N>:index (N from 0).",
    ]
    for name in sorted(functions):
        fn = functions[name]
        marks = []
        if fn.get("bool-semantics"):
            marks.append("return:bool")
        if fn.get("index-semantics"):
            marks.append("return:index")
        for i, arg in enumerate(fn.get("arguments", [])):
            (_, detail), = arg.items()
            if detail.get("bool-semantics"):
                marks.append("arg%d:bool" % i)
            if detail.get("index-semantics"):
                marks.append("arg%d:index" % i)
        if marks:
            lines.append(name + "\t" + "\t".join(marks))
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    if list_out:
        write_list_marks(document, commit, list_out)


if __name__ == "__main__":
    if len(sys.argv) not in (4, 5):
        sys.exit(__doc__)
    main(*sys.argv[1:])
