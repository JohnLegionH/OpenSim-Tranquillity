# =====================================================================================
# assert-patched-joltc.ps1  —  POST-PUBLISH guard for the Jolt physics module.
#
# The per-instance _simLock in the Jolt backend REQUIRES the PATCHED joltc native
# (per-system TempAllocator). Stock JoltPhysics.Native 1.0.4 shares ONE TempAllocator
# across all physics systems, so with per-instance locks it aborts the moment two
# regions step at once ("TempAllocator: Freeing in the wrong order" -> std::abort()).
#
# The patched native is checked in at runtimes/win-x64/native/joltc.dll and ships to
# output via the csproj <Content> glob. This guard runs AFTER a publish and fails loudly
# if a stray NuGet restore, package bump, or manual copy reintroduced the stock DLL.
#
# Provenance + rebuild recipe: legion-grid-source/native/joltc/README.md.
#
# Usage (point it at the published region-server dir, or any dir holding joltc.dll):
#     powershell -File assert-patched-joltc.ps1 -PublishDir "D:\legiongrid\regionserver"
#     powershell -File assert-patched-joltc.ps1 -JoltcPath  "path\to\joltc.dll"
# Exit 0 = patched (OK). Exit 1 = stock/unknown/missing (do NOT boot).
# =====================================================================================
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$JoltcPath
)

$ErrorActionPreference = "Stop"

$patchedHash = "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5"
$stockHash   = "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910"

if (-not $JoltcPath) {
    if (-not $PublishDir) { Write-Host "Provide -PublishDir or -JoltcPath." -ForegroundColor Red; exit 1 }
    # Prefer the runtimes/<rid>/native layout; fall back to a flattened copy at the root.
    $candidates = @(
        (Join-Path $PublishDir "runtimes\win-x64\native\joltc.dll"),
        (Join-Path $PublishDir "joltc.dll")
    )
    $JoltcPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $JoltcPath) {
        Write-Host "JOLTC GUARD FAIL: no joltc.dll found under $PublishDir" -ForegroundColor Red
        Write-Host "  looked in: $($candidates -join '; ')" -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path $JoltcPath)) { Write-Host "JOLTC GUARD FAIL: missing $JoltcPath" -ForegroundColor Red; exit 1 }

$h = (Get-FileHash $JoltcPath -Algorithm SHA256).Hash
if ($h -eq $patchedHash) {
    Write-Host ("joltc.dll PATCHED build OK ({0}...)  {1}" -f $h.Substring(0,8), $JoltcPath) -ForegroundColor Green
    exit 0
}

$kind = if ($h -eq $stockHash) { "STOCK NuGet 1.0.4" } else { "UNKNOWN build ($($h.Substring(0,12))...)" }
Write-Host ""
Write-Host ("JOLTC GUARD FAIL: {0} detected at" -f $kind) -ForegroundColor Red
Write-Host "    $JoltcPath" -ForegroundColor Red
Write-Host "The per-instance _simLock requires the PATCHED native (per-system TempAllocator);" -ForegroundColor Red
Write-Host "the stock DLL shares one allocator across regions and WILL crash under load." -ForegroundColor Red
Write-Host "Restore the patched build from runtimes/win-x64/native/joltc.dll (SHA 16AF7638...)." -ForegroundColor Yellow
exit 1
