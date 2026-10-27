# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""In-region texture check: run it after a load, with the archive that was loaded, to see that a viewer will show its pictures.

Every texture asset of the archives is fetched THROUGH THE REGION as a logged-in avatar (the region's GetTexture capability, the way a viewer asks: a
first range request for the header, then the whole asset) and decoded. A texture passes when
  - the region answers and serves the same bytes the archive holds,
  - the bytes are a bare JPEG 2000 codestream (FF 4F FF 51): the viewer's decoder (OpenJPEG, OPJ_CODEC_J2K in llimagej2coj.cpp) reads nothing else, so a
    file wrapped in JP2 boxes shows as a grey face although the region's own decoder reads it,
  - and it decodes (Pillow's OpenJPEG) to the size and pixels of the archive's own decode.

usage: python region-texture-check.py --login-uri <uri> --first <name> --last <name> [--client <path>] <region> <archive.oar> [<archive.oar> ...]
  --login-uri   the grid's login URI, for example http://grid.example.org:8002/
  --first, --last   the avatar to log in as; it must be allowed in the region
  --client      the program that logs in and fetches the textures (default: oar-texfetch beside this script)
  <region>      the name of the region the archive was loaded into
The avatar's password is read from the environment variable OAR_CHECK_PASSWORD and never from the command line. It needs Python 3 with Pillow
(pip install pillow). Run it once per login: it logs the avatar in and out.

exit 0 all pass, 1 some texture failed, 2 could not run.
"""
import argparse
import io
import os
import subprocess
import sys
import tarfile
import tempfile

CODESTREAM = bytes([0xFF, 0x4F, 0xFF, 0x51])
JP2_BOX = bytes([0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20])
PASSWORD_VARIABLE = "OAR_CHECK_PASSWORD"


def archive_textures(path):
    out = {}
    with tarfile.open(path) as t:
        for m in t.getmembers():
            if m.isfile() and m.name.startswith("assets/") and m.name.endswith("_texture.jp2"):
                out[m.name[len("assets/"):-len("_texture.jp2")]] = t.extractfile(m).read()
    return out


def pixels(image_module, data):
    im = image_module.open(io.BytesIO(data))
    im.load()
    return im.size, im.mode, im.tobytes()


def default_client():
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "oar-texfetch.exe" if os.name == "nt" else "oar-texfetch")


def client_command(path):
    if path.lower().endswith(".dll"):
        return ["dotnet", path]
    if path.lower().endswith(".py"):
        return [sys.executable, path]
    return [path]


def main(argv):
    parser = argparse.ArgumentParser(prog="region-texture-check.py", usage="%(prog)s --login-uri URI --first NAME --last NAME [--client PATH] REGION ARCHIVE [ARCHIVE ...]",
                                     description="Fetches the textures of imported archives through a region and checks that a viewer can decode them.")
    parser.add_argument("--login-uri", required=True)
    parser.add_argument("--first", required=True)
    parser.add_argument("--last", required=True)
    parser.add_argument("--client", default=None)
    parser.add_argument("region")
    parser.add_argument("archives", nargs="+")
    args = parser.parse_args(argv)

    if not os.environ.get(PASSWORD_VARIABLE):
        print("set the environment variable %s to the avatar's password (it is never read from the command line)" % PASSWORD_VARIABLE)
        return 2
    try:
        from PIL import Image
    except ImportError:
        print("this check needs Pillow: pip install pillow")
        return 2

    wanted = {}
    for a in args.archives:
        for tid, data in archive_textures(a).items():
            wanted[tid] = (a.replace("\\", "/").rsplit("/", 1)[-1], data)
    if not wanted:
        print("no texture assets in the archives")
        return 2

    client = client_command(args.client or default_client())
    with tempfile.TemporaryDirectory(prefix="texture-check-") as out:
        # the client keeps a texture cache in its working folder, so it runs in the empty folder the served bytes go to
        command = client + ["--login-uri", args.login_uri, "--first", args.first, "--last", args.last, "--region", args.region, "--out", out] + sorted(wanted)
        try:
            r = subprocess.run(command, capture_output=True, text=True, timeout=900, cwd=out)
        except (FileNotFoundError, PermissionError) as e:
            print("the fetch client could not run: %s" % e)
            return 2
        for line in r.stdout.splitlines():
            if not line.startswith("texture "):
                print(line)
        if r.returncode == 2:
            print("the fetch client could not run (exit 2)")
            return 2

        print("%-36s %-18s %-9s %-8s %s" % ("texture", "archive", "bytes", "served", "result"))
        failed = 0
        for tid, (name, stored) in sorted(wanted.items(), key=lambda kv: (kv[1][0], kv[0])):
            f = os.path.join(out, tid + ".bin")
            body = open(f, "rb").read() if os.path.exists(f) else b""
            problems = []
            size = "?"
            if not body:
                problems.append("the region served nothing")
            else:
                if body != stored:
                    problems.append("the served bytes differ from the archive's asset")
                if body[:8] == JP2_BOX:
                    problems.append("JP2 boxes: the viewer's decoder cannot read it (grey face)")
                elif body[:4] != CODESTREAM:
                    problems.append("not a codestream (starts %s)" % body[:4].hex())
                try:
                    a, b = pixels(Image, body), pixels(Image, stored)
                    if a != b:
                        problems.append("the decoded pixels differ")
                    size = "%dx%d" % a[0]
                except Exception as e:
                    problems.append("does not decode: %s" % str(e)[:80])
            failed += bool(problems)
            print("%-36s %-18s %-9d %-8s %s" % (tid, name[:18], len(body), "same" if body == stored else "DIFF", "PASS " + size if not problems else "FAIL: " + "; ".join(problems)))
    print("textures checked: %d, failed: %d" % (len(wanted), failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
