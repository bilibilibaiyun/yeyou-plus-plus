; 页游++ 安装包脚本（Inno Setup 7）
; 目标：安装到 D 盘、开始菜单/桌面快捷方式、可卸载；
; 卸载时彻底清除软件在本机的数据（安装目录 + 自定义数据目录）。

#define MyAppName "页游++"
#define MyAppNameEn "YeyouPlusPlus"
#define MyAppVersion "2.0.5"
#define MyAppPublisher "Yeyou Plus Plus contributors"
#define MyAppExeName "YeyouPlusPlus.exe"

[Setup]
AppId={{7C1F2A64-9B33-4E7E-A0D1-8E2B4C6D1A0F}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={sd}\页游++
DefaultGroupName=页游++
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\artifacts
OutputBaseFilename=YeyouPlusPlus_2.0.5_x64_Setup
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
Name: "chinesesimplified"; MessagesFile: "D:\Codex\Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序与 CEF 运行库（整个 release 输出目录）
Source: "..\cs\src\bin\x64\Release\net462\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 交互安装：显示「运行」复选框由用户决定。
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
; 静默安装（内置更新覆盖安装）：安装完成后自动启动软件。
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifnotsilent

[UninstallDelete]
; 卸载时清除程序目录下的全部运行时数据（缓存、配置、收藏、快捷入口、下载、更新包）
Type: filesandordirs; Name: "{app}"

[Code]
// 卸载时同步删除用户自定义数据目录（记录于注册表 HKCU\Software\YeyouPlusPlus\DataDirPath）。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\YeyouPlusPlus', 'DataDirPath', DataDir) then
    begin
      DataDir := Trim(DataDir);
      if (Length(DataDir) > 3) and DirExists(DataDir) then
      begin
        DelTree(DataDir, True, True, True);
      end;
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\YeyouPlusPlus');
    end;
  end;
end;
