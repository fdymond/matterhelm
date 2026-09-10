; MatterHelm Windows installer (S10, Inno Setup 6).
; Built by the release workflow after build.ps1 has produced dist\:
;   ISCC /DAppVersion=x.y.z installer\matterhelm.iss
; Design notes:
; - Per-user by default (lowest privileges, no UAC) with an override dialog —
;   the pattern modern tray tools use; also the sane default for an unsigned
;   binary. Per-user install lands in {localappdata}\Programs\MatterHelm.
; - AppMutex matches Program.cs's single-instance mutex, so BOTH install and
;   uninstall over a running MatterHelm prompt the user to close it first
;   (closing the tray app takes the sidecar down via its stdin tether).
;   Silent (/VERYSILENT) runs abort instead of prompting - by design.
; - Uninstall deliberately leaves %APPDATA%\MatterHelm alone: it holds the
;   Matter fabric credentials — deleting it would silently unpair the user
;   from Google Home. The user guide documents manual cleanup.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{7C1E9A2E-4E1B-4B5B-9A7D-3D3A9B6F51C4}
AppName=MatterHelm
AppVersion={#AppVersion}
AppVerName=MatterHelm {#AppVersion}
AppPublisher=MatterHelm project
AppPublisherURL=https://github.com/fdymond/matterhelm
AppSupportURL=https://github.com/fdymond/matterhelm/blob/main/SUPPORT.md
AppUpdatesURL=https://github.com/fdymond/matterhelm/releases
DefaultDirName={autopf}\MatterHelm
DefaultGroupName=MatterHelm
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist-installer
OutputBaseFilename=MatterHelm-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=MatterHelm.SingleInstance
MinVersion=10.0.17763
CloseApplications=yes
UninstallDisplayName=MatterHelm

[Tasks]
Name: "startup"; Description: "Start MatterHelm when you sign in to Windows"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\dist\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\*"; DestDir: "{app}"; Excludes: "THIRD-PARTY-NOTICES.txt"; Flags: recursesubdirs ignoreversion

[InstallDelete]
; Remove both known sidecar payload shapes before [Files] installs the one
; declared by sidecar-layout.json, so a layout-changing upgrade leaves no stale peer.
Type: files; Name: "{app}\sidecar\bridge.exe"
Type: files; Name: "{app}\sidecar\node.exe"
Type: files; Name: "{app}\sidecar\bridge.cjs"

[Icons]
Name: "{autoprograms}\MatterHelm"; Filename: "{app}\MatterHelm.exe"
Name: "{autodesktop}\MatterHelm"; Filename: "{app}\MatterHelm.exe"; Tasks: desktopicon

[Registry]
; Sign-in autostart via the per-user Run key (cleaner than a Startup-folder
; shortcut; removed automatically on uninstall).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "MatterHelm"; ValueData: """{app}\MatterHelm.exe"""; \
  Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\MatterHelm.exe"; Description: "{cm:LaunchProgram,MatterHelm}"; \
  Flags: nowait postinstall skipifsilent
