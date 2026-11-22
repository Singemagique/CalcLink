# CalcLink

A modern Windows 10/11 app (x64 and ARM64) for connecting TI graphing calculators to a PC. It transfers programs, variables, apps, OS upgrades and backups, and takes screenshots, sets the clock and dumps ROMs.

CalcLink is a ground-up replacement for the GTK2 front-ends of [TiLP2 and GFM](https://github.com/debrouxl/tilp_and_gfm). It is built on the same proven calculator-protocol libraries, [tilibs](https://github.com/debrouxl/tilibs).

> **Status:** early preview (v0.1.0).
> * **Tested on real hardware:** a TI-83 Plus (OS 1.16) on a SilverLink cable, with Windows 10 x64 and the WinUSB driver.
> * **Not yet tested:** the other models and cables.
> * **Without hardware:** the app also runs against a built-in **simulator**.
>
> Hardware support needs the native `calclink_native.dll` (see [docs/BUILDING.md](docs/BUILDING.md)) and a one-time driver setup per calculator model (see [docs/DRIVERS.md](docs/DRIVERS.md)).

## What it does

* **Devices.** Finds calculators on USB and connects with one click. If none is found, it shows a "Calculator not found?" checklist with help setting up the driver.
* **Calculator.** Lists variables, programs and apps with their type, size and location (RAM or Archive), and lets you filter, sort and search them.
  * **Send:** drag files onto the list or use **Send files**. CalcLink asks before replacing a variable that already exists, since the calculator would overwrite it without asking.
  * **Manage:** receive, delete, run, archive / move to RAM, create folders and rename.
* **Screen.** Captures the calculator screen, or shows it live; you can copy it or save it as a PNG.
* **Tools.** Shows calculator information: model, ID, product code, OS and boot versions, battery. It also sets the clock, makes backups and ROM dumps, and sends OS upgrades.
* **Long transfers.** The status bar shows the item count, the speed and the time left, and the taskbar button shows progress. Closing the window during a transfer asks first.
* **Backups and ROM dumps** go to a temporary file first, so a failed or cancelled run never destroys an existing file.

Commands the connected calculator can't perform are hidden. For example, a TI-83 Plus can't archive variables over the link, so the Archive command doesn't appear.

## Why a new app?

The TiLP audit ([docs/TILP_AUDIT.md](docs/TILP_AUDIT.md)) found these problems with the old apps:

* GTK+ 2 is end-of-life.
* The Windows build, installer and USB driver setup date from around 2012.
* Several features in the libraries never reached the UI.

CalcLink keeps the roughly 72k lines of protocol code in tilibs and replaces everything above it with native Windows technology.

```
WinUI 3 app (C#, MVVM)          src/CalcLink.App
        │  ICalculatorService / ICalculatorSession
CalcLink.Core (C#)              src/CalcLink.Core
   ├─ Native backend ── P/Invoke ──► calclink_native.dll (flat C ABI)   native/
   │                                     └─ tilibs (ticables2, ticalcs2, tifiles2, ticonv)
   │                                           └─ libusb ─► USB cable / calculator
   └─ Simulator backend (no hardware needed)
```

## Supported calculators

These are all the models tilibs supports:
* TI-73, TI-80, TI-82, TI-83, TI-85, TI-86
* TI-83 Plus and TI-84 Plus families, including the 84 Plus C SE, 83 Premium CE, 84 Plus CE / CE-T / CE Python, 82 Advanced and 84 Plus T
* TI-89 / TI-89 Titanium, TI-92 / 92 Plus, Voyage 200
* TI-Nspire family

Which features are available depends on the model, and the UI shows only what the connected calculator supports.

## Repository layout

| Path | What |
|---|---|
| `src/CalcLink.App` | WinUI 3 desktop app (unpackaged, self-contained Windows App SDK) |
| `src/CalcLink.Core` | Backend-agnostic contract (`Abstractions/`), native P/Invoke backend, simulator |
| `tests/CalcLink.Core.Tests` | xUnit tests: simulator, interop struct layout, factory, calculator IDs, name-clash rules and safe file writes |
| `native/` | `calclink_native.dll` C shim over tilibs (CMake + vcpkg presets), plus the `clprobe` command-line hardware test tool |
| `installer/` | Inno Setup script (per-user, x64/arm64) and winget manifest templates |
| `docs/` | [Building](docs/BUILDING.md), [Architecture](docs/ARCHITECTURE.md), [USB drivers](docs/DRIVERS.md), [Code and UI review](docs/REVIEW.md) |

## Quick start (no hardware)

```powershell
dotnet test tests/CalcLink.Core.Tests
dotnet run --project src/CalcLink.App -p:Platform=x64
```

If `calclink_native.dll` is missing, the app falls back to the simulator automatically. You can also choose the backend in **Settings**; **Restart now** applies the change.

## Roadmap

These features are planned; the IDs refer to [docs/REVIEW.md](docs/REVIEW.md) §3. No order is set yet.

* **F4 Dated backups:** "Back up now" to a folder named by date, with a list of past backups and Restore.
* **F1 Program viewer:** read TI-BASIC programs on the PC. Assembly programs are labelled as such.
* **F5 Group-file explorer:** open `.8xg` / `.tig` / `.89g` files, extract items and build new groups.
* **F6 Picture viewer:** show Pic variables and image files, and convert PNGs to calculator images.
* **F7 Explorer integration:** double-click a calculator file, or right-click → Send to calculator.
* **F9 Command-line tool:** `calclink send / recv / backup / screen / info`.
* **F12 Emulator link:** connect to an emulator instead of a real calculator.

Known open bugs are listed in [docs/REVIEW.md](docs/REVIEW.md) §1: B4, B6, B7 and B8.

## License

GPL-2.0-or-later (see [LICENSE](LICENSE)). The license is required because the tilibs libraries are GPLv2. The .NET runtime and Windows App SDK are MIT-licensed.

CalcLink is built on tilibs by Romain Liévin, Kevin Kofler, Lionel Debroux and contributors. "TI", "TI-84 Plus", "TI-Nspire" and related names are trademarks of Texas Instruments; CalcLink is not affiliated with Texas Instruments.
