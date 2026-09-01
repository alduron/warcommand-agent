; WarCommand Agent installer. Inno Setup 6.
;
; Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=1.4.0 /Oartifacts /FWarCommand-Setup-1.4.0 installer\warcommand.iss
;
; Per-user, no elevation. The agent installs a global keyboard hook and draws an overlay; neither
; needs administrator rights, and asking for them is the single largest reason a user abandons an
; install of a tool like this.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName      "WarCommand"
#define AppExe       "WarCommand.exe"
#define AppPublisher "WarCommand"
#define AppUrl       "https://warcommand.app"

; The Vosk small English model, fetched at install time rather than carried in the installer.
; 40 MB that changes far less often than the agent does, so bundling it would put it in every
; release download for no reason. Pinned by name: a model swap is a deliberate edit here.
#define VoskModelName "vosk-model-small-en-us-0.15"
#define VoskModelUrl  "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"

[Setup]
AppId={{9C4E1F72-6B2A-4D1E-9F3B-0A5D7E8C2B14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}

; Per-user install: no UAC prompt, and {autopf} resolves under %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; Windows 10 1903. Windows Graphics Capture needs it and the agent will not start below it.
MinVersion=10.0.18362
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=artifacts
OutputBaseFilename=WarCommand-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\WarCommand.Agent\Resources\Icons\app.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start WarCommand when I sign in"; GroupDescription: "Startup"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts"; Flags: unchecked

[Files]
; The whole self-contained publish folder. Not single-file: Vosk loads libvosk.dll by name.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; NOT a {userstartup} shortcut. The tray's "Start with Windows" row reads and writes the HKCU Run
; value below, and a shortcut would be a second, invisible mechanism the toggle could not see:
; switching it off in the tray would leave the shortcut launching the agent anyway.

[Registry]
; warcommand:// , the pairing link the web app opens. HKCU because this is a per-user install;
; a per-machine registration would need elevation and would claim the scheme for other accounts.
Root: HKCU; Subkey: "Software\Classes\warcommand"; ValueType: string; ValueName: ""; ValueData: "URL:WarCommand Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\warcommand"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\warcommand\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\warcommand\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

; Autostart. The same value WindowsStartup.cs reads and writes, so the installer checkbox and the
; tray toggle are one setting rather than two that disagree. uninsdeletevalue so an uninstall does
; not leave Windows trying to launch an exe that is gone.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WarCommand"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; The self-update path runs this installer with /SILENT /UPDATE. `skipifsilent` above means the
; normal post-install launch does not fire then, so without this line an update would replace the
; agent and leave the user with nothing running.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: LaunchAfterSilentUpdate

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; NOTHING here removes %LOCALAPPDATA%\WarCommand. install.id, tokens.dat and config.json live
; there, install.id survives updates by contract, and an uninstall that took the pairing with it
; would orphan the device server-side. See docs/design/10-agent-spec.md "Updates".

[Code]
var
  ModelPage: TOutputProgressWizardPage;

{ True when the agent started this installer to update itself. See UpdateDownloader.Launch. }
function LaunchAfterSilentUpdate(): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/UPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function ModelDirectory(): string;
begin
  Result := ExpandConstant('{localappdata}\WarCommand\models\vosk-small-en-us');
end;

{ True when a model is already unpacked. An update must not re-download 40 MB. }
function ModelPresent(): Boolean;
begin
  Result := FileExists(ModelDirectory() + '\am\final.mdl');
end;

procedure InitializeWizard();
begin
  ModelPage := CreateOutputProgressPage('Speech model', 'Fetching the offline recognizer.');
end;

{ Downloads and unpacks the Vosk model. A failure here is reported and then ignored: the agent
  starts without it and says voice is unavailable, which beats failing the whole install. }
procedure InstallSpeechModel();
var
  ZipPath, Parent, Command: string;
  ResultCode: Integer;
begin
  if ModelPresent() then
    Exit;

  ZipPath := ExpandConstant('{tmp}\vosk-model.zip');
  Parent := ExpandConstant('{localappdata}\WarCommand\models');

  ModelPage.SetText('Downloading the speech model (about 40 MB).', '');
  ModelPage.Show();
  try
    try
      DownloadTemporaryFile('{#VoskModelUrl}', 'vosk-model.zip', '', nil);
    except
      MsgBox('The speech model could not be downloaded. WarCommand will run without voice'
        + ' recognition; reinstall when you are online to enable it.', mbInformation, MB_OK);
      Exit;
    end;

    ModelPage.SetText('Unpacking the speech model.', '');
    ForceDirectories(Parent);

    { Expand-Archive ships with Windows 10, so this needs nothing the machine does not have.
      The archive unpacks to its own versioned folder, renamed to the stable name the agent
      looks for: VoskModelLoader.DefaultModelFolder. }
    Command := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "'
      + '$ErrorActionPreference=''Stop'';'
      + 'Expand-Archive -LiteralPath ''' + ZipPath + ''' -DestinationPath ''' + Parent + ''' -Force;'
      + 'if (Test-Path ''' + ModelDirectory() + ''') { Remove-Item -Recurse -Force ''' + ModelDirectory() + ''' };'
      + 'Rename-Item -LiteralPath ''' + Parent + '\{#VoskModelName}'' -NewName ''vosk-small-en-us''"';

    if not Exec('powershell.exe', Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
      or (ResultCode <> 0) then
      MsgBox('The speech model could not be unpacked. WarCommand will run without voice'
        + ' recognition.', mbInformation, MB_OK);
  finally
    ModelPage.Hide();
    DeleteFile(ZipPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallSpeechModel();
end;
