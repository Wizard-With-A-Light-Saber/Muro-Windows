; Muro for Windows: per-user installer (no admin rights). Built by scripts/publish.ps1, which passes
; /DAppVersion=<version> and /DStageDir=<the published folder>.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef StageDir
  #error StageDir is required: build with scripts/publish.ps1
#endif

[Setup]
AppId={{7C1A5E54-3B1D-4F1E-9B7D-5A6F0C2E8D41}
AppName=Muro
AppVersion={#AppVersion}
AppPublisher=Muro (Windows port)
DefaultDirName={localappdata}\Programs\Muro
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Muro-Setup-{#AppVersion}
SetupIconFile=..\assets\muro.ico
UninstallDisplayIcon={app}\Muro.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Muro and its engine are stopped by hand below: the engine has no window for the installer to close.
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start Muro with Windows (quietly, with its tray icon)"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\Muro"; Filename: "{app}\Muro.exe"
Name: "{userdesktop}\Muro"; Filename: "{app}\Muro.exe"; Tasks: desktopicon

[Registry]
; The same entry the app's Settings writes: start the engine, which shows the tray icon and plays the wallpaper.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Muro"; ValueData: """{app}\Muro.Engine.exe"""; Tasks: startup

[Run]
Filename: "{app}\Muro.exe"; Description: "Start Muro"; Flags: nowait postinstall skipifsilent

[Code]
// Stops Muro, its engine (wallpaper windows are destroyed with it, so the desktop wallpaper comes back) and a
// running screen saver, so their files can be replaced or removed.
procedure StopMuro;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM Muro.exe /IM Muro.Engine.exe /IM Muro.scr', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopMuro;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Saver: String;
  RestoreCode: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  StopMuro;
  // Muro keeps the Windows wallpaper as a still of its own wallpaper (so UAC prompts show it). Put the user's own
  // wallpaper, or slideshow, back before the engine that knows how is removed.
  Exec(ExpandConstant('{app}\Muro.Engine.exe'), '--restore-windows-wallpaper', '', SW_HIDE, ewWaitUntilTerminated, RestoreCode);
  // No longer start with Windows, whichever way it was switched on.
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Muro');
  // Windows must not be left pointing at a screen saver that is gone. What it used before Muro is kept in
  // Muro's own settings, not here: turn Muro's screen saver off in Settings first to get that back.
  if RegQueryStringValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE', Saver) then
    if CompareText(ExtractFileName(Saver), 'Muro.scr') = 0 then
      RegDeleteValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE');
end;
