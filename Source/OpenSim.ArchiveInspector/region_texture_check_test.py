# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
"""Tests of region-texture-check.py. The check runs as a program (a subprocess), and the program that fetches textures from a region is replaced by a small
fake that serves what the test says, so no region and no login are needed.

run: python -I region_texture_check_test.py
"""
import io
import json
import os
import subprocess
import sys
import tarfile
import tempfile
import unittest

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
CHECK = os.path.join(HERE, "region-texture-check.py")

# a fetch client that writes <id>.bin for each id from the JSON map in FAKE_SERVED (hex bytes, or null for "not served") and records how it was called
FAKE_CLIENT = r'''
import json, os, sys
args = sys.argv[1:]
record = {"args": args, "password_seen": os.environ.get("OAR_CHECK_PASSWORD")}
open(os.environ["FAKE_RECORD"], "w").write(json.dumps(record))
out = args[args.index("--out") + 1]
ids = [a for a in args[args.index("--out") + 2:]]
served = json.loads(os.environ["FAKE_SERVED"])
for i in ids:
    if served.get(i) is not None:
        open(os.path.join(out, i + ".bin"), "wb").write(bytes.fromhex(served[i]))
    print("texture " + i + " ok")
print("login: ok")
sys.exit(int(os.environ.get("FAKE_EXIT", "0")))
'''

PASSWORD = "correct-horse-battery"
TEXTURE_A = "11111111-2222-4333-8444-555555555555"
TEXTURE_B = "66666666-7777-4888-9999-aaaaaaaaaaaa"


def picture(color, boxes):
    """A small picture as JPEG 2000: a bare codestream (what a viewer can decode) or wrapped in JP2 boxes (what it shows as grey)."""
    buf = io.BytesIO()
    Image.new("RGB", (8, 8), color).save(buf, format="JPEG2000", no_jp2=not boxes, irreversible=False)
    return buf.getvalue()


class RegionTextureCheckTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="texture-check-")
        self.client = os.path.join(self.folder, "fake_client.py")
        open(self.client, "w").write(FAKE_CLIENT)
        self.record = os.path.join(self.folder, "record.json")

    def archive(self, name, textures):
        path = os.path.join(self.folder, name)
        with tarfile.open(path, "w:gz") as t:
            for tid, data in textures.items():
                info = tarfile.TarInfo("assets/" + tid + "_texture.jp2")
                info.size = len(data)
                t.addfile(info, io.BytesIO(data))
        return path

    def run_check(self, archives, served, extra=None, password=PASSWORD, exit_code=0):
        env = dict(os.environ)
        env.pop("OAR_CHECK_PASSWORD", None)
        if password is not None:
            env["OAR_CHECK_PASSWORD"] = password
        env["FAKE_SERVED"] = json.dumps({k: (v.hex() if v is not None else None) for k, v in served.items()})
        env["FAKE_RECORD"] = self.record
        env["FAKE_EXIT"] = str(exit_code)
        args = [sys.executable, "-I", CHECK, "--login-uri", "http://grid.example.org:8002/", "--first", "Test", "--last", "User", "--client", self.client]
        args += (extra or []) + ["region.example.org"] + archives
        r = subprocess.run(args, capture_output=True, text=True, env=env, timeout=120, cwd=self.folder)
        return r.returncode, r.stdout + r.stderr

    def test_a_codestream_served_unchanged_passes(self):
        data = picture((200, 40, 10), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: data})
        self.assertEqual(0, code, out)
        self.assertIn("PASS", out)
        self.assertIn("textures checked: 1, failed: 0", out)

    def test_a_jp2_wrapped_texture_fails_because_the_viewer_shows_it_grey(self):
        data = picture((200, 40, 10), boxes=True)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: data})
        self.assertEqual(1, code, out)
        self.assertIn("JP2 boxes", out)
        self.assertIn("textures checked: 1, failed: 1", out)

    def test_bytes_that_differ_from_the_archive_fail(self):
        stored = picture((200, 40, 10), boxes=False)
        served = picture((10, 40, 200), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: stored})], {TEXTURE_A: served})
        self.assertEqual(1, code, out)
        self.assertIn("differ", out)

    def test_a_texture_the_region_does_not_serve_fails(self):
        data = picture((200, 40, 10), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: None})
        self.assertEqual(1, code, out)
        self.assertIn("served nothing", out)

    def test_several_archives_are_checked_in_one_run_and_each_is_named(self):
        a, b = picture((1, 2, 3), boxes=False), picture((9, 8, 7), boxes=True)
        code, out = self.run_check([self.archive("first.oar", {TEXTURE_A: a}), self.archive("second.oar", {TEXTURE_B: b})], {TEXTURE_A: a, TEXTURE_B: b})
        self.assertEqual(1, code, out)
        self.assertIn("first.oar", out)
        self.assertIn("second.oar", out)
        self.assertIn("textures checked: 2, failed: 1", out)

    def test_the_account_reaches_the_client_and_the_password_only_through_the_environment(self):
        data = picture((200, 40, 10), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: data})
        self.assertEqual(0, code, out)
        record = json.load(open(self.record))
        args = record["args"]
        for flag, value in (("--login-uri", "http://grid.example.org:8002/"), ("--first", "Test"), ("--last", "User"), ("--region", "region.example.org")):
            self.assertIn(flag, args)
            self.assertEqual(value, args[args.index(flag) + 1])
        self.assertIn(TEXTURE_A, args)
        self.assertEqual(PASSWORD, record["password_seen"])
        self.assertFalse(any(PASSWORD in a for a in args), "the password is on the client's command line")
        self.assertNotIn(PASSWORD, out)

    def test_without_the_password_variable_nothing_is_fetched_and_the_exit_is_2(self):
        data = picture((200, 40, 10), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: data}, password=None)
        self.assertEqual(2, code, out)
        self.assertIn("OAR_CHECK_PASSWORD", out)
        self.assertFalse(os.path.exists(self.record), "the client ran without a password")

    def test_a_password_option_is_refused(self):
        data = picture((200, 40, 10), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: data}, extra=["--password", PASSWORD])
        self.assertEqual(2, code, out)
        self.assertFalse(os.path.exists(self.record), "the client ran")
        self.assertNotIn(PASSWORD, out)

    def test_a_client_that_cannot_log_in_gives_exit_2(self):
        data = picture((200, 40, 10), boxes=False)
        code, out = self.run_check([self.archive("a.oar", {TEXTURE_A: data})], {TEXTURE_A: data}, exit_code=2)
        self.assertEqual(2, code, out)
        self.assertIn("could not run", out)

    def test_an_archive_without_textures_gives_exit_2(self):
        code, out = self.run_check([self.archive("empty.oar", {})], {})
        self.assertEqual(2, code, out)
        self.assertIn("no texture assets", out)

    def test_the_script_names_no_folder_or_account_of_one_machine(self):
        text = open(CHECK, encoding="utf-8").read()
        for word in ("D:/", "D:\\", "C:\\", "legiongrid", "Tester", "alias"):
            self.assertNotIn(word, text, word)


if __name__ == "__main__":
    unittest.main(verbosity=2)
