; GlassLink installer (Inno Setup 6/7)
; Build with: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{9B5C57E8-3A77-4E8B-92E5-5B5C4F4D2A11}
AppName=GlassLink
AppVersion={#MyAppVersion}
AppVerName=GlassLink {#MyAppVersion}
AppPublisher=GlassLink
DefaultDirName={autopf}\GlassLink
DefaultGroupName=GlassLink
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\GlassLink.exe
SetupIconFile=..\src\GlassLink\Assets\glasslink.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
OutputDir=Output
OutputBaseFilename=GlassLink-Setup-{#MyAppVersion}
SetupLogging=yes
UsePreviousAppDir=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=GlassLink 低延迟桌面串流
VersionInfoProductName=GlassLink
VersionInfoCompany=GlassLink

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce
Name: "startup"; Description: "Windows 登录时自动启动 GlassLink"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GlassLink"; Filename: "{app}\GlassLink.exe"; IconFilename: "{app}\Assets\glasslink.ico"; Comment: "GlassLink 桌面串流"
Name: "{autoprograms}\卸载 GlassLink"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GlassLink"; Filename: "{app}\GlassLink.exe"; IconFilename: "{app}\Assets\glasslink.ico"; Tasks: desktopicon
Name: "{userstartup}\GlassLink"; Filename: "{app}\GlassLink.exe"; Tasks: startup

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GlassLink"; ValueData: """{app}\GlassLink.exe"""; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Classes\*\shell\GlassLinkSend"; ValueType: string; ValueName: "MUIVerb"; ValueData: "发送到当前串流的设备"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\GlassLinkSend"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Assets\glasslink.ico"
Root: HKCU; Subkey: "Software\Classes\*\shell\GlassLinkSend\command"; ValueType: string; ValueData: """powershell.exe"" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\send-file.ps1"" ""%1"""

[Run]
Filename: "{app}\GlassLink.exe"; Description: "启动 GlassLink（首次启动自动配置 HIDMaestro 驱动）"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('GlassLink 需要 64 位 Windows 10/11。', mbError, MB_OK);
    Result := False;
  end;
end;
