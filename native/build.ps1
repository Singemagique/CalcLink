<#
.SYNOPSIS
    Builds calclink_native.dll (and clprobe.exe) from any PowerShell window.

.DESCRIPTION
    Finds Visual Studio (C++ tools), enters its developer environment for the target
    architecture, locates a CMake >= 3.25, Ninja and vcpkg, then configures, builds and installs the
    chosen preset. Output lands in native\out\install\<preset>\bin.

    vcpkg is looked up in this order: -VcpkgRoot, $env:VCPKG_ROOT, $env:VCPKG_INSTALLATION_ROOT,
    C:\src\vcpkg, C:\vcpkg, %USERPROFILE%\vcpkg, and Visual Studio's bundled vcpkg. If the clone
    exists but vcpkg.exe does not, it is bootstrapped.

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Preset win-arm64-release -VcpkgRoot D:\tools\vcpkg
.EXAMPLE
    .\build.ps1 -TilibsSourceDir I:\Claude\tilp\tilibs   # use a local tilibs checkout instead of fetching it
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64-release', 'win-x64-debug', 'win-arm64-release')]
    [string]$Preset = 'win-x64-release',
    [string]$VcpkgRoot,
    [string]$TilibsSourceDir,
    # Diagnostic build: libticables logs every byte sent/received to %USERPROFILE%\.ticables
    [switch]$CableLogging,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Fail([string]$message) {
    Write-Host ''
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

function Invoke-Step([string]$title, [scriptblock]$action) {
    Write-Host "==> $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { Fail "$title failed (exit code $LASTEXITCODE)." }
}

# --- Visual Studio --------------------------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Fail 'Visual Studio Installer (vswhere.exe) not found. Install Visual Studio 2026 with "Desktop development with C++".' }

$targetArch = if ($Preset -like '*arm64*') { 'arm64' } else { 'amd64' }
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
$requiredComponent = if ($targetArch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }

$vs = & $vswhere -latest -products * -requires $requiredComponent -property installationPath
if (-not $vs) { Fail "No Visual Studio with the MSVC tools for $targetArch ($requiredComponent). Add it in the Visual Studio Installer." }
Write-Host "Visual Studio: $vs"

if (-not $env:VSCMD_VER -or $env:VSCMD_ARG_TGT_ARCH -ne ($targetArch -replace 'amd64', 'x64')) {
    $env:PATH = "$(Split-Path $vswhere);$env:PATH"   # VsDevCmd calls vswhere by name
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$targetArch -host_arch=$hostArch -no_logo" | Out-Null
}

# --- CMake >= 3.25 and Ninja ----------------------------------------------------------------
$cmakeCandidates = @(
    (Get-Command cmake -All -ErrorAction SilentlyContinue | ForEach-Object Source)
    (Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')
    (Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$cmake = $null
foreach ($candidate in $cmakeCandidates) {
    $line = (& $candidate --version 2>$null | Select-Object -First 1)
    if ($line -match '(\d+)\.(\d+)') {
        if ([version]"$($Matches[1]).$($Matches[2])" -ge [version]'3.25') { $cmake = $candidate; break }
        Write-Host "Skipping $candidate ($line): too old, need 3.25+"
    }
}
if (-not $cmake) {
    Fail 'CMake 3.25+ not found. Either add "C++ CMake tools for Windows" in the Visual Studio Installer, or run: winget install Kitware.CMake'
}
$env:PATH = "$(Split-Path $cmake);$env:PATH"
Write-Host "CMake: $cmake"

$ninja = Get-Command ninja -ErrorAction SilentlyContinue
if (-not $ninja) {
    $ninjaDirs = @(@(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links')              # winget install Ninja-build.Ninja
        (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages') -Directory -Filter 'Ninja-build.Ninja_*' -ErrorAction SilentlyContinue | ForEach-Object FullName)
        (Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja')
    ) | Where-Object { $_ -and (Test-Path (Join-Path $_ 'ninja.exe')) })   # @() so [0] is a path, not a char
    if ($ninjaDirs) { $env:PATH = "$($ninjaDirs[0]);$env:PATH" }
    else { Fail 'Ninja not found. Add "C++ CMake tools for Windows" in the Visual Studio Installer, or run: winget install Ninja-build.Ninja' }
}

# --- vcpkg ----------------------------------------------------------------------------------
$vcpkgCandidates = @($VcpkgRoot, $env:VCPKG_ROOT, $env:VCPKG_INSTALLATION_ROOT, 'C:\src\vcpkg', 'C:\vcpkg',
    (Join-Path $env:USERPROFILE 'vcpkg'), (Join-Path $vs 'VC\vcpkg')) |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'scripts\buildsystems\vcpkg.cmake')) }
$vcpkg = $vcpkgCandidates | Select-Object -First 1
if (-not $vcpkg) {
    Fail @'
vcpkg not found. Install it once with:
    git clone https://github.com/microsoft/vcpkg C:\src\vcpkg
    C:\src\vcpkg\bootstrap-vcpkg.bat -disableMetrics
then re-run this script (or pass -VcpkgRoot <path>).
'@
}
if (-not (Test-Path (Join-Path $vcpkg 'vcpkg.exe'))) {
    Invoke-Step "Bootstrapping vcpkg in $vcpkg" { & (Join-Path $vcpkg 'bootstrap-vcpkg.bat') -disableMetrics }
}
$env:VCPKG_ROOT = $vcpkg
Write-Host "vcpkg: $vcpkg"

# --- Configure / build / install ------------------------------------------------------------
$buildDir = "out/build/$Preset"
if ($Clean -and (Test-Path $buildDir)) { Remove-Item -Recurse -Force $buildDir }

$ninjaExe = (Get-Command ninja -ErrorAction Stop).Source
Write-Host "Ninja: $ninjaExe"
$configureArgs = @('--preset', $Preset, "-DCMAKE_MAKE_PROGRAM=$($ninjaExe -replace '\\', '/')",
    "-DCALCLINK_TILIBS_CABLE_LOGGING=$(if ($CableLogging) { 'ON' } else { 'OFF' })")
if ($TilibsSourceDir) {
    if (-not (Test-Path (Join-Path $TilibsSourceDir 'CMakeLists.txt'))) { Fail "No tilibs checkout at $TilibsSourceDir" }
    $configureArgs += "-DTILIBS_SOURCE_DIR=$((Resolve-Path $TilibsSourceDir).Path -replace '\\', '/')"
}

Invoke-Step "Configuring $Preset (first run builds GLib and friends via vcpkg; this can take 20-40 minutes)" { & $cmake @configureArgs }
Invoke-Step "Building $Preset" { & $cmake --build --preset $Preset }
Invoke-Step "Installing $Preset" { & $cmake --install $buildDir }

$bin = Join-Path $PSScriptRoot "out\install\$Preset\bin"
Write-Host ''
Write-Host "Done. Native backend: $bin" -ForegroundColor Green
Get-ChildItem $bin -File | Select-Object Name, Length | Format-Table -AutoSize
