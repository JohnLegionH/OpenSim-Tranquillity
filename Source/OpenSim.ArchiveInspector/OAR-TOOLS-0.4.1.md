<!-- Copyright (c) 2026 Legion Builds. Released under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/). -->

# oar tools 0.4.1: the programs start on a Linux without libssl

0.4.1 is 0.4 with two changes. The five programs now start and do their work on a Linux that does not have the system's OpenSSL library (libssl), and the
report files that `oar-convert` and `oar-restore` write with `--report` use line feeds on every platform. Every archive, plan folder and `.glb` file the
programs write is byte for byte what 0.4 wrote. Everything else about the programs, including `oar-import`, the manifest, the notes record and the check
after a load, is described in [OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md), which is in the package beside this file; the merge is described in
[OAR-TOOLS-0.3.1.md](OAR-TOOLS-0.3.1.md).

## What changed

1. **No libssl needed to start.** In 0.4, `oar-convert`, `oar-restore`, `oar-import` and `oar-texfetch` stopped at start on a Linux without libssl, with
   `No usable version of libssl was found` and exit code 134 (only `oar-export` ran). The 0.4 notes said that `oar-convert` and `oar-restore` needed it and
   `oar-texfetch` needed it for an https login; that was incomplete, because `oar-import` needed it too, and `oar-texfetch` needed it for an http login as well.
   All five programs of 0.4.1 run without it (table below).
2. **Report files use line feeds.** `oar-convert --report` (the text and `.json` files, and the merge report) and `oar-restore --report` (the file, its
   `.json` and its `.plan.json`) are written with line feeds on every platform. On Windows 0.4 wrote carriage return and line feed.
3. **`oar-texfetch` says what is missing.** Given an https login address on a Linux that has no libssl, it prints a message that names the package to
   install and the http alternative, and exits with 2, instead of stopping with the library's abort.

## Which programs need libssl on Linux, and when

| Program | Needs libssl on Linux? |
|---|---|
| `oar-convert` | no |
| `oar-export` | no |
| `oar-restore` | no |
| `oar-import` | no |
| `oar-texfetch` | **only for an https login address.** An http login address needs none. |

.NET has no TLS of its own on Linux: an https connection loads the system's OpenSSL. `oar-texfetch` is the one program that makes a network connection,
so an https login (`--login-uri https://...`) needs libssl installed: the package `libssl3t64` on Ubuntu 24.04, `libssl3` on Debian 12. Without it the
program refuses the login with
`an https login needs the system's OpenSSL library (libssl), and this system does not have one the runtime can load; install it (Debian and Ubuntu: apt install libssl3t64) or use an http login URI`
and exit code 2. A login address that starts with http and is answered with a redirect to https is not caught by that check and needs libssl when it
follows the redirect. Windows has its own TLS and needs nothing.

## What loaded libssl in 0.4

The OpenMetaverse library that the programs carry (`OpenMetaverse.Types.dll`, the package UtopiaSkye.OpenMetaverse.Types 1.1.7) creates three hash
objects, `new MD5CryptoServiceProvider()`, `new SHA1CryptoServiceProvider()` and `new SHA256Managed()`, in the static constructor of its `Utils` class. Any
program that touches a UUID or a vector runs that constructor, and on Linux the framework's hash classes load the system's OpenSSL, which aborts the process
when there is none. The viewer-side library of `oar-texfetch` (LibreMetaverse 3.1.4) does the same with `MD5.Create()`, `SHA1.Create()` and `SHA256.Create()`
in its `Utils` class, and calls `MD5.Create()` again to hash the password when it logs in. `oar-export` never reached that constructor, which is why it ran.

In 0.4.1 the publish step runs a small tool (`OpenSim.Archive.PackagePatch` in the source) on the copies of those libraries that go into the programs. In
each copy it replaces those calls with calls to `OpenSim.Archive.ManagedHash`, which gives the same three algorithms as framework hash classes that run in
managed code (the digests are BouncyCastle's, which the programs already carried). The libraries in the NuGet cache are not touched; nothing else in them
changes. The digests are the same bytes, so nothing the programs write changes. The change is listed in THIRD-PARTY-NOTICES.txt, with the unchanged
copyright notices and licences of both libraries.

## Limits

- The change is made when the programs are published. A program built and run from the source tree without it (for example `dotnet run`) uses the
  framework's hash classes and needs libssl on Linux.
- Only the three places above were changed. The programs were run end to end without libssl (below), but a code path that no run reached and that calls one
  of the framework's hash methods would still stop with the libssl message. No such path is known.
- The limits of 0.4 stand ([OAR-TOOLS-0.4.md](OAR-TOOLS-0.4.md), "What 0.4 cannot do"). The 0.4 `oar-texfetch` has since been run against a test region on
  Windows (the check passed the three textures of a corrected archive and failed the three of an archive with JP2 files); the 0.4.1 `oar-texfetch` has not been run
  against a region. Its differences from 0.4 are the hash objects (the login sends the same password hash, checked against a stand-in server) and the https message.

## Packages

`oar-tools-windows-x64.zip` and `oar-tools-linux-x64.tar.gz` (64-bit, glibc), each with `oar-convert`, `oar-export`, `oar-restore`, `oar-import` and
`oar-texfetch`, the two native libraries they share, `region-texture-check.py`, QUICKSTART.txt, this file, OAR-TOOLS-0.4.md, MPL-2.0.txt and
THIRD-PARTY-NOTICES.txt. The SHA-256 of each package is in `SHA256SUMS.txt` beside them on the release page.

## Checks

Run from the programs as published, on Windows and, as an ordinary user with an empty home folder, in an Ubuntu 24.04 container, once with the image's libssl
(`libssl3t64` 3.0.13) and once after `libssl3t64` was removed (`ldconfig -p` then lists no libssl or libcrypto). The same steps as for 0.4: `oar-convert --merge`
over seven test archives, twice each (the second pass over the first pass's output); `oar-export` and `oar-restore` of a 48-part archive; `oar-import` of a
one-part radio model and of a two-part test house, each twice with `--created` and `--check`; `oar-texfetch` against an unreachable login address.

- All 21 steps exit as they did in 0.4 (0 for every step except the unreachable login of `oar-texfetch`, which exits 2), with libssl and without it. In 0.4 the
  same container without libssl gave exit 134 for every step except the one `oar-export` run.
- The five programs print `0.4.1` with `--version`.
- The 1 078 files the Linux run writes (archives, `.glb` files, plan folders, reports) are identical with and without libssl, and identical to what 0.4 wrote
  in the same container, once the build stamp, the durations and the memory figures in the report files are left out. The 71 archive and `.glb` files are
  byte for byte identical, except the four archives `oar-import` writes: they differ from 0.4's in one line of the notes entry, which now reads
  `oar-import 0.4.1`.
- On Windows the archives, `.glb` files and plan files of 0.4.1 equal those of 0.4 in the same way (67 of 71 archive and `.glb` files identical, the four import
  archives differing in the same line), and the test house archive is byte-identical between Windows and Linux.
- `oar-texfetch` on Linux without libssl: an http login address starts, reaches "ConnectingToLogin" and exits 2 when the address is unreachable; an https login
  address prints the message above and exits 2. Against a stand-in login server on Windows, 0.4 and 0.4.1 send the same password hash.
- `oar-texfetch` 0.4.1 was not run against a region.

Tests: the converter test project 1159 passed (1119 in 0.4 plus 40 new), the restore test project 82 passed (81 plus 1), and the 11 tests of `region-texture-check.py`
(`region_texture_check_test.py`, run with `python -I`) passed. The new and changed tests (44 of the converter project and 4 of the restore project) also passed in the
Ubuntu 24.04 container, built there from a fresh clone.

Built from commit fdbc8248ad; the tag names a later commit that changes documentation only.
