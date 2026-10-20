namespace QuickPalette.App.Services;

using System.Collections.Generic;

/// <summary>
/// 主面板 / 托盘 / App 启动流程的界面文案（zh-CN / en-US）。
/// 本文件是 UiText partial 类的"主界面"部分：静态初始化时由 UiText.cs 的
/// <c>static UiText() => RegisterMain()</c> 挂接点调用，通过 <see cref="UiText.AddTexts"/> 注册。
/// 设置窗口相关文案由 UiText.cs 自带条目覆盖，键名不重叠（本分区用 main./tray./app. 前缀）。
/// </summary>
public static partial class UiText
{
    /// <summary>注册主面板、托盘与启动流程文案</summary>
    static partial void RegisterMain()
    {
        AddTexts(new Dictionary<string, string>
        {
            // ---- 主面板：搜索区 / 空态 ----
            ["main.search.watermark"] = "搜索应用…",
            ["main.empty.guide"] = "试试 >help 查看命令 / 拖入 exe 添加应用",
            ["main.empty.noMatch"] = "无匹配结果",
            ["main.empty.noFiles"] = "文件索引未就绪，仅显示应用结果",

            // ---- 主面板：底部快捷键提示 ----
            ["main.hint.normal"] = "Enter 启动 | Shift+Enter 管理员 | Ctrl+Enter 打开目录 | Ctrl+1..9 快捷启动 | Ctrl+C 复制路径 | ↑↓ 选择 | PgUp/PgDn 翻页 | Esc 关闭",
            ["main.hint.command"] = "Enter 执行命令 | ↑↓ 选择 | Esc 取消",
            ["main.hint.file"] = "Enter 打开 | Shift+Enter 管理员 | Ctrl+Enter 打开所在目录 | Ctrl+C 复制路径 | Esc 关闭",

            // ---- 主面板：分组标题与类型标签 ----
            ["main.group.app"] = "应用",
            ["main.group.file"] = "文件",
            ["main.group.command"] = "命令",
            ["main.tag.app"] = "应用",
            ["main.tag.folder"] = "文件夹",
            ["main.tag.image"] = "图片",
            ["main.tag.audio"] = "音频",
            ["main.tag.video"] = "视频",
            ["main.tag.archive"] = "压缩包",
            ["main.tag.document"] = "文档",
            ["main.tag.program"] = "程序",
            ["main.tag.file"] = "文件",

            // ---- 主面板：匹配维度徽标 ----
            ["main.match.exact"] = "精确匹配",
            ["main.match.prefix"] = "前缀匹配",
            ["main.match.pinyin"] = "拼音匹配",
            ["main.match.initials"] = "首字母匹配",
            ["main.match.alias"] = "别名匹配",
            ["main.match.command"] = "命令",

            // ---- 主面板：右键菜单 ----
            ["main.menu.launch"] = "启动",
            ["main.menu.launchAdmin"] = "以管理员身份启动",
            ["main.menu.openDir"] = "打开所在目录",
            ["main.menu.open"] = "打开",
            ["main.menu.openFolder"] = "打开文件夹",
            ["main.menu.copyPath"] = "复制路径",
            ["main.menu.favorite"] = "收藏",
            ["main.menu.unfavorite"] = "取消收藏",
            ["main.menu.edit"] = "编辑",
            ["main.menu.hide"] = "隐藏（不再显示）",
            ["main.menu.delete"] = "删除",
            ["main.menu.cancel"] = "取消",

            // ---- 主面板：命令模式 ----
            ["main.cmd.settings"] = "打开设置",
            ["main.cmd.capture"] = "捕获前台窗口入库",
            ["main.cmd.refresh"] = "重新扫描 / 刷新索引",
            ["main.cmd.theme"] = "切换主题：>theme dark|light|system",
            ["main.cmd.backup"] = "立即备份数据",
            ["main.cmd.diag"] = "显示诊断信息",
            ["main.cmd.help"] = "显示命令帮助",
            ["main.ds.hint"] = "DeepSeek 搜索模式 · 回车打开浏览器搜索 · Esc 退出",
            ["main.ds.row"] = "用 DeepSeek 网络搜索「{0}」",
            ["main.ds.rowSecondary"] = "打开浏览器并自动搜索（需已登录 DeepSeek 账号）",
            ["main.ds.launchHint"] = "搜索",
            ["main.ds.group"] = "网络搜索",
            ["main.ds.empty"] = "输入要搜索的内容，回车打开 DeepSeek",
            ["main.command.unknown"] = "未知命令，输入 >help 查看全部命令",
            ["main.command.helpTitle"] = "命令帮助",
            ["main.command.themeInvalid"] = "主题参数无效，可用值：dark / light / system",
            ["main.command.refreshDone"] = "已重新加载应用列表并请求重建文件索引",
            ["main.command.themeApplied"] = "主题已切换为 {0}",

            // ---- 主面板：状态 / 提示 ----
            ["main.status.copied"] = "已复制路径",
            ["main.status.hidden"] = "已隐藏该项",
            ["main.title.error"] = "错误",
            ["main.title.info"] = "提示",
            ["main.title.warning"] = "警告",
            ["main.title.delete"] = "确认删除",
            ["main.title.diag"] = "诊断信息",
            ["main.title.relocate"] = "路径失效",
            ["main.launch.failed"] = "启动失败: {0}",
            ["main.delete.confirm"] = "确定要删除 \"{0}\" 吗？",
            ["main.open.failed"] = "无法打开: {0}",
            ["main.copy.failed"] = "复制失败",

            // ---- 路径失效重新定位引导 ----
            ["main.relocate.message"] = "目标不存在（可能已被移动或卸载）：\n{0}\n\n请选择处理方式：",
            ["main.relocate.change"] = "重新选择路径",
            ["main.relocate.openDir"] = "打开所在目录",
            ["main.relocate.remove"] = "从列表移除",
            ["main.relocate.dirMissing"] = "所在目录也不存在",
            ["main.relocate.updated"] = "路径已更新",
            ["main.relocate.removed"] = "已从列表移除",

            // ---- 拖拽 / 捕获 ----
            ["main.drop.unsupported"] = "仅支持拖入 .exe / .lnk / .url 文件",
            ["main.drop.title"] = "添加应用",
            ["app.capture.none"] = "未捕获到可入库的窗口（请先切换到目标应用再执行捕获）",
            ["app.capture.self"] = "不能捕获轻启自身",

            // ---- 托盘 ----
            ["tray.tooltip"] = "轻启 - 快捷启动器",
            ["tray.show"] = "显示面板",
            ["tray.add"] = "添加应用",
            ["tray.capture"] = "捕获当前窗口到轻启",
            ["tray.settings"] = "设置",
            ["tray.exit"] = "退出",
            ["tray.exit.title"] = "确认退出",
            ["tray.exit.confirm"] = "确定要退出轻启吗？",

            // ---- App 启动 / 全局 ----
            ["app.running"] = "轻启已在运行中。",
            ["app.running.title"] = "提示",
            ["app.hotkey.conflict.title"] = "热键冲突",
            ["app.hotkey.conflict"] = "热键 {0} 注册失败，可能被其他程序占用。\n可通过托盘图标打开面板。",
            ["app.start.degraded.title"] = "数据加载失败",
            ["app.start.degraded"] = "配置/数据加载失败，已以空列表启动，原有数据未被修改。\n详情见日志：{0}",
            ["app.backup.done"] = "备份完成：{0}",
            ["app.backup.failed"] = "备份失败: {0}",

            // ---- 文档预览 ----
            ["preview.hint"] = "单击左侧结果可切换预览 · 文本可选中复制",
            ["preview.loading"] = "加载中…",
            ["preview.truncated"] = "内容过长，仅显示前部分",
            ["preview.error"] = "预览加载失败：{0}",
            ["preview.pages"] = "共 {0} 页 · 预览前 {1} 页"
        }, new Dictionary<string, string>
        {
            // ---- Main panel: search / empty states ----
            ["main.search.watermark"] = "Search apps…",
            ["main.empty.guide"] = "Try >help for commands / drop an .exe to add",
            ["main.empty.noMatch"] = "No matches",
            ["main.empty.noFiles"] = "File index not ready, showing apps only",

            // ---- Main panel: shortcut hints ----
            ["main.hint.normal"] = "Enter launch | Shift+Enter admin | Ctrl+Enter open folder | Ctrl+1..9 quick launch | Ctrl+C copy path | ↑↓ select | PgUp/PgDn page | Esc close",
            ["main.hint.command"] = "Enter run command | ↑↓ select | Esc cancel",
            ["main.hint.file"] = "Enter open | Shift+Enter admin | Ctrl+Enter reveal | Ctrl+C copy path | Esc close",

            // ---- Main panel: group headers & type tags ----
            ["main.group.app"] = "Apps",
            ["main.group.file"] = "Files",
            ["main.group.command"] = "Commands",
            ["main.tag.app"] = "App",
            ["main.tag.folder"] = "Folder",
            ["main.tag.image"] = "Image",
            ["main.tag.audio"] = "Audio",
            ["main.tag.video"] = "Video",
            ["main.tag.archive"] = "Archive",
            ["main.tag.document"] = "Document",
            ["main.tag.program"] = "Program",
            ["main.tag.file"] = "File",

            // ---- Main panel: match badges ----
            ["main.match.exact"] = "Exact",
            ["main.match.prefix"] = "Prefix",
            ["main.match.pinyin"] = "Pinyin",
            ["main.match.initials"] = "Initials",
            ["main.match.alias"] = "Alias",
            ["main.match.command"] = "Command",

            // ---- Main panel: context menu ----
            ["main.menu.launch"] = "Launch",
            ["main.menu.launchAdmin"] = "Run as administrator",
            ["main.menu.openDir"] = "Open containing folder",
            ["main.menu.open"] = "Open",
            ["main.menu.openFolder"] = "Open folder",
            ["main.menu.copyPath"] = "Copy path",
            ["main.menu.favorite"] = "Add to favorites",
            ["main.menu.unfavorite"] = "Remove from favorites",
            ["main.menu.edit"] = "Edit",
            ["main.menu.hide"] = "Hide (don't show again)",
            ["main.menu.delete"] = "Delete",
            ["main.menu.cancel"] = "Cancel",

            // ---- Main panel: command mode ----
            ["main.cmd.settings"] = "Open settings",
            ["main.cmd.capture"] = "Capture foreground window into launcher",
            ["main.cmd.refresh"] = "Rescan / refresh index",
            ["main.cmd.theme"] = "Switch theme: >theme dark|light|system",
            ["main.cmd.backup"] = "Back up data now",
            ["main.cmd.diag"] = "Show diagnostics",
            ["main.cmd.help"] = "Show command help",
            ["main.ds.hint"] = "DeepSeek mode · Enter to search in browser · Esc to exit",
            ["main.ds.row"] = "Search DeepSeek for \"{0}\"",
            ["main.ds.rowSecondary"] = "Open the browser and search automatically (DeepSeek account sign-in required)",
            ["main.ds.launchHint"] = "Search",
            ["main.ds.group"] = "Web search",
            ["main.ds.empty"] = "Type what to search, press Enter to open DeepSeek",
            ["main.command.unknown"] = "Unknown command, type >help for the command list",
            ["main.command.helpTitle"] = "Command Help",
            ["main.command.themeInvalid"] = "Invalid theme, use: dark / light / system",
            ["main.command.refreshDone"] = "App list reloaded and file index rebuild requested",
            ["main.command.themeApplied"] = "Theme switched to {0}",

            // ---- Main panel: status / messages ----
            ["main.status.copied"] = "Path copied",
            ["main.status.hidden"] = "Item hidden",
            ["main.title.error"] = "Error",
            ["main.title.info"] = "Info",
            ["main.title.warning"] = "Warning",
            ["main.title.delete"] = "Confirm Delete",
            ["main.title.diag"] = "Diagnostics",
            ["main.title.relocate"] = "Path Missing",
            ["main.launch.failed"] = "Launch failed: {0}",
            ["main.delete.confirm"] = "Delete \"{0}\"?",
            ["main.open.failed"] = "Cannot open: {0}",
            ["main.copy.failed"] = "Copy failed",

            // ---- Relocation guide ----
            ["main.relocate.message"] = "Target not found (moved or uninstalled):\n{0}\n\nChoose how to continue:",
            ["main.relocate.change"] = "Locate new path",
            ["main.relocate.openDir"] = "Open containing folder",
            ["main.relocate.remove"] = "Remove from list",
            ["main.relocate.dirMissing"] = "Containing folder is missing too",
            ["main.relocate.updated"] = "Path updated",
            ["main.relocate.removed"] = "Removed from list",

            // ---- Drag & drop / capture ----
            ["main.drop.unsupported"] = "Only .exe / .lnk / .url files are supported",
            ["main.drop.title"] = "Add App",
            ["app.capture.none"] = "No capturable window found (switch to the target app first)",
            ["app.capture.self"] = "Cannot capture QuickPalette itself",

            // ---- Tray ----
            ["tray.tooltip"] = "QuickPalette - Launcher",
            ["tray.show"] = "Show palette",
            ["tray.add"] = "Add app",
            ["tray.capture"] = "Capture current window",
            ["tray.settings"] = "Settings",
            ["tray.exit"] = "Exit",
            ["tray.exit.title"] = "Confirm Exit",
            ["tray.exit.confirm"] = "Exit QuickPalette?",

            // ---- App startup / global ----
            ["app.running"] = "QuickPalette is already running.",
            ["app.running.title"] = "Info",
            ["app.hotkey.conflict.title"] = "Hotkey Conflict",
            ["app.hotkey.conflict"] = "Hotkey {0} could not be registered (maybe in use).\nYou can open the palette from the tray icon.",
            ["app.start.degraded.title"] = "Data Load Failed",
            ["app.start.degraded"] = "Settings/data failed to load; started with an empty list (your files were not modified).\nSee log: {0}",
            ["app.backup.done"] = "Backup created: {0}",
            ["app.backup.failed"] = "Backup failed: {0}",

            // ---- Document preview ----
            ["preview.hint"] = "Click a result on the left to switch preview · text is selectable",
            ["preview.loading"] = "Loading…",
            ["preview.truncated"] = "Content truncated",
            ["preview.error"] = "Preview failed: {0}",
            ["preview.pages"] = "{0} pages · showing first {1}"
        });
    }
}
