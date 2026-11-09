; The installer of Stratoscope for Windows: one Setup.exe, one optional checkbox (desktop icon, off): a progress window that says
; what is happening, then one "Finish" that says where to look. Per user (no administrator rights), into
; %LOCALAPPDATA%\Stratoscope, with a Start menu entry and "start with Windows" (the shortcut the app's own switch manages), then it starts.
; An update closes the running app first, keeps the settings and keeps the user's choice about starting with Windows.
; /VERYSILENT works too. Built by scripts\windows\build.ps1 with Inno Setup (ISCC), which passes AppVersion and SourceDir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is not set: run scripts\windows\build.ps1
#endif

[Setup]
AppId={{BED3E514-C20B-4074-9E13-CBEACBE2ADD4}
AppName=Stratoscope
AppVersion={#AppVersion}
AppPublisher=Stratoscope contributors
AppPublisherURL=https://github.com/yosephyne/stratoscope
DefaultDirName={localappdata}\Stratoscope
PrivilegesRequired=lowest
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousAppDir=yes
; Follows the user's light or dark Windows setting, at the small default size (not the 20 % larger one), with the artwork of the app
; (drawn by StratoscopeTools.exe --installer-images from the app's own icon and type).
WizardStyle=modern dynamic
WizardSizePercent=100
; In dynamic style a dark Windows uses the DynamicDark image, which is Inno's own unless set: the same files, stored once.
WizardImageFile=installer\wizard.png
WizardImageFileDynamicDark=installer\wizard.png
WizardSmallImageFile=installer\small.png
WizardSmallImageFileDynamicDark=installer\small.png
WizardImageBackColor=#101118
SetupIconFile=..\..\Sources\Windows\App\stratoscope.ico
UninstallDisplayIcon={app}\Stratoscope.exe
OutputDir=..\..\Sources\Windows\artifacts
OutputBaseFilename=Stratoscope-{#AppVersion}-windows-setup
Compression=lzma2/max
SolidCompression=yes

[Messages]
; The window says in plain words what is happening: it never shows a console.
InstallingLabel=Stratoscope is being installed. This takes a few seconds.
StatusExtractFiles=Installing Stratoscope...
StatusCreateIcons=Adding Stratoscope to your Start menu...
StatusRunProgram=Starting Stratoscope...
FinishedHeadingLabel=Stratoscope is running
FinishedLabelNoIcons=Look for the readout in your taskbar, next to the clock. Click it to open the panel.

[Tasks]
; Inno's own wording, off unless ticked. /TASKS="desktopicon" does the same silently.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb"

[Icons]
Name: "{userprograms}\Stratoscope"; Filename: "{app}\Stratoscope.exe"; Comment: "A live gauge for your local LLM engine"
Name: "{userdesktop}\Stratoscope"; Filename: "{app}\Stratoscope.exe"; Comment: "A live gauge for your local LLM engine"; Tasks: desktopicon
Name: "{userstartup}\Stratoscope"; Filename: "{app}\Stratoscope.exe"; Comment: "Stratoscope, started at logon"; Check: StartWithWindows

[Registry]
; The entry the earlier PowerShell installer made in Settings > Apps: Setup has its own now.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\Stratoscope"; Flags: deletekey

[Run]
Filename: "{app}\Stratoscope.exe"; Flags: nowait runasoriginaluser
Filename: "{app}"; Description: "Open settings folder"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallRun]
Filename: "{app}\Stratoscope.exe"; Parameters: "--quit"; Flags: runhidden; RunOnceId: "QuitStratoscope"

[Code]
function IsUpdate: Boolean;
begin
  Result := RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{BED3E514-C20B-4074-9E13-CBEACBE2ADD4}_is1')
    or RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Stratoscope');
end;

{ On for a new install; an update keeps the earlier choice (the shortcut is there or it is not). }
function StartWithWindows: Boolean;
begin
  Result := (not IsUpdate) or FileExists(ExpandConstant('{userstartup}\Stratoscope.lnk'));
end;

{ A running instance has no window to close: ask it to quit, as the app's own --quit does. A copy that did not quit in time is ended
  (the app holds no unsaved work, and a copy left running would keep the new one from starting). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\Stratoscope.exe')) then
  begin
    Exec(ExpandConstant('{app}\Stratoscope.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Stratoscope.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
