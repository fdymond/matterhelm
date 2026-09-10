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
                                 only with -AllowNodeLayoutFallback)
    dist\LICENSE, dist\NOTICE, dist\THIRD-PARTY-NOTICES.txt,
    dist\README-dist.md, dist\sidecar-layout.json

No Node.js or .NET is required on the target machine.

Requires on the BUILD machine: Node.js 22.13+ / npm on PATH, dotnet SDK on PATH
(or at C:\Program Files\dotnet). PowerShell 5.1 compatible.

The postject call scopes native stderr handling so PowerShell 5.1 callers that
redirect with `2>&1` cannot silently select the fallback layout.

.PARAMETER Configuration
dotnet build configuration for the tray app (default Release).

.PARAMETER SkipTests
Skips `npm run verify` (lint + typecheck + tests). Use only when the same
commit was already verified — e.g. the release workflow runs verify and the
app test suite as explicit gate steps immediately before calling this script.

.PARAMETER AllowNodeLayoutFallback
Allows packaging node.exe + bridge.cjs if SEA construction fails. Without this
explicit opt-in, an SEA failure terminates the build.

.EXAMPLE
./build.ps1
.EXAMPLE
./build.ps1 -Configuration Release -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$AllowNodeLayoutFallback
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
$postjectExe = Join-Path $bridgeDir "node_modules\.bin\postject.cmd"

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

# --- 2. Sidecar: Node SEA exe, with an explicit opt-in fallback ------------
# BLUEPRINT §2.6: esbuild CJS bundle -> SEA blob -> postject into a node.exe
# copy. With -AllowNodeLayoutFallback, a failure may use the pre-approved
# official node.exe beside bridge.cjs (ADR-007 §2); otherwise it fails loudly.
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
        if (-not (Test-Path -LiteralPath $postjectExe -PathType Leaf)) {
            throw "local postject binary not found at $postjectExe; run npm ci"
        }
        $savedErrorActionPreference = $ErrorActionPreference
        try {
            # Windows PowerShell 5.1 can promote native stderr redirected with
            # 2>&1 into terminating ErrorRecords. postject's signature warning
            # is expected, so judge this invocation by its process exit code.
            $ErrorActionPreference = "Continue"
            & $postjectExe bridge.exe NODE_SEA_BLOB sea-prep.blob --sentinel-fuse $seaFuse | Out-Host
            $postjectExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }
        if ($postjectExitCode -ne 0) {
            throw "local postject failed with exit code $postjectExitCode"
        }

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
    $seaFailure = $_.Exception.Message
    if (-not $AllowNodeLayoutFallback) {
        throw "Node SEA build failed and node-layout fallback was not allowed: $seaFailure. Re-run with -AllowNodeLayoutFallback to opt in."
    }
    Write-Warning "Node SEA build failed: $seaFailure"
    Write-Warning "Explicitly falling back to the node.exe + bridge.cjs sidecar layout (ADR-007 section 2)."
    Write-Host "BUILD LAYOUT: node (sidecar\node.exe + sidecar\bridge.cjs)"
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
function Resolve-InstalledPackage {
    param([string]$Name, [string]$FromDirectory)

    $current = [IO.Path]::GetFullPath($FromDirectory)
    while ($current.StartsWith($bridgeDir, [StringComparison]::OrdinalIgnoreCase)) {
        $candidate = Join-Path (Join-Path $current "node_modules") $Name
        if (Test-Path -LiteralPath (Join-Path $candidate "package.json") -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) {
            break
        }
        $current = $parent
    }
    return $null
}

function Get-ProductionPackages {
    $rootManifest = Get-Content -LiteralPath (Join-Path $bridgeDir "package.json") -Raw | ConvertFrom-Json
    $queue = New-Object System.Collections.Queue
    $rootDependencies = $rootManifest.PSObject.Properties["dependencies"].Value
    foreach ($property in $rootDependencies.PSObject.Properties) {
        $queue.Enqueue([PSCustomObject]@{ Name = $property.Name; From = $bridgeDir; Optional = $false })
    }

    $seen = @{}
    $packages = @()
    while ($queue.Count -gt 0) {
        $request = $queue.Dequeue()
        $packageDir = Resolve-InstalledPackage $request.Name $request.From
        if ($null -eq $packageDir) {
            if ($request.Optional) {
                continue
            }
            throw "production dependency $($request.Name) is missing under bridge\node_modules"
        }
        $seenKey = $packageDir.ToLowerInvariant()
        if ($seen.ContainsKey($seenKey)) {
            continue
        }
        $seen[$seenKey] = $true

        $manifest = Get-Content -LiteralPath (Join-Path $packageDir "package.json") -Raw | ConvertFrom-Json
        $licenseProperty = $manifest.PSObject.Properties["license"]
        $licenseValue = if ($null -eq $licenseProperty) {
            "not declared"
        } elseif ($licenseProperty.Value -is [string]) {
            $licenseProperty.Value
        } else {
            $licenseProperty.Value | ConvertTo-Json -Compress
        }
        $packages += [PSCustomObject]@{
            Name = [string]$manifest.name
            Version = [string]$manifest.version
            License = $licenseValue
            Directory = $packageDir
        }

        $dependencies = $manifest.PSObject.Properties["dependencies"]
        if ($null -ne $dependencies) {
            foreach ($property in $dependencies.Value.PSObject.Properties) {
                $queue.Enqueue([PSCustomObject]@{
                    Name = $property.Name
                    From = $packageDir
                    Optional = $false
                })
            }
        }
        $optionalDependencies = $manifest.PSObject.Properties["optionalDependencies"]
        if ($null -ne $optionalDependencies) {
            foreach ($property in $optionalDependencies.Value.PSObject.Properties) {
                $queue.Enqueue([PSCustomObject]@{
                    Name = $property.Name
                    From = $packageDir
                    Optional = $true
                })
            }
        }
    }
    return $packages | Sort-Object Name, Version, Directory
}

function Add-NoticeSection {
    param(
        [Text.StringBuilder]$Builder,
        [string]$Heading,
        [string]$License,
        [string[]]$LicenseFiles
    )

    [void]$Builder.AppendLine("==============================================================================")
    [void]$Builder.AppendLine($Heading)
    [void]$Builder.AppendLine("License: $License")
    [void]$Builder.AppendLine("==============================================================================")
    [void]$Builder.AppendLine()
    foreach ($licenseFile in $LicenseFiles) {
        [void]$Builder.AppendLine("--- $(Split-Path -Leaf $licenseFile) ---")
        [void]$Builder.AppendLine((Get-Content -LiteralPath $licenseFile -Raw).TrimEnd())
        [void]$Builder.AppendLine()
    }
}

function Get-DotNetRuntimePacks {
    $projectPath = Join-Path $repoRoot "app\MatterHelm\MatterHelm.csproj"
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $targetFramework = $project.SelectSingleNode('//TargetFramework').InnerText
    $depsFiles = @(
        (Join-Path $publishDir "MatterHelm.deps.json"),
        (Join-Path $repoRoot "app\MatterHelm\obj\$Configuration\$targetFramework\win-x64\MatterHelm.deps.json")
    )
    $requiredPacks = @(
        [PSCustomObject]@{
            Dependency = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64"
            PackageId = "Microsoft.NETCore.App.Runtime.win-x64"
        },
        [PSCustomObject]@{
            Dependency = "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64"
            PackageId = "Microsoft.WindowsDesktop.App.Runtime.win-x64"
        },
        [PSCustomObject]@{
            Dependency = "runtimepack.Microsoft.Windows.SDK.NET.Ref"
            PackageId = "Microsoft.Windows.SDK.NET.Ref"
        }
    )

    foreach ($depsFile in $depsFiles) {
        if (-not (Test-Path -LiteralPath $depsFile -PathType Leaf)) {
            continue
        }

        $deps = Get-Content -LiteralPath $depsFile -Raw | ConvertFrom-Json
        foreach ($target in $deps.targets.PSObject.Properties) {
            if ($target.Name -notlike "*/win-x64") {
                continue
            }
            $appLibrary = $target.Value.PSObject.Properties |
                Where-Object { $_.Name -like "MatterHelm/*" } |
                Select-Object -First 1
            if ($null -eq $appLibrary) {
                continue
            }
            $dependencyProperty = $appLibrary.Value.PSObject.Properties["dependencies"]
            if ($null -eq $dependencyProperty) {
                continue
            }
            $dependencies = $dependencyProperty.Value
            $resolved = @()
            foreach ($required in $requiredPacks) {
                $runtime = $dependencies.PSObject.Properties[$required.Dependency]
                if ($null -eq $runtime) {
                    $resolved = @()
                    break
                }
                $resolved += [PSCustomObject]@{
                    PackageId = $required.PackageId
                    Version = [string]$runtime.Value
                }
            }
            if ($resolved.Count -eq $requiredPacks.Count) {
                return $resolved
            }
        }
    }
    throw "published app dependency metadata does not identify all required .NET runtime packs: $($requiredPacks.PackageId -join ', ')"
}

function Resolve-DotNetRuntimePackDirectory {
    param([string]$PackageId, [string]$Version)

    $runtimePack = Join-Path $env:USERPROFILE ".nuget\packages\$($PackageId.ToLowerInvariant())\$Version"
    if (-not (Test-Path -LiteralPath $runtimePack -PathType Container)) {
        throw "referenced runtime pack $PackageId@$Version was not found at '$runtimePack'; refusing to emit incomplete third-party notices"
    }
    return $runtimePack
}

function New-ThirdPartyNotices {
    param([string]$OutputPath)

    $builder = New-Object Text.StringBuilder
    [void]$builder.AppendLine("MatterHelm third-party notices")
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("Generated from the exact production dependency closure installed for this build.")
    [void]$builder.AppendLine()

    foreach ($package in (Get-ProductionPackages)) {
        $noticeFiles = @(Get-ChildItem -LiteralPath $package.Directory -File | Where-Object {
            $_.Name -match '^(LICENSE|LICENCE|NOTICE)(\..*)?$'
        } | Sort-Object Name | ForEach-Object { $_.FullName })
        $licenseFiles = @($noticeFiles | Where-Object {
            (Split-Path -Leaf $_) -match '^(LICENSE|LICENCE)(\..*)?$'
        })
        if ($licenseFiles.Count -eq 0) {
            throw "production dependency $($package.Name)@$($package.Version) has no license text"
        }
        Add-NoticeSection $builder "$($package.Name)@$($package.Version)" $package.License $noticeFiles
    }

    $nodeLicense = Join-Path (Split-Path -Parent $nodeExe) "LICENSE"
    if (-not (Test-Path -LiteralPath $nodeLicense -PathType Leaf)) {
        throw "Node.js runtime license not found beside $nodeExe"
    }
    Add-NoticeSection $builder "Node.js@$nodeVersionText" "see included Node.js LICENSE" @($nodeLicense)

    $runtimePacks = @(Get-DotNetRuntimePacks)
    $coreRuntime = $runtimePacks | Where-Object { $_.PackageId -eq "Microsoft.NETCore.App.Runtime.win-x64" }
    $coreRuntimeDir = Resolve-DotNetRuntimePackDirectory $coreRuntime.PackageId $coreRuntime.Version
    $coreLicense = Join-Path $coreRuntimeDir "LICENSE.TXT"
    $coreNotices = Join-Path $coreRuntimeDir "THIRD-PARTY-NOTICES.TXT"
    if (-not (Test-Path -LiteralPath $coreLicense -PathType Leaf) -or
        -not (Test-Path -LiteralPath $coreNotices -PathType Leaf)) {
        throw "referenced runtime pack $($coreRuntime.PackageId)@$($coreRuntime.Version) is missing LICENSE.TXT or THIRD-PARTY-NOTICES.TXT"
    }
    Add-NoticeSection $builder "$($coreRuntime.PackageId)@$($coreRuntime.Version)" `
        "MIT and third-party licenses; see included files" @($coreLicense, $coreNotices)

    $desktopRuntime = $runtimePacks | Where-Object { $_.PackageId -eq "Microsoft.WindowsDesktop.App.Runtime.win-x64" }
    $desktopRuntimeDir = Resolve-DotNetRuntimePackDirectory $desktopRuntime.PackageId $desktopRuntime.Version
    $desktopLicense = Join-Path $desktopRuntimeDir "LICENSE"
    if (-not (Test-Path -LiteralPath $desktopLicense -PathType Leaf)) {
        throw "referenced runtime pack $($desktopRuntime.PackageId)@$($desktopRuntime.Version) is missing its LICENSE file"
    }
    Add-NoticeSection $builder "$($desktopRuntime.PackageId)@$($desktopRuntime.Version)" "MIT" @($desktopLicense)

    $windowsSdk = $runtimePacks | Where-Object { $_.PackageId -eq "Microsoft.Windows.SDK.NET.Ref" }
    $null = Resolve-DotNetRuntimePackDirectory $windowsSdk.PackageId $windowsSdk.Version
    Add-NoticeSection $builder "$($windowsSdk.PackageId)@$($windowsSdk.Version)" `
        "Windows SDK reference metadata; license terms: https://aka.ms/WinSDKLicenseURL" @()

    [xml]$appProject = Get-Content -LiteralPath (Join-Path $repoRoot "app\MatterHelm\MatterHelm.csproj") -Raw
    $qrReference = $appProject.SelectSingleNode('//PackageReference[@Include="QRCoder"]')
    if ($null -eq $qrReference) {
        throw "QRCoder PackageReference not found in MatterHelm.csproj"
    }
    $qrVersion = $qrReference.GetAttribute("Version")
    $qrLicense = Join-Path $env:USERPROFILE ".nuget\packages\qrcoder\$qrVersion\LICENSE.txt"
    if (Test-Path -LiteralPath $qrLicense -PathType Leaf) {
        Add-NoticeSection $builder "QRCoder@$qrVersion" "MIT" @($qrLicense)
    }
    else {
        [void]$builder.AppendLine("==============================================================================")
        [void]$builder.AppendLine("QRCoder@$qrVersion")
        [void]$builder.AppendLine("License: MIT (embedded fallback; NuGet package license file was unavailable)")
        [void]$builder.AppendLine("==============================================================================")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("Copyright (c) 2013-2025 Raffael Herrmann")
        [void]$builder.AppendLine("Copyright (c) 2024-2025 Shane Krueger")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("Permission is hereby granted, free of charge, to any person obtaining a copy")
        [void]$builder.AppendLine("of this software and associated documentation files (the 'Software'), to deal")
        [void]$builder.AppendLine("in the Software without restriction, including without limitation the rights")
        [void]$builder.AppendLine("to use, copy, modify, merge, publish, distribute, sublicense, and/or sell")
        [void]$builder.AppendLine("copies of the Software, and to permit persons to whom the Software is")
        [void]$builder.AppendLine("furnished to do so, subject to the following conditions:")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("The above copyright notice and this permission notice shall be included in all")
        [void]$builder.AppendLine("copies or substantial portions of the Software.")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("THE SOFTWARE IS PROVIDED 'AS IS', WITHOUT WARRANTY OF ANY KIND, EXPRESS OR")
        [void]$builder.AppendLine("IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,")
        [void]$builder.AppendLine("FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE")
        [void]$builder.AppendLine("AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER")
        [void]$builder.AppendLine("LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,")
        [void]$builder.AppendLine("OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE")
        [void]$builder.AppendLine("SOFTWARE.")
        [void]$builder.AppendLine()
    }

    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($OutputPath, $builder.ToString(), $utf8NoBom)
}

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
    $sidecarLayoutJson = '{"layout":"sea","files":["sidecar/bridge.exe"]}'
}
else {
    Copy-Item -Path $nodeExe -Destination (Join-Path $sidecarDir "node.exe")
    Copy-Item -Path (Join-Path $bridgeDist "bridge.cjs") -Destination (Join-Path $sidecarDir "bridge.cjs")
    $sidecarLayout = "node.exe + bridge.cjs (ADR-007 section 2 fallback)"
    $sidecarLayoutJson = '{"layout":"node","files":["sidecar/node.exe","sidecar/bridge.cjs"]}'
}

# `files` is informational metadata for packaging tools. Runtime and updater
# readers must select behavior solely from the authoritative `layout` value.
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $distDir "sidecar-layout.json"), $sidecarLayoutJson, $utf8NoBom)

Copy-Item -Path (Join-Path $repoRoot "LICENSE") -Destination $distDir
Copy-Item -Path (Join-Path $repoRoot "NOTICE") -Destination $distDir
Copy-Item -Path (Join-Path $repoRoot "docs\README-dist.md") -Destination $distDir
New-ThirdPartyNotices (Join-Path $distDir "THIRD-PARTY-NOTICES.txt")

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
