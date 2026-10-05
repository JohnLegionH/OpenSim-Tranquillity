# Copyright (c) 2026 Legion Builds
#
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

# =====================================================================================
# assert-patched-joltc.ps1 - checks that a build or publish output carries the patched
# joltc natives the Jolt physics module ships, and no other joltc.
#
# The per-instance _simLock in the Jolt backend requires the patched joltc (one
# TempAllocator per physics system). Stock JoltPhysics.Native 1.0.4 shares one
# TempAllocator across all physics systems, so with per-instance locks it aborts the
# moment two regions step at once ("TempAllocator: Freeing in the wrong order" ->
# std::abort()).
#
# The module loads its native only from runtimes/<rid>/native/ (JoltNative.cs in the
# backend), after checking its SHA-256 against the same record as the table below. This
# script checks an output before it is deployed:
#   - every runtimes/<rid>/native/ file of a platform the module supports must be the
#     patched build for that platform;
#   - at least one of them must be present;
#   - any other joltc file (a copy at the root, another platform's stock build, the
#     double-precision variant) is a stray: the module never loads it, but it should not be
#     in the output. Strays fail the check unless -AllowStray is given (for example on an
#     installation that still holds files from an older deploy).
# It runs in Windows PowerShell and in PowerShell 7 (pwsh) on Linux and macOS.
#
# Provenance and rebuild recipe: native/joltc/README.md.
#
# Usage:
#     powershell -File assert-patched-joltc.ps1 -PublishDir "<output or publish directory>" [-AllowStray]
#     pwsh -File assert-patched-joltc.ps1 -PublishDir "<output or publish directory>"
#     powershell -File assert-patched-joltc.ps1 -NativePath "<path to a joltc.dll or libjoltc.so>"
# Exit 0 = every shipped file is the patched build and (without -AllowStray) nothing else is there.
# Exit 1 = a shipped file is stock or unknown, none was found, or a stray file is present.
# =====================================================================================
[CmdletBinding()]
param(
    [string]$PublishDir,
    [Alias("JoltcPath")]
    [string]$NativePath,
    [switch]$AllowStray
)

$ErrorActionPreference = "Stop"

# The natives the module ships, by runtime identifier. Must equal JoltNative.Shipped in
# OpenSim.Region.PhysicsModules.Jolt.Backend/JoltNative.cs (a unit test checks both against the files).
$shipped = [ordered]@{
    "win-x64"   = @{ File = "joltc.dll";   Sha256 = "1F855744227482146708AB9AF683F4975CFC4C262030E22DAACE855F9D7479B6" }
    "linux-x64" = @{ File = "libjoltc.so"; Sha256 = "EEAD7C1AA7FDFAC07132E26913E03B268DFD825CA72FFE6CEC3A181DA2EC95BB" }
}
# Stock JoltPhysics.Native 1.0.4 builds, named in the report when one is found.
$stock = @{
    "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910" = "stock JoltPhysics.Native 1.0.4 win-x64"
}

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() }

function Get-Kind([string]$hash) {
    if ($stock.ContainsKey($hash)) { return $stock[$hash] }
    foreach ($rid in $shipped.Keys) { if ($shipped[$rid].Sha256 -eq $hash) { return "the patched $rid build" } }
    return "an unknown build"
}

# The runtime identifier a path's runtimes/<rid>/native/<file> location names, if it is a shipped one.
function Get-ShippedRid([string]$path) {
    $parts = $path -split '[\\/]'
    if ($parts.Count -lt 4) { return $null }
    $file = $parts[-1]; $native = $parts[-2]; $rid = $parts[-3]; $runtimes = $parts[-4]
    if ($runtimes -ne "runtimes" -or $native -ne "native") { return $null }
    if (-not $shipped.Contains($rid)) { return $null }
    if ($shipped[$rid].File -cne $file) { return $null }
    return $rid
}

if ($NativePath) {
    if (-not (Test-Path -LiteralPath $NativePath)) { Write-Host "JOLTC GUARD FAIL: missing $NativePath"; exit 1 }
    $full = (Resolve-Path -LiteralPath $NativePath).Path
    $h = Get-Sha256 $full
    $match = @($shipped.Keys | Where-Object { $shipped[$_].Sha256 -eq $h })
    if ($match.Count -gt 0) { Write-Host ("  OK      {0,-10} {1}  {2}" -f $match[0], $h.Substring(0,8), $full); exit 0 }
    Write-Host ("  FAIL    {0,-10} {1}  {2}   <== {3}" -f "", $h.Substring(0,8), $full, (Get-Kind $h))
    exit 1
}

if (-not $PublishDir) { Write-Host "Provide -PublishDir or -NativePath."; exit 1 }
if (-not (Test-Path -LiteralPath $PublishDir)) { Write-Host "JOLTC GUARD FAIL: no such directory $PublishDir"; exit 1 }

$root = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd('\', '/')
$all = @(Get-ChildItem -LiteralPath $root -Recurse -File |
         Where-Object { $_.Name -like "joltc*.dll" -or $_.Name -like "libjoltc*.so*" -or $_.Name -like "libjoltc*.dylib" } |
         Sort-Object FullName)

Write-Host ("Checking {0} joltc file(s) under {1}:" -f $all.Count, $root)
$bad = 0; $stray = 0; $found = @()
foreach ($f in $all) {
    $rel = $f.FullName.Substring($root.Length).TrimStart('\', '/')
    $h = Get-Sha256 $f.FullName
    $rid = Get-ShippedRid $f.FullName
    if ($rid) {
        if ($h -eq $shipped[$rid].Sha256) {
            Write-Host ("  OK      {0,-10} {1}  {2}" -f $rid, $h.Substring(0,8), $rel)
            $found += $rid
        } else {
            Write-Host ("  FAIL    {0,-10} {1}  {2}   <== {3}, not the patched build" -f $rid, $h.Substring(0,8), $rel, (Get-Kind $h))
            $bad++
        }
        continue
    }
    $label = if ($AllowStray) { "stray" } else { "FAIL" }
    Write-Host ("  {0,-7} {1,-10} {2}  {3}   <== {4}; the module never loads it" -f $label, "", $h.Substring(0,8), $rel, (Get-Kind $h))
    $stray++
}

if ($found.Count -eq 0 -and $bad -eq 0) {
    Write-Host ("JOLTC GUARD FAIL: no shipped native under {0} (expected runtimes/<rid>/native/ for one of: {1})" -f $root, ($shipped.Keys -join ", "))
    exit 1
}
if ($bad -gt 0) {
    Write-Host ("JOLTC GUARD FAIL: {0} shipped native(s) are not the patched build. Stock joltc shares one allocator" -f $bad)
    Write-Host "across regions and aborts the process under load. Restore the files from the module's runtimes folder."
    exit 1
}
if ($stray -gt 0 -and -not $AllowStray) {
    Write-Host ("JOLTC GUARD FAIL: {0} stray joltc file(s) beside the shipped ones; the output should hold only runtimes/<rid>/native/ files." -f $stray)
    exit 1
}
Write-Host ("joltc guard OK: {0} patched ({1})." -f $found.Count, ($found -join ", "))
exit 0
