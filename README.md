# CalcLink

A modern Windows 10/11 app (x64 and ARM64) for connecting TI graphing calculators to a PC. It transfers programs, variables, apps, OS upgrades and backups, and takes screenshots, sets the clock and dumps ROMs.

CalcLink is a ground-up replacement for the GTK2 front-ends of [TiLP2 and GFM](https://github.com/debrouxl/tilp_and_gfm). It is built on the same proven calculator-protocol libraries, [tilibs](https://github.com/debrouxl/tilibs).

> **Status:** early scaffold (v0.1.0). The UI runs against a built-in **simulator** with no hardware. Real-hardware support needs the native `calclink_native.dll`; see [docs/BUILDING.md](docs/BUILDING.md).

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

Which features are available depends on the model, and the UI enables only what the connected calculator supports.

## Repository layout

| Path | What |
|---|---|
| `src/CalcLink.App` | WinUI 3 desktop app (unpackaged, self-contained Windows App SDK) |
| `src/CalcLink.Core` | Backend-agnostic contract (`Abstractions/`), native P/Invoke backend, simulator |
| `tests/CalcLink.Core.Tests` | xUnit tests (simulator, interop struct layout, factory) |
| `native/` | `calclink_native.dll` C shim over tilibs (CMake + vcpkg presets), plus the `clprobe` hardware smoke test |
| `installer/` | Inno Setup script (per-user, x64/arm64) and winget manifest templates |
| `docs/` | [Building](docs/BUILDING.md), [Architecture](docs/ARCHITECTURE.md), [USB drivers](docs/DRIVERS.md) |

## Quick start (no hardware)

```powershell
dotnet test tests/CalcLink.Core.Tests
dotnet run --project src/CalcLink.App -p:Platform=x64
```

If `calclink_native.dll` is missing, the app falls back to the simulator automatically. You can also choose the backend in **Settings**.

## License

GPL-2.0-or-later (see [LICENSE](LICENSE)). The license is required because the tilibs libraries are GPLv2. The .NET runtime and Windows App SDK are MIT-licensed.

CalcLink is built on tilibs by Romain Liévin, Kevin Kofler, Lionel Debroux and contributors. "TI", "TI-84 Plus", "TI-Nspire" and related names are trademarks of Texas Instruments; CalcLink is not affiliated with Texas Instruments.
