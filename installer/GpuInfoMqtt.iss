; GPU Info MQTT installer (Inno Setup 6)
;
; Build:  ISCC.exe /DAppVersion=1.2.3 /DFileVersion=1.2.3 installer\GpuInfoMqtt.iss
; Input:  artifacts\app  (self-contained publish of the service + tray, see build\build.ps1)
; Output: artifacts\installer\GpuInfoMqtt-Setup-<version>.exe
; Signing: pass /DSIGN and /Ssigntool=<command using $f>; setup and uninstaller are then signed.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif

#define AppName "GPU Info MQTT"
#define ServiceName "GpuInfoMqtt"
#define ServiceExe "GpuInfoMqtt.Service.exe"
#define TrayExe "GpuInfoMqtt.Tray.exe"

[Setup]
AppId={{6F1D3B52-8C2E-4B7A-9F0D-2E7C5A41B9E3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=DevelMagic s.r.o.
AppPublisherURL=https://github.com/mejmo/GPUInfo-MQTT
AppSupportURL=https://github.com/mejmo/GPUInfo-MQTT/issues
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\GpuInfoMqtt
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=GpuInfoMqtt-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#TrayExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; We stop the service and tray ourselves (see [Code]); the Restart Manager would only ask about them.
CloseApplications=no
#ifdef SIGN
SignTool=signtool
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostarttray"; Description: "Start the tray icon when I sign in"; GroupDescription: "Tray app:"

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#TrayExe}"; Comment: "GPU dashboard and MQTT settings"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GpuInfoMqttTray"; \
  ValueData: """{app}\{#TrayExe}"""; Flags: uninsdeletevalue; Tasks: autostarttray

[Run]
Filename: "{app}\{#TrayExe}"; Description: "Open {#AppName} now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#TrayExe}"; Flags: runhidden; RunOnceId: "KillTray"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[UninstallDelete]
; Settings in %ProgramData%\GpuInfoMqtt are kept on purpose (they survive reinstall/upgrade).
Type: filesandordirs; Name: "{app}"

[Code]
function Run(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function ServiceExists(): Boolean;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}') = 0;
end;

procedure StopEverything();
begin
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#TrayExe}');
  if ServiceExists() then
    Run(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
end;

procedure InstallService();
var
  Sc, BinPath: String;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  BinPath := ExpandConstant('"{app}\{#ServiceExe}"');
  if ServiceExists() then
    Run(Sc, 'config {#ServiceName} binPath= "' + BinPath + '" start= delayed-auto')
  else
    Run(Sc, 'create {#ServiceName} binPath= "' + BinPath + '" start= delayed-auto DisplayName= "{#AppName}"');
  Run(Sc, 'description {#ServiceName} "Publishes NVIDIA GPU power, memory, temperature and fan to Home Assistant over MQTT."');
  Run(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/10000/restart/10000/restart/60000');
  if Run(Sc, 'start {#ServiceName}') <> 0 then
    Log('Service did not start (it may already be running).');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopEverything()
  else if CurStep = ssPostInstall then
    InstallService();
end;
