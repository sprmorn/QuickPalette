# 参与贡献

感谢你对 **轻启 QuickPalette** 的关注！

## 开发环境

- Windows 10/11 x64
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- 可选：Visual Studio 2022 17.12+（或 Rider / VS Code + C# Dev Kit）
- 可选：Inno Setup 6（仅在编译安装包时需要）

## 构建与测试

```powershell
dotnet build QuickPalette.sln -c Release
dotnet test  QuickPalette.sln -c Release
```

运行主程序：

```powershell
dotnet run --project src\QuickPalette.App
```

打包发布见 [build/README.md](build/README.md)。

## 提交 Pull Request 前请确认

1. `dotnet build` 与 `dotnet test` 全部通过；
2. 新功能/修复有对应测试（`tests/` 下 xunit 项目）；
3. 遵循现有分层架构：`Domain` 不依赖 UI/Windows API，业务规则放 `Domain`，IO 放 `Infrastructure`；
4. 不要提交构建产物（`bin/`、`obj/`、`dist/`、`build/out/`）和运行时数据；
5. 提交信息简明扼要，说明「做了什么、为什么」。

## 报告问题

请用 [Issues](../../issues) 提交 Bug 或建议，附上：

- 复现步骤
- 系统版本与 .NET 运行时版本
- 日志（`%APPDATA%\QuickPalette\logs\`，可脱敏后粘贴关键片段）

## 行为准则

保持友善与尊重，就事论事。维护者保留删除不当内容的权利。
