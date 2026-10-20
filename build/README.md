# build\ 打包脚本说明

| 文件 | 用途 |
| --- | --- |
| `publish.ps1` | 一键发布：构建并发布主程序与 FileIndexWorker → 汇总到 `dist\轻启-<版本>\` → 生成 `dist\轻启-<版本>.zip`（`-Installer` 时另编译安装包） |
| `轻启-installer.iss` | Inno Setup 6 安装包脚本（UTF-8），依赖 `publish.ps1` 的产物目录 |

## 常用命令

```powershell
# 仅发布 + zip（框架依赖）
pwsh -File build\publish.ps1

# 自包含单文件 + 安装包
pwsh -File build\publish.ps1 -Version 1.1.0 -SelfContained -Installer
```

## 注意

- `dist\`、`logs\`、`bin\`、`obj\` 均已在 `.gitignore` 中忽略，不要提交发布产物。
- 主程序与文件索引 Worker 必须一起分发（Worker 为提权进程，缺它则文件搜索不可用）。
- 数据目录固定为 `%APPDATA%\QuickPalette\`，升级/重装不会触碰；导出备份可在设置页完成。
