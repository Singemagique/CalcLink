<#
.SYNOPSIS
    Publishes CalcLink for one runtime, adds the native backend, and zips a portable build.

.DESCRIPTION
    Used by CI (.github/workflows/ci.yml, release.yml) and for local builds (docs/BUILDING.md).

    The staged layout is:
        <OutDir>\stage\<Rid>\CalcLink\
            CalcLink.exe, CalcLink.dll, ... (dotnet publish output, self-contained)
            calclink-cli.exe (command-line tool, same runtime)
            native\<Rid>\calclink_native.dll, tilibs/glib/libusb DLLs, clprobe.exe, calclink-driver.exe
            LICENSE.txt
            docs\DRIVERS.md
    and the outputs are:
        <OutDir>\dist\CalcLink-<Version>-<Rid>-portable.zip
        <OutDir>\dist\CalcLink-<Version>-<Rid>-symbols.zip   (all *.pdb)

    The native DLLs go in native\<Rid>\ by default rather than next to CalcLink.exe, so the
    GLib/iconv/libusb DLLs stay out of the app root and cannot collide with the .NET publish
    output. CalcLink.Core's DLL resolver
    (src/CalcLink.Core/Native/NativeRuntime.cs) probes native\<rid>\calclink_native.dll and loads it by
    absolute path, so Windows finds its dependencies in the same folder.
    -NativeLayout Flat copies the DLLs next to the exe instead, and fails on any name collision.

.PARAMETER Rid
    win-x64 or win-arm64.
.PARAMETER Version
    Version string for the file names and -p:Version (default: <Version> in Directory.Build.props).
.PARAMETER NativeBin
    Folder holding calclink_native.dll and its runtime DLLs
    (default: native\out\install\<rid>-release\bin, i.e. the CMake preset install dir).
.PARAMETER SkipPublish
    Reuse an existing stage folder (e.g. after code signing) and only re-create the zips.
.PARAMETER AllowMissingNative
    Package without calclink_native.dll (the app then runs on the simulator only). Off by default.

.EXAMPLE
    pwsh installer/Build-Portable.ps1 -Rid win-x64
.EXAMPLE
    pwsh installer/Build-Portable.ps1 -Rid win-arm64 -Version 1.2.0 -NativeBin C:\tmp\native-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Rid = 'win-x64',
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $NativeBin,
    [string] $OutDir,
    [ValidateSet('Subfolder', 'Flat')]
    [string] $NativeLayout = 'Subfolder',
    [switch] $SkipPublish,
    [switch] $NoZip,
    [switch] $AllowMissingNative
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'artifacts' }
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
if ($NativeBin) { $NativeBin = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($NativeBin) }

if (-not $Version) {
    [xml] $props = Get-Content -Raw (Join-Path $RepoRoot 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'No <Version> in Directory.Build.props; pass -Version.' }
}

$Platform = if ($Rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$Preset = "$Rid-release"                    # CMake preset name: win-x64-release / win-arm64-release
if (-not $NativeBin) { $NativeBin = Join-Path $RepoRoot "native\out\install\$Preset\bin" }

$StageRoot = Join-Path $OutDir "stage\$Rid"
$Stage = Join-Path $StageRoot 'CalcLink'
$Symbols = Join-Path $OutDir "symbols\$Rid"
$Dist = Join-Path $OutDir 'dist'
$ExeName = 'CalcLink.exe'   # AssemblyName in src/CalcLink.App/CalcLink.App.csproj

function Invoke-Native([string] $File, [string[]] $Arguments) {
    Write-Host "> $File $($Arguments -join ' ')"
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with code $LASTEXITCODE" }
}

if (-not $SkipPublish) {
    if (Test-Path $StageRoot) { Remove-Item -Recurse -Force $StageRoot }
    if (Test-Path $Symbols) { Remove-Item -Recurse -Force $Symbols }
    New-Item -ItemType Directory -Force $Stage, $Symbols | Out-Null

    # 1. Self-contained WinUI 3 publish (SelfContained/WindowsAppSDKSelfContained are set in the csproj).
    #    CalcLinkNativeBin points nowhere: the projects' dev-convenience native copy is off, because
    #    step 2 places the native files itself (and refuses collisions).
    $noNative = Join-Path $StageRoot '__no-native'
    Invoke-Native 'dotnet' @(
        'publish', (Join-Path $RepoRoot 'src\CalcLink.App'),
        '-c', $Configuration, '-r', $Rid, "-p:Platform=$Platform",
        "-p:Version=$Version", "-p:CalcLinkNativeBin=$noNative", '-o', $Stage, '--nologo')

    if (-not (Test-Path (Join-Path $Stage $ExeName))) {
        throw "$ExeName not found in publish output ($Stage). Did the app's AssemblyName change?"
    }

    # 1b. The command-line tool (calclink-cli.exe), self-contained on the same .NET runtime. It's
    #     published apart and merged in: files it shares with the app must be identical.
    $cliOut = Join-Path $StageRoot 'cli'
    Invoke-Native 'dotnet' @(
        'publish', (Join-Path $RepoRoot 'src\CalcLink.Cli'),
        '-c', $Configuration, '-r', $Rid, '--self-contained', "-p:Platform=$Platform",
        "-p:Version=$Version", "-p:CalcLinkNativeBin=$noNative", '-o', $cliOut, '--nologo')
    $cliRoot = (Get-Item $cliOut).FullName   # long form; $cliOut may be an 8.3 short path
    foreach ($f in Get-ChildItem -Recurse -File $cliRoot) {
        $rel = [IO.Path]::GetRelativePath($cliRoot, $f.FullName)
        $dest = Join-Path $Stage $rel
        if (Test-Path $dest) {
            if ((Get-FileHash $dest).Hash -ne (Get-FileHash $f.FullName).Hash) {
                throw "calclink-cli and the app both ship '$rel', with different contents (runtime version mismatch?)."
            }
            continue
        }
        New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
        Copy-Item $f.FullName $dest
    }
    Remove-Item -Recurse -Force $cliOut
    if (-not (Test-Path (Join-Path $Stage 'calclink-cli.exe'))) {
        throw "calclink-cli.exe not found after merging the CLI publish."
    }

    # 2. Native backend.
    $nativeDll = Join-Path $NativeBin 'calclink_native.dll'
    if (Test-Path $nativeDll) {
        $nativeDest = if ($NativeLayout -eq 'Subfolder') { Join-Path $Stage "native\$Rid" } else { $Stage }
        New-Item -ItemType Directory -Force $nativeDest | Out-Null
        foreach ($f in Get-ChildItem -File $NativeBin) {
            $dest = Join-Path $nativeDest $f.Name
            if (Test-Path $dest) {
                # Test-Path is case-insensitive on NTFS, so this also catches clashes that differ only in case.
                throw "Native file '$($f.Name)' collides with an existing file in $nativeDest."
            }
            Copy-Item $f.FullName $dest
        }
        Write-Host "Native backend copied from $NativeBin to $nativeDest"
    }
    elseif ($AllowMissingNative) {
        Write-Warning "calclink_native.dll not found in $NativeBin; packaging a simulator-only build."
    }
    else {
        throw "calclink_native.dll not found in $NativeBin (build native/ first, or pass -NativeBin / -AllowMissingNative)."
    }

    # 3. License and the driver guide (the installer links to it as well).
    Copy-Item (Join-Path $RepoRoot 'LICENSE') (Join-Path $Stage 'LICENSE.txt')
    $driversDoc = Join-Path $RepoRoot 'docs\DRIVERS.md'
    if (Test-Path $driversDoc) {
        New-Item -ItemType Directory -Force (Join-Path $Stage 'docs') | Out-Null
        Copy-Item $driversDoc (Join-Path $Stage 'docs\DRIVERS.md')
    }

    # 4. Symbols out of the shipped tree.
    Get-ChildItem -Recurse -File -Filter *.pdb $Stage | ForEach-Object {
        $rel = $_.FullName.Substring($Stage.Length).TrimStart('\')
        $dest = Join-Path $Symbols $rel
        New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
        Move-Item $_.FullName $dest -Force
    }
}
elseif (-not (Test-Path (Join-Path $Stage $ExeName))) {
    throw "-SkipPublish given but $Stage has no $ExeName."
}

$zip = Join-Path $Dist "CalcLink-$Version-$Rid-portable.zip"
$symZip = Join-Path $Dist "CalcLink-$Version-$Rid-symbols.zip"
if (-not $NoZip) {
    New-Item -ItemType Directory -Force $Dist | Out-Null
    Remove-Item -Force -ErrorAction SilentlyContinue $zip, $symZip
    # Zip the CalcLink folder itself, so extracting gives CalcLink\CalcLink.exe.
    Compress-Archive -Path $Stage -DestinationPath $zip -CompressionLevel Optimal
    if ((Test-Path $Symbols) -and (Get-ChildItem -Recurse -File $Symbols | Select-Object -First 1)) {
        Compress-Archive -Path (Join-Path $Symbols '*') -DestinationPath $symZip -CompressionLevel Optimal
    }
    Write-Host "Portable zip: $zip"
}

# Expose paths to GitHub Actions steps.
if ($env:GITHUB_OUTPUT) {
    "stage=$Stage" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
    "dist=$Dist" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
    "zip=$zip" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT
}

[pscustomobject]@{ Stage = $Stage; Zip = $zip; Symbols = $symZip; Version = $Version; Rid = $Rid }
