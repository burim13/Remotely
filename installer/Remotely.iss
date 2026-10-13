; Remotely installer (Inno Setup 6). One per architecture, built by build\publish.ps1 or the CI "installers" job:
;   ISCC.exe /DArch=arm64 /DVersion=0.2.0 installer\Remotely.iss
;   ISCC.exe /DArch=x64   /DVersion=0.2.0 installer\Remotely.iss
; At the start the installer asks: for all users (administrator: the Host runs as a Windows service that survives sign-out and shows the
; lock screen) or only for me (no administrator: the Host runs while you are signed in). Same choice as Portico's per-user installer, plus the service.
; Silent install for scripts and tests:  Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /Server=wss://host:8443/ws /Pin=<hex> /Code=XXXX-XXXX /Name="Dad's PC" /Trust=<viewer id>
;   add /CURRENTUSER for the no-administrator mode, /Insecure=1 only for local testing over ws://

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Version
  #define Version "0.2.0"
#endif
#define Publish "..\publish\" + Arch

[Setup]
AppId={{A979AC82-5CEB-4AFD-9C80-A0AC7E51F7E0}
AppName=Remotely
AppVersion={#Version}
AppVerName=Remotely {#Version}
AppPublisher=Remotely (open source)
DefaultDirName={autopf}\Remotely
UsePreviousAppDir=yes
DisableDirPage=yes
DefaultGroupName=Remotely
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\publish
OutputBaseFilename=Remotely-Setup-{#Version}-{#Arch}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Remotely
UninstallDisplayIcon={app}\Remotely.exe
SetupIconFile=..\src\Remotely.Viewer\Assets\remotely.ico
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Both: control other computers, and let this PC be controlled"
Name: "custom"; Description: "Choose what to install"; Flags: iscustom

[Components]
Name: "viewer"; Description: "Remotely: control your other computers from this PC"; Types: full custom
Name: "host"; Description: "Remotely Host: let your other devices control this PC"; Types: full custom

[Files]
Source: "{#Publish}\viewer\Remotely.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: viewer
Source: "{#Publish}\host\Remotely.Host.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: host

[Icons]
Name: "{group}\Remotely"; Filename: "{app}\Remotely.exe"; Components: viewer
Name: "{group}\Uninstall Remotely"; Filename: "{uninstallexe}"

[Run]
; Configure and start the host. Both commands are the same ones you can run by hand (Remotely.Host.exe setup / install).
Filename: "{app}\Remotely.Host.exe"; Parameters: "{code:HostSetupArgs}"; Flags: runhidden waituntilterminated; Components: host; StatusMsg: "Connecting this PC to your server..."; Check: HostConfigured
Filename: "{app}\Remotely.Host.exe"; Parameters: "{code:HostInstallArgs}"; Flags: runhidden waituntilterminated; Components: host; StatusMsg: "Starting Remotely Host..."; Check: HostConfigured
Filename: "{app}\Remotely.exe"; Description: "Open Remotely"; Flags: nowait postinstall skipifsilent; Components: viewer

[UninstallRun]
; Removes the service (or the sign-in entry). Your settings, device key and logs are never touched.
Filename: "{app}\Remotely.Host.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHostService"; Check: IsAdminInstallMode
Filename: "{app}\Remotely.Host.exe"; Parameters: "uninstall --user"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHostUser"; Check: not IsAdminInstallMode

; RULE (as in Portico): the installer and uninstaller never touch user data. The host's key, settings and logs live in
; %ProgramData%\Remotely (service) or %LocalAppData%\Remotely (user), the viewer's in %LocalAppData%\Remotely, all outside {app}.
; A newer installer over an installed Remotely (same AppId) is an in-place upgrade of the program files only.

[Code]
var
  HostPage: TInputQueryWizardPage;

function Param(Name: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|}');
end;

function HostPageValue(Index: Integer; ParamName: String): String;
begin
  if WizardSilent then Result := Param(ParamName)
  else Result := Trim(HostPage.Values[Index]);
end;

function IsInsecure: Boolean;
begin
  Result := Param('Insecure') = '1';
end;

function UserFlag: String;
begin
  if IsAdminInstallMode then Result := '' else Result := '--user ';
end;

procedure InitializeWizard;
begin
  HostPage := CreateInputQueryPage(wpSelectComponents, 'Connect this PC to your server',
    'Remotely Host needs the details of your own Remotely server.',
    'The address, key pin and a one-time pairing code are shown by "Remotely Server info" on your server. ' +
    'Leave the server address empty to set this PC up later.');
  HostPage.Add('Server address (wss://...):', False);
  HostPage.Add('Server key pin:', False);
  HostPage.Add('Pairing code:', False);
  HostPage.Add('This PC''s name:', False);
  HostPage.Add('Viewer ids allowed to control this PC (optional, separate with spaces):', False);
  HostPage.Values[0] := Param('Server');
  HostPage.Values[1] := Param('Pin');
  HostPage.Values[2] := Param('Code');
  HostPage.Values[3] := Param('Name');
  if HostPage.Values[3] = '' then HostPage.Values[3] := GetComputerNameString;
  HostPage.Values[4] := Param('Trust');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = HostPage.ID) and (not WizardIsComponentSelected('host'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Server: String;
begin
  Result := True;
  if CurPageID = HostPage.ID then
  begin
    Server := Trim(HostPage.Values[0]);
    if Server = '' then Exit;
    if (Pos('wss://', Server) <> 1) and (Pos('ws://', Server) <> 1) then
    begin
      MsgBox('The server address should start with wss://', mbError, MB_OK);
      Result := False;
    end
    else if (Pos('wss://', Server) = 1) and (Length(Trim(HostPage.Values[1])) <> 64) then
    begin
      MsgBox('The server key pin is 64 characters. Copy it from the server''s info screen.', mbError, MB_OK);
      Result := False;
    end
    else if Trim(HostPage.Values[2]) = '' then
    begin
      MsgBox('Enter the pairing code from your server (it works once).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// The host is configured only if a server address was given (wizard) or passed on the command line (silent).
function HostConfigured: Boolean;
begin
  Result := HostPageValue(0, 'Server') <> '';
end;

function HostSetupArgs(Param: String): String;
var
  Ids, One: String;
  P: Integer;
begin
  Result := 'setup ' + UserFlag + '--server "' + HostPageValue(0, 'Server') + '" --code "' + HostPageValue(2, 'Code') + '" --name "' + HostPageValue(3, 'Name') + '"';
  if HostPageValue(1, 'Pin') <> '' then Result := Result + ' --pin "' + HostPageValue(1, 'Pin') + '"';
  if IsInsecure then Result := Result + ' --insecure';
  Ids := HostPageValue(4, 'Trust');
  while Ids <> '' do
  begin
    P := Pos(' ', Ids);
    if P = 0 then begin One := Ids; Ids := ''; end
    else begin One := Copy(Ids, 1, P - 1); Ids := Trim(Copy(Ids, P + 1, MaxInt)); end;
    if One <> '' then Result := Result + ' --trust "' + One + '"';
  end;
end;

function HostInstallArgs(Param: String): String;
begin
  Result := 'install ' + UserFlag;
end;

// Stop a running viewer or user-mode host before the program files are replaced. Data is not touched.
procedure StopRemotely();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Remotely.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  StopRemotely();
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  StopRemotely();
  Result := True;
end;
