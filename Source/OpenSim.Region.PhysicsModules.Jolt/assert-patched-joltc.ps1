# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

# =====================================================================================
# assert-patched-joltc.ps1 - checks that a build or publish output carries only the
# patched joltc native the Jolt physics module needs.
#
# The per-instance _simLock in the Jolt backend requires the patched joltc (one
# TempAllocator per physics system). Stock JoltPhysics.Native 1.0.4 shares one
# TempAllocator across all physics systems, so with per-instance locks it aborts the
# moment two regions step at once ("TempAllocator: Freeing in the wrong order" ->
# std::abort()).
#
# A joltc.dll in the application directory is loaded in preference to one under
# runtimes\, so checking only the runtimes\ copy can pass while a stock copy at the root
# is the one that loads. The script therefore checks EVERY joltc*.dll under the
# directory, root first, and fails if ANY of them is not the patched build. Order
# matters for the message, not the verdict.
#
# Provenance and rebuild recipe: native/joltc/README.md.
#
# Usage:
#     powershell -File assert-patched-joltc.ps1 -PublishDir "<output or publish directory>"
#     powershell -File assert-patched-joltc.ps1 -JoltcPath  "path\to\joltc.dll"
# Exit 0 = every copy is the patched build. Exit 1 = any copy is stock/unknown, or none found.
# =====================================================================================
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$JoltcPath
)

$ErrorActionPreference = "Stop"

$patchedHash = "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5"
$stockHash   = "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910"

# joltc_double.dll is NOT a failure: the patched build produces no double-precision variant
# (DOUBLE_PRECISION=OFF), so any joltc_double.dll present is stock and is never loaded.
# It is reported for information only.

function Test-One([string]$path, [string]$label) {
    $h = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($h -eq $patchedHash) {
        Write-Host ("  OK      {0,-9} {1}  {2}" -f $label, $h.Substring(0,8), $path) -ForegroundColor Green
        return $true
    }
    $kind = if ($h -eq $stockHash) { "STOCK NuGet 1.0.4" } else { "UNKNOWN build" }
    Write-Host ("  FAIL    {0,-9} {1}  {2}   <== {3}" -f $label, $h.Substring(0,8), $path, $kind) -ForegroundColor Red
    return $false
}

if ($JoltcPath) {
    if (-not (Test-Path $JoltcPath)) { Write-Host "JOLTC GUARD FAIL: missing $JoltcPath" -ForegroundColor Red; exit 1 }
    if (Test-One $JoltcPath "explicit") { exit 0 } else { exit 1 }
}

if (-not $PublishDir) { Write-Host "Provide -PublishDir or -JoltcPath." -ForegroundColor Red; exit 1 }
if (-not (Test-Path $PublishDir)) { Write-Host "JOLTC GUARD FAIL: no such directory $PublishDir" -ForegroundColor Red; exit 1 }

$rootPath = (Resolve-Path $PublishDir).Path.TrimEnd('\')

# Every joltc*.dll under the root, ROOT FIRST - the application directory is what the
# loader prefers, so it is reported first and is the one a reader should look at.
$all = Get-ChildItem $PublishDir -Recurse -File -Filter "joltc*.dll" |
       Sort-Object @{ Expression = { $_.DirectoryName -eq $rootPath }; Descending = $true }, FullName

if (-not $all) {
    Write-Host "JOLTC GUARD FAIL: no joltc*.dll found anywhere under $PublishDir" -ForegroundColor Red
    exit 1
}

Write-Host ("Checking {0} joltc*.dll under {1} (root first):" -f $all.Count, $rootPath)
$bad = 0
$sawJoltc = $false
foreach ($f in $all) {
    $label = if ($f.DirectoryName -eq $rootPath) { "ROOT" } else { "runtimes" }

    # A native for another architecture can never be loaded by this process, so it is reported and
    # not judged. Only the application root and runtimes\win-x64\ can supply the DLL that runs.
    if ($f.DirectoryName -ne $rootPath -and $f.DirectoryName -notmatch '\\runtimes\\win-x64\\native$') {
        $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        Write-Host ("  note    {0,-9} {1}  {2}   (other architecture; cannot be loaded here)" -f $label, $h.Substring(0,8), $f.FullName) -ForegroundColor DarkGray
        continue
    }

    if ($f.Name -ieq "joltc_double.dll") {
        $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        Write-Host ("  note    {0,-9} {1}  {2}   (double-precision variant; not built by the patch, never loaded)" -f $label, $h.Substring(0,8), $f.FullName) -ForegroundColor DarkGray
        continue
    }

    $sawJoltc = $true
    if (-not (Test-One $f.FullName $label)) { $bad++ }
}

if (-not $sawJoltc) {
    Write-Host "JOLTC GUARD FAIL: found only joltc_double.dll, no joltc.dll, under $PublishDir" -ForegroundColor Red
    exit 1
}

if ($bad -eq 0) {
    Write-Host "joltc guard OK: every joltc.dll under the root is the PATCHED build." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host ("JOLTC GUARD FAIL: {0} copy/copies are not the patched build." -f $bad) -ForegroundColor Red
Write-Host "The per-instance _simLock requires the PATCHED native (per-system TempAllocator);" -ForegroundColor Red
Write-Host "stock joltc shares ONE allocator across regions and aborts the process under load." -ForegroundColor Red
Write-Host "A DLL in the application directory is loaded in preference to one under runtimes\," -ForegroundColor Yellow
Write-Host "so a stock copy at the root defeats a patched copy beneath it." -ForegroundColor Yellow
Write-Host "Restore from runtimes/win-x64/native/joltc.dll (SHA 16AF7638...)." -ForegroundColor Yellow
exit 1
