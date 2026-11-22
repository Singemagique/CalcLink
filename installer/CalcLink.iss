; CalcLink installer: Inno Setup 6.3+ script.
;
; One installer per architecture. Stage the app first, then compile (from the repo root):
;   pwsh installer\Build-Portable.ps1 -Rid win-x64 -Version 1.2.0
;   ISCC.exe /DArch=x64 /DAppVersion=1.2.0 installer\CalcLink.iss
;   pwsh installer\Build-Portable.ps1 -Rid win-arm64 -Version 1.2.0
;   ISCC.exe /DArch=arm64 /DAppVersion=1.2.0 installer\CalcLink.iss
;
; Defines (all optional except where the default doesn't fit):
;   Arch               "x64" (default) or "arm64"
;   AppVersion         display/semantic version, e.g. "1.2.0" or "1.2.0-beta.1"
;   AppVersionNumeric  numeric file version, default = AppVersion without any "-suffix"
;   SourceDir          staged app folder, default ..\artifacts\stage\win-<Arch>\CalcLink
;   OutputDir          default ..\artifacts\dist
;   RepoUrl            project URL used for support links and the driver guide
;
; Policy (see AUDIT.md 2.3): per-user by default, no shared DLLs, nothing in system32,
; no environment variables, no drivers. The USB driver is a separate, explicit user step
; (docs/DRIVERS.md); this installer only offers to open that guide. The app ships
; native\<rid>\calclink-driver.exe, which assigns WinUSB only when the user clicks "Set up USB
; driver" in the app and approves Windows' administrator prompt.

#if VER < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required (for the x64compatible architecture identifier).
#endif

#ifndef Arch
  #define Arch "x64"
#endif
#if (Arch != "x64") && (Arch != "arm64")
  #error Arch must be "x64" or "arm64".
#endif
#define Rid "win-" + Arch

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef AppVersionNumeric
  #define DashPos Pos("-", AppVersion)
  #if DashPos > 0
    #define AppVersionNumeric Copy(AppVersion, 1, DashPos - 1)
  #else
    #define AppVersionNumeric AppVersion
  #endif
#endif

#ifndef SourceDir
  #define SourceDir AddBackslash(SourcePath) + "..\artifacts\stage\" + Rid + "\CalcLink"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts\dist"
#endif
#ifndef RepoUrl
  ; Placeholder: CI passes /DRepoUrl=https://github.com/<owner>/<repo>.
  #define RepoUrl "https://github.com/OWNER/CalcLink"
#endif

#define AppName      "CalcLink"
; AssemblyName in src/CalcLink.App/CalcLink.App.csproj.
#define AppExeName   "CalcLink.exe"
#define AppPublisher "CalcLink contributors"
#define ProgId       "CalcLink.TIFile"
#define DriversUrl   RepoUrl + "/blob/main/docs/DRIVERS.md"
#define AppIcon      AddBackslash(SourcePath) + "..\src\CalcLink.App\Assets\CalcLink.ico"

#if !FileExists(AddBackslash(SourceDir) + AppExeName)
  #error CalcLink.exe not found in SourceDir. Run installer\Build-Portable.ps1 first or pass /DSourceDir=...
#endif

[Setup]
; Keep AppId stable forever: it identifies the installation for upgrades and uninstall.
; Both architectures share it, so installing one over the other replaces it in place.
AppId={{471239A9-AF73-4883-A5B0-BAF873B4B013}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (C) 2026 CalcLink contributors. GPL-2.0-or-later.
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup ({#Arch})

; Per-user install by default; the user can choose "all users" (elevates) in the dialog.
; With PrivilegesRequired=lowest, {autopf} is %LOCALAPPDATA%\Programs and HKA is HKCU.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsedUserAreasWarning=no
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; The x64 build installs on x64 Windows and on ARM64 Windows 11 (x64 emulation).
; The arm64 build installs only on ARM64.
#if Arch == "arm64"
ArchitecturesAllowed=arm64
#else
ArchitecturesAllowed=x64compatible
#endif
ArchitecturesInstallIn64BitMode=x64compatible arm64
; Windows App SDK minimum is Windows 10 1809.
MinVersion=10.0.17763

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-{#Rid}-setup
#if FileExists(AppIcon)
SetupIconFile={#AppIcon}
#endif
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Add CalcLink to ""Open with"" for TI calculator files (.8xp, .8xk, .8xu, .tns, .tig, .89p ...)"; GroupDescription: "File types:"

[Files]
; The whole self-contained app: .NET runtime, Windows App SDK runtime, native\win-<arch>\ backend.
; Every file is private to {app}; nothing is marked sharedfile and nothing goes to {sys}.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; File associations, the documented Inno Setup way: HKA (HKCU per-user, HKLM all-users) under
; Software\Classes. CalcLink registers itself under OpenWithProgids only, so it appears in
; "Open with" and never takes over the default handler (e.g. TI Connect CE) for an extension.
; Uninstall removes the ProgID, the Applications key and our OpenWithProgids values only.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "TI calculator file"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: fileassoc
; Per extension: create the key if needed (removed on uninstall only if empty), add our ProgID to
; OpenWithProgids, and list it in SupportedTypes. Variables/groups, apps, OS files, backups,
; certificates (83+/84+ family, 82/83/85/86/73, 89/92+/V200, Nspire) and TIGroups.
Root: HKA; Subkey: "Software\Classes\.8xp"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xp\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xp"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xv"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xv"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xg"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xg\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xg"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xk"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xk\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xk"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8ek"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8ek\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8ek"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xu"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xu\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xu"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8eu"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8eu\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8eu"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8cu"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8cu\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8cu"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xl"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xl\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xl"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xm"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xm\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xm"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xn"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xn\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xn"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xs"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xs\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xs"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xy"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xy\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xy"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xi"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xi\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xi"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8ci"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8ci\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8ci"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8ca"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8ca\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8ca"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xb"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xb\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xb"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8cb"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8cb\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8cb"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xq"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.8xq\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".8xq"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.83p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.83p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".83p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.83g"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.83g\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".83g"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.82p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.82p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".82p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.82g"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.82g\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".82g"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.85p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.85p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".85p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.86p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.86p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".86p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.73p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.73p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".73p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".89p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89g"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89g\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".89g"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89k"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89k\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".89k"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89u"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.89u\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".89u"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.9xp"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.9xp\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".9xp"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.9xk"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.9xk\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".9xk"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.9xu"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.9xu\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".9xu"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.v2p"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.v2p\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".v2p"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.v2u"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.v2u\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".v2u"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tns"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tns\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tns"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tno"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tno\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tno"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tnc"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tnc\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tnc"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tco"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tco\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tco"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tcc"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tcc\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tcc"; ValueData: ""; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tig"; Flags: uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.tig\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".tig"; ValueData: ""; Tasks: fileassoc

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Opens the online guide; a copy is installed as {app}\docs\DRIVERS.md. Never installs a driver.
Filename: "{#DriversUrl}"; Description: "Open the USB driver setup guide (needed once before connecting a calculator)"; Flags: shellexec postinstall skipifsilent unchecked nowait

[UninstallDelete]
; Installed files are removed by Inno itself. This only removes the folder if it is empty.
; User settings in %LOCALAPPDATA%\CalcLink are kept on purpose.
Type: dirifempty; Name: "{app}"
