# winget manifests

These are templates for publishing CalcLink to the [Windows Package Manager community repository](https://github.com/microsoft/winget-pkgs). They use the multi-file format with manifest schema **1.9.0**. Newer schema versions may exist; `wingetcreate update` migrates templates automatically.

| File | Manifest type |
|---|---|
| `CalcLink.CalcLink.yaml` | version |
| `CalcLink.CalcLink.installer.yaml` | installer (Inno Setup, x64 + arm64, user and machine scope) |
| `CalcLink.CalcLink.locale.en-US.yaml` | defaultLocale |

Placeholders look like `__VERSION__`, `__X64_URL__` or `__X64_SHA256__`. `Update-WingetManifests.ps1` fills them in.

The package identifier `CalcLink.CalcLink` is a placeholder too. Pick the final `Publisher.Package` before the first submission and keep it unchanged afterwards. If you pass `-PackageIdentifier`, the script renames the files and their contents to match.

## Generating manifests for a release

`release.yml` does this automatically. It uploads the result as the **winget-manifests** workflow artifact, built from the exact signed installers attached to the GitHub release.

To run it by hand on downloaded release assets:

```powershell
pwsh installer/winget/Update-WingetManifests.ps1 -Version 1.2.0 -InstallerDir .\downloads `
    -RepoUrl https://github.com/OWNER/CalcLink `
    -BaseUrl https://github.com/OWNER/CalcLink/releases/download/v1.2.0
```

The output goes to `winget-out/manifests/c/CalcLink/CalcLink/1.2.0/`.

> The hashes must match the files on the release page byte for byte. If an installer is re-signed or re-uploaded, regenerate the manifests.

## Validating and testing locally

```powershell
winget validate --manifest winget-out\manifests\c\CalcLink\CalcLink\1.2.0
# Allows installing from a local manifest (needs an elevated prompt):
winget settings --enable LocalManifestFiles
winget install --manifest winget-out\manifests\c\CalcLink\CalcLink\1.2.0
winget uninstall --id CalcLink.CalcLink
```

Test both `--scope user` and `--scope machine`, and test on an ARM64 machine if one is available.

## Submitting to winget-pkgs

Use either option:

* **wingetcreate.** This is the usual route.
  * For the first version: `wingetcreate submit --token <PAT> winget-out\manifests\c\CalcLink\CalcLink\1.2.0`
  * For later versions, wingetcreate can create the manifests itself: `wingetcreate update CalcLink.CalcLink --version 1.3.0 --urls "<x64-url>|x64" "<arm64-url>|arm64" --submit --token <PAT>`
* **Manual pull request.**
  1. Fork `microsoft/winget-pkgs`.
  2. Copy the folder to `manifests/c/CalcLink/CalcLink/<version>/`.
  3. Open a pull request and complete the checklist in the PR template.

The winget-pkgs bots then run validation and a test install in a sandbox VM. Things to know:

* **Stable versions only.** Pre-releases such as `1.3.0-beta.1` are usually not accepted in the main package, so don't submit them.
* **SmartScreen and antivirus.** Unsigned installers can be flagged during validation. Sign releases first (see `release.yml`).
* **No driver installation.** CalcLink's installer installs no drivers, which keeps it eligible for unattended install. The USB driver is set up later, only when the user asks for it: with **Set up USB driver** in the app (one administrator prompt) or with Zadig (see `docs/DRIVERS.md`).
* **Uninstall entry.** `ProductCode` is the Inno Setup uninstall key: the `AppId` from `installer/CalcLink.iss` plus `_is1`. If the AppId ever changes, upgrades break, so don't change it.

Automating submission, for example with a `wingetcreate` step in `release.yml` using a fine-grained PAT stored as a secret, is possible but not set up. Do it only once the package identifier is final.
