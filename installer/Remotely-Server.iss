; Remotely Server installer (Inno Setup 6), x64, for the always-on PC or VM that runs your own coordinator (for example Windows Server 2025).
;   ISCC.exe /DVersion=0.4.0 installer\Remotely-Server.iss
; Installs Remotely.Server.exe as the "Remotely.Server" service (runs as LocalService), opens its TCP port in Windows Firewall, and shows an invite
; (address, key pin and pairing code in one string) at the end. Silent:  Setup.exe /VERYSILENT /SUPPRESSMSGBOXES [/Address=myhome.example.com]

#ifndef Version
  #define Version "0.4.0"
#endif
#define Publish "..\publish\server"

[Setup]
AppId={{4AD4465B-1297-47FA-96EB-767F6966DD9D}
AppName=Remotely Server
AppVersion={#Version}
AppVerName=Remotely Server {#Version}
AppPublisher=Remotely (open source)
DefaultDirName={autopf}\Remotely Server
DisableDirPage=yes
DefaultGroupName=Remotely Server
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\publish
OutputBaseFilename=Remotely-Server-Setup-{#Version}-x64
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Remotely Server
UninstallDisplayIcon={app}\Remotely.Server.exe
SetupIconFile=..\src\Remotely.App\Assets\remotely.ico
SetupLogging=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#Publish}\Remotely.Server.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Publish}\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

[Icons]
; Needs administrator rights because it reads the server key.
Name: "{group}\Remotely Server info: invite a new device"; Filename: "{app}\Remotely.Server.exe"; Parameters: "info --pause"; WorkingDir: "{app}"
Name: "{group}\Uninstall Remotely Server"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Remotely.Server.exe"; Parameters: "install"; Flags: runhidden waituntilterminated; StatusMsg: "Starting your Remotely server..."
Filename: "{app}\Remotely.Server.exe"; Parameters: "set-address ""{code:AddressValue}"""; Flags: runhidden waituntilterminated; Check: HasAddress
Filename: "{app}\Remotely.Server.exe"; Parameters: "info --pause"; Description: "Show the invite for your first device"; Flags: postinstall skipifsilent

[UninstallRun]
; Your device list and key (%ProgramData%\Remotely Server) are kept.
Filename: "{app}\Remotely.Server.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveServerService"

[Code]
var
  AddressPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  AddressPage := CreateInputQueryPage(wpWelcome, 'How will your devices reach this server?',
    'This goes into the invite your devices use to join.',
    'At home, leave this empty: the invite uses this PC''s home-network address. To reach the server from outside your home, enter your public name or ' +
    'address (for example myhome.example.com) and forward TCP port 8443 on your router to this PC; docs\server-network-setup.md in the Remotely repository ' +
    'lists the exact steps and how to test them with your phone. You can change the name later with: Remotely.Server.exe set-address <name>');
  AddressPage.Add('Name or address (optional):', False);
  AddressPage.Values[0] := ExpandConstant('{param:Address|}');
end;

// Same rule as the Server Manager's setup wizard: a host name or IP address, no spaces, no https:// in front.
function IsPlausibleAddress(const S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := True;
  if S = '' then Exit;
  if (Length(S) > 253) or (Pos('://', S) > 0) then
  begin
    Result := False;
    Exit;
  end;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((C >= '0') and (C <= '9')) or
            (C = '.') or (C = '-') or (C = ':') or (C = '[') or (C = ']')) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = AddressPage.ID then
    if not IsPlausibleAddress(Trim(AddressPage.Values[0])) then
    begin
      MsgBox('Type only a name such as myhome.example.com, or an IP address, with no spaces and without https:// in front. You can leave it empty.', mbError, MB_OK);
      Result := False;
    end;
end;

function AddressValue(Param: String): String;
begin
  if WizardSilent then Result := ExpandConstant('{param:Address|}') else Result := Trim(AddressPage.Values[0]);
end;

function HasAddress: Boolean;
begin
  Result := AddressValue('') <> '';
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Let go of the program file if an older version is running as a service.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop Remotely.Server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
