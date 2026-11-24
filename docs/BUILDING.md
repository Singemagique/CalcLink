# Building CalcLink

CalcLink has two halves, built with different toolchains:

| Part | Path | Toolchain | Output |
|---|---|---|---|
| Native backend | `native/` | CMake presets + Ninja + MSVC + vcpkg | `calclink_native.dll`, tilibs/GLib/libusb DLLs, `clprobe.exe`, `calclink-driver.exe` |
| Managed app and CLI | `src/`, `tests/` | .NET 10 SDK | `CalcLink.exe` (WinUI 3, self-contained), `calclink-cli.exe` |

You can build and run the app **without** the native half. It then uses the built-in simulator.

All commands below are PowerShell (7+ recommended; Windows PowerShell 5.1 also works) and run from the repository root unless stated otherwise.

## 1. Prerequisites

* **Windows 10 1809+ or Windows 11**, x64 or ARM64.
* **Visual Studio 2026** (Community is fine), or VS 2022 17.x (expected to work but not tested), with:
  * the **Desktop development with C++** workload, which needs these components:
    * **MSVC v14x C++ x64/x86 build tools** (latest)
    * **MSVC C++ ARM64 build tools** (`Microsoft.VisualStudio.Component.VC.Tools.ARM64`), needed only for ARM64 builds, including cross-compiling from x64
    * **C++ CMake tools for Windows**, which provides CMake ≥ 3.21 and Ninja
    * a **Windows 11 SDK**
  * optionally, the **WinUI application development** workload, for XAML editing and debugging in VS. It isn't needed for CLI builds.
* **.NET 10 SDK** (`dotnet --version` should print 10.0.x).
* **vcpkg** with `VCPKG_ROOT` set. Either option works:
  * **Your own clone.** This is what CI does.
    ```powershell
    git clone https://github.com/microsoft/vcpkg C:\src\vcpkg
    C:\src\vcpkg\bootstrap-vcpkg.bat -disableMetrics
    [Environment]::SetEnvironmentVariable('VCPKG_ROOT', 'C:\src\vcpkg', 'User')   # then open a new terminal
    ```
  * **The vcpkg bundled with Visual Studio.** It sets `VCPKG_ROOT` inside a VS developer shell.
* **Inno Setup 6.3+** (optional; only to build the installer). From <https://jrsoftware.org/isdl.php>, or `winget install JRSoftware.InnoSetup`.

The native build gets tilibs' dependencies from vcpkg in manifest mode: GLib, gettext/libintl, libiconv, libarchive, zlib, and libusb (libusb-1.0) on Windows. **The first configure builds all of them from source and takes a long time.** Later builds reuse vcpkg's binary cache (`%LOCALAPPDATA%\vcpkg\archives` by default).

## 2. Native backend (`calclink_native.dll`)

**Easiest: run the build script from any PowerShell window.** It finds Visual Studio, enters the right developer environment, picks a CMake ≥ 3.25 over older ones on your PATH, and finds Ninja and vcpkg. If vcpkg isn't bootstrapped yet, it bootstraps it. It then configures, builds and installs the preset.

```powershell
C:\src\CalcLink\native\build.ps1                                   # win-x64-release
C:\src\CalcLink\native\build.ps1 -Preset win-arm64-release
C:\src\CalcLink\native\build.ps1 -TilibsSourceDir C:\src\tilibs    # use a local tilibs checkout
C:\src\CalcLink\native\build.ps1 -Clean                            # wipe the build dir first
```

Without `VCPKG_ROOT` set, the script looks for vcpkg in `C:\src\vcpkg`, `C:\vcpkg`, `%USERPROFILE%\vcpkg` and Visual Studio's bundled copy. If a tool is missing, it says so and prints the install command. On a fresh machine, the minimum setup is:

```powershell
winget install Kitware.CMake Ninja-build.Ninja
git clone https://github.com/microsoft/vcpkg C:\src\vcpkg
```

The first build takes roughly 20–40 minutes while vcpkg compiles GLib and the other dependencies. After that, rebuilds take seconds.

### Manual build

The CMake presets need a Visual Studio developer environment for the **target** architecture. In PowerShell:

```powershell
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
# x64 target on an x64 PC:
& "$vs\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
# ...or ARM64 target cross-compiled on an x64 PC (use a fresh terminal per architecture):
# & "$vs\Common7\Tools\Launch-VsDevShell.ps1" -Arch arm64 -HostArch amd64 -SkipAutomaticLocation
# On an ARM64 PC, building for ARM64: -Arch arm64 -HostArch arm64
```

Then configure, build and install with a preset:

```powershell
cd native
cmake --preset win-x64-release              # configure (runs vcpkg install the first time)
cmake --build --preset win-x64-release      # build
cmake --install out/build/win-x64-release --config Release
cd ..
```

| Preset | Target | Dev shell `-Arch` |
|---|---|---|
| `win-x64-release` | x64, Release | `amd64` |
| `win-x64-debug` | x64, Debug | `amd64` |
| `win-arm64-release` | ARM64, Release | `arm64` (host `amd64` or `arm64`) |

The output goes to `native/out/install/<preset>/bin/`:
* `calclink_native.dll`, with tilibs (ticables2, ticalcs2, tifiles2, ticonv) linked in statically;
* the vcpkg runtime DLLs it needs: GLib, PCRE2, intl, iconv, libarchive, zlib, and `libusb-1.0.dll`;
* the VC++ runtime DLLs (`vcruntime140*.dll`, `msvcp140*.dll`, `concrt140.dll`);
* `clprobe.exe`, a console smoke test for real hardware. It prints the library versions and the USB devices it finds, and if a calculator is present it opens it and prints its info and variable list (options in `native/README.md`);
* `calclink-driver.exe`, the "Set up USB driver" helper (see [DRIVERS.md](DRIVERS.md) §3);
* `licenses\`, the license texts of tilibs and the vcpkg libraries above, for [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

tilibs is fetched at a pinned commit, or taken from `-TilibsSourceDir`, and patched by the two files in `native/patches/`: the libusb-1.0 backend for Windows, and UTF-8 file paths for ROM dumps and certificates. A fetched tilibs gets both automatically. A local checkout must already have them applied, and configure stops with a message if it doesn't. `.gitattributes` keeps the patch files byte-exact; regenerate them with `git diff --output=<file>`, not by redirecting PowerShell output, which changes the line endings.

The build trees in `native/out/build/` and the install trees in `native/out/install/` are git-ignored.

## 3. Core library and tests

```powershell
dotnet test tests/CalcLink.Core.Tests
```

The tests (about 230) use the simulator, the interop definitions and the managed file formats only. They need neither hardware nor `calclink_native.dll`. To get a TRX report like CI's:

```powershell
dotnet test tests/CalcLink.Core.Tests -c Release --logger "trx;LogFileName=CalcLink.Core.Tests.trx" --results-directory TestResults
```

## 4. Running the app during development

From the repository root:

```powershell
dotnet run --project src/CalcLink.App -p:Platform=x64
```

If `native\out\install\win-x64-release\bin\calclink_native.dll` exists, the app build copies that folder into `native\win-x64\` next to `CalcLink.exe`, and the app uses the real tilibs backend. The Devices page shows "Backend: tilibs (…)". Otherwise the app falls back to the simulator.

The command-line tool runs the same way; arguments go after `--`:

```powershell
dotnet run --project src/CalcLink.Cli -- devices
dotnet run --project src/CalcLink.Cli -- list --simulator --device 1
```

Set `$env:CALCLINK_LOG = 1` to see tilibs' own log messages in the console, which the native layer otherwise drops.

Other ways to choose the native files:
* `-p:CalcLinkNativeBin=<dir>` selects which native folder the build copies.
* `$env:CALCLINK_NATIVE_PATH = <dir or dll>` overrides the location at run time.

On an ARM64 PC, use `-p:Platform=ARM64` and the `win-arm64-release` install dir. The architecture of the app process must match the architecture of `calclink_native.dll`.

USB access to a real calculator also needs the one-time driver setup in [DRIVERS.md](DRIVERS.md). That works on x64 only for now.

To debug native code, build `win-x64-debug`, point `CALCLINK_NATIVE_PATH` at `native\out\install\win-x64-debug\bin`, and use mixed-mode debugging. In VS, enable *Native code debugging* in the launch profile.

## 5. Publishing the app

```powershell
dotnet publish src/CalcLink.App -c Release -r win-x64   -p:Platform=x64   -o artifacts\publish\win-x64
dotnet publish src/CalcLink.App -c Release -r win-arm64 -p:Platform=ARM64 -o artifacts\publish\win-arm64
```

The project is **unpackaged and self-contained**: `WindowsPackageType=None`, `SelfContained`, `WindowsAppSDKSelfContained`. The output folder runs on a clean PC with no .NET or Windows App SDK runtime installed. x64 → ARM64 cross-publishing works from any machine. `src/CalcLink.Cli` publishes the same way; `Build-Portable.ps1` (section 6) puts both into one folder.

## 6. Assembling a portable build

`installer/Build-Portable.ps1` does the publish, adds the native backend, and zips the result. It is the same script CI uses.

```powershell
# After building native/ for that architecture (section 2):
pwsh installer/Build-Portable.ps1 -Rid win-x64
pwsh installer/Build-Portable.ps1 -Rid win-arm64

# Options: -Version 1.2.0, -NativeBin <dir with calclink_native.dll>, -OutDir <dir>,
#          -AllowMissingNative (simulator-only package), -SkipPublish (re-zip an existing stage)
```

| Output | What it is |
|---|---|
| `artifacts\stage\<rid>\CalcLink\` | The app folder: `CalcLink.exe`, `calclink-cli.exe`, `native\<rid>\` (calclink_native.dll and its DLLs, `calclink-driver.exe`, `clprobe.exe`), `LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, `licenses\` (third-party license texts) and `docs\DRIVERS.md` |
| `artifacts\dist\CalcLink-<version>-<rid>-portable.zip` | The zip of that folder |
| `artifacts\dist\CalcLink-<version>-<rid>-symbols.zip` | All `.pdb` files, kept out of the shipped tree |

`<version>` defaults to `<Version>` in `Directory.Build.props`.

The native DLLs live in `native\<rid>\` rather than next to `CalcLink.exe`, for two reasons:
* **A tidy app root, with no name clashes.** The GLib, iconv and libusb DLLs stay out of the app root, so they can never collide with a file from the .NET publish output. That is also why the native library is called `calclink_native.dll`: a plain `calclink.dll` would clash with the managed `CalcLink.dll`, because Windows file names are case-insensitive.

CalcLink.Core looks in `native\<rid>\` automatically. With `-NativeLayout Flat`, the script copies the DLLs next to the exe instead and refuses to overwrite anything.

The app and the CLI are published separately and merged into one folder. Both are self-contained on the same .NET runtime, so the files they share must be identical, and the script stops if they aren't. During this publish the projects' own development-time copy of the native files is switched off (`CalcLinkNativeBin` points at an empty folder), so only the script copies them.

## 7. Building the installer

You need Inno Setup 6.3 or newer. Stage the app first (section 6), then:

```powershell
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
& $iscc /DArch=x64   /DAppVersion=1.0.0 installer\CalcLink.iss
& $iscc /DArch=arm64 /DAppVersion=1.0.0 installer\CalcLink.iss
# -> artifacts\dist\CalcLink-1.0.0-win-x64-setup.exe, ...-win-arm64-setup.exe
```

Optional defines:
* `/DSourceDir=<staged CalcLink folder>`
* `/DOutputDir=<dir>`
* `/DRepoUrl=https://github.com/<owner>/<repo>`, used for the support links and the "Open USB driver setup guide" checkbox
* `/DAppVersionNumeric=1.2.0.0`

How the installer behaves:
* **Per-user by default.** It installs to `%LOCALAPPDATA%\Programs\CalcLink` with no UAC prompt. The first page offers "Install for all users", which elevates and installs to `Program Files`.
* **File associations.** An optional task adds CalcLink to **Open with** for TI file types, and a **Send to calculator (CalcLink)** right-click command (`SystemFileAssociations\.<ext>\shell\CalcLink.Send`, which runs `CalcLink.exe --send "%1"`), under `HKCU\Software\Classes` (HKLM for all-users). It never replaces the default handler.
* **No system changes.** There are no drivers, shared DLLs, `system32` files or environment variables. `calclink-driver.exe` is installed with the app, but it runs only when the user clicks **Set up USB driver** and approves the administrator prompt.
* **Uninstall** removes everything it installed and leaves `%LOCALAPPDATA%\CalcLink` (settings) in place.
* **Silent install.** `CalcLink-<ver>-win-x64-setup.exe /VERYSILENT /CURRENTUSER` (or `/ALLUSERS`).

## 8. CI and releases

* **CI.** `.github/workflows/ci.yml` runs on every push to a branch and on pull requests. It runs three jobs:
  * .NET tests (TRX uploaded);
  * native x64 and ARM64 (ARM64 cross-compiled on the x64 runner; vcpkg binary cache in the GitHub Actions cache);
  * app publish + portable zip per RID.

  The artifacts are `native-<rid>`, `app-<rid>`, `CalcLink-<version>-<rid>-portable` and `symbols-<rid>`. Set the repository variable `ENABLE_ARM64_RUNNER=true` to also run the tests natively on the `windows-11-arm` hosted runner.
* **Releases.** `.github/workflows/release.yml` runs on a tag such as `v1.2.0` (`v1.2.0-beta.1` becomes a pre-release):
  ```powershell
  git tag -a v1.2.0 -m "CalcLink 1.2.0"
  git push origin v1.2.0
  ```
  It reuses `ci.yml` to build, optionally signs, builds both installers, and publishes a GitHub release. The release contains:
  * the portable zips;
  * the installers;
  * the symbol zips;
  * `CalcLink-<version>-third-party-sources.zip`, the source of the shipped third-party libraries, which the LGPL and GPL ask for next to the binaries (made by `installer/Get-ThirdPartySources.ps1` in the x64 native job);
  * `SHA256SUMS.txt`.

  It also uploads filled-in winget manifests as an artifact (see `installer/winget/README.md`).
* **Code signing** (optional) uses Azure Trusted Signing. Add these repository secrets: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT` and `TRUSTED_SIGNING_PROFILE`. When all six are present, the release:
  * signs every `.exe`/`.dll` that doesn't already have a valid signature. Microsoft-signed runtime files keep their own signatures.
  * signs the setup executables.

  When they are missing, the release is built unsigned, with a warning.

## 9. Troubleshooting the build

| Problem | Fix |
|---|---|
| `Could not find toolchain file: /scripts/buildsystems/vcpkg.cmake` | `VCPKG_ROOT` isn't set in this shell. |
| vcpkg: `failed to git show versions/baseline.json` | Your vcpkg clone is older than the manifest's `builtin-baseline`. Run `git -C $env:VCPKG_ROOT pull`. |
| `ninja: not found`, or the compiler isn't found | You aren't in a VS developer shell (section 2), or the C++ CMake tools component is missing. |
| ARM64 configure picks x64 tools | The dev shell was started with the wrong `-Arch`. Open a new terminal and use `-Arch arm64 -HostArch amd64`. |
| App runs but says the native backend is unavailable | The architecture doesn't match (x64 app with ARM64 DLL or vice versa), or the DLLs aren't in `native\<rid>\`, or `CALCLINK_NATIVE_PATH` is wrong. The message includes the reason, for example an ABI version mismatch. |
| Build-Portable: "collides with an existing file" | A native DLL has the same name as a published file. Keep the default `-NativeLayout Subfolder`. |
| Build-Portable: "calclink-cli and the app both ship …, with different contents" | The two projects resolved different runtime or package versions. Restore both with the same SDK and rerun. |
| Configure: "The tilibs checkout at … lacks the … patch" | A local `-TilibsSourceDir` checkout needs both patches applied. Run the `git apply` command the message prints. |
| Configure: a fetched tilibs fails to apply a patch | A patch file was changed, often its line endings. `git diff native/patches/` should show nothing; if it does, `git checkout` the file. |
| App fails at start with a `XamlParseException` that the code doesn't explain | Stale incremental XAML output. Rebuild with `--no-incremental`, or delete `src/CalcLink.App/obj` and `bin`. |
