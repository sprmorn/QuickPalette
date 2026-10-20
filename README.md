# 轻启 QuickPalette

Windows 本地**白名单快捷启动器**：托盘常驻、全局热键（默认 `Ctrl+Alt+Space`）唤起命令面板，支持中文/拼音/别名搜索，回车即启动。

- 拖拽 exe / 运行中捕获 / 开始菜单导入，三种方式把常用软件收进白名单
- 拼音搜索：`wx` 找到「微信」，`vscode` 找到「Visual Studio Code」
- 命令模式：输入 `>` 进入（`>settings`、`>capture`、`>refresh`、`>theme`、`>backup`、`>diag`、`>help`）
- 本地优先：数据全部存 `%APPDATA%\QuickPalette\`，无账号无云依赖，自动备份可导出/导入

详细设计见 [轻启-快捷启动器详细设计方案.md](轻启-快捷启动器详细设计方案.md)。

## 系统要求

- Windows 10 / 11 x64
- 运行：.NET 9.0 Desktop Runtime（自包含版无需安装）
- 开发/构建：.NET 9.0 SDK，见 [CONTRIBUTING.md](CONTRIBUTING.md)

## 下载安装

- **绿色版**：到 [Releases](../../releases) 下载 `轻启-x.y.z.zip`，解压后直接运行 `QuickPalette.exe`
- **安装包**：下载 `轻启-Setup-x.y.z.exe`，按向导安装（可选开机自启）
- **从源码构建**：见下文「构建与测试」

## 目录结构

```
src/
  QuickPalette.App/            WPF 表现层（命令面板、设置、向导、托盘、热键）
  QuickPalette.Application/    应用层（事件总线、用例编排）
  QuickPalette.Domain/         领域层（模型、契约、搜索/过滤/拼音引擎，零 UI 依赖）
  QuickPalette.Infrastructure/ 基础设施（JSON 存储/迁移/备份、扫描器、启动器、图标、文件索引）
  QuickPalette.FileIndexWorker/ 文件索引工作进程（提权，读 USN 变更日志建全盘文件索引）
  QuickPalette.Plugins.Sdk/    插件 SDK（契约与清单，V3 加载器预留）
tests/                         xunit 单元测试（Domain / Infrastructure）
tools/                         UsnDiag（USN 诊断）、ParseTest（索引快照诊断）
build/                         打包脚本（publish.ps1、Inno Setup 安装脚本）
docs/                          文档
```

## 构建与测试

```powershell
dotnet build QuickPalette.sln -c Release
dotnet test QuickPalette.sln -c Release
```

运行主程序：`dotnet run --project src\QuickPalette.App`

## 打包发布

```powershell
pwsh -File build\publish.ps1 -Version 1.0.0            # 发布 + zip
pwsh -File build\publish.ps1 -Installer                # 另编译 Inno Setup 安装包
pwsh -File build\publish.ps1 -SelfContained            # 自包含单文件（无需安装运行时）
```

详见 [build/README.md](build/README.md)。

## 架构速览

- 分层：`App → Application → Domain ← Infrastructure`，Domain 零 UI/Windows 依赖，可单测
- 存储：`apps.json` / `settings.json` / `hidden.json` / `usage.json`，临时文件 + fsync + 原子替换，写前自动备份并按类别保留最近 10 份，损坏自动从备份恢复，schema 版本化迁移
- 搜索：匹配维度分层（精确 > 前缀 > 子串 > 拼音全拼 > 拼音首字母 > 别名/标签），层内按收藏/使用次数/最近使用（指数衰减）排序
- 文件搜索（可选）：FileIndexWorker 读 NTFS USN 变更日志维护文件索引，主进程按版本号轮询热加载，支持随时关闭

## 数据与隐私

- 全部数据保存在本机 `%APPDATA%\QuickPalette\`（应用列表、设置、图标缓存、索引、日志、备份），无账号、无遥测、无云依赖
- 卸载/清除时删除该目录即可；`icons\`、`FileIndex\`、`logs\` 为可再生的缓存/日志，`apps.json`、`settings.json` 等为你的数据本体

## 参与贡献

欢迎 Issue 和 PR，见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 开源许可

本项目基于 [MIT License](LICENSE) 开源。你可以自由使用、修改、分发（包括商用），只需保留版权与许可声明。
