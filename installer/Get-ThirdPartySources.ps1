<#
.SYNOPSIS
    Bundles the source code of the third-party libraries CalcLink ships, as built, into one zip.

.DESCRIPTION
    CalcLink's packages include LGPL libraries (GLib, libintl, libiconv, libusb) and GPL code
    (tilibs). Their licenses ask whoever distributes the binaries to also make the corresponding
    source available. Releases do that by attaching this zip next to the binaries, so nobody has
    to request it.

    For each library the native build installed, vcpkg's vcpkg.spdx.json records the exact
    source archive (URL and SHA-512) and the git tree of the port recipe (portfile and patches)
    it was built with. This script collects:
        sources\<archive>        each source archive, checked against vcpkg's SHA-512. It's
                                 taken from vcpkg's downloads folder when present, otherwise
                                 downloaded from the recorded URL.
        ports\<port>.zip         the vcpkg port recipe (build script and patches), from the
                                 vcpkg clone's git history.
        tilibs\<commit>.tar.gz   tilibs at the commit native/CMakeLists.txt pins, plus the
                                 patches from native/patches.
        README.txt               what's where.

.PARAMETER VcpkgInstalled
    The vcpkg installed tree for one triplet, e.g. native\out\build\win-x64-release\vcpkg_installed\x64-windows.
.PARAMETER VcpkgRoot
    The vcpkg clone the build used (default: $env:VCPKG_ROOT). Needed for the port recipes.
.PARAMETER DownloadsDir
    Where to look for already-downloaded archives (default: <VcpkgRoot>\downloads).
.PARAMETER OutFile
    The zip to write.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $VcpkgInstalled,
    [string] $VcpkgRoot = $env:VCPKG_ROOT,
    [string] $DownloadsDir,
    [Parameter(Mandatory)] [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without it

# Keep in sync with the license list in native/CMakeLists.txt: every vcpkg library that ships.
$Ports = 'glib', 'gettext-libintl', 'libiconv', 'libusb', 'pcre2', 'zlib', 'libarchive'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $VcpkgRoot -or -not (Test-Path (Join-Path $VcpkgRoot '.git'))) {
    throw "VcpkgRoot '$VcpkgRoot' is not a vcpkg git clone (set VCPKG_ROOT or pass -VcpkgRoot)."
}
if (-not $DownloadsDir) { $DownloadsDir = Join-Path $VcpkgRoot 'downloads' }

$work = Join-Path ([IO.Path]::GetTempPath()) ("calclink-sources-" + [guid]::NewGuid().ToString('N'))
$sources = New-Item -ItemType Directory -Force (Join-Path $work 'sources')
$portsDir = New-Item -ItemType Directory -Force (Join-Path $work 'ports')
$tilibsDir = New-Item -ItemType Directory -Force (Join-Path $work 'tilibs')

function Get-Sha512([string] $Path) { (Get-FileHash -Algorithm SHA512 $Path).Hash.ToLowerInvariant() }

$readme = [System.Collections.Generic.List[string]]::new()
$readme.Add('Source code of the third-party libraries shipped with this CalcLink release, as built.')
$readme.Add('CalcLink''s own source is the release''s Git tag (GitHub''s "Source code" downloads).')
$readme.Add('')

try {
    foreach ($port in $Ports) {
        $spdxPath = Join-Path $VcpkgInstalled "share\$port\vcpkg.spdx.json"
        if (-not (Test-Path $spdxPath)) { throw "No $spdxPath. Build native/ first." }
        $spdx = Get-Content -Raw $spdxPath | ConvertFrom-Json
        $version = ($spdx.packages | Where-Object { $_.name -eq $port } | Select-Object -First 1).versionInfo
        $readme.Add("$port $version")

        foreach ($pkg in $spdx.packages) {
            $loc = [string] $pkg.downloadLocation
            if ($loc -match '^git\+https://github\.com/Microsoft/vcpkg@([0-9a-f]{40})$') {
                # The port recipe: a git tree in the vcpkg repository.
                $tree = $Matches[1]
                $zip = Join-Path $portsDir "$port.zip"
                git -C $VcpkgRoot archive --format=zip -o $zip $tree
                if ($LASTEXITCODE -ne 0) { throw "vcpkg tree $tree ($port) not found in $VcpkgRoot. Fetch its full history." }
                $readme.Add("    ports\$port.zip  (vcpkg port recipe, git tree $tree)")
                continue
            }
            $sha = ($pkg.checksums | Where-Object { $_.algorithm -eq 'SHA512' } | Select-Object -First 1).checksumValue
            if (-not $sha) { continue }
            if ($pkg.name -like '*.json') { continue }   # e.g. PCRE2's "gitlink" metadata; the submodule itself is listed separately

            if ($loc -match '^git\+https://github\.com/([^/]+)/([^@]+)@(.+)$') {
                # vcpkg_from_github: the repository's tarball at that ref.
                $url = "https://github.com/$($Matches[1])/$($Matches[2])/archive/$($Matches[3]).tar.gz"
                $name = "$($Matches[1])-$($Matches[2])-$($Matches[3]).tar.gz" -replace '/', '-'
            }
            elseif ($loc -match '^https?://') {
                $url = $loc
                $name = [string] $pkg.name
            }
            else { throw "Unexpected download location for $port`: $loc" }

            $dest = Join-Path $sources $name
            $cached = Join-Path $DownloadsDir $name
            if ((Test-Path $cached) -and (Get-Sha512 $cached) -eq $sha.ToLowerInvariant()) {
                Copy-Item $cached $dest
            }
            else {
                Write-Host "Downloading $url"
                Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
                $got = Get-Sha512 $dest
                if ($got -ne $sha.ToLowerInvariant()) {
                    throw "SHA-512 mismatch for $url`n  expected $sha`n  got      $got"
                }
            }
            $readme.Add("    sources\$name  ($url)")
        }
    }

    # tilibs at the pinned commit, plus CalcLink's patches to it.
    $cmake = Get-Content -Raw (Join-Path $RepoRoot 'native\CMakeLists.txt')
    if ($cmake -notmatch 'TILIBS_GIT_TAG "([0-9a-f]{40})"') { throw 'No TILIBS_GIT_TAG commit in native/CMakeLists.txt.' }
    $commit = $Matches[1]
    $url = "https://github.com/debrouxl/tilibs/archive/$commit.tar.gz"
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile (Join-Path $tilibsDir "tilibs-$commit.tar.gz") -UseBasicParsing
    Copy-Item (Join-Path $RepoRoot 'native\patches\*.patch') $tilibsDir
    $readme.Add('')
    $readme.Add("tilibs $commit")
    $readme.Add("    tilibs\tilibs-$commit.tar.gz  ($url)")
    $readme.Add('    tilibs\*.patch  (applied by CalcLink''s native build; see native/README.md)')

    $readme.Add('')
    $readme.Add('Each sources\ archive matches the SHA-512 that vcpkg checked when it built the library.')
    $readme.Add('To rebuild: check out the CalcLink tag and follow docs/BUILDING.md; vcpkg uses the same archives and recipes.')
    Set-Content -Path (Join-Path $work 'README.txt') -Value $readme -Encoding utf8

    $outDir = Split-Path -Parent ([IO.Path]::GetFullPath($OutFile))
    New-Item -ItemType Directory -Force $outDir | Out-Null
    if (Test-Path $OutFile) { Remove-Item $OutFile }
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $OutFile -CompressionLevel Optimal
    $size = [math]::Round((Get-Item $OutFile).Length / 1MB, 1)
    Write-Host "Wrote $OutFile ($size MB)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
