; ════════════════════════════════════════════════════════════════
; GRCS.Dashboard 安装包（Inno Setup 6）
; 打包内容：自包含启动器 GRCS.Dashboard.Launcher.exe + wwwroot 静态资源
; 用法：由 build-installer.ps1 调用，或手动执行 iscc installer.iss
; ════════════════════════════════════════════════════════════════
#define MyAppName "GRCS Dashboard"
#define MyAppVersion "1.0.0"
#define MyAppExeName "GRCS.Dashboard.Launcher.exe"
#define MyAppAssocName "GRCS 控制面板"

[Setup]
AppId={{8C7A3E2F-5B1D-4E6A-9C3F-2D8B4A1E6C55}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Wayzim
DefaultDirName={autopf}\Wayzim\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts
OutputBaseFilename=GRCS.Dashboard.Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\..\artifacts\launcher\GRCS.Dashboard.Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\launcher\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\launcher\*.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\launcher\wwwroot\*"; DestDir: "{app}\wwwroot"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\wwwroot"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;