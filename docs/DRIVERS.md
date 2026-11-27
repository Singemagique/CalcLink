# USB drivers on Windows 10 and 11

CalcLink talks to calculators through the **tilibs** libraries, built with their **libusb-1.0** backend. On Windows, libusb-1.0 reaches a USB device through **WinUSB**, the generic USB driver that ships with Windows and is signed by Microsoft. Windows doesn't assign WinUSB to a calculator by itself, so before CalcLink can see a calculator over USB, you assign WinUSB to it **once per calculator model** (once per USB ID, see §1).

There are two ways to do this; both give the same result:
* **Let CalcLink do it** ([§3](#3-letting-calclink-set-it-up)). When a TI calculator or SilverLink is plugged in with another driver, a scan in CalcLink offers a **Set up USB driver…** button.
* **Do it yourself with Zadig** ([§4](#4-setting-up-a-calculator-with-zadig)).

The CalcLink installer itself never installs or changes drivers. Every driver change on this page happens only when you ask for it, and can be undone ([§5](#5-switching-back-to-ti-connect)).

> **No hardware?** The built-in simulator needs no driver. Pick it in **Settings → Backend**.

---

## 1. Which cables and calculators

| Connection | Calculators | USB ID (VID:PID) | Device name Windows usually shows |
|---|---|---|---|
| **DirectLink**: the calculator's own USB port and the cable that came with it | TI-84 Plus, TI-82 Advanced | `0451:E003` | TI-84 Plus |
| | TI-84 Plus Silver Edition, TI-84 Plus C Silver Edition, TI-84 Plus CE (incl. CE-T and CE Python), TI-83 Premium CE, TI-84 Plus T | `0451:E008` | TI-84 Plus Silver Edition (or the model name) |
| | TI-89 Titanium | `0451:E004` | TI-89 Titanium |
| | TI-Nspire family | `0451:E012` | TI-Nspire |
| **SilverLink**: TI-GRAPH LINK USB cable with a 2.5 mm plug for the calculator's I/O port | TI-73, 82, 83, 83 Plus, 84 Plus (I/O port), 85, 86, 89, 92, 92 Plus, Voyage 200 | `0451:E001` | TI-GRAPH LINK USB |

We took the USB IDs and the model detection from tilibs: `libticables/trunk/src/ticables.h` (the `PID_*` values) and `probe.cc`. tilibs tells models that share a PID apart by their USB product string, or for a SilverLink, by asking the calculator over the cable.

Some practical consequences:

* **One driver setup usually covers every calculator with the same PID.** Windows matches drivers by hardware ID. For example, setting up a TI-84 Plus CE (`E008`) also covers a TI-83 Premium CE plugged in later.
* **Tested on real hardware:** a SilverLink with a TI-83 Plus (OS 1.16), on Windows 10 x64 with WinUSB. CalcLink identified the calculator automatically, read its version and listed all its variables and apps.
* **The gray TI-GRAPH LINK serial cable needs no driver from CalcLink.** tilibs talks to it through Windows' ordinary COM port API, so it works on a real serial port or through a USB-serial adapter (with the adapter's own driver). A scan can't find it: on the Devices page, use **Set up a connection…** and pick its COM port. Not yet tested on hardware.
* **Not supported on Windows:**
  * The black **TI-GRAPH LINK serial** cable and the **parallel** cable. tilibs drives them by toggling the port's lines directly, which needed kernel port drivers that modern Windows won't load (see TILP_AUDIT.md §2.3).
  * Unofficial home-made link cables.
* **Not verified yet:** the DirectLink models (TI-84 Plus family, TI-89 Titanium, Nspire) over WinUSB, and whether the newest Nspire hardware (CX II and later) still enumerates as `0451:E012`.

## 2. Why WinUSB (and not libusb-win32)

* **WinUSB ships with Windows**, is signed by Microsoft, and works on **x64 and ARM64**. Nothing has to be downloaded into the kernel.
* **libusb-win32 didn't work on the hardware we tested.** With a SilverLink and a TI-83 Plus, the libusb-win32 driver (1.4.0.0) and DLL let CalcLink send commands, but every reply from the calculator came back empty. TI Connect worked on the same cable and calculator. Switching to WinUSB, with tilibs' libusb-1.0 backend, fixed it immediately.
* **How CalcLink builds tilibs.** Upstream tilibs uses libusb-win32 on Windows. CalcLink builds it with a small patch (`native/patches/tilibs-0001-windows-libusb-1.0-backend.patch`) that selects tilibs' existing libusb-1.0 backend instead. The patch is intended for upstream.
* **Also fine: libusbK, or the libusb-win32 driver.** libusb-1.0 can drive a device that uses either. So a calculator already set up with libusb-win32 for an old TiLP is detected too. Prefer WinUSB, though; it's the combination we tested.

## 3. Letting CalcLink set it up

1. **Plug in the calculator** (or the SilverLink cable) and close TI Connect, TI Connect CE and TI-Nspire Computer Link.
2. **In CalcLink, press Scan on the Devices page.** If a TI calculator or cable is plugged in but uses another driver (usually TI Connect's) or none, the top of the page shows **USB driver needed** with a **Set up USB driver…** button.
   * The scan may still list the device. A SilverLink on TI's driver, for example, appears as "Unknown calculator", because CalcLink can see the cable but can't talk through it.
3. **Click it and confirm.** CalcLink explains what will change first.
4. **Approve Windows' administrator prompt.** The prompt is for `calclink-driver.exe`, a small helper that ships with CalcLink.
5. CalcLink scans again when the driver is in place.

**Tested on real hardware:** a SilverLink with a TI-83 Plus on Windows 10 x64. The cable was switched from TI's driver (`SilvrLnk`) to WinUSB, and CalcLink then detected and connected to the calculator. Not yet tested: the DirectLink models and ARM64.

### Why it needs administrator rights

* **A driver applies to the whole PC.** It affects every user account, so Windows only lets administrators install one. Zadig and TI's own installer need the same permission.
* **Only the helper runs as administrator, and only for this step.** CalcLink itself keeps running with your normal rights. Saying **No** to the prompt changes nothing.

### What the helper does

1. **It writes a tiny driver package for the device's USB ID.** The package is an INF file that tells Windows to use its **built-in WinUSB driver**. No driver files are copied: WinUSB is already part of Windows.
2. **It signs the package.** Windows installs a driver package only if a publisher it trusts has signed it.
   * The helper creates a certificate on your PC, named "CalcLink USB driver for VID_0451&PID_E001" (or your device's ID), and signs the package with it.
   * It then **deletes the certificate's private key**. Without that key, the certificate can't sign anything else.
   * Finally it adds the certificate to the computer's **Trusted Root Certification Authorities** and **Trusted Publishers** stores, so Windows accepts the package without a warning.
   * Zadig signs its packages the same way.
3. **It installs the package for the connected device**, replacing the driver the device had before.

Safeguards:
* **Only TI devices.** The helper accepts only the five calculator and cable USB IDs in [§1](#1-which-cables-and-calculators). Texas Instruments' vendor ID `0451` is also used by ordinary USB hubs and other chips; those are never touched.
* **Nothing can be swapped in.** The helper builds the package in a folder under `%windir%\Temp` that only administrators can write to, so other programs can't replace the package between signing and install. It also loads Windows libraries from System32 only.
* **A failed run leaves nothing behind.** If anything fails, it removes the new certificate again.
* **A log is kept.** The log of the last run is `%LOCALAPPDATA%\CalcLink\driver-setup.log`.

### Undoing it

* **To give the device back to TI Connect,** follow [§5](#5-switching-back-to-ti-connect).
* **To also remove the certificate:**
  1. Run `certlm.msc`.
  2. Open **Trusted Root Certification Authorities → Certificates** and **Trusted Publishers → Certificates**.
  3. In each, delete the "CalcLink USB driver for …" entries.

  Removing it doesn't affect a driver that is already installed.

## 4. Setting up a calculator with Zadig

1. **Connect the calculator** (or the SilverLink cable, with the calculator plugged into it) and switch the calculator on.
2. **Close TI Connect / TI Connect CE / TI-Nspire Computer Link**, so they don't hold the device.
3. **Download Zadig** from its official site only: <https://zadig.akeo.ie/>. Run it; Windows asks for admin rights, because installing a driver is a system change.
4. **Show every device.** Choose **Options → List All Devices**.
5. **Pick the calculator** from the drop-down (for a SilverLink: **TI-GRAPH LINK USB** or **Texas Instruments SilverLink (USB GraphLink) Cable**). Check that the **USB ID** shows `0451` and the PID from §1.
6. **Pick the driver.** Next to the green arrow, choose **WinUSB**.
7. **Click Replace Driver** (or Install Driver) and wait for "The driver was installed successfully".

In Device Manager, the calculator is now listed under **Universal Serial Bus devices**. Its Properties → Driver tab shows provider **libwdi** (Zadig), **CalcLink** ([§3](#3-letting-calclink-set-it-up)) or **Microsoft**, with the **WinUSB** service.

Then start CalcLink and press **Scan**. For a SilverLink, keep the calculator on and at its home screen (`2nd` `QUIT`, `CLEAR`), with the 2.5 mm plug pushed fully in.

## 5. Switching back to TI Connect

TI's software uses TI's own USB driver. **Only one driver can own a device at a time.** While the calculator uses WinUSB, TI Connect won't see it, and while it uses TI's driver, CalcLink won't. To give it back to TI:

1. **Open Device Manager** (`Win + X` → **Device Manager**).
2. **Find the calculator** under **Universal Serial Bus devices** and double-click it.
3. **On the Driver tab, click Roll Back Driver.** If that button is greyed out, click **Update Driver → Browse my computer → Let me pick from a list**, untick **Show compatible hardware** if needed, and choose TI's entry (for a SilverLink: **Texas Instruments SilverLink (USB GraphLink) Cable**).

TI's driver package stays installed on your PC once TI Connect has been installed, so switching back and forth needs no downloads. To return to CalcLink, repeat §3 or §4.

Windows tracks each USB port separately. If you plug the cable into a different port later, Windows may use TI's driver there again. Just repeat §3 or §4 for that port; with §3, CalcLink offers the button again.

## 6. ARM64 PCs (Snapdragon / Windows on ARM)

WinUSB is part of Windows on ARM, and libusb-1.0 supports ARM64, so the CalcLink ARM64 build uses the same setup as §3 and §4. **Not verified yet:** an actual ARM64 PC with a calculator, CalcLink's own driver setup on ARM64, and whether Zadig's ARM64 build handles every step. Please report your results.

## 7. Troubleshooting

| Symptom | Things to check |
|---|---|
| **The calculator doesn't appear in CalcLink** | 1. In Device Manager (**View → Devices by type**), is it under **Universal Serial Bus devices** with the WinUSB driver? If it's elsewhere, or has a yellow warning icon, redo [§3](#3-letting-calclink-set-it-up) or [§4](#4-setting-up-a-calculator-with-zadig).<br>2. Check the hardware ID: Properties → Details → **Hardware Ids** should start with `USB\VID_0451&PID_E0..`.<br>3. Did you plug into a different USB port than when you set up the driver? See the end of [§5](#5-switching-back-to-ti-connect). |
| **"Set up USB driver" failed** | The message shows the helper's log, which is also saved in `%LOCALAPPDATA%\CalcLink\driver-setup.log`. Nothing was changed if it failed. Zadig ([§4](#4-setting-up-a-calculator-with-zadig)) is the fallback. |
| **The "Set up USB driver" button doesn't appear** | It's only offered when a TI calculator or cable is plugged in with a driver CalcLink can't use. If the device already has WinUSB, the problem is elsewhere: check the other rows. |
| **The calculator isn't listed in Device Manager at all** | Try another cable and another port, and avoid unpowered hubs. Many cheap mini-/micro-USB cables carry power only. |
| **SilverLink is found, but the calculator shows as "Unknown" or doesn't answer** | The cable answers but the calculator doesn't. Push the 2.5 mm plug fully in (past the first click), switch the calculator on and go to the home screen. If it still fails, unplug and replug the SilverLink's USB end: a SilverLink can get stuck after a USB error until it's unplugged. |
| **"Busy", "access denied" or "can't open device"** | Another program has the device open. Close TI Connect, Computer Link, TI-SmartView or another CalcLink window, then unplug and replug. |
| **Timeouts during transfers** | Unplug and replug, then reconnect in CalcLink. Large OS or app transfers need a good battery. On CE models, make sure no program is running. |
| **Worked before, broken after installing or updating TI Connect** | The TI installer has probably taken the device back. Set up the driver again (§3 or §4). |
| **Old TiLP installed the libusb-win32 filter driver** | Uninstall old TiLP, then remove the filter. libusb-win32's `install-filter-win.exe` has a *Remove* option (not verified). Unplug all calculators first, and reboot if USB devices misbehave afterwards. |

### Diagnostics

* **`clprobe.exe`.** The portable zip and the installer include `native\win-<arch>\clprobe.exe`, a console tool that uses the same native backend as the app. From a terminal in that folder:
  * `clprobe --list` lists the USB devices tilibs detects.
  * `clprobe` also opens the first device and prints the calculator's version and variable list.
  * `clprobe --model N` opens it as tilibs model *N* regardless of what the probe found (4 = TI-83 Plus, 5 = TI-84 Plus, 9 = TI-89 Titanium).
* **`calclink-cli.exe`**, next to `CalcLink.exe`: `calclink-cli devices` and `calclink-cli info` check the same things from the app folder.
* **Link library messages.** Set `CALCLINK_LOG=1` before running `clprobe` or `calclink-cli` (`$env:CALCLINK_LOG = 1` in PowerShell) to see tilibs' own log, which is hidden otherwise.
* **Reporting a USB problem.** Please include the calculator model and OS version, the PID from Device Manager, the driver name and version (Properties → Driver), whether TI Connect is installed, and the output of `clprobe` with `CALCLINK_LOG=1`.
