<#
.SYNOPSIS
Produces the single self-contained MatterHelm release folder (dist\).

.DESCRIPTION
S3-1 packaging (BLUEPRINT §2.6): builds the bridge bundle, wraps it as a Node
SEA (single executable application) sidecar exe, publishes the tray app as a
self-contained single-file exe (ADR-003 item 7 flags; never PublishTrimmed),
and assembles everything into dist\:

    dist\MatterHelm.exe          self-contained tray app (+ publish siblings)
    dist\sidecar\bridge.exe      Node SEA sidecar (preferred layout), or
    dist\sidecar\node.exe        + bridge.cjs (ADR-007 §2 fallback layout —
                                 used automatically when the SEA build fails)
    dist\LICENSE, dist\NOTICE, dist\README-dist.md

No Node.js or .NET is required on the target machine.

Requires on the BUILD machine: Node.js 22.13+ / npm on PATH, dotnet SDK on PATH
(or at C:\Program Files\dotnet). PowerShell 5.1 compatible.

.PARAMETER Configuration
dotnet build configuration for the tray app (default Release).

.PARAMETER SkipTests
Skips `npm run verify` (lint + typecheck + tests). Use only when the same
commit was already verified — e.g. the release workflow runs verify and the
app test suite as explicit gate steps immediately before calling this script.

.EXAMPLE
./build.ps1
.EXAMPLE
./build.ps1 -Configuration Release -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$bridgeDir = Join-Path $repoRoot "bridge"
$bridgeDist = Join-Path $bridgeDir "dist"
$distDir = Join-Path $repoRoot "dist"
$publishDir = Join-Path $repoRoot "app\MatterHelm\bin\$Configuration\publish-win-x64"

# The documented fuse for `node --experimental-sea-config` blobs (Node SEA
# docs); postject needs it to locate the injection point inside node.exe.
$seaFuse = "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2"
$postjectPackage = "postject@1.0.0-alpha.6"

function Assert-LastExit {
    param([string]$What)
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE"
    }
}

function Resolve-Tool {
    param([string]$Name, [string]$FallbackPath)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $cmd) {
        return $cmd.Source
    }
    if (($null -ne $FallbackPath) -and (Test-Path $FallbackPath)) {
        return $FallbackPath
    }
    throw "$Name not found on PATH; install it or add it to PATH"
}

$nodeExe = Resolve-Tool "node"
$dotnetExe = Resolve-Tool "dotnet" "C:\Program Files\dotnet\dotnet.exe"
$nodeVersionText = (& $nodeExe --version).TrimStart("v")
$nodeVersion = [Version]$nodeVersionText
if ($nodeVersion -lt [Version]"22.13.0") {
    throw "Node.js 22.13+ is required by matter.js; found $nodeVersionText at $nodeExe"
}
Write-Host "node:   $nodeExe (v$nodeVersionText)"
Write-Host "dotnet: $dotnetExe"

# --- 1. Bridge: deps, verify, bundle -------------------------------------
Push-Location $bridgeDir
try {
    Write-Host "`n=== bridge: npm ci ==="
    npm ci
    Assert-LastExit "npm ci"

    if ($SkipTests) {
        Write-Host "=== bridge: verify SKIPPED (-SkipTests) ==="
    }
    else {
        Write-Host "`n=== bridge: npm run verify ==="
        npm run verify
        Assert-LastExit "npm run verify"
    }

    Write-Host "`n=== bridge: npm run bundle ==="
    npm run bundle
    Assert-LastExit "npm run bundle"
}
finally {
    Pop-Location
}

# --- 2. Sidecar: Node SEA exe, with the pre-approved fallback -------------
# BLUEPRINT §2.6: esbuild CJS bundle -> SEA blob -> postject into a node.exe
# copy. Any failure falls back (no ADR needed, ADR-007 §2) to shipping the
# official node.exe beside bridge.cjs; SidecarLaunchSpec launches either.
function New-SeaSidecar {
    Push-Location $bridgeDist
    try {
        @'
{
  "main": "bridge.cjs",
  "output": "sea-prep.blob",
  "disableExperimentalSEAWarning": true
}
'@ | Out-File -FilePath "sea-config.json" -Encoding utf8

        # Native stdout is piped to Out-Host throughout this function: its
        # return value is the exe path, and PowerShell would otherwise fold
        # every tool's output lines into it.
        Write-Host "`n=== sidecar: generating SEA blob ==="
        & $nodeExe --experimental-sea-config sea-config.json | Out-Host
        Assert-LastExit "node --experimental-sea-config"

        Copy-Item -Path $nodeExe -Destination "bridge.exe" -Force
        Write-Host "=== sidecar: injecting blob (postject) ==="
        # postject warns that the copied node.exe's Authenticode signature is
        # invalidated by the injection — expected; the exe ships unsigned.
        npx --yes $postjectPackage bridge.exe NODE_SEA_BLOB sea-prep.blob --sentinel-fuse $seaFuse | Out-Host
        Assert-LastExit "npx postject"

        # Boot sanity: with no session token the bridge must reach config
        # parsing and exit 1 ("config error: HTPC_BRIDGE_IPC_TOKEN..."). That
        # proves the embedded bundle actually executes in the SEA runtime.
        $savedToken = $env:HTPC_BRIDGE_IPC_TOKEN
        $env:HTPC_BRIDGE_IPC_TOKEN = $null
        try {
            & ".\bridge.exe" | Out-Host
        }
        finally {
            $env:HTPC_BRIDGE_IPC_TOKEN = $savedToken
        }
        if ($LASTEXITCODE -ne 1) {
            throw "SEA exe boot sanity check: expected exit 1 (config error), got $LASTEXITCODE"
        }
        Write-Host "=== sidecar: SEA exe boots (config-error sanity check passed) ==="
        return Join-Path $bridgeDist "bridge.exe"
    }
    finally {
        Pop-Location
    }
}

$seaExe = $null
try {
    $seaExe = New-SeaSidecar
}
catch {
    Write-Warning "Node SEA build failed: $($_.Exception.Message)"
    Write-Warning "Falling back to the node.exe + bridge.cjs sidecar layout (ADR-007 section 2)."
}

# --- 3. Tray app: self-contained single-file publish ----------------------
# ADR-003 item 7. Never PublishTrimmed (WinForms COM).
Write-Host "`n=== app: dotnet publish ($Configuration, win-x64, single file) ==="
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
& $dotnetExe publish (Join-Path $repoRoot "app\MatterHelm\MatterHelm.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
Assert-LastExit "dotnet publish"

# --- 4. Assemble dist\ ----------------------------------------------------
Write-Host "`n=== assembling dist\ ==="
if (Test-Path $distDir) {
    Remove-Item -Recurse -Force $distDir
}
New-Item -ItemType Directory -Path $distDir | Out-Null

# Publish output (MatterHelm.exe + any siblings, e.g. DemoAssets); debug
# symbols and the compiler-generated API-doc XML stay out of the shipped
# folder.
Copy-Item -Path (Join-Path $publishDir "*") -Destination $distDir -Recurse -Exclude "*.pdb", "*.xml"

$sidecarDir = Join-Path $distDir "sidecar"
New-Item -ItemType Directory -Path $sidecarDir | Out-Null
if ($null -ne $seaExe) {
    Copy-Item -Path $seaExe -Destination (Join-Path $sidecarDir "bridge.exe")
    $sidecarLayout = "Node SEA (sidecar\bridge.exe)"
}
else {
    Copy-Item -Path $nodeExe -Destination (Join-Path $sidecarDir "node.exe")
    Copy-Item -Path (Join-Path $bridgeDist "bridge.cjs") -Destination (Join-Path $sidecarDir "bridge.cjs")
    $sidecarLayout = "node.exe + bridge.cjs (ADR-007 section 2 fallback)"
}

Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $distDir
Copy-Item -Path (Join-Path $repoRoot "NOTICE") -Destination $distDir
Copy-Item -Path (Join-Path $repoRoot "docs\README-dist.md") -Destination $distDir

# --- 5. Sizes summary -----------------------------------------------------
function Get-SizeMB {
    param([string]$Path)
    if (Test-Path $Path -PathType Container) {
        $bytes = (Get-ChildItem -Path $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    }
    else {
        $bytes = (Get-Item $Path).Length
    }
    return [math]::Round($bytes / 1MB, 1)
}

Write-Host "`n=== dist summary ==="
Write-Host "sidecar layout: $sidecarLayout"
foreach ($item in Get-ChildItem -Path $distDir) {
    Write-Host ("  {0,-18} {1,8} MB" -f $item.Name, (Get-SizeMB $item.FullName))
}
Write-Host ("  {0,-18} {1,8} MB" -f "TOTAL", (Get-SizeMB $distDir))
Write-Host "`ndist ready: $distDir"
