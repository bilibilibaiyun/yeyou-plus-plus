; 页游++ 安装包脚本（Inno Setup 7）
; 目标：安装到 D 盘、开始菜单/桌面快捷方式、可卸载；
; 卸载时彻底清除软件在本机的数据（安装目录 + 自定义数据目录）。

#define MyAppName "页游++"
#define MyAppNameEn "YeyouPlusPlus"
#define MyAppVersion "2.3.7"
#define MyAppPublisher "Yeyou Plus Plus contributors"
#define MyAppExeName "YeyouPlusPlus.exe"

[Setup]
AppId={{7C1F2A64-9B33-4E7E-A0D1-8E2B4C6D1A0F}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={sd}\YeyouPlusPlus
DefaultGroupName=页游++
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\artifacts
OutputBaseFilename=YeyouPlusPlus_2.3.7_x64_Setup
SetupIconFile=..\assets\icon\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
DisableProgramGroupPage=yes
UsedUserAreasWarning=no
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序与 CEF 运行库（整个 release 输出目录，排除调试符号/日志/文档）。
; 视频背景主题资源 Assets\video\intro.mp4 位于输出目录内，由 recursesubdirs 一并打包。
Source: "..\cs\src\bin\x64\Release\net462\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.log,*.xml"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 交互安装：显示「运行」复选框由用户决定。
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
; 静默安装（内置更新覆盖安装）：安装完成后自动启动软件。
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent

[Code]
const
  RegKey = 'Software\YeyouPlusPlus';

// 安装开始前：若检测到旧版本（覆盖安装升级），写升级标志，
// 使新版卸载程序在升级时保留用户数据（账号/收藏/配置）。
// 注意：InitializeSetup 阶段 {app} 常量尚未初始化，ExpandConstant('{app}') 会报
// 「An attempt was made to expand the "app" constant before it was initialized」，
// 因此改用「注册表卸载信息键 + 默认安装目录」双重检测。
function InitializeSetup(): Boolean;
begin
  Result := True;
  if RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{7C1F2A64-9B33-4E7E-A0D1-8E2B4C6D1A0F}_is1')
     or DirExists(ExpandConstant('{sd}\YeyouPlusPlus')) then
  begin
    RegWriteStringValue(HKCU, RegKey, 'IsUpgrading', '1');
  end;
end;

// 安装完成：清除升级标志。
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegDeleteValue(HKCU, RegKey, 'IsUpgrading');
  end;
end;

// 卸载：覆盖安装升级时保留用户数据；用户主动卸载时才彻底清除。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, IsUpgrading: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, RegKey, 'IsUpgrading', IsUpgrading) and (IsUpgrading = '1') then
    begin
      // 覆盖安装升级：保留数据，仅清除升级标志。
      RegDeleteValue(HKCU, RegKey, 'IsUpgrading');
    end
    else
    begin
      // 用户主动卸载：彻底清除默认数据目录与自定义数据目录。
      if DirExists(ExpandConstant('{app}\data')) then
      begin
        DelTree(ExpandConstant('{app}\data'), True, True, True);
      end;
      if RegQueryStringValue(HKCU, RegKey, 'DataDirPath', DataDir) then
      begin
        DataDir := Trim(DataDir);
        if (Length(DataDir) > 3) and DirExists(DataDir) then
        begin
          DelTree(DataDir, True, True, True);
        end;
      end;
      RegDeleteKeyIncludingSubkeys(HKCU, RegKey);
    end;
  end;
end;
