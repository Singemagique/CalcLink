# TiLP2 / GFM — Modern Windows & Feature Audit

**Repository:** [debrouxl/tilp_and_gfm](https://github.com/debrouxl/tilp_and_gfm), cloned to `tilp_and_gfm/`. HEAD is `4a399bc4` (2026-01-24).
**Reference:** [debrouxl/tilibs](https://github.com/debrouxl/tilibs), cloned to `tilibs/`. HEAD is `6dba390` (2026-09-07). These are the libticables2, libticalcs2, libtifiles2 and libticonv libraries that TiLP and GFM wrap.
**Date:** 2026-11-21. **Method:** I read the code and cross-checked it against the tilibs headers. Nothing was built, because this machine has no MSYS2 or GTK toolchain and only CMake 3.4, while the project needs at least 3.9.

Paths below are relative to `tilp_and_gfm/` unless they start with `tilibs/`.

---

## Executive summary

* **The apps are still in reasonable shape in places.** The code is roughly 16k lines of C and is "late GTK2". tilp already uses GtkBuilder and GtkTreeView, and none of the long-removed GTK1-era APIs remain.
* **The Windows tooling has not been touched since about 2012.** The pieces are VC6/VS2008 project files, an Inno Setup script that needs the 2012 GTK 2.24 runtime, the libusb-win32 filter drivers, and a CMake build that says outright *"WISH: add support for Windows"*.
* **Out of the box, a CMake build cannot produce a working Windows exe.** Three things break it:
  * GCC-only flags are applied to every compiler.
  * GtkBuilder signal export is broken on Windows.
  * The install layout doesn't match where the exe looks for its UI files.
* **USB uses libusb-0.1 (libusb-win32) on Windows.** It has no ARM64 support, and the filter driver is discouraged on Windows 10/11. tilibs already has a libusb-1.0 backend with Win32 shims, and switching to it plus WinUSB (installed via Zadig) is the modern path.
* **The libraries can do more than the UI exposes.** Missing from the UI:
  * Rename or move a variable on the calculator.
  * Archive/unarchive or lock a variable on the calculator.
  * Remote key presses.
  * Run a program after sending it.
  * Certificate transfer (the code exists but nothing calls it).
  * Live screen mirroring.
  * TCP/emulator cable types.
* **GFM can't create groups for any modern model.** That includes the CE, CSE and Nspire.
* **Several small bugs** affect TI-84 Plus CE users today: the backup file filter, colour EPS/PDF screenshots, and screen scaling.

I confirmed every item tagged ✅ by reading the code myself. The rest come from a careful read but were not reproduced.

---

## 1. Bugs worth fixing immediately (small, high value)

| # | Bug | Evidence | Fix |
|---|---|---|---|
| B1 ✅ | **GtkBuilder callbacks aren't exported on Windows.** CMake sets `-DHAVE_FVISIBILITY=1` for every compiler. On Windows that makes `TILP_EXPORT` expand to `__attribute__((visibility))` instead of `__declspec(dllexport)`. On MinGW nothing is exported from the PE file, so `gtk_builder_connect_signals` finds none of the ~146 handlers. On MSVC the build fails to compile. | `CMakeLists.txt:58`, `tilp/trunk/src/tilp_defs.h:90-96`, `tilp/trunk/src/support.h:67-73`, `gfm/trunk/src/support.h:91-97` | Define `HAVE_FVISIBILITY` only for non-WIN32 GCC/Clang, and set `ENABLE_EXPORTS ON` on both exe targets. |
| B2 ✅ | **`WinMain` calls `main(__argc, __argv, NULL)`, but `main` takes 2 arguments**, so a GUI-subsystem build won't compile. The single-instance mutex only shows a warning and then carries on. | `tilp/trunk/src/main.c:223-238` vs `:101` | Delete `WinMain` and use `WIN32_EXECUTABLE` with `/ENTRY:mainCRTStartup` (or MinGW `-mwindows`). |
| B3 ✅ | **CE backup file filter is broken.** `tifiles_fext_of_backup()` returns `NULL` for the 83PCE/84+CE. `g_strconcat("*.", NULL, ";*.tig", NULL)` stops at that NULL, which leaves the filter as `*.` and drops `*.tig`. | `tilp/trunk/src/tilp.c:350` (also `:307`, `:318`); `tilibs/libtifiles/trunk/src/filetypes.cc:153-155` | Check for NULL and fall back to `tifiles_fext_of_group()` or `tig`. |
| B4 ✅ | **`format_bytes` uses `sizeof(str)` where `str` is a pointer**, so sizes are truncated to 7 characters ("12345 b"). | `tilp/trunk/src/labels.c:100-110` | Use `maxlen`. |
| B5 | **EPS/PDF screenshots of colour calcs come out garbled.** The bitmap is always treated as 1 bpp, and `screen.pixel_format` is ignored. | `tilp/trunk/src/tilp_screen.c:265-267`, PDF path near `:360` | Convert with `ticalcs_screen_convert_native_to_rgb888` and write RGB. |
| B6 ✅ | **The `screen_scaling` option is saved but never applied.** | `tilp/trunk/src/tilp_config.c:359,522`, used nowhere else | Apply it with `gdk_pixbuf_scale_simple` in `screenshot.c`. |
| B7 | **The CLI sends variables and then returns**, so apps, OS files and TIGroups in the same call are skipped. | `tilp/trunk/src/tilp_cmdline.c:227-232` | Process every selection list. |
| B8 ✅ | **GFM truncates renamed variables to 8 characters**, which is wrong for Nspire and for 68k folder-qualified names. | `gfm/trunk/src/ctree.c:179-180` | Use the model's real limit, via a libticonv/libtifiles helper or a per-model table. |
| B9 | **`format_path` does an unchecked `strcpy` into a 1024-byte buffer.** It overflows once long paths are enabled. | `tilp/trunk/src/labels.c:63-87`, `gfm/trunk/src/labels.c:60-62` | Use `g_strlcpy`, or build the string with `GString`. |
| B10 | **`ShellExecute` result is cast with `(int)hInst`**, which truncates the pointer on x64. It is also passed a UTF-8 path through the ANSI API. | `tilp/trunk/src/bookmark.c:40-48` | Use `g_app_info_launch_default_for_uri()`, or `ShellExecuteW` and compare with `(INT_PTR)`. |
| B11 | **`FileEntry.size` is an `off_t`, which is 32-bit on Windows.** GLib on Windows also requires `GStatBuf` instead of `struct stat` for `g_stat`. | `tilp/trunk/src/tilp_struct.h:101`, `tilp_files.c:538,576` | Use `goffset` and `GStatBuf`. |
| B12 | **GFM compares version strings as plain strings**, so "1.1.10" sorts before "1.1.9". | `gfm/trunk/src/tilibs.c:39-45` | Compare the numbers instead. |

---

## 2. Modern Windows audit

### 2.1 Build system (CMake): *blocker*

| Sev | Finding | Evidence | Recommendation |
|---|---|---|---|
| Blocker | GCC `-W…`/`-Werror=…` flags are applied unconditionally, so MSVC and clang-cl reject them. | `CMakeLists.txt:34` | Split by compiler ID the way tilibs does (`tilibs/CMakeLists.txt:101-131`). For MSVC use `/utf-8 /W3 /sdl /guard:cf`. |
| Blocker | Visibility/export is broken (see B1). | | |
| Blocker (runtime) | **Install layout doesn't match the code.** CMake installs `bin/tilp2.exe` and `share/tilp2/builder/...`. On Windows, tilp looks for `<exedir>\builder\`, and its bin→share relocation is commented out. GFM relocates but uses different rules. | `tilp/trunk/src/tilp_paths.c:84-140`, `gfm/trunk/src/paths.c:56-105`, `tilp/trunk/CMakeLists.txt:83-88` | Use `g_win32_get_package_installation_directory_of_module(NULL)` and the same `share/<pkg>` relative layout on every platform. |
| High | `-rdynamic` is hard-coded. It is meaningless for PE files. | `tilp/trunk/CMakeLists.txt:71`, `gfm/trunk/CMakeLists.txt:44` | `ENABLE_EXPORTS ON`. |
| High | `<unistd.h>` is included unconditionally in about 18 files, and the `dirent.h`/`unistd.h` shims are never on the include path. | e.g. `tilp/trunk/src/about.c:29`, `clist.c:36` | Guard them with `#ifndef _MSC_VER`, or add a `win32_config/` shim directory like tilibs has. |
| High | Dependencies are found only through pkg-config. There is no `vcpkg.json` and no `CMakePresets.json`, even though tilibs has both. | `tilp/trunk/CMakeLists.txt:55-58` | Add presets, and either `FetchContent`/`add_subdirectory` tilibs or consume its `.pc` files. |
| High | The `.rc` icon/version resources aren't wired into CMake. There is **no manifest**, and no `WIN32_EXECUTABLE`, so a console window opens behind the GUI. | `tilp/trunk/build/mingw/tilp.rc`, `gfm/trunk/build/mingw/gfm.rc` | `target_sources(... app.rc app.manifest)`. Generate the version strings from `PROJECT_VERSION`. |
| Medium | The exe is named `tilp2`, but the installer and registry expect `tilp.exe`. | `tilp/trunk/CMakeLists.txt:52` vs `build/InnoSetup/tilp2.iss:118` | `OUTPUT_NAME tilp` on WIN32. |
| Low | IPO/LTO is forced on, and the minimum CMake is 3.9. | `CMakeLists.txt:1,30` | Make IPO optional, and require CMake 3.21+ for presets. |

**Toolchain reality check.** vcpkg has `gtk3` and `gtk` (4.x) ports but, as far as I know, no `gtk2` or `libglade` port. **Building the GTK2 code on Windows today realistically means MSYS2** (UCRT64 for x64, CLANGARM64 for ARM64), which still packages `mingw-w64-ucrt-x86_64-gtk2`. vcpkg + MSVC only becomes an option after the GTK3 port.

### 2.2 Legacy Windows artifacts: delete

* **`tilp/trunk/build/msvc/`, `gfm/trunk/build/msvc/`:**
  * VC6 `.dsp/.dsw` files, VS2008 `.sln/.vcproj` files, binary `.suo` files and `TiLP.mak`.
  * Hard-coded `C:\LPG\GTK\...` paths.
  * `HowToBuildIt.txt` recommends GTK 2.4 and "MSVC 5.0 mini".
  * Only the `dirent.h`/`unistd.h` shims are worth keeping, and only if MSVC is a goal.
* **`build/mingw/ReadMe.txt`** (2007, SVN/SourceForge era): keep just the `.rc` and `.ico` files.
* **`install.bat`** copies a `glade/` directory that tilp no longer has, into `c:\lpg\msvc`.

### 2.3 Installer (Inno Setup): rewrite

`tilp/trunk/build/InnoSetup/tilp2.iss` and its siblings:

* **Source paths and install mode:**
  * Sources are hard-coded under `C:\lpg\...`.
  * It requires admin and installs to `{pf}`.
  * There is no `ArchitecturesInstallIn64BitMode`.
* **GTK runtime and shared DLLs:**
  * It detects a system-wide GTK through `HKLM\Software\GTK\2.0` and downloads the 2.12/2.24 runtime with a bundled `wget`.
  * It puts shared DLLs in `{cf}\LPG Shared\libs`.
  * It drops `libusb0.dll` into `system32`.
* **Global environment changes:**
  * It sets a global `PANGO_WIN32_NO_UNISCRIBE` env var.
  * It uses App Paths `Path` for DLL lookup.
* **Drivers:** it installs the **DhaHelper/RwPorts kernel drivers** for parallel/serial port I/O. Unsigned-era drivers like these are unlikely to load under Secure Boot or HVCI (memory integrity) (not verified).
* **Leftovers:** Win9x, Windows Media Player and Quick Launch logic.

**Recommendation:**
* Make the install per-user (or `{autopf}` x64) with a **private GTK runtime bundled next to the exe**.
* No shared DLLs, no writes to `system32`, no global environment variables.
* `ArchitecturesAllowed=x64compatible arm64`.
* Register file associations under HKCU.
* Drop the parallel/serial port drivers and treat the legacy port cables as unsupported on Windows.
* Also ship a portable ZIP.

### 2.4 Unicode, paths and config

* **ANSI APIs everywhere.**
  * `GetModuleFileName` (ANSI) is used for the install directory: `tilp/trunk/src/tilp_paths.c:95-99` ✅ and `gfm/trunk/src/paths.c:65-69`.
  * argv is ANSI.
  * Plain `fopen`/`stat`/`access`/`unlink` receive UTF-8 paths: `tilp_config.c`, `tilp_screen.c`, `tilp_calcs.c:269`, `about.c`, `manpage.c`, `release.c`, `gfm/file.c:108`.
  * **Result:** anything under a non-ASCII user folder (e.g. `C:\Users\José\Documents`) fails.
  * Fix: switch to `g_fopen`/`g_stat`/`g_access`/`g_unlink`, and use `g_win32_get_command_line()`. Also add `<activeCodePage>UTF-8</activeCodePage>` to the manifest as a backstop.
* **The config file lives in the profile root.** `tilp.ini` and `.tilp.log` go to `%USERPROFILE%` (`tilp_config.c:105-108`). Move them to `g_get_user_config_dir()` (`%LOCALAPPDATA%\tilp2\`) and migrate the old files.
* **GFM is found only through the HKLM App Paths registry key**, so portable or CMake installs never find it. The key handle also leaks (`tilp/trunk/src/clist_rbm.c:185-210`). Look next to `tilp.exe` first.
* **Drive and UNC handling.** `format_path` assumes a 3-character `C:\` prefix, which breaks `\\server\share` paths. `tilp_drive_change` writes into a heap string without reallocating it (`clist_rbm.c:46-104`, `tilp_misc.c:35`, `labels.c:48-51`).
* **The Win32 file dialog uses fixed buffers.** The 2048-byte `lpstrFile` buffer makes large multi-selects fail silently, and the filter is built with an unbounded `wcscpy` (`tilp/trunk/src/filesel.c:138-145,389-470`; duplicated in GFM). GTK3's `GtkFileChooserNative` replaces all of this with the native Windows dialog.
* **Cleanup:** remove the hard-coded `TILP_VERSION "1.19"` and the `PACKAGE`/`LOCALEDIR` redefinitions that clash with CMake (`tilp_version.h:37-38`, `intl.h:57-61`).

### 2.5 Manifest, DPI, dark mode, signing

* **Add an application manifest.** It should include:
  * `supportedOS` entries for Windows 10/11.
  * Common Controls v6.
  * `longPathAware` (only after fixing B9).
  * `activeCodePage UTF-8`.
* **DPI:** GTK2 can't do HiDPI. Leave the app DPI-unaware (or use `gdiScaling`) until the GTK3 port; GTK3 does integer scaling only (`GDK_SCALE`).
* **Dark mode:** not possible in GTK2. On GTK3, use `gtk-application-prefer-dark-theme` plus `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` for the title bar.
* **Signing:** nothing is Authenticode-signed today. Sign the exe, the DLLs and the installer (e.g. with Azure Trusted Signing) to avoid SmartScreen warnings.
* **Distribution:** publish a **winget** manifest. MSIX is possible once drivers are no longer part of the installer.

### 2.6 USB drivers: *high*

* **Today on Windows** (all confirmed ✅):
  * `tilibs/libticables/trunk/src/link_usb.cc:30-35` forces `HAVE_LIBUSB`, which is the **libusb-0.1 API through libusb-win32**.
  * `tilibs/vcpkg.json` selects `libusb-win32` on Windows.
* **The bundled INFs are from 2011.** They are libusb-win32 1.2.5.0, cover x86/amd64/ia64 only, and have **no ARM64** (`tilibs/libticables/trunk/src/win32/usb/silverlk.inf`).
* **The installer still installs the filter driver** through `rundll32 libusb0.dll,usb_install_driver_np_rundll` and checks for TiglUsb.
* **Recommendation:**
  * Build Windows with tilibs' **libusb-1.0 backend** (`src/linux/link_usb1.cc`), which already has `__WIN32__` shims.
  * Use the **WinUSB** driver, assigned with Zadig or embedded libwdi. WinUSB ships with Windows, is Microsoft-signed and works on ARM64.
  * Switch `vcpkg.json` to `libusb` on all platforms.
  * Replace `win32_check_libusb` (`src/win32/detect.cc:92-98`).
  * This is an **upstream tilibs change**.
* **Rewrite the user docs:** `README.win32` (it targets XP/Vista/7, is Latin-1 encoded and mentions libusb-win32 1.2.6) and `help/usb_driver.html` (the XP "Update Driver… Continue Anyway" flow). Keep the caveat that WinUSB and TI Connect CE's driver can't both own the device at the same time.

### 2.7 GUI toolkit: GTK+ 2 is end-of-life

* **Prerequisite:** GFM still uses **libglade**: about 58 `glade_*` calls, plus `glade/*.glade` files that use the glade-2.0 DTD. Convert it to GtkBuilder the way tilp already was.
* **GTK3 porting, C code** (about 30 call sites):
  * `GTK_DIALOG(d)->vbox` and `widget->window`.
  * `gtk_vbox_new`.
  * `gtk_widget_modify_bg/font`.
  * `GtkObject` in signatures.
  * `GDK_<key>` → `GDK_KEY_*`.
  * The removed `size_request` signal (`tilp.c:144`).
* **GTK3 porting, UI files** (19 `.ui` files in `tilp/trunk/builder`):
  * GtkVBox/HBox/HButtonBox/HSeparator.
  * `has_separator`.
  * GtkTable packing.
  * GtkImageMenuItem and stock items (these are only deprecated in GTK3).
* **Estimate:** **GTK 3.24 port ≈ 1–2 developer-weeks**, including GFM's glade conversion and testing drag-and-drop. GTK4 would take about 4–8 weeks (no `gtk_dialog_run`, event controllers, a drag-and-drop rewrite, and TreeView is deprecated). **Target GTK3 first.** It is maintained on MSYS2 and vcpkg, and it fixes the file dialog, DPI (partly) and theming.

### 2.8 CI

`.travis.yml` covers only Linux (xenial) and macOS. Add **GitHub Actions** jobs for Windows (MSYS2 UCRT64 and CLANGARM64) that build tilibs and then tilp/gfm, and upload a portable ZIP as an artifact.

---

## 3. Calculator functionality: what's missing

### 3.1 Library capabilities TiLP doesn't expose

| Capability | tilibs API | Gate | TiLP today | Effort |
|---|---|---|---|---|
| **Rename / move variable on calc** | `ticalcs_calc_rename_var` | `OPS_RENAME` | Not used ✅ | S–M |
| **Archive / unarchive / lock on calc** | `ticalcs_calc_change_attr` | `OPS_CHATTR` | Icons shown only ✅ | S |
| **Certificate send/receive** | `ticalcs_calc_send_cert2` / `recv_cert2` | `FTS_CERT` | `tilp_calc_send_cert`/`recv_cert` exist with **no callers** ✅ | S |
| **Remote control (key sender)** | `ticalcs_calc_send_key`, `ticalcs_keys_73/83/83p/86/89/92p` | `OPS_KEYS` | Internal HOME-key hack only ✅ (`tilp_calcs.c:118`) | M |
| **Run program/app after send** | `ticalcs_calc_execute`; `MODE_SEND_EXEC_ASM` (82/85) | per model | Not used ✅ | S–M |
| **Live screen mirror** | `ticalcs_calc_recv_screen_rgb888` on a timer | `OPS_SCREEN` | Manual refresh only | M |
| **TCP / emulator cables** | `CABLE_TCPC`, `CABLE_TCPS` (`CABLE_DEV`/`CABLE_ILP` on Linux) | — | Missing from `device.ui` | S |
| **Protocol/event log view** | `ticalcs_calc_set_event_hook`, `ticables_cable_set_event_hook` | — | Not used | M |
| **Accurate transfer rate** | `ticables_progress_get` | — | TiLP computes its own rate | S |
| **Structured calc info** | `CalcInfos` fields (Python on board, battery, colour, exact math) | `OPS_VERSION` | Text dump only | S |
| **Flash-app capability check** | `ticalcs_model_supports_installing_flashapps` / `OPS_FLASH` | — | Not checked; 82A and 84+T lack Flash apps | S |

### 3.2 TiLP feature roadmap (prioritised)

1. **Calculator variable context menu: Rename, Move to folder, Archive/Unarchive, Lock.** Gate each item on the model's feature flags. This is the most-requested kind of thing for CE and Nspire users. (S–M)
2. **OS-upgrade safety checks.** Before `ticalcs_calc_send_os2`:
   * read the battery status (`INFOS_BATTERY_ENOUGH`);
   * compare the OS/boot version with the file's version;
   * warn on downgrade, since CE has anti-downgrade protection.
   
   The current warning text only covers 68k and 83+ (`tilp_calcs.c:414`). (M)
3. **Proper menu gating.** Screenshot isn't gated by `OPS_SCREEN`, Send OS isn't gated by `OPS_OS`, and Get Info isn't gated by `OPS_VERSION` (`toolbar.c:49-54`, which also sets `restore` twice). (S)
4. **Screenshot improvements.** Scaling (B6), colour EPS/PDF (B5), a live-mirror toggle, and copy to clipboard. (S–M)
5. **Remote-control window.** A clickable keypad, using the per-model key tables. (M)
6. **"Send and run".** A checkbox in the send dialog that calls `ticalcs_calc_execute`. (S–M)
7. **USB hotplug / auto-reconnect.** Poll `ticables_get_usb_device_info` on a timer, then re-probe and reopen the handle. This fixes TODO items #2 and #4 ("need to exit TiLP to clear an USB error" and "…to restart NSpire connection"). (M–L)
8. **Full CLI.** Add receive, backup, screenshot, dirlist, probe, set clock, ROM dump and OS-version query. Each maps one-to-one to an existing API, and together they make TiLP scriptable for classrooms. (M)
9. **Drag from the calculator view to Explorer.** Use a `text/uri-list` drag source that receives into a temp file (`dnd.c:228-230` currently only logs). (M)
10. **Content preview.** Show a BASIC program listing (TI-Basic detokenisation is not in tilibs; I'd need to verify whether ticonv has more than variable names), and show pictures and images (`.8ci`/`.8ca`) as images. (L)
11. **Multiple calculators at once.** The library is handle-based, but TiLP uses one global `calc_handle` (`tilp_device.c:262`). (L)

### 3.3 GFM gaps

* **The model picker only offers TI-73 to V200**, so there is no 84+CSE, 83PCE, 84+CE, 82A, 84+T or Nspire (`gfm/trunk/glade/dialogs.glade:635-646`). You can't create a new group or TIGroup for any modern calc. **Highest-value GFM fix (S).**
* **Renames are truncated to 8 characters** (B8).
* **It can't add or edit Flash apps inside TIGroups, and can't move variables or delete folders** (`gfm/trunk/src/gui.c` ~310-340, `gfm/trunk/README`). Useful library helpers sit unused: `tifiles_group_add_file/del_file`, `tifiles_tigroup_add_file/del_file`, `tifiles_file_write_flash`.
* **No "save for another model".** GFM never sets `FileContent.model_dst`, so it can't convert e.g. 83+ to CE.
* **The CLI only has `--version`.** A headless group/ungroup would be cheap to add.

### 3.4 Library-side gaps (upstream tilibs)

* **No distinct models for CE-T, CE Python, or Nspire CX vs CX II.** They are folded into `CALC_TI84PCE_USB` and `CALC_NSPIRE`, and Python support is only detectable at runtime through `INFOS_PYTHON_ON_BOARD`.
* **The CE drivers have `OPS_IDLIST`, `OPS_RENAME` and `OPS_CHATTR` commented out** (`tilibs/libticalcs/trunk/src/calc_84p.cc:2218-2219, 2289-2290`). Until these are implemented and tested, rename and archive on CE will be unavailable even after the UI work.
* **No bundle support** (`.b84`/`.b83`), and no CX II OS formats (`.tco2`/`.tcc2`/`.tct2`). Not found in libtifiles.
* **No hotplug API.**

---

## 4. Suggested work plan

| Phase | Scope | Size | Outcome |
|---|---|---|---|
| **0. Quick fixes** | B1–B12 and the GFM model picker | ~2–3 days | Correct behaviour for CE users; code ready for a Windows build |
| **1. Windows build** | Split CMake flags by compiler; fix export and layout; add rc and manifest; `WIN32_EXECUTABLE`; UTF-8 path sweep; config in `%LOCALAPPDATA%`; MSYS2 UCRT64 and CLANGARM64 GitHub Actions; delete the legacy build directories | ~1 week | Reproducible x64 and ARM64 builds of the current GTK2 code |
| **2. USB modernisation** (tilibs upstream) | libusb-1.0 + WinUSB on Windows; Zadig/libwdi flow; rewrite README.win32 and the help pages | ~3–5 days | Signed inbox driver; ARM64 USB |
| **3. Packaging** | New Inno Setup script (per-user/x64/arm64, private runtime), portable ZIP, code signing, winget | ~3 days | Clean install on Windows 10/11 without SmartScreen warnings |
| **4. Calculator features** | Rename/move/archive menu, OS safety checks, menu gating, screenshot work, send-and-run, remote control, CLI expansion, hotplug | ~2–3 weeks | Feature parity with what tilibs offers |
| **5. GTK3 port** | Convert GFM from libglade to GtkBuilder; port both apps to GTK 3.24; native file dialog; dark mode | ~1–2 weeks | Supported toolkit, HiDPI, vcpkg/MSVC possible |

Phase 0 and the CMake part of Phase 1 can start right away. Phases 2 and 3.4 depend on upstream tilibs, so it's worth opening issues or PRs there early.
