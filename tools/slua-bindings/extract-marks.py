#!/usr/bin/env python3
# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Writes lsl-definition-marks.txt: which arguments and results of the ll functions Second Life's definitions mark as
positions (index-semantics) or truth values (bool-semantics).

usage: extract-marks.py <lsl_definitions.yaml> <commit of the definitions> <output file>

The input is secondlife/lsl-definitions (MIT, Copyright (c) 2025 Linden Research, Inc.; see ThirdPartyLicenses). Each function
is one line, in name order: the name, then tab-separated marks, "return:bool", "return:index", "arg<N>:bool" or "arg<N>:index"
(N counts from 0). Needs PyYAML; the generator reads the file this writes and does not need PyYAML.
"""
import sys

import yaml


def main(path, commit, out):
    with open(path, encoding="utf-8") as f:
        functions = yaml.safe_load(f)["functions"]
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


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
