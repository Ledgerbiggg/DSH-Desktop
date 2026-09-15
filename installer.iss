; ============================================================
; Dsh 安装包脚本 (Inno Setup 6)
; 用法: iscc installer.iss [/DMyAppVersion=0.1.1]
; ============================================================

#ifndef MyAppVersion
#define MyAppVersion "0.1.0"
#endif

#define MyAppName "Dsh"
#define MyAppPublisher "Dsh"
#define MyAppExeName "Dsh.exe"

[Setup]
; 安装包/卸载程序的唯一标识
AppId={{B4C5D6E7-3A4F-5B6C-8D7E9F0A1B2C}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
SetupIconFile=Dsh\Assets\deepseek-dark_48x48.ico
OutputDir=package
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64os
UninstallFilesDir={app}\Uninstall
; 不使用 Restart Manager 自动关闭应用：本程序托盘常驻，主窗口会拦截关闭消息
; （关闭按钮=隐藏到托盘），RM 关不掉它只会弹出「无法自动关闭应用程序」错误框。
; 改由下方 [Code] 的 PrepareToInstall 主动结束进程，行为可预期
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 将 dotnet publish 产物整体打包
Source: "_publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "额外快捷方式:"

[Run]
; 安装完成后可选启动
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 覆盖安装前先关掉正在运行的旧版本。
// 背景：本程序托盘常驻，"点 ×" 是隐藏到托盘而非退出，主窗口会拦截 WM_CLOSE，
// Inno 的 Restart Manager 因此关不掉它，会弹「Setup was unable to automatically
// close all applications」。
// 这里只做一件事：taskkill 结束进程树（/T 一并结束托管的 dsh web 子进程；
// 即便有残留，下次启动的 KillStaleDshProcesses 也会清理）。
// ⚠️ 严禁改成 Exec('{app}\应用.exe', '--exit-for-update', ..., ewWaitUntilTerminated)
// 之类的"先请应用优雅退出"：旧版本不认识该开关，会把这次启动当成正常启动并常驻
// 托盘永不退出，安装程序将永久卡在「Preparing to Install」页面（无响应且无取消按钮）。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  NeedsRestart := False;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM "{#MyAppExeName}"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
