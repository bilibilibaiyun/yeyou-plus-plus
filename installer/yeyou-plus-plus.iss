; 页游++ 安装脚本 (Inno Setup 7.1.0)
; 生成带开始菜单/桌面快捷方式/卸载的正式安装包

#define MyAppName "页游++"
#define MyAppNameEn "YeyouPlusPlus"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "YeyouPlusPlus"
#define MyAppExeName "yeyou-plus-plus.exe"

[Setup]
AppId={{3B7A2F5E-9C41-4D6B-8E3A-1F2C5D8A9B0E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序
DefaultDirName={sd}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=YeyouPlusPlus_{#MyAppVersion}_x64_Setup
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.10240
; 安装到 D 盘可写的用户目录，避免 Program Files 权限问题
PrivilegesRequired=lowest
CloseApplications=yes
AllowUNCPath=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\target\release\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\ruffle\*"; DestDir: "{app}\assets\ruffle"; Flags: ignoreversion recursesubdirs createallsubdirs
; 随包附 Ruffle 许可证（MIT/Apache-2.0）
Source: "..\assets\ruffle\LICENSE_APACHE"; DestDir: "{app}\assets\ruffle"; Flags: ignoreversion
Source: "..\assets\ruffle\LICENSE_MIT"; DestDir: "{app}\assets\ruffle"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// 检测 WebView2 Runtime 是否可用（Win10/11 通常内置）
function IsWebView2Available(): Boolean;
var
  KeyPath: String;
begin
  Result := False;
  KeyPath := 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  if RegKeyExists(HKLM, KeyPath) then
    Result := True
  else
  begin
    KeyPath := 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
    if RegKeyExists(HKLM, KeyPath) then
      Result := True
    else
    begin
      KeyPath := 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
      if RegKeyExists(HKCU, KeyPath) then
        Result := True;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWebView2Available() then
  begin
    MsgBox('未检测到 Microsoft Edge WebView2 Runtime。' + #13#10 +
           '页游++ 需要 WebView2 才能运行。' + #13#10 +
           'Windows 10/11 通常已内置；若无法启动，请从微软官网安装 WebView2 Runtime。',
           mbInformation, MB_OK);
  end;
end;
