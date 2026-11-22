# CalcLink review: code, UI/UX and next features (2026-11-21)

**Scope.** This review covers commit `fe0e335` plus the uncommitted ID / product-code work.

**Sources.**
* A code review of the native shim, Core and App.
* A UI/UX audit of the XAML and view models.
* Screenshots taken on a TI-83 Plus over a SilverLink on Windows 10.

**Verification.**
* Findings marked ✅ were re-checked against the code by hand.
* "Plausible" means the code path exists but hasn't been reproduced on hardware.

**ABI check.**
* No struct layout mismatches were found across the C ABI. `cl_device` is 80 bytes, `cl_calc_info` 248, `cl_var` 48 and `cl_clock` 12 in both `calclink.h` and `NativeMethods.cs`.
* String marshalling and native-memory ownership are consistent.

---

## 1. Bugs

**Fixed since this review:** B1, B2, B3 and B9.
* **B1:** backups and ROM dumps write `<name>.<guid>.partial<ext>` next to the destination, then move it over the destination on success.
* **B2:** in `cl_set_clock`, a format of 0 means "keep the calculator's current setting". Unit tests cover B1 and B2.
* **B3:** closing the window during an operation asks first. The window then cancels the operation, waits for it to stop, and disconnects before closing.
* **B9:** before sending, CalcLink reads the variables in each file with the new `cl_read_file_vars`. Names that clash with the calculator's list trigger Replace / Skip / Cancel. On TI-73/83/84 a name may hold one variable per kind, so a program `A` doesn't clash with a number `A`.

B3 and B9 were checked in the app against the simulator.

**Hardware check of B9 (TI-83 Plus, OS 1.16, SilverLink).** The calculator **silently overwrites** a variable with the same name, whether it is in RAM or archived. An archived variable stays archived, and the transfer reports success. That confirms the prompt is needed and that "Replace" really replaces. The test round trip used a throwaway program, `CLTEST`:
1. Send version A, then version B, then read it back: B.
2. With A on the calculator, archive it, send B, then read it back: B, still archived.

(The TI-83 Plus can't change attributes over the link, so it was archived with remote key presses.)

| # | Sev | Where | Problem | Fix |
|---|---|---|---|---|
| B1 ✅ | **High** | `NativeCalculatorSession.cs:509,523` | A failed or cancelled **Backup or ROM dump deletes the destination file**. That may be an older backup the user chose to replace. tilibs only writes the file on success, so the old file was still intact before CalcLink deleted it. | Write to `<path>.partial`, then `File.Move(overwrite: true)` on success. Never delete the destination. |
| B2 ✅ | Medium | `ToolsViewModel.cs:200`, `calclink.cc:2005-2006` | **Set / Sync clock resets the calculator's date and time formats** when the clock hasn't been read first. It sends format 0, which the 84+ decodes as Y/M/D, and coerces the time format to 24 h. | In `cl_set_clock`, keep the formats from the `get_clock` it already does when the input is 0. |
| B3 | Medium | `MainWindow.xaml.cs:84`, `SerialWorker.cs:16` | **Closing the window mid-transfer kills the link thread** with no warning. During Send OS that can leave the calculator in its boot loader. | Handle `AppWindow.Closing`. If an operation is running, cancel the close and ask. Otherwise await `DisconnectAsync`. |
| B4 | Medium | `calclink.cc:2076` → tilibs `romdump.cc:345` | **ROM dump fails, or writes a garbled name, under non-ASCII paths** such as `C:\Users\José`. tilibs uses `fopen` instead of `g_fopen`. | Add `g_fopen` to the tilibs patch. |
| B5 ✅ | Medium (plausible) | `DevicesViewModel.cs:104`, `calclink.cc:968` | **Scan is allowed while a session is open.** On a SilverLink the probe opens the same cable, and tilibs keeps SilverLink transfer state in file-level statics shared by all handles. | Disable Scan while connected, or serialize all USB calls on one thread. |
| B6 | Low | `calclink.cc:395` | **Cancel can be lost.** `OpScope` clears the cancel flag at the start of every call, so a Cancel that lands between calls is dropped. | Reset the flag once per work item from C#, not in `OpScope`. |
| B7 | Low | `ConnectionService.cs:119-124` | **Cancelling "Connecting…" leaves a live session** that the UI says was cancelled. | On exception after open, dispose the session and clear it. |
| B8 | Low | `CalculatorPage.xaml.cs:81` | **Dropping a folder stops at the first non-TI file** (for example `desktop.ini`), partway through the batch. | Skip unknown files and report them. |
| B9 | Medium (probable) | `CalculatorViewModel.cs:361-388` | **Send never checks for a variable with the same name.** Whether the calculator overwrites or skips depends on the model; the simulator overwrites. | Compare names with the loaded list and offer Replace / Skip / Cancel. |
| B10 | Medium | `ShellViewModel.cs:66-77`, `OperationService.cs:86` | **The "Connection lost" message is replaced** by "…was cancelled" from the operation it cancelled. | Don't post the cancel notice when the cancel came from a lost link. |

**Maintainability.**
* Make `OperationService` the only gate to the link. Screen capture bypasses it today, which is the root of B5.
* Route every file write through one "temp file, then move" helper.
* Call `cl_classify_file` directly. It is stateless and currently blocks the UI thread behind a probe.
* Either wire up `IsReadyAsync` for hot-plug recovery or remove it.
* Clean up `%TEMP%\CalcLink\drag\*` at startup.
* Document in `calclink.h` who owns the cancel flag.

## 2. UI/UX

**Fixed since this review: every item in this section.** All four pages, the shell and Settings were checked in the app at 720×520 and 1180×780 against the simulator. The "nothing found" help was checked for real with no cable attached. The main changes:
* **Layout:**
  * The navigation pane collapses to an icon rail below 1008 DIP.
  * The status bar has two rows: transfer progress, then the connection.
  * The Calculator page adapts to its own width (`ElementWidthTrigger`).
* **Commands:**
  * Commands a model can't do are hidden, not disabled. That covers New folder, Group by folder, Rename, Archive, Move to RAM and Run, plus the Clock card body and the OS warning.
  * Run needs exactly one program or app. Archive and Move to RAM depend on the selection, and Move to RAM no longer unlocks locked items.
* **Progress:**
  * Long operations show "Item n of m · rate · ~m:ss left" and progress on the taskbar button.
  * Success messages close by themselves.
  * "Connection lost" is no longer replaced by "…was cancelled".
* **Errors:** dialogs show the code once, in a Details line, plus a recovery hint (`ErrorHints`).
* **Devices:**
  * A "Calculator not found?" checklist with driver help.
  * The connected row shows a Connected badge and Disconnect.
  * The duplicate toolbar Connect and the backend version string are gone.
* **Calculator page:**
  * A compact Show · View ▾ · Find row.
  * One vocabulary: Location = RAM / Archive, commands Archive and Move to RAM.
  * Friendly type names, with the raw code in the tooltip.
  * Distinct icons.
  * The Delete key acts only on the list.
  * Receive is a split button that can send to the default folder.
* **Tools:**
  * A battery warning appears only when the battery is low.
  * Distinct icons and F5 for Refresh.
* **Screen:**
  * Capture shows a busy state.
  * Live view reports its real frame rate.
* **Settings:**
  * The default folder is shown as plain text.
  * "Restart now" and "Open settings folder" buttons.
  * The license text is collapsed.
* **Theming:** secondary text uses theme brushes instead of `Opacity`.

**Not yet tested live:**
* The "Connection lost" flow (the simulator can't drop a link on demand).
* The Capture busy ring on a slow serial link.

### High
1. **The layout breaks at the 720×520 minimum.** The navigation pane is forced open (`PaneDisplayMode="Left"`), leaving about 500 DIP for content. The Calculator options row needs about 700 DIP and its columns about 570, so the Find box and the Size and Attribute columns get clipped; the transfer status bar clips too.
   * Use `PaneDisplayMode="Auto"`.
   * Add adaptive states: hide the Type column, and fold the view options into a flyout.
2. **Disabled controls rely on tooltips that never show.** WinUI doesn't show tooltips on disabled controls, so "Not supported on TI-83 Plus" is never seen. **Hide** what the model can't do:
   * the Clock card body (pickers plus a "Sync with PC" button that looks enabled in dark mode);
   * New folder and Group by Folder on folderless models;
   * the OS warning when OS transfer is unsupported;
   * Run, except on programs and apps.
3. **The Devices page has no troubleshooting state.** When nothing is found, add an expander with:
   * the driver (Zadig/WinUSB) link;
   * "close TI Connect";
   * "turn the calculator on, home screen";
   * Retry.

   The connected device's row should show **Connected** and Disconnect, not Connect.
4. **Overwrite prompt on send** (B9).

### Medium
* **Long transfers need better progress.** Add "n of m · ~mm:ss left" and taskbar progress. Success banners should auto-dismiss instead of following you to other pages.
* **Archive wording is inconsistent.** It appears as Attribute, RAM / Archive, Archive / Unarchive, and Move to archive / RAM. Pick one term, "Location: RAM / Archive". Enable Archive and Unarchive according to the selection's current state.
* **The Type column shows raw tilibs codes** (`PPRGM`). Use friendly names, such as "Program (protected)".
* **About 200 DIP of controls sit above the variable list**, and the clickable column headers already sort. Use one compact row: Show filter · View ▾ (group / sort) · Find.
* **Screen capture bypasses the operation system.** It has no busy state, and the Live tooltip promises about 3 fps, which a TI-83 Plus can't deliver.
* **Battery is shown twice on Tools** (badge and row). Keep the row, and show a badge only when the battery is low.

### Low
* **The error code is shown twice** in error dialogs. Map common codes to a fix, for example a timeout → "turn the calculator on and go to the home screen".
* **Secondary text uses `Opacity` instead of theme brushes,** which breaks High Contrast. Use `TextFillColorSecondaryBrush`.
* **Icons are reused with different meanings:**
  * Archive is reused for ROM dump.
  * Send OS looks like Refresh.
  * Unarchive and the group header share a glyph.
* **Tools → Refresh has no F5** shortcut.
* **The model name appears 3–4 times on screen** at once.
* **Settings:**
  * The read-only "Not set" TextBox looks editable.
  * The backend message needs a "Restart now" button.
  * The settings path should be an "Open folder" link.
  * Collapse the GPL text.
* **Delete may fire while typing in Find.** Scope the accelerator to the list.

## 3. Features worth adding

Ranked by value for students, teachers and hobbyists, against effort.

| # | Feature | Why | Effort |
|---|---|---|---|
| F1 | **Program viewer (TI-BASIC detokeniser)** | Read programs on the PC, with no need to send them back or squint at the calculator. Later this could grow into editing and tokenising so programs can be written on the PC. tilibs doesn't detokenise, so this needs a token table (83+/84+ first). | M (view) / L (edit) |
| F2 | **Remote keypad window** | Core already has `SendKeyAsync`, and tilibs has per-model key tables. We used it in this session to open the About screen. Pair it with Live screen for a full remote control. | M |
| F3 | **Hot-plug: auto-detect and reconnect** | Today the user must press Scan, and a replug needs a manual reconnect. Watch USB arrivals for VID 0451 (`CM_Register_Notification` / `DeviceWatcher`). | M |
| F4 | **One-click dated backups and restore** | "Back up now" to a default folder named by date, a list of past backups, and Restore. This is a common classroom need. | S–M |
| F5 | **Group-file explorer (replaces GFM)** | Open `.8xg`/`.tig`/`.89g` files, list and extract their contents, build new groups, and send selected items. libtifiles already has the helpers. | M |
| F6 | **Picture and image viewer** | Show `.8xi`/`.8ci`/`.8ca` and Pic variables as images, and convert PNG to calculator images. | M |
| F7 | **Explorer integration** | Double-click `.8xp`/`.8xk`/… to open in CalcLink. Add "Send to calculator" to the right-click menu. Allow drag onto the app's taskbar or tray. | S–M |
| F8 | **Diagnostics view** | A runtime toggle for cable byte logging and a protocol event log (`ticalcs_calc_set_event_hook`), plus "Export diagnostics" for bug reports. This was how the SilverLink and WinUSB problem was solved. | M |
| F9 | **Command-line tool** | `calclink send/recv/backup/screen/info` for scripting and classroom setup. `clprobe` is already most of it. | M |
| F10 | **Screen recording** | Record animated GIF or MP4 from Live view, add a scale option, and include the frame or bezel. | S–M |
| F11 | **Multiple calculators / classroom mode** | Send the same files to every connected calculator at once. tilibs is handle-based, so it's feasible, but the current UI assumes one session. | L |
| F12 | **Emulator link** (TCP cable to TilEm or CEmu) | Develop and test without hardware. | M |

## 4. Suggested order

1. **Data-safety fixes:** B1, B2, B3 and B9, then B5 and B10.
2. **UX:** hide unsupported features, the adaptive layout, the troubleshooting empty state, and transfer progress.
3. **Features:** F2 (cheap, since the code exists), F4 and F1, then F3.

## Appendix: blue screen on 2026-11-21 23:17

**What happened.**
* Stop code `0x3B` SYSTEM_SERVICE_EXCEPTION (`c0000005`, an access violation in kernel code).
* CalcLink was probably still connected over SilverLink/WinUSB, idle after the screenshots, when it happened.
* No USB or driver errors were logged beforehand.

**Is CalcLink to blame?** A user-mode program like CalcLink can't cause a 0x3B on its own; a driver has to fault. B5 is a user-mode race, so it can't explain the crash by itself either.

**Next step.** Identify the faulting driver by opening `C:\Windows\Minidump\112126-26328-01.dmp` in WinDbg as administrator and running `!analyze -v`. Until it's identified, treat the WinUSB/SilverLink path as a suspect.
