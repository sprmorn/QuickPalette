namespace QuickPalette.App.Services;

using System.Collections.Generic;

/// <summary>
/// 界面文案轻量本地化（zh-CN / en-US，按 Settings.Locale 选择）。
/// 风格：静态类 + 两语言字典 + T(key) 取词。
/// 扩展方式（供其他分区追加文案，避免覆盖本文件）：
/// 1) 直接向 <see cref="ZhCn"/> / <see cref="EnUs"/> 字典追加条目；
/// 2) 或新建 partial 文件（如 UiText.Main.cs），用 [ModuleInitializer] 调用
///    <see cref="AddTexts"/> 注册自己的双语表。
/// </summary>
public static partial class UiText
{
    private static string _locale = "zh-CN";

    static UiText() => RegisterMain();

    /// <summary>
    /// 主面板/托盘文案注册钩子：由 Services\UiText.Main.cs 实现（partial method）。
    /// 未实现时编译器自动移除调用，不影响本分区。
    /// </summary>
    static partial void RegisterMain();

    /// <summary>
    /// 当前语言（"zh-CN" / "en-US"）
    /// </summary>
    public static string Locale => _locale;

    /// <summary>
    /// 设置当前语言（按 Settings.Locale；未识别的值回退 zh-CN）
    /// </summary>
    public static void SetLocale(string? locale)
    {
        _locale = string.Equals(locale, "en-US", System.StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "zh-CN";
    }

    /// <summary>
    /// 取文案；缺失时回退中文表，再缺失返回键名
    /// </summary>
    public static string T(string key)
    {
        if (string.Equals(_locale, "en-US", System.StringComparison.OrdinalIgnoreCase)
            && EnUs.TryGetValue(key, out var en))
        {
            return en;
        }

        return ZhCn.TryGetValue(key, out var zh) ? zh : key;
    }

    /// <summary>
    /// 取文案并格式化（{0}/{1} 占位符）
    /// </summary>
    public static string Format(string key, params object[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch (System.FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// 追加注册一组双语表（供其他 partial 文件在模块初始化时调用）
    /// </summary>
    public static void AddTexts(IReadOnlyDictionary<string, string> zh, IReadOnlyDictionary<string, string> en)
    {
        foreach (var kv in zh)
        {
            ZhCn[kv.Key] = kv.Value;
        }

        foreach (var kv in en)
        {
            EnUs[kv.Key] = kv.Value;
        }
    }

    #region 双语字典

    private static readonly Dictionary<string, string> ZhCn = new()
    {
        // 通用按钮
        ["Common.Save"] = "保存",
        ["Common.Cancel"] = "取消",
        ["Common.Add"] = "添加",
        ["Common.Delete"] = "删除",
        ["Common.Edit"] = "编辑",
        ["Common.Close"] = "关闭",
        ["Common.Browse"] = "浏览…",
        ["Common.Error"] = "错误",
        ["Common.Hint"] = "提示",
        ["Common.Confirm"] = "确认",

        // 设置窗口
        ["Settings.Title"] = "轻启 - 设置",
        ["Settings.WindowTitle"] = "⚙ 设置",
        ["Settings.TabApps"] = "📋 应用管理",
        ["Settings.TabHotkey"] = "⌨ 快捷键",
        ["Settings.TabGeneral"] = "⚙ 通用",
        ["Settings.TabFilters"] = "🔍 过滤规则",
        ["Settings.TabBackup"] = "💾 备份",
        ["Settings.AddApp"] = "添加应用",
        ["Settings.StatusTotal"] = "共 {0} 个应用",
        ["Settings.StatusFiltered"] = "显示 {0} / {1} 个应用",
        ["Settings.DeleteConfirm"] = "确定要删除 \"{0}\" 吗？",
        ["Settings.DeleteConfirmTitle"] = "确认删除",
        ["Settings.LoadFailed"] = "加载设置失败。",
        ["Settings.SaveFailed"] = "保存失败: {0}",

        // 快捷键
        ["Settings.HotkeySection"] = "全局快捷键设置",
        ["Settings.HotkeyTrigger"] = "唤起/隐藏面板",
        ["Settings.HotkeyUsage"] = "使用说明",
        ["Hotkey.Record"] = "修改",
        ["Hotkey.Recording"] = "请按下新的快捷键…（Esc 取消）",
        ["Hotkey.RestoreDefault"] = "恢复默认",
        ["Hotkey.PressComboHint"] = "请按下包含修饰键（Ctrl/Alt/Shift/Win）的组合键…",
        ["Hotkey.NeedModifier"] = "快捷键必须包含至少一个修饰键（Ctrl/Alt/Shift/Win）。",
        ["Hotkey.InvalidKey"] = "该按键不支持作为快捷键，请换一个键。",
        ["Hotkey.Conflict"] = "该组合已被其他程序占用，请换一个。",
        ["Hotkey.Available"] = "可用",
        ["Hotkey.CurrentRegistered"] = "（当前热键）",
        ["Hotkey.Changed"] = "快捷键已改为 {0}",
        ["Hotkey.RegisterFailed"] = "快捷键注册失败，可能被其他程序占用，本次未保存。",

        // 通用设置
        ["Settings.GeneralSection"] = "通用设置",
        ["Settings.Appearance"] = "外观",
        ["Settings.ThemeLight"] = "浅色模式",
        ["Settings.ThemeDark"] = "深色模式",
        ["Settings.ThemeSystem"] = "跟随系统",
        ["Settings.ThemePreviewHint"] = "切换主题会立即预览效果",
        ["Settings.Startup"] = "启动行为",
        ["Settings.AutoStart"] = "开机自动启动",
        ["Settings.MaxResultsLabel"] = "最大结果条数",
        ["Settings.MaxResultsHint"] = "结果条数：{0}（保存后生效）",
        ["Settings.PreviewSection"] = "文档预览",
        ["Settings.PreviewDesc"] = "单击文档结果时在面板旁弹出预览窗口（支持 PDF/Word/Excel/PPT/Markdown/TXT）。",
        ["Settings.PreviewEnable"] = "单击文档时显示预览窗口",
        ["Settings.DeepSeekSection"] = "DeepSeek 网络搜索",
        ["Settings.DeepSeekDesc"] = "在搜索框输入 DS 后按 Tab 进入 DeepSeek 搜索模式，输入内容回车即打开浏览器自动搜索。",
        ["Settings.DeepSeekEnable"] = "启用 DS 快捷搜索（DS + Tab）",
        ["Settings.DeepSeekHint"] = "提示：使用 DeepSeek 网络搜索需要登录 DeepSeek 账号。",
        ["Settings.FileSearchSection"] = "本地文件搜索",
        ["Settings.FileSearchDesc"] = "基于 NTFS USN 日志的全盘文件名索引（Everything 同款机制），搜索应用时同时匹配本地文件。",
        ["Settings.FileSearchEnable"] = "启用本地文件搜索（首次启用需授予管理员权限建立索引）",
        ["Settings.RebuildIndex"] = "立即重建索引",
        ["Settings.RebuildIndexConfirm"] = "重建索引将重新扫描全盘文件（通常只需几十秒），确定继续吗？",
        ["Settings.RebuildIndexTitle"] = "重建索引",
        ["Settings.FileSearchUnavailable"] = "文件搜索不可用。",
        ["Settings.FileSearchDisabled"] = "已停用。",
        ["Settings.FileSearchStarting"] = "索引服务启动中，正在建立全盘索引…",
        ["Settings.FileSearchReady"] = "索引就绪：已收录 {0:N0} 个文件/文件夹。",
        ["Settings.FileSearchElevation"] = "需要管理员权限：请在弹窗中允许以管理员身份运行索引服务。",
        ["Settings.FileSearchNoIndex"] = "尚未建立索引，等待索引服务启动…",
        ["Settings.FileSearchError"] = "索引服务出错，请点击下方按钮重建。",

        // 重置
        ["Settings.ResetSection"] = "重置",
        ["Settings.ResetDesc"] = "重置所有设置将恢复默认值，但不会删除已添加的应用。",
        ["Settings.ResetButton"] = "重置所有设置",
        ["Settings.ResetConfirm"] = "确定要重置所有设置吗？\n这将恢复默认设置，但不会删除已添加的应用。",
        ["Settings.ResetConfirmTitle"] = "确认重置",
        ["Settings.ResetDone"] = "设置已重置为默认值（热键已恢复为 {0}）。请点击保存按钮持久化更改。",

        // 过滤规则
        ["Settings.FiltersSection"] = "过滤规则",
        ["Settings.FiltersDesc"] = "名称/路径命中黑名单的候选不会出现在扫描结果中；白名单中的条目永不过滤。",
        ["Settings.FilterKeywords"] = "名称关键词黑名单",
        ["Settings.FilterPaths"] = "路径黑名单",
        ["Settings.Whitelist"] = "白名单（永不过滤）",
        ["Settings.FilterAddHint"] = "输入条目后点击“添加”",
        ["Settings.FilterSystemComponents"] = "过滤系统组件（设置、反馈中心等明显系统项）",
        ["Settings.FilterEmptyInput"] = "请先输入要添加的条目。",
        ["Settings.FilterDuplicate"] = "该条目已存在。",

        // 备份
        ["Settings.BackupSection"] = "备份与恢复",
        ["Settings.BackupDesc"] = "导出为单一 quickpalette-backup.json，可在重装或换机后一键恢复。",
        ["Settings.BackupNow"] = "立即备份",
        ["Settings.ExportBackup"] = "导出备份…",
        ["Settings.ImportBackup"] = "导入备份…",
        ["Settings.OpenDataDir"] = "打开数据目录",
        ["Settings.RestoreLatest"] = "从最近备份恢复",
        ["Settings.BackupOk"] = "备份已创建：{0}",
        ["Settings.BackupFailed"] = "备份失败: {0}",
        ["Settings.ExportOk"] = "已导出到：{0}",
        ["Settings.ExportFailed"] = "导出失败: {0}",
        ["Settings.ExportFilter"] = "QuickPalette 备份 (*.json)|*.json|所有文件 (*.*)|*.*",
        ["Settings.ImportConfirm"] = "导入将覆盖当前的应用列表与设置。\n导入前会自动创建一次备份，确定继续吗？",
        ["Settings.ImportConfirmTitle"] = "确认导入",
        ["Settings.ImportOk"] = "导入完成。",
        ["Settings.ImportFailed"] = "导入失败: {0}",
        ["Settings.RestoreNoBackup"] = "没有找到可用的备份。",
        ["Settings.RestoreConfirm"] = "确定要从最近备份恢复吗？\n{0}\n当前数据将被覆盖（恢复前会自动备份）。",
        ["Settings.RestoreConfirmTitle"] = "确认恢复",
        ["Settings.RestoreOk"] = "恢复完成，请重新启动轻启以加载恢复后的数据。",
        ["Settings.RestoreFailed"] = "恢复失败: {0}",

        // 添加应用窗口
        ["AddApp.Title"] = "添加应用",
        ["AddApp.Name"] = "名称 *",
        ["AddApp.Path"] = "路径 *",
        ["AddApp.PathHint"] = "拖拽 .exe / .lnk / .url / .cmd / .bat 文件到上方输入框",
        ["AddApp.Type"] = "应用类型",
        ["AddApp.Args"] = "启动参数",
        ["AddApp.WorkDir"] = "工作目录",
        ["AddApp.Aliases"] = "别名（逗号分隔）",
        ["AddApp.Admin"] = "以管理员身份运行",
        ["AddApp.Add"] = "添加",
        ["AddApp.MsgNameRequired"] = "请输入应用名称。",
        ["AddApp.MsgPathRequired"] = "请输入应用路径。",
        ["AddApp.MsgPathMissing"] = "路径不存在：{0}",
        ["AddApp.MsgBadUrl"] = "请输入合法的网址（如 https://example.com）。",
        ["AddApp.MsgUnsupportedFile"] = "不支持的文件类型，请选择 .exe / .lnk / .url / .cmd / .bat 文件。",
        ["AddApp.OpenFilter"] = "程序与快捷方式|*.exe;*.lnk;*.url;*.cmd;*.bat|所有文件 (*.*)|*.*",

        // 编辑应用窗口
        ["Edit.Title"] = "编辑应用",
        ["Edit.Name"] = "名称",
        ["Edit.Path"] = "路径",
        ["Edit.Args"] = "启动参数",
        ["Edit.WorkDir"] = "工作目录",
        ["Edit.Aliases"] = "别名（逗号分隔）",
        ["Edit.Tags"] = "标签（逗号分隔）",
        ["Edit.Icon"] = "图标文件",
        ["Edit.Admin"] = "以管理员身份运行",
        ["Edit.Enabled"] = "启用",
        ["Edit.MsgNameRequired"] = "请输入应用名称。",
        ["Edit.MsgPathRequired"] = "请输入应用路径。",
        ["Edit.MsgPathMissing"] = "路径不存在：{0}\n仍要保存吗？",
        ["Edit.MsgPathMissingTitle"] = "路径校验",
        ["Edit.IconFilter"] = "图片文件|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.svg|所有文件 (*.*)|*.*",

        // 首次运行向导
        ["FirstRun.Title"] = "轻启 - 首次使用引导",
        ["FirstRun.Welcome"] = "欢迎使用轻启",
        ["FirstRun.Scanning"] = "正在扫描已安装的应用…",
        ["FirstRun.ScanDone"] = "扫描完成，共 {0} 个候选应用",
        ["FirstRun.ScanCanceled"] = "扫描已取消。",
        ["FirstRun.ScanFailed"] = "扫描失败，请稍后重试。",
        ["FirstRun.CancelScan"] = "取消扫描",
        ["FirstRun.Rescan"] = "重新扫描",
        ["FirstRun.SelectAll"] = "全选",
        ["FirstRun.Count"] = "已选 {0} 项",
        ["FirstRun.NeverRecommend"] = "不再推荐",
        ["FirstRun.HideKind"] = "隐藏此类",
        ["FirstRun.Import"] = "导入选中的应用",
        ["FirstRun.Skip"] = "跳过，稍后添加",
        ["FirstRun.Done"] = "完成，开始使用",
        ["FirstRun.Importing"] = "导入中…",
        ["FirstRun.MsgSelectOne"] = "请至少选择一个应用。",
        ["FirstRun.ImportFailed"] = "导入失败: {0}",
        ["FirstRun.ImportedTitle"] = "已导入 {0} 个应用",
        ["FirstRun.DoneSubtitle"] = "应用已就绪，先花 10 秒了解如何使用",
        ["FirstRun.HowToOpen"] = "如何打开轻启",
        ["FirstRun.HowToOpenBody1"] = "按下快捷键 ",
        ["FirstRun.HowToOpenBody2"] = " 即可随时唤起面板（可在设置中修改）。",
        ["FirstRun.HowToOpenBody3"] = "输入应用或文件名称，按回车启动，全程无需鼠标。",
        ["FirstRun.Features"] = "支持的功能",
        ["FirstRun.Feature1"] = "· 应用快速启动：搜索应用名，回车即启动",
        ["FirstRun.Feature2"] = "· 全盘文件搜索：像 Everything 一样秒搜本地文件（设置中开启）",
        ["FirstRun.Feature3"] = "· 系统托盘常驻：左键点击托盘图标可随时呼出面板",
        ["FirstRun.Feature4"] = "· 主题切换：深色 / 浅色 / 跟随系统（设置中修改）",
        ["FirstRun.Feature5"] = "· 打开设置：右键点击系统托盘图标 → 设置，可修改热键、主题、文件搜索等",
    };

    private static readonly Dictionary<string, string> EnUs = new()
    {
        // 通用按钮
        ["Common.Save"] = "Save",
        ["Common.Cancel"] = "Cancel",
        ["Common.Add"] = "Add",
        ["Common.Delete"] = "Delete",
        ["Common.Edit"] = "Edit",
        ["Common.Close"] = "Close",
        ["Common.Browse"] = "Browse…",
        ["Common.Error"] = "Error",
        ["Common.Hint"] = "Notice",
        ["Common.Confirm"] = "Confirm",

        // 设置窗口
        ["Settings.Title"] = "QuickPalette - Settings",
        ["Settings.WindowTitle"] = "⚙ Settings",
        ["Settings.TabApps"] = "📋 Apps",
        ["Settings.TabHotkey"] = "⌨ Hotkey",
        ["Settings.TabGeneral"] = "⚙ General",
        ["Settings.TabFilters"] = "🔍 Filters",
        ["Settings.TabBackup"] = "💾 Backup",
        ["Settings.AddApp"] = "Add App",
        ["Settings.StatusTotal"] = "{0} apps",
        ["Settings.StatusFiltered"] = "Showing {0} of {1}",
        ["Settings.DeleteConfirm"] = "Delete \"{0}\"?",
        ["Settings.DeleteConfirmTitle"] = "Confirm Delete",
        ["Settings.LoadFailed"] = "Failed to load settings.",
        ["Settings.SaveFailed"] = "Save failed: {0}",

        // 快捷键
        ["Settings.HotkeySection"] = "Global Hotkey",
        ["Settings.HotkeyTrigger"] = "Show / hide panel",
        ["Settings.HotkeyUsage"] = "Usage",
        ["Hotkey.Record"] = "Change",
        ["Hotkey.Recording"] = "Press the new hotkey… (Esc to cancel)",
        ["Hotkey.RestoreDefault"] = "Restore default",
        ["Hotkey.PressComboHint"] = "Press a combination with a modifier (Ctrl/Alt/Shift/Win)…",
        ["Hotkey.NeedModifier"] = "The hotkey must contain at least one modifier (Ctrl/Alt/Shift/Win).",
        ["Hotkey.InvalidKey"] = "This key cannot be used in a hotkey. Try another key.",
        ["Hotkey.Conflict"] = "This combination is already taken by another app.",
        ["Hotkey.Available"] = "Available",
        ["Hotkey.CurrentRegistered"] = "(current hotkey)",
        ["Hotkey.Changed"] = "Hotkey changed to {0}",
        ["Hotkey.RegisterFailed"] = "Failed to register the hotkey (possibly in use). Changes were not saved.",

        // 通用设置
        ["Settings.GeneralSection"] = "General",
        ["Settings.Appearance"] = "Appearance",
        ["Settings.ThemeLight"] = "Light",
        ["Settings.ThemeDark"] = "Dark",
        ["Settings.ThemeSystem"] = "Follow system",
        ["Settings.ThemePreviewHint"] = "Theme changes are previewed instantly",
        ["Settings.Startup"] = "Startup",
        ["Settings.AutoStart"] = "Start with Windows",
        ["Settings.MaxResultsLabel"] = "Max results",
        ["Settings.MaxResultsHint"] = "Results: {0} (applies after saving)",
        ["Settings.PreviewSection"] = "Document preview",
        ["Settings.PreviewDesc"] = "Show a preview window beside the palette when clicking a document (PDF/Word/Excel/PPT/Markdown/TXT).",
        ["Settings.PreviewEnable"] = "Show preview when clicking a document",
        ["Settings.DeepSeekSection"] = "DeepSeek web search",
        ["Settings.DeepSeekDesc"] = "Type DS in the search box and press Tab to enter DeepSeek search mode; type your query and press Enter to search in the browser.",
        ["Settings.DeepSeekEnable"] = "Enable DS quick search (DS + Tab)",
        ["Settings.DeepSeekHint"] = "Note: DeepSeek web search requires signing in to your DeepSeek account.",
        ["Settings.FileSearchSection"] = "Local file search",
        ["Settings.FileSearchDesc"] = "NTFS USN journal based whole-disk file name index (same mechanism as Everything); file matches are shown alongside apps.",
        ["Settings.FileSearchEnable"] = "Enable local file search (admin rights needed for the first index build)",
        ["Settings.RebuildIndex"] = "Rebuild index now",
        ["Settings.RebuildIndexConfirm"] = "Rebuilding re-scans all drives (usually a few tens of seconds). Continue?",
        ["Settings.RebuildIndexTitle"] = "Rebuild Index",
        ["Settings.FileSearchUnavailable"] = "File search is unavailable.",
        ["Settings.FileSearchDisabled"] = "Disabled.",
        ["Settings.FileSearchStarting"] = "Index service starting, building the index…",
        ["Settings.FileSearchReady"] = "Index ready: {0:N0} files/folders indexed.",
        ["Settings.FileSearchElevation"] = "Admin rights required: please approve the elevation prompt for the index service.",
        ["Settings.FileSearchNoIndex"] = "No index yet, waiting for the index service…",
        ["Settings.FileSearchError"] = "Index service error. Use the button below to rebuild.",

        // 重置
        ["Settings.ResetSection"] = "Reset",
        ["Settings.ResetDesc"] = "Resetting restores all defaults. Added apps are kept.",
        ["Settings.ResetButton"] = "Reset all settings",
        ["Settings.ResetConfirm"] = "Reset all settings?\nDefaults will be restored. Added apps are kept.",
        ["Settings.ResetConfirmTitle"] = "Confirm Reset",
        ["Settings.ResetDone"] = "Settings reset to defaults (hotkey restored to {0}). Click Save to persist.",

        // 过滤规则
        ["Settings.FiltersSection"] = "Filter rules",
        ["Settings.FiltersDesc"] = "Candidates matching a blocked keyword/path are hidden from scans; whitelisted entries are never filtered.",
        ["Settings.FilterKeywords"] = "Blocked name keywords",
        ["Settings.FilterPaths"] = "Blocked paths",
        ["Settings.Whitelist"] = "Whitelist (never filtered)",
        ["Settings.FilterAddHint"] = "Type an entry, then click Add",
        ["Settings.FilterSystemComponents"] = "Filter system components (Settings, Feedback Hub, etc.)",
        ["Settings.FilterEmptyInput"] = "Please type an entry first.",
        ["Settings.FilterDuplicate"] = "This entry already exists.",

        // 备份
        ["Settings.BackupSection"] = "Backup & Restore",
        ["Settings.BackupDesc"] = "Export to a single quickpalette-backup.json and restore it after reinstalling or moving to a new PC.",
        ["Settings.BackupNow"] = "Back up now",
        ["Settings.ExportBackup"] = "Export backup…",
        ["Settings.ImportBackup"] = "Import backup…",
        ["Settings.OpenDataDir"] = "Open data folder",
        ["Settings.RestoreLatest"] = "Restore latest backup",
        ["Settings.BackupOk"] = "Backup created: {0}",
        ["Settings.BackupFailed"] = "Backup failed: {0}",
        ["Settings.ExportOk"] = "Exported to: {0}",
        ["Settings.ExportFailed"] = "Export failed: {0}",
        ["Settings.ExportFilter"] = "QuickPalette backup (*.json)|*.json|All files (*.*)|*.*",
        ["Settings.ImportConfirm"] = "Importing replaces the current app list and settings.\nA backup is created automatically first. Continue?",
        ["Settings.ImportConfirmTitle"] = "Confirm Import",
        ["Settings.ImportOk"] = "Import finished.",
        ["Settings.ImportFailed"] = "Import failed: {0}",
        ["Settings.RestoreNoBackup"] = "No backup found.",
        ["Settings.RestoreConfirm"] = "Restore from the latest backup?\n{0}\nCurrent data will be replaced (a backup is created first).",
        ["Settings.RestoreConfirmTitle"] = "Confirm Restore",
        ["Settings.RestoreOk"] = "Restore finished. Restart QuickPalette to load the restored data.",
        ["Settings.RestoreFailed"] = "Restore failed: {0}",

        // 添加应用窗口
        ["AddApp.Title"] = "Add App",
        ["AddApp.Name"] = "Name *",
        ["AddApp.Path"] = "Path *",
        ["AddApp.PathHint"] = "Drop an .exe / .lnk / .url / .cmd / .bat file onto the box above",
        ["AddApp.Type"] = "App type",
        ["AddApp.Args"] = "Arguments",
        ["AddApp.WorkDir"] = "Working directory",
        ["AddApp.Aliases"] = "Aliases (comma-separated)",
        ["AddApp.Admin"] = "Run as administrator",
        ["AddApp.Add"] = "Add",
        ["AddApp.MsgNameRequired"] = "Please enter a name.",
        ["AddApp.MsgPathRequired"] = "Please enter a path.",
        ["AddApp.MsgPathMissing"] = "Path not found: {0}",
        ["AddApp.MsgBadUrl"] = "Please enter a valid URL (e.g. https://example.com).",
        ["AddApp.MsgUnsupportedFile"] = "Unsupported file type. Choose .exe / .lnk / .url / .cmd / .bat.",
        ["AddApp.OpenFilter"] = "Programs & shortcuts|*.exe;*.lnk;*.url;*.cmd;*.bat|All files (*.*)|*.*",

        // 编辑应用窗口
        ["Edit.Title"] = "Edit App",
        ["Edit.Name"] = "Name",
        ["Edit.Path"] = "Path",
        ["Edit.Args"] = "Arguments",
        ["Edit.WorkDir"] = "Working directory",
        ["Edit.Aliases"] = "Aliases (comma-separated)",
        ["Edit.Tags"] = "Tags (comma-separated)",
        ["Edit.Icon"] = "Icon file",
        ["Edit.Admin"] = "Run as administrator",
        ["Edit.Enabled"] = "Enabled",
        ["Edit.MsgNameRequired"] = "Please enter a name.",
        ["Edit.MsgPathRequired"] = "Please enter a path.",
        ["Edit.MsgPathMissing"] = "Path not found: {0}\nSave anyway?",
        ["Edit.MsgPathMissingTitle"] = "Path Check",
        ["Edit.IconFilter"] = "Image files|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.svg|All files (*.*)|*.*",

        // 首次运行向导
        ["FirstRun.Title"] = "QuickPalette - First Run",
        ["FirstRun.Welcome"] = "Welcome to QuickPalette",
        ["FirstRun.Scanning"] = "Scanning installed apps…",
        ["FirstRun.ScanDone"] = "Scan finished: {0} candidates",
        ["FirstRun.ScanCanceled"] = "Scan canceled.",
        ["FirstRun.ScanFailed"] = "Scan failed, please try again later.",
        ["FirstRun.CancelScan"] = "Cancel scan",
        ["FirstRun.Rescan"] = "Rescan",
        ["FirstRun.SelectAll"] = "Select all",
        ["FirstRun.Count"] = "{0} selected",
        ["FirstRun.NeverRecommend"] = "Don't recommend",
        ["FirstRun.HideKind"] = "Hide similar",
        ["FirstRun.Import"] = "Import selected",
        ["FirstRun.Skip"] = "Skip for now",
        ["FirstRun.Done"] = "Done, start using",
        ["FirstRun.Importing"] = "Importing…",
        ["FirstRun.MsgSelectOne"] = "Please select at least one app.",
        ["FirstRun.ImportFailed"] = "Import failed: {0}",
        ["FirstRun.ImportedTitle"] = "Imported {0} apps",
        ["FirstRun.DoneSubtitle"] = "Apps are ready — 10 seconds to learn the basics",
        ["FirstRun.HowToOpen"] = "How to open QuickPalette",
        ["FirstRun.HowToOpenBody1"] = "Press ",
        ["FirstRun.HowToOpenBody2"] = " to show the panel anytime (changeable in Settings).",
        ["FirstRun.HowToOpenBody3"] = "Type an app or file name, press Enter to launch — no mouse needed.",
        ["FirstRun.Features"] = "Features",
        ["FirstRun.Feature1"] = "· Quick app launch: search by name, press Enter",
        ["FirstRun.Feature2"] = "· Whole-disk file search: Everything-style instant file lookup (enable in Settings)",
        ["FirstRun.Feature3"] = "· System tray: left-click the tray icon to show the panel",
        ["FirstRun.Feature4"] = "· Themes: dark / light / follow system (changeable in Settings)",
        ["FirstRun.Feature5"] = "· Settings: right-click the tray icon → Settings, for hotkey, theme, file search and more",
    };

    #endregion
}
