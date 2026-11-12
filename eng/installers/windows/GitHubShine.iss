; The Windows installer. Built by the release workflow, once per architecture:
;
;   iscc /DAppVersion=1.2.0 /DFileVersion=1.2.0 /DArch=x64 /DSourceDir=<publish dir> /DOutputDir=<dir> GitHubShine.iss
;
; Per-user, into %LOCALAPPDATA%\Programs\GitHub Shine, so nobody needs to be an administrator to
; install it, and because "Start with the computer" writes this exe's path into the HKCU Run key —
; the path has to stay put across upgrades.
;
; The app does not update itself on Windows yet (Features/Updates only opens the release page), so
; an upgrade is the user running the newer setup over the old one; AppId is what lets it find the
; copy it is replacing.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish output>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppExe "GitHubShine.exe"

[Setup]
; Never change this: it is how an upgrade finds the copy it is replacing.
AppId={{D1D0EC9F-0C9E-4A92-B182-8F075F9C61FE}
AppName=GitHub Shine
AppVersion={#AppVersion}
AppVerName=GitHub Shine {#AppVersion}
AppPublisher=Allan Ritchie
AppPublisherURL=https://github.com/aritchie/githubmon
AppSupportURL=https://github.com/aritchie/githubmon/issues
AppUpdatesURL=https://github.com/aritchie/githubmon/releases
VersionInfoVersion={#FileVersion}
PrivilegesRequired=lowest
; Not %LOCALAPPDATA%\GitHubShine: that is the app's data folder (AppPaths.DataDirectory).
DefaultDirName={autopf}\GitHub Shine
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=GitHub Shine
OutputDir={#OutputDir}
OutputBaseFilename=GitHubShine-{#AppVersion}-windows-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The tray app keeps running with its window closed; Restart Manager closes it for an upgrade.
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GitHub Shine"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\GitHub Shine"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Not written here - the app writes them (Shiny.Extensions.MauiHosting's startup service) when
; "Start with the computer" is turned on. The value name is StartupServiceOptions.Identifier in
; MauiProgram.cs. Only removed on uninstall, so an uninstalled app is not started at next sign-in;
; StartupApproved\Run is where Windows records the user disabling it in Task Manager.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "GitHubShine"; ValueType: none; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueName: "GitHubShine"; ValueType: none; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,GitHub Shine}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app keeps running behind the tray icon, which would hold its files open.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopGitHubShine"

; The data folder (%LOCALAPPDATA%\GitHubShine - accounts, tokens, the SQLite database, sync cache
; and mobile clones) is left alone: an uninstall to reinstall should not take every account with it.
