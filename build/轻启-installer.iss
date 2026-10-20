; 轻启 QuickPalette 安装脚本（Inno Setup 6，UTF-8 编码）
; 使用：先运行 build\publish.ps1 产出 dist\轻启-<版本>\，再用 ISCC 编译本脚本
#define MyAppName "轻启"
; 版本号由 build\publish.ps1 经 /DMyAppVersion 传入；此处仅为直接调用 ISCC 时的缺省值
#ifndef MyAppVersion
  #define MyAppVersion "1.1.1"
#endif
#define MyAppExeName "QuickPalette.App.exe"

[Setup]
AppId={{9E5B7F2A-3C41-4D8E-B6A0-1F2C7D8E9A0B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=轻启
DefaultDirName={autopf}\轻启
DefaultGroupName=轻启
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\dist
OutputBaseFilename=轻启-Setup-{#MyAppVersion}
SetupIconFile=..\src\QuickPalette.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=轻量 Windows 快捷启动器

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
; 发布目录全部文件（含文件索引 Worker 全套）
Source: "..\dist\轻启-{#MyAppVersion}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\轻启"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载轻启"; Filename: "{uninstallexe}"
Name: "{autodesktop}\轻启"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 注册索引服务计划任务（按需触发 + 最高权限）：此后主程序启动索引服务走任务计划静默提权，不再弹 UAC；
; 任务缺失/不可用时主程序自动回退 UAC 启动（便携版场景）
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /F /TN ""QuickPalette\FileIndexWorker"" /TR ""\""{app}\QuickPalette.FileIndexWorker.exe\"" --parent auto"" /SC ONCE /ST 00:00 /RL HIGHEST"; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "运行轻启"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载时尝试结束正在运行的实例（含索引服务，避免文件被占用）
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM QuickPalette.FileIndexWorker.exe"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillWorker"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM QuickPalette.PreviewHost.exe"; Flags: runhidden skipifdoesntexist; RunOnceId: "KillPreviewHost"
; 删除索引服务计划任务
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""QuickPalette\FileIndexWorker"" /F"; Flags: runhidden; RunOnceId: "DeleteIndexTask"
