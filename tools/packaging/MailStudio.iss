[Setup]
AppId={{2A0E9FAC-B378-41AF-9A18-A35F4B7E42CA}
AppName=MailStudio for TRMachinist
AppVersion=1.5.0
AppPublisher=TRMachinist
DefaultDirName={localappdata}\Programs\MailStudio
DefaultGroupName=MailStudio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputPath}
OutputBaseFilename=MailStudio_Setup_x64
SetupIconFile={#PublishDir}\MailStudio.ico
UninstallDisplayIcon={app}\MailStudio.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "{#Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView

[Icons]
Name: "{group}\MailStudio"; Filename: "{app}\MailStudio.exe"
Name: "{autodesktop}\MailStudio"; Filename: "{app}\MailStudio.exe"

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Preparing WebView2 (internet required if missing)..."; Flags: waituntilterminated; Check: NeedsWebView
Filename: "{app}\MailStudio.exe"; Description: "Launch MailStudio"; Flags: nowait postinstall skipifsilent; Check: HasWebView

[Code]
function HasVersion(RootKey: Integer): Boolean;
var Version: String;
begin
  Result := RegQueryStringValue(RootKey, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function HasWebView: Boolean;
begin
  Result := HasVersion(HKCU32) or HasVersion(HKCU64) or HasVersion(HKLM32) or HasVersion(HKLM64);
end;

function NeedsWebView: Boolean;
begin
  Result := not HasWebView;
end;

