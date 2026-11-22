<#
.SYNOPSIS
    Fills the winget manifest templates in this folder for one release.

.DESCRIPTION
    Reads CalcLink-<Version>-win-x64-setup.exe and CalcLink-<Version>-win-arm64-setup.exe from
    -InstallerDir, computes their SHA-256, and writes a ready-to-submit manifest folder:
        <OutDir>\manifests\<p>\<Publisher>\<Package>\<Version>\*.yaml
    which mirrors the layout of the microsoft/winget-pkgs repository.

    The hashes must be those of the files actually attached to the GitHub release (i.e. after
    signing). release.yml runs this script on exactly those files.

.EXAMPLE
    pwsh installer/winget/Update-WingetManifests.ps1 -Version 1.2.0 -InstallerDir artifacts/dist `
        -RepoUrl https://github.com/OWNER/CalcLink `
        -BaseUrl https://github.com/OWNER/CalcLink/releases/download/v1.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallerDir,
    [Parameter(Mandatory)] [string] $RepoUrl,
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $OutDir = 'winget-out',
    [string] $PackageIdentifier = 'CalcLink.CalcLink',
    [string] $ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoUrl = $RepoUrl.TrimEnd('/')
$BaseUrl = $BaseUrl.TrimEnd('/')

function Get-Installer([string] $Rid) {
    $name = "CalcLink-$Version-$Rid-setup.exe"
    $path = Join-Path $InstallerDir $name
    if (-not (Test-Path $path)) { throw "Missing installer: $path" }
    [pscustomobject]@{
        Url    = "$BaseUrl/$name"
        Sha256 = (Get-FileHash -Algorithm SHA256 $path).Hash.ToUpperInvariant()
    }
}

$x64 = Get-Installer 'win-x64'
$arm64 = Get-Installer 'win-arm64'

$tokens = [ordered]@{
    '__VERSION__'           = $Version
    '__RELEASE_DATE__'      = $ReleaseDate
    '__REPO_URL__'          = $RepoUrl
    '__RELEASE_NOTES_URL__' = "$RepoUrl/releases/tag/v$Version"
    '__X64_URL__'           = $x64.Url
    '__X64_SHA256__'        = $x64.Sha256
    '__ARM64_URL__'         = $arm64.Url
    '__ARM64_SHA256__'      = $arm64.Sha256
}

# winget-pkgs layout: manifests/<first letter, lower case>/<Publisher>/<Package>/<Version>/
$parts = $PackageIdentifier.Split('.')
$segments = @('manifests', $parts[0].Substring(0, 1).ToLowerInvariant()) + $parts + @($Version)
$dest = $OutDir
foreach ($s in $segments) { $dest = Join-Path $dest $s }
New-Item -ItemType Directory -Force $dest | Out-Null

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
foreach ($template in Get-ChildItem -File (Join-Path $PSScriptRoot 'CalcLink.CalcLink*.yaml')) {
    $text = [IO.File]::ReadAllText($template.FullName)
    # Drop the template-only comment line.
    $text = ($text -split "`r?`n" | Where-Object { $_ -notmatch '^# Template:' }) -join "`n"
    foreach ($k in $tokens.Keys) { $text = $text.Replace($k, $tokens[$k]) }
    $text = $text.Replace('CalcLink.CalcLink', $PackageIdentifier)
    if ($text -match '__[A-Z0-9_]+__') { throw "Unfilled token $($Matches[0]) in $($template.Name)" }
    $outName = $template.Name.Replace('CalcLink.CalcLink', $PackageIdentifier)
    [IO.File]::WriteAllText((Join-Path $dest $outName), $text, $utf8NoBom)
    Write-Host "Wrote $(Join-Path $dest $outName)"
}
