# CalcLink review: code, UI/UX and next features (2026-11-21)

**Scope.** This review covers commit `fe0e335` plus the uncommitted ID / product-code work. Later notes record what was fixed or built since; §4 has the current status.

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

**Fixed later (2026-11-22):** B4, B6, B7 and B8. Every bug in this list is now fixed.
* **B4:** `patches/tilibs-0002-utf8-file-paths.patch` switches libticalcs' own file writes (TI-83 Plus-family and Nspire ROM dumps, certificate receipt) to `g_fopen`. A fetched tilibs gets it automatically, and configure checks a local checkout for it.
  * This work turned up a separate problem: `tilibs-0001-…patch` had been committed with **no line breaks at all**, so a build that fetches tilibs could not apply it. Local-checkout builds never hit this.
  * Both patches were regenerated with `git diff --output` and checked to apply cleanly to upstream. `.gitattributes` now keeps `*.patch` byte-exact.
  * Not tested with a real ROM dump to a non-ASCII folder.
* **B6:** calls no longer clear the cancel flag. The new `cl_reset_cancel` (ABI 3) is called once per work item by `NativeCalculatorSession.Run`, before it registers for cancellation, so a cancel between two calls of one operation stays set. `calclink.h` documents who owns the flag.
* **B7:** if `ConnectionService.ConnectAsync` fails or is cancelled after the link opened (while reading info), it now unhooks, clears and disposes the session. `NativeCalculatorSession.OpenAsync` also closes a half-opened handle on failure.
* **B8:** `SendPathsAsync` classifies every path first. Files that aren't calculator files are set aside: hidden and system ones (`desktop.ini`) silently, the rest listed in a warning after the send.
* **Tests:**
  * B8 was checked in the simulator: `A.8Xn` was sent, `notes.txt` was reported as skipped, and `desktop.ini` was skipped silently.
  * B6 and B7 are races that the simulator can't trigger on demand, and haven't been seen live.

**Hardware check of B9 (TI-83 Plus, OS 1.16, SilverLink).** The calculator **silently overwrites** a variable with the same name, whether it is in RAM or archived. An archived variable stays archived, and the transfer reports success. That confirms the prompt is needed and that "Replace" really replaces. The test round trip used a throwaway program, `CLTEST`:
1. Send version A, then version B, then read it back: B.
2. With A on the calculator, archive it, send B, then read it back: B, still archived.

(The TI-83 Plus can't change attributes over the link, so it was archived with remote key presses.)

**Correction (F4 restore test, 2026-11-22).** Restoring the protected program `AVALANCH` (556 B) from a backup over its archived copy left it **in RAM**. The contents were identical, and the only file differences were the archived flag and the checksum. So where a replaced archived variable lands isn't reliable: the CLTEST case above stayed archived, this one didn't. Restore therefore re-reads the list afterwards and tells the user which items to archive again.

| # | Sev | Where | Problem | Fix |
|---|---|---|---|---|
| B1 ✅ | **High** | `NativeCalculatorSession.cs:509,523` | A failed or cancelled **Backup or ROM dump deletes the destination file**. That may be an older backup the user chose to replace. tilibs only writes the file on success, so the old file was still intact before CalcLink deleted it. | Write to `<path>.partial`, then `File.Move(overwrite: true)` on success. Never delete the destination. |
| B2 ✅ | Medium | `ToolsViewModel.cs:200`, `calclink.cc:2005-2006` | **Set / Sync clock resets the calculator's date and time formats** when the clock hasn't been read first. It sends format 0, which the 84+ decodes as Y/M/D, and coerces the time format to 24 h. | In `cl_set_clock`, keep the formats from the `get_clock` it already does when the input is 0. |
| B3 | Medium | `MainWindow.xaml.cs:84`, `SerialWorker.cs:16` | **Closing the window mid-transfer kills the link thread** with no warning. During Send OS that can leave the calculator in its boot loader. | Handle `AppWindow.Closing`. If an operation is running, cancel the close and ask. Otherwise await `DisconnectAsync`. |
| B4 ✅ | Medium | `calclink.cc:2076` → tilibs `romdump.cc:345` | **ROM dump fails, or writes a garbled name, under non-ASCII paths** such as `C:\Users\José`. tilibs uses `fopen` instead of `g_fopen`. | Add `g_fopen` to the tilibs patch. |
| B5 ✅ | Medium (plausible) | `DevicesViewModel.cs:104`, `calclink.cc:968` | **Scan is allowed while a session is open.** On a SilverLink the probe opens the same cable, and tilibs keeps SilverLink transfer state in file-level statics shared by all handles. | Disable Scan while connected, or serialize all USB calls on one thread. |
| B6 ✅ | Low | `calclink.cc:395` | **Cancel can be lost.** `OpScope` clears the cancel flag at the start of every call, so a Cancel that lands between calls is dropped. | Reset the flag once per work item from C#, not in `OpScope`. |
| B7 ✅ | Low | `ConnectionService.cs:119-124` | **Cancelling "Connecting…" leaves a live session** that the UI says was cancelled. | On exception after open, dispose the session and clear it. |
| B8 ✅ | Low | `CalculatorPage.xaml.cs:81` | **Dropping a folder stops at the first non-TI file** (for example `desktop.ini`), partway through the batch. | Skip unknown files and report them. |
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
| F1 ✅ | **Program viewer (TI-BASIC detokeniser)** | Read programs on the PC, with no need to send them back or squint at the calculator. Later this could grow into editing and tokenising so programs can be written on the PC. tilibs doesn't detokenise, so this needs a token table (83+/84+ first). **Viewing and editing done** (see below). | M (view) / L (edit) |
| F2 | **Remote keypad window** | Core already has `SendKeyAsync`, and tilibs has per-model key tables. It was used during testing to open the About screen. Pair it with Live screen for a full remote control. | M |
| F3 | **Hot-plug: auto-detect and reconnect** | Today the user must press Scan, and a replug needs a manual reconnect. Watch USB arrivals for VID 0451 (`CM_Register_Notification` / `DeviceWatcher`). | M |
| F4 ✅ | **One-click dated backups and restore** | "Back up now" to a default folder named by date, a list of past backups, and Restore. This is a common classroom need. **Done** (see below). | S–M |
| F5 ✅ | **Group-file explorer (replaces GFM)** | Open `.8xg`/`.tig`/`.89g` files, list and extract their contents, build new groups, and send selected items. libtifiles already has the helpers. **Done for `.tig` and the TI-83/84 Plus family** (see below); 68k groups (`.89g`) not yet. | M |
| F6 | **Picture and image viewer** | Show `.8xi`/`.8ci`/`.8ca` and Pic variables as images, and convert PNG to calculator images. | M |
| F7 ✅ | **Explorer integration** | Double-click `.8xp`/`.8xk`/… to open in CalcLink. Add "Send to calculator" to the right-click menu. Allow drag onto the app's taskbar or tray. **Done** except the taskbar/tray drop (see below). | S–M |
| F8 | **Diagnostics view** | A runtime toggle for cable byte logging and a protocol event log (`ticalcs_calc_set_event_hook`), plus "Export diagnostics" for bug reports. This was how the SilverLink and WinUSB problem was solved. | M |
| F9 ✅ | **Command-line tool** | `calclink send/recv/backup/screen/info` for scripting and classroom setup. `clprobe` is already most of it. **Done** as `calclink-cli.exe` (see below). | M |
| F10 | **Screen recording** | Record animated GIF or MP4 from Live view, add a scale option, and include the frame or bezel. | S–M |
| F11 | **Multiple calculators / classroom mode** | Send the same files to every connected calculator at once. tilibs is handle-based, so it's feasible, but the current UI assumes one session. | L |
| F12 | **Emulator link** (TCP cable to TilEm or CEmu) | Develop and test without hardware. | M |

**F9 as built.** `src/CalcLink.Cli`, published as `calclink-cli.exe`. It isn't called "calclink.exe" because Windows names are case-insensitive and would clash with `CalcLink.exe`.
* **Commands:** devices, info, list, send, receive (with wildcards), backup, screenshot, view.
* **Built on Core**, so it behaves like the app:
  * Backups go to the same folder. Model names and backup extensions moved from the app into `CalcModelNames`, so both name the folder identically, and the tool reads the app's backups-folder setting.
  * `send` refuses name clashes unless `--replace` or `--skip-existing` is given, and refuses OS files (use the app's guarded Send OS).
* **New in Core:** `ScreenPng` writes screenshots as PNG without an imaging library; its checksum and pixels are unit-tested.
* **Native layer:** tilibs' GLib log output, routine link chatter that printed dozens of warnings per command, is now dropped unless `CALCLINK_LOG` is set.
* **Packaging:**
  * `Build-Portable.ps1` publishes the tool into the app folder and checks that shared runtime files are identical.
  * It also fixes an existing failure: the app's dev-time native copy collided with the script's own copy whenever the native layer was built.
  * The portable zip was built and both executables ran from it.
* **Tested:**
  * On the simulator, every command, including clash refusal, `--replace`, wildcard receive and PNG output.
  * On the TI-83 Plus: devices, info, list (66 items), view, screenshot, receive and a full backup (77 s).

**F7 as built.**
* **One CalcLink at a time.** `Program.cs` replaces the XAML-generated `Main` and registers the instance key "CalcLink.Main". A second launch redirects its activation to the running process with `AppInstance.RedirectActivationToAsync`, hands it the foreground, and exits. Before Settings → Restart now, the key is unregistered so the new process doesn't redirect to the one that's exiting.
* **Routing (`LaunchArguments`).**
  * A plain open of one file the Files page can read (checked by its header) opens it there.
  * `--send`, several files, or anything else (apps, OS, 68k) is queued in `PendingFilesService`, which now appends.
  * Queued files are sent at once if a calculator is connected. Otherwise the app goes to Devices and scans, and a single calculator found connects and sends.
* **Installer.**
  * The existing "Open with" registration is kept.
  * New: a "Send to calculator (CalcLink)" verb under `SystemFileAssociations\.<ext>\shell`, so it appears whichever app is the default. It covers 28 variable, group, app and backup extensions, not OS files.
* **Tested** with the simulator, by launching the exe as Explorer does:
  * opening a `.8xp` showed it on the Files page;
  * a second launch with a `.tig` redirected in 0.1 s, and the running window showed the backup;
  * `--send` while not connected queued the file on Devices, and connecting sent it (with the name-clash prompt);
  * `--send` while connected sent at once.
* **Not tested:** the registry entries themselves. Inno Setup isn't installed here, so the script wasn't compiled, and nothing was registered on this PC.

**F5 as built.** A **Files** page that works without a calculator (`FilesViewModel`, `CalcLink.Core/GroupFile.cs`).
* **What it opens.**
  * `.tig` files: unpacked with the F4 extractor. TI-83 Plus family variables are parsed, apps are listed as apps, and other models' files are listed as-is.
  * `**TI83**` / `**TI83F*` files, with one variable or a group: each entry is written out as its own file.
* **Names.** Lists, matrices, equations, strings, pictures and GDBs are named by tokens, so `TiVarFile` decodes them (`L₁`, `ʟAB`, `[B]`, `Str1`). The token's second byte may be 00, which was the first bug caught. System variables get the calculator's names (Window, RclWindw, TblSet).
* **Actions.**
  * **Open program** (uses F1).
  * **Save to folder** copies the per-item files.
  * **Save as group** writes a new `.8xg` with `TiVarFile.Write`, a byte-exact round trip that includes the checksum.
  * **Send to calculator** uses the Calculator page's send path, so name clashes still ask first.
* **Browse contents** opens a backup from Tools → Backups.
* **Hardware-tested.** The TI-83 Plus backup listed 64 items (37 programs, 2 apps) with correct names, and INTEGRAL opened from it.
* **Not tested through the UI:** Save to folder, Save as group and Send, because their pickers and the send step can't be automated here. The file writing is covered by unit tests.

**F1 as built (editing).** A **Program editor** page (`EditorViewModel`, `ProgramEditorPage`), reached from the navigation or from **Edit** in the program viewer.
* **Checking as you type.** `TiBasic.TryTokenize` returns where it failed, so errors say "Line 4, column 6: "½" isn't part of any TI-BASIC command or character", with a Go to error button. Save and Send stay disabled while the program is invalid or the name is.
  * Names must follow `TiBasic.IsValidProgramName`: 1–8 characters, A–Z, 0–9 and θ, starting with a letter or θ.
* **Typing help.** A symbol bar, plus typing shortcuts in the editor only: `->`, `<=`, `>=`.
  * There's no `!=` shortcut, because `N!=120` is valid TI-BASIC (factorial, then equals).
  * Plain `Tokenize` stays exact, so listings still round-trip.
* **Placeholder safety.** Placeholders (`[BB D0]`) are read back only for codes the tables don't name. Before this, the matrix literal `[[12]]` would have been read as token 12.
* **Look-alike warning.** A program that doesn't round-trip, such as ACT with its statistics variables, warns before saving, because saving would turn those into ordinary letters.
* **Saving and sending.** `TiVarFile.WriteProgram` makes the `.8xp`. Send goes through the Calculator page's send path, so a name clash asks first.
* **Tested in the simulator.** Edit opened QUADFORM. An invalid character was reported by line and column. A new program typed with shortcuts sent and read back with → and ≥.
  * Bug found on the way: the editor's view model ran its first check before its text was set, which crashed the app when the page opened.

**F1 as built (viewing).** `CalcLink.Core/TiBasic.cs` holds the TI-83 / TI-83 Plus / TI-84 Plus token tables, a detokeniser and a longest-match tokeniser. `TiVarFile.cs` reads `**TI83**` / `**TI83F*` variable files.
* **How the tables were checked.**
  * Against TI's `ti83plus.inc`: all 256 one-byte tokens, the two-byte groups 5C–63, 7E and AA, and BB 00–CD.
  * The EF group (TI-84 Plus OS 2.x) has only the well-known clock and statistics tokens. Anything else shows as `[BB D0]` instead of a guess.
* **Tested on a real TI-83 Plus's 37 programs** (from a backup).
  * The 25 TI-BASIC programs (about 2,700 lines) had no unknown tokens.
  * 24 of 25 convert back to identical bytes. ACT uses the statistics variables `e`, `r`, `b`, `n`, which look the same as lowercase letters.
  * All 12 assembly programs were recognised as assembly.
* **The viewer.** Calculator page → **Open program** (Enter, double-click) receives the program to a temporary folder and shows it with line numbers, Copy and Save as `.txt`. Assembly gets a hex and ASCII dump.
* **Simulator.** Its sample programs are now real tokenised BASIC.
* **Hardware-tested.** On the TI-83 Plus, INTEGRAL (archived BASIC) opened in 3 s and PHOENIX (assembly) in 6 s.

**F4 as built.** What decided the design: a test backup of a TI-83 Plus (OS 1.16) with 64 items over SilverLink.
* **Which format.**
  * `.8xb` (2.6 s, 6.6 KB) is a RAM image. It holds none of the 37 archived programs or the 2 apps.
  * `.tig` (70 s, 87 KB) holds all 64 items, each with its archived flag.
  * So **Back up now** writes a `.tig` on flash models. It goes to `<Backups folder>\<model> (<last 4 ID digits>)\yyyy-MM-dd HH-mm-ss.tig`, and the backups folder defaults to `Documents\CalcLink Backups`.
* **Why restore is selective.**
  * Sending the `.tig` back as it is (`ticalcs_calc_send_tigroup`) would send each archived variable with the archived flag. The TI-83 Plus never answers such a transfer.
  * Even in RAM, 80 KB of programs doesn't fit in its ~24 KB of RAM.
* **How restore works.**
  1. It unpacks the `.tig` and compares each item with the calculator's list.
  2. It pre-selects only the missing items and warns when the selection exceeds free RAM. Free RAM now comes from `cl_get_memfree` on models whose version reply lacks it.
  3. It sends the chosen files through the normal send path, which delivers them to RAM.
  4. On models that can archive over the link, it archives what was archived; otherwise it lists those items for the user to archive on the calculator.
* **Older formats.** Native RAM backups (`.8xb` and friends) can still be restored, after a "replaces all of RAM" confirmation.
* **Hardware-tested on a TI-83 Plus.**
  * Back up now saved 64 items (85.4 KB).
  * The restore dialog listed all 64 as "On calculator", with nothing pre-selected.
  * Restoring `A` (RAM) and `AVALANCH` (archived) replaced both with byte-identical contents. `AVALANCH` came back in RAM, and CalcLink listed it to be archived again.

## 4. Where things stand (2026-11-22)

* **Bugs:** B1–B10 are all fixed (§1).
* **UI/UX:** every item in §2 is fixed.
* **Features done:** F1 (viewer and editor), F4, F5 (except 68k groups), F7 (except the taskbar drop) and F9.
* **Still planned:** F6 picture viewer and F12 emulator link, plus the rest of F5 and F7.
* **Not planned for now:** F2, F3, F8, F10 and F11.
