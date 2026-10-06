; DesktopUtility installer. Build with:  .\build_release.ps1   (publishes to dist\DesktopUtility first)
#define AppName "DesktopUtility"
#define AppVersion "1.1.0"
#define AppExe "DesktopUtility.exe"
#define DistDir "dist\DesktopUtility"

[Setup]
AppId={{6F1D2B8E-3C4A-4E7B-9A55-D3571C0A9E21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DesktopUtility
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=DesktopUtility_Setup
SetupIconFile=src\DesktopUtility.Host\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
WizardImageFile=installer\wizard-1.bmp,installer\wizard-2.bmp
WizardSmallImageFile=installer\small-1.bmp,installer\small-2.bmp
Compression=lzma2/ultra64
SolidCompression=yes
; Admin is needed for the firewall rule and for installing the virtual display driver on first use.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no

[Languages]
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ukrainian.Firewall=Дозволити DesktopUtility в брандмауері Windows (приватні мережі)
english.Firewall=Allow DesktopUtility through Windows Firewall (private networks)
ukrainian.Components=Додаткові параметри:
english.Components=Options:

[Tasks]
Name: "firewall"; Description: "{cm:Firewall}"; GroupDescription: "{cm:Components}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DesktopUtility"""; Flags: runhidden; Tasks: firewall
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""DesktopUtility"" dir=in action=allow program=""{app}\{#AppExe}"" profile=private enable=yes"; \
    Flags: runhidden; Tasks: firewall; StatusMsg: "Windows Firewall..."
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Quit first so the app removes its virtual monitor, then remove the driver if it was installed on first use.
Filename: "{app}\{#AppExe}"; Parameters: "--quit"; Flags: runhidden; RunOnceId: "Quit"
Filename: "{app}\VirtualDisplayDriver\deviceinstaller64.exe"; Parameters: "remove usbmmidd"; WorkingDir: "{app}\VirtualDisplayDriver"; \
    Flags: runhidden; RunOnceId: "RemoveDriver"
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DesktopUtility"""; Flags: runhidden; RunOnceId: "RemoveFirewall"

[Code]
procedure StopRunningInstance;
var
  ResultCode: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningInstance;
  Result := '';
end;
