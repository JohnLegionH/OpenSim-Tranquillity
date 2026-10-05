# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""List the names a joltc native exports, one per line, sorted.

Reads a Windows PE (joltc.dll) or a 64-bit ELF shared object (libjoltc.so) with the Python
standard library only, so the same check runs on a build runner and on any developer machine.

    python list-exports.py <native file>                  print the exported names
    python list-exports.py <native file> --expect <list>  exit 1 unless the names equal the list

For an ELF file the exported names are the defined dynamic symbols with global or weak binding
and default or protected visibility, which is what the dynamic loader can resolve.
"""

import struct
import sys


def pe_exports(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("not a PE file")
    num_sections = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    dirs = opt + (112 if magic == 0x20B else 96)
    export_rva = struct.unpack_from("<I", data, dirs)[0]
    if export_rva == 0:
        return []
    sections = []
    sec = opt + opt_size
    for i in range(num_sections):
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, sec + i * 40 + 8)
        sections.append((va, max(vsize, rawsize), rawptr))

    def off(rva):
        for va, size, rawptr in sections:
            if va <= rva < va + size:
                return rva - va + rawptr
        raise ValueError("RVA 0x%x is in no section" % rva)

    exp = off(export_rva)
    num_names, _, names_rva = struct.unpack_from("<III", data, exp + 24)
    names = []
    for i in range(num_names):
        name_rva = struct.unpack_from("<I", data, off(names_rva) + i * 4)[0]
        start = off(name_rva)
        names.append(data[start:data.index(b"\0", start)].decode("ascii"))
    return names


def elf_exports(data):
    if data[4] != 2 or data[5] != 1:
        raise ValueError("only 64-bit little-endian ELF is read")
    shoff = struct.unpack_from("<Q", data, 0x28)[0]
    shentsize, shnum = struct.unpack_from("<HH", data, 0x3A)
    headers = [struct.unpack_from("<IIQQQQIIQQ", data, shoff + i * shentsize) for i in range(shnum)]
    names = []
    for h in headers:
        if h[1] != 11:  # SHT_DYNSYM
            continue
        sym_off, sym_size, link, entsize = h[4], h[5], h[6], h[9]
        strtab_off = headers[link][4]
        for i in range(1, sym_size // entsize):
            st_name, st_info, st_other, st_shndx = struct.unpack_from("<IBBH", data, sym_off + i * entsize)
            binding, visibility = st_info >> 4, st_other & 3
            if st_shndx == 0 or binding not in (1, 2) or visibility not in (0, 3):
                continue
            start = strtab_off + st_name
            names.append(data[start:data.index(b"\0", start)].decode("ascii"))
    return names


def exports(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] == b"MZ":
        return sorted(set(pe_exports(data)))
    if data[:4] == b"\x7fELF":
        return sorted(set(elf_exports(data)))
    raise ValueError("%s is neither a PE nor an ELF file" % path)


def main(argv):
    if len(argv) not in (2, 4) or (len(argv) == 4 and argv[2] != "--expect"):
        print(__doc__, file=sys.stderr)
        return 2
    names = exports(argv[1])
    if len(argv) == 2:
        sys.stdout.write("".join(n + "\n" for n in names))
        return 0
    with open(argv[3], encoding="ascii") as f:
        expected = sorted(set(line.strip() for line in f if line.strip()))
    missing = sorted(set(expected) - set(names))
    extra = sorted(set(names) - set(expected))
    for n in missing:
        print("missing: " + n)
    for n in extra:
        print("extra:   " + n)
    print("%d exported, %d expected, %d missing, %d extra" % (len(names), len(expected), len(missing), len(extra)))
    return 0 if not missing and not extra else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
