# CalcLink native layer

`calclink_native.dll` exposes the flat C ABI in [`include/calclink.h`](include/calclink.h)
(P/Invoked by `CalcLink.Core`). It wraps [tilibs](https://github.com/debrouxl/tilibs)
(libticables2, libticalcs2, libtifiles2, libticonv), which are **linked statically into the DLL**.
`clprobe.exe` is a small console tool for hardware smoke tests, and `calclink-driver.exe` is the
USB driver setup helper. The current ABI version is 4 (`CL_ABI_VERSION`); Core won't use a DLL
with any other version and falls back to the simulator.

## clprobe

```
clprobe [--list] [--model N] [--key CODE]... [--screen FILE.bmp] [--backup FILE]...
        [--recv NAME DIR]... [--send FILE]... [--attr NAME N]... [--delete NAME]...
clprobe --file-vars FILE
```

With no options it opens the first calculator found and prints its info and variable list.
`--list` only lists devices; `--model N` forces a tilibs model number; the other options run
an action on the opened calculator and can be repeated.
`--file-vars` lists the variables inside a file without any device. Set `CALCLINK_LOG=1` to
see tilibs' log messages, which the DLL otherwise drops.

The DLL is named `calclink_native.dll`, not `calclink.dll`: the managed app assembly is
`CalcLink.dll`, and Windows file names are case-insensitive. The header stays `calclink.h`
and the exported symbols stay `cl_*`. The CMake target is still called `calclink`.

## Building

You need:

* Visual Studio 2026 (or 2022 17.10+) with the *Desktop development with C++* workload,
  including **C++ CMake tools** (CMake 3.25 or newer, Ninja) and, for ARM64, the
  *MSVC ARM64 build tools*.
* [vcpkg](https://github.com/microsoft/vcpkg), cloned somewhere, with `VCPKG_ROOT` pointing at it.
  Run `bootstrap-vcpkg.bat` once. vcpkg must contain the `builtin-baseline` commit from
  `vcpkg.json`, so a full clone is needed (`git clone https://github.com/microsoft/vcpkg`), not a shallow one.
* Network access on the first configure: vcpkg builds GLib & co., and CMake fetches tilibs
  at commit `6dba390e7390c4b98ae96287b39a3971c331fbef`.

**The easy way is `build.ps1`, which works from any PowerShell window.** It finds VS, CMake ≥ 3.25, Ninja and vcpkg itself:

```powershell
.\build.ps1                                     # win-x64-release
.\build.ps1 -Preset win-arm64-release
.\build.ps1 -TilibsSourceDir C:\src\tilibs
```

To build by hand instead, use a **Developer PowerShell for VS**. Use an x64 prompt for `win-x64-*`. For ARM64, use an ARM64 or x64_arm64 developer prompt, e.g. `Enter-VsDevShell -Arch arm64 -HostArch amd64`. Make sure the `cmake` on your PATH is 3.25+:

```powershell
cd native
$env:VCPKG_ROOT = 'C:\src\vcpkg'          # wherever vcpkg is
cmake --preset win-x64-release
cmake --build --preset win-x64-release
cmake --install out/build/win-x64-release  # -> out/install/win-x64-release/bin
```

Presets: `win-x64-release`, `win-x64-debug`, `win-arm64-release` (Ninja, vcpkg triplets
`x64-windows` / `arm64-windows`, build in `out/build/<preset>`, install in `out/install/<preset>`).

To build against a local tilibs checkout instead of fetching it:

```powershell
cmake --preset win-x64-release -DTILIBS_SOURCE_DIR=C:/src/tilibs
```

Other cache options: `CALCLINK_TILIBS_NLS` (default OFF: tilibs messages stay English and no
gettext tools run at build time), `CALCLINK_TILIBS_CABLE_LOGGING` (default OFF; ON makes
libticables log every byte to `%USERPROFILE%\.ticables`), and `CALCLINK_BUILD_CLPROBE` (default ON).

## What ships

After `cmake --install`, `out/install/<preset>/bin` holds everything the app needs, and
packaging copies it to `native\<rid>\` next to the app:

* `calclink_native.dll`, plus `clprobe.exe` (a test tool; it does not need to ship to users)
* `calclink-driver.exe`: the "Set up USB driver" helper (`tools/calclink-driver.c`). The app runs it to list TI devices' drivers, and elevated to assign WinUSB. It doesn't use tilibs, links the CRT statically and loads system DLLs from System32 only, because it runs as administrator.
* vcpkg runtime DLLs, collected with `install(RUNTIME_DEPENDENCIES)` (dumpbin-based):
  GLib (`glib-2.0-0.dll`), `intl-8.dll`, `iconv-2.dll`, `pcre2-8.dll`, `zlib1.dll`,
  `archive.dll`, `libusb-1.0.dll` (exact names depend on the vcpkg port versions)
* the VC++ runtime (`vcruntime140*.dll`, `msvcp140*.dll`, ...) via `InstallRequiredSystemLibraries`.
  The UCRT is part of Windows 10+ and is not copied.
* `include/calclink.h`, and `lib/calclink_native.lib` (the import library, only needed by C consumers)

## Known limitations

* **USB driver:** tilibs is built with its **libusb-1.0** backend on Windows
  (`patches/tilibs-0001-windows-libusb-1.0-backend.patch`, applied automatically to a fetched
  tilibs; a local `TILIBS_SOURCE_DIR` checkout must have it applied, and configure says so if not).
  The calculator must use the **WinUSB** driver, assigned once with the app's "Set up USB driver" button or with Zadig (see `docs/DRIVERS.md`).
  libusbK and libusb-win32's driver also work through libusb-1.0. Calculators bound to
  TI Connect's own driver are not seen.
* **Non-ASCII paths:** libtifiles opens variables, apps, OS images, backups and TIGroups with
  `g_fopen`, which takes UTF-8 paths on Windows, so these work with any path.
  * Upstream libticalcs uses plain `fopen` (ANSI code page) for **ROM dumps** and `.cer` certificate
    receipt. `patches/tilibs-0002-utf8-file-paths.patch` switches those to `g_fopen` too. It is applied
    automatically to a fetched tilibs, and configure checks a local checkout for it.
  * The app manifest also sets `activeCodePage=UTF-8`.
* **Non-silent models** (TI-82/85: `FTS_SILENT` unset): `cl_recv_var` returns `CL_E_NOT_SUPPORTED`,
  because those models need the transfer to be started on the calculator. `cl_backup` on these models
  keeps waiting (as TiLP does) until the user starts the backup on the calculator or calls `cl_cancel`.
* **Remote execution** has no feature bit in tilibs. `cl_execute` returns `CL_E_NOT_SUPPORTED` for
  TI-80/82/85/86 and Nspire, where tilibs would otherwise silently do nothing.
* **Dirlist:** folders are not listed as entries; only variables and apps are. An empty folder
  therefore does not show up.
* **Debug builds:** tilibs compiles its Debug objects with `/fsanitize=address`, so the Debug DLL
  needs `clang_rt.asan_dynamic-<arch>.dll` (the install step copies it from the MSVC bin directory).
  Use the Release presets for normal app development. Upstream tilibs CI only builds Release on Windows.
* **Exported symbols:** the static tilibs objects are compiled with `__declspec(dllexport)`, so
  `calclink_native.dll` also exports the tilibs C API. Only `cl_*` is supported.
* `cl_is_ready` follows TiLP on Windows: for DirectLink it closes and re-opens the USB device
  before checking, to survive hot-plug. With a SilverLink, `cl_open` waits 2 s after resetting the cable.
* `cl_probe_usb` opens each SilverLink to identify the calculator behind it, which takes about
  1 s per SilverLink with no calculator attached.
