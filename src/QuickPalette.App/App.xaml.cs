namespace QuickPalette.App;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using QuickPalette.App.Services;
using QuickPalette.Application.Events;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.Launchers;
using QuickPalette.Infrastructure.Scanners;
using QuickPalette.Infrastructure.Singleton;
using QuickPalette.Infrastructure.Storage;
using Serilog;

/// <summary>
/// 应用程序入口。
/// 稳定性约定：
///  - 挂全局异常兜底（Dispatcher / TaskScheduler / AppDomain），非致命异常记日志不退出；
///  - OnStartup 的迁移/仓储加载失败走降级（提示 + 空列表），绝不启动即崩；
///  - 所有 async void 事件处理器自带 try/catch。
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstanceLock? _instanceLock;
    private TrayService? _trayService;
    private MainWindow? _mainWindow;
    private EventBus? _eventBus;
    private ILogger? _logger;
    private HotkeyService? _hotkeyService;
    private ForegroundWindowTracker? _foregroundTracker;
    private JsonRepository? _repository;
    private LauncherManager? _launcherManager;
    private FileSearchManager? _fileSearchManager;
    private SearchEngine? _searchEngine;
    private HiddenItemStore? _hiddenItemStore;
    private UsageStore? _usageStore;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 1. 数据目录 + 日志（设计 7.3：%APPDATA%\QuickPalette\logs\app-{yyyyMMdd}.log，滚动保留）
            // 健壮性：日志目录不可写时降级到 %TEMP%，仍失败则静默——日志问题绝不允许弄死启动
            var dataDir = ResolveWritableDataDir();
            _logger = CreateLoggerWithFallback(dataDir, out var logDir);

            // 2. 全局异常兜底（在任何业务代码之前挂上）
            InitGlobalExceptionHandlers();

            _logger.Information("=== 轻启启动 ===（日志目录：{LogDir}）", logDir);

            // 低完整性运行告警：常见于直接从 dist\ 等带「低」标签目录运行，
            // 强制完整性控制会封禁系统预览处理器（COM）的激活与渲染
            if (QuickPalette.Infrastructure.ProcessIntegrity.IsLowIntegrity())
            {
                _logger.Warning(
                    "检测到进程运行在低完整性级别（通常因直接从 dist 等带低标签目录启动）；" +
                    "系统预览处理器（COM）将无法工作，请改用安装版，或把整个程序目录复制到普通位置后运行");
            }

            // 3. 单实例检查
            _instanceLock = new SingleInstanceLock("QuickPalette", _logger);
            if (!_instanceLock.TryAcquire())
            {
                _logger.Warning("Another instance is already running");
                MessageBox.Show(UiText.T("app.running"), UiText.T("app.running.title"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // 4. 初始化事件总线与仓储（目录级，几乎不失败）
            _eventBus = new EventBus(_logger);
            _repository = new JsonRepository(dataDir, _logger);

            // 5. 执行数据迁移 + 加载数据（失败降级：空列表继续跑，不崩、不覆盖用户数据）
            var degraded = false;
            var settings = new Settings();
            var apps = new List<AppItem>();
            try
            {
                var migrationEngine = new MigrationEngine(dataDir, _logger);
                await migrationEngine.MigrateAsync();
                settings = await _repository.GetAsync();
                apps = await _repository.GetAllAsync();
            }
            catch (Exception ex)
            {
                degraded = true;
                settings = new Settings();
                apps = new List<AppItem>();
                _logger.Error(ex, "Migration/data load failed, degrade to empty list");
            }

            // 6. 语言 + 主题（必须在任何窗口显示之前，否则首帧会闪浅色）
            UiText.SetLocale(settings.Locale);
            ApplyTheme(settings.Theme);
            _previewEnabled = settings.PreviewEnabled;
            _deepSeekSearchEnabled = settings.DeepSeekSearchEnabled;

            // 7. 检查是否首次运行
            if (!degraded && apps.Count == 0 && !File.Exists(Path.Combine(dataDir, "apps.json")))
            {
                _logger.Information("First run detected, showing welcome wizard");
                var firstRunWindow = new FirstRunWindow(_repository, new FilterService(settings), _logger, settings.Hotkey);
                firstRunWindow.ShowDialog();

                // 重新加载应用列表
                apps = await _repository.GetAllAsync();
            }

            // 8. 拼音补全（只补空值；存量数据补齐后一次性落盘）
            await EnsurePinyinAsync(apps, degraded);

            // 9. 初始化搜索引擎、隐藏/使用统计、文件搜索和启动器
            _searchEngine = new SearchEngine(settings.MaxResults);
            _hiddenItemStore = new HiddenItemStore(_repository.DataDirectory, _logger);
            _usageStore = new UsageStore(_repository.DataDirectory, _logger);
            _fileSearchManager = new FileSearchManager(_logger, settings.FileSearchEnabled);
            _launcherManager = new LauncherManager(_logger);

            // 10. 创建主窗口（先启动前台窗口跟踪：托盘"捕获窗口"要靠它记菜单弹出前的目标窗口）
            _foregroundTracker = new ForegroundWindowTracker(_logger);
            _foregroundTracker.Start();
            _mainWindow = new MainWindow(
                _searchEngine,
                _fileSearchManager,
                _eventBus,
                _logger,
                apps,
                _repository,
                _launcherManager,
                _hiddenItemStore,
                _usageStore,
                CreateHostActions());
            _mainWindow.ApplyIconTheme(settings.Theme);

            // 11. 初始化托盘（含"捕获当前窗口到轻启"）
            _trayService = new TrayService(
                _logger,
                () => _mainWindow?.ShowPalette(),
                () => ShowAddApp(),
                CaptureCurrentWindow,
                ShowSettings,
                ExitApplication);
            _trayService.SetTheme(settings.Theme);

            // 12. 注册全局热键（任何异常都不允许中断启动——否则托盘/设置全没了）
            try
            {
                _hotkeyService = new HotkeyService(_logger);
                if (!_hotkeyService.TryRegisterHotkey(settings.Hotkey, OnHotkeyPressed, out var hotkeyError))
                {
                    _logger.Error("Failed to register hotkey: {Hotkey} ({Error})", settings.Hotkey, hotkeyError);
                    MessageBox.Show(
                        $"{string.Format(UiText.T("app.hotkey.conflict"), settings.Hotkey)}\n{hotkeyError}\n\n可在设置页重新录制热键。",
                        UiText.T("app.hotkey.conflict.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    _logger.Information("Hotkey registered: {Hotkey}", settings.Hotkey);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Hotkey init failed (continuing without global hotkey)");
                MessageBox.Show(
                    $"全局热键初始化失败：{ex.Message}\n可在设置页重新录制热键。",
                    UiText.T("app.hotkey.conflict.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            if (degraded)
            {
                MessageBox.Show(
                    string.Format(UiText.T("app.start.degraded"), logDir),
                    UiText.T("app.start.degraded.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            _logger.Information("QuickPalette started successfully with {Count} apps", apps.Count);
        }
        catch (Exception ex)
        {
            // 兜底：启动期任何异常都记日志并体面退出，不裸崩
            _logger?.Fatal(ex, "Startup failed");
            MessageBox.Show(
                $"启动失败：{ex.Message}\n详见日志目录。",
                UiText.T("app.start.degraded.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    #region 启动健壮性：日志/数据目录降级

    /// <summary>
    /// 数据目录：%APPDATA%\QuickPalette，不可写（权限/重定向/受控环境）时降级 %TEMP%\QuickPalette。
    /// 任何情况下都不抛异常。
    /// </summary>
    private static string ResolveWritableDataDir()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPalette"),
                     Path.Combine(Path.GetTempPath(), "QuickPalette"),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return dir;
            }
            catch
            {
                // 尝试下一个候选目录
            }
        }
        return Path.Combine(Path.GetTempPath(), "QuickPalette");
    }

    /// <summary>
    /// 日志初始化：目录不可写时逐级降级（数据目录 → %TEMP% → 静默），
    /// 日志问题绝不允许让应用启动失败。
    /// </summary>
    private static Serilog.ILogger CreateLoggerWithFallback(string dataDir, out string logDir)
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(dataDir, "logs"),
                     Path.Combine(Path.GetTempPath(), "QuickPalette", "logs"),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var logger = new LoggerConfiguration()
                    .WriteTo.Console()
                    .WriteTo.File(
                        Path.Combine(dir, "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7)
                    .CreateLogger();
                Log.Logger = logger;
                logDir = dir;
                return logger;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Log init failed at {dir}: {ex.Message}");
            }
        }

        logDir = "（日志不可用）";
        var silent = new LoggerConfiguration().CreateLogger();
        Log.Logger = silent;
        return silent;
    }

    #endregion

    #region 全局异常兜底

    private void InitGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    /// <summary>UI 线程未处理异常：记日志、标记已处理，非致命不退出</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error(e.Exception, "Unhandled dispatcher exception");
        e.Handled = true;
    }

    /// <summary>未观察任务异常：记日志，不让终结器线程把进程带走</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    /// <summary>AppDomain 未处理异常：尽力记日志并落盘（CLR 即将终止时无法阻止）</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.Fatal(e.ExceptionObject as Exception,
            "Unhandled AppDomain exception (terminating={Terminating})", e.IsTerminating);
        Log.CloseAndFlush();
    }

    #endregion

    private void OnHotkeyPressed()
    {
        try
        {
            if (_mainWindow?.IsVisible == true)
            {
                _mainWindow.HidePalette();
            }
            else
            {
                _mainWindow?.ShowPalette();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Hotkey toggle failed");
        }
    }

    /// <summary>命令模式/主面板需要的宿主能力</summary>
    private PaletteHostActions CreateHostActions() => new()
    {
        OpenSettings = ShowSettings,
        ShowAddApp = ShowAddApp,
        SwitchTheme = SwitchTheme,
        BackupNow = BackupNow,
        CaptureCurrentWindow = CaptureCurrentWindow,
        IsPreviewEnabled = () => _previewEnabled,
        IsDeepSeekSearchEnabled = () => _deepSeekSearchEnabled
    };

    /// <summary>文档预览开关（设置热更新缓存；默认开）</summary>
    private volatile bool _previewEnabled = true;

    /// <summary>DeepSeek 网络搜索快捷模式开关（设置热更新缓存；默认开）</summary>
    private volatile bool _deepSeekSearchEnabled = true;

    /// <summary>添加应用（可预填：拖拽/捕获流程）</summary>
    private async void ShowAddApp(AppItem? prefill = null)
    {
        try
        {
            if (_repository == null) return;

            AddAppWindow addWindow;
            _mainWindow?.SuspendAutoHide();
            try
            {
                addWindow = new AddAppWindow(prefill);
                if (_mainWindow != null)
                {
                    addWindow.Owner = _mainWindow;
                }

                if (addWindow.ShowDialog() != true || addWindow.Result == null)
                    return;
            }
            finally
            {
                _mainWindow?.ResumeAutoHide();
            }

            PinyinProvider.Instance.EnsurePinyin(addWindow.Result);
            await _repository.AddAsync(addWindow.Result);
            _mainWindow?.RefreshApps();
            _logger?.Information("Added app: {Name}", addWindow.Result.Name);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Add app failed");
        }
    }

    /// <summary>
    /// 运行中捕获：隐藏面板 → 捕获目标窗口进程 → 预填入库。
    /// 关键点：托盘菜单/任务栏右键弹出后"当前前台"已不是目标窗口（是菜单自己），
    /// 必须用前台窗口跟踪器记住的"菜单弹出前的窗口"作为第一候选。
    /// </summary>
    private async void CaptureCurrentWindow()
    {
        try
        {
            if (_logger == null) return;

            // 先隐藏面板，让被遮挡的目标窗口回到前台再捕获
            _mainWindow?.HidePalette();
            await Task.Delay(300);

            RawApp? raw = null;
            await Task.Run(() =>
            {
                var scanner = new ProcessScanner(_logger);

                // 候选顺序：跟踪器记住的最后有效前台窗口 → 实时前台窗口
                var tracked = _foregroundTracker?.LastForeground ?? IntPtr.Zero;
                if (tracked != IntPtr.Zero)
                {
                    raw = scanner.CaptureWindow(tracked);
                    if (raw != null && IsSelfProcess(raw)) raw = null;
                }

                if (raw == null)
                {
                    raw = scanner.CaptureForeground();
                    if (raw != null && IsSelfProcess(raw)) raw = null;
                }
            });

            if (raw == null)
            {
                MessageBox.Show(UiText.T("app.capture.none"), UiText.T("main.title.info"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var prefill = new AppItem
            {
                Name = raw.Name,
                Path = raw.Path,
                Args = raw.Args,
                WorkDir = raw.WorkDir,
                IconPath = raw.IconPath,
                Type = AppType.Exe,
                Source = AppSource.Process
            };

            ShowAddApp(prefill);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Capture foreground window failed");
        }
    }

    /// <summary>排除轻启自身（托盘/面板窗口被误捕获）</summary>
    private static bool IsSelfProcess(RawApp raw)
    {
        if (raw.Name.Contains("QuickPalette", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return !string.IsNullOrEmpty(raw.Path) &&
                   raw.Path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async void ShowSettings()
    {
        try
        {
            if (_repository == null || _logger == null || _searchEngine == null)
                return;

            // 热键服务初始化失败过也不阻塞设置页：惰性补建，保证用户总能改热键
            if (_hotkeyService == null)
            {
                try
                {
                    _hotkeyService = new HotkeyService(_logger);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Lazy HotkeyService creation failed");
                    MessageBox.Show(
                        $"全局热键服务不可用：{ex.Message}",
                        UiText.T("app.hotkey.conflict.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            _mainWindow?.SuspendAutoHide();
            try
            {
                var settingsWindow = new SettingsWindow(_repository, _logger, _hotkeyService, OnHotkeyPressed, _fileSearchManager);
                if (_mainWindow != null)
                {
                    settingsWindow.Owner = _mainWindow;
                }

                settingsWindow.ShowDialog();
            }
            finally
            {
                _mainWindow?.ResumeAutoHide();
            }

            // 保存后重新加载设置并热更新（MaxResults / 语言 / 主题 / 文件搜索开关）
            var updated = await _repository.GetAsync();
            _searchEngine.MaxResults = updated.MaxResults;
            UiText.SetLocale(updated.Locale);
            ApplyTheme(updated.Theme);
            _fileSearchManager?.Configure(updated.FileSearchEnabled);
            _previewEnabled = updated.PreviewEnabled;
            _deepSeekSearchEnabled = updated.DeepSeekSearchEnabled;

            // 重新加载应用列表
            _mainWindow?.RefreshApps();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Show settings failed");
        }
    }

    /// <summary>&gt;theme 命令：切换并持久化主题</summary>
    private async void SwitchTheme(string theme)
    {
        try
        {
            if (_repository == null) return;

            var settings = await _repository.GetAsync();
            settings.Theme = theme;
            await _repository.SaveAsync(settings);
            ApplyTheme(theme);
            _logger?.Information("Theme switched: {Theme}", theme);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Switch theme failed");
        }
    }

    /// <summary>&gt;backup 命令：立即备份 apps.json / settings.json 并做备份轮转</summary>
    private void BackupNow()
    {
        try
        {
            if (_repository == null) return;

            var dataDir = _repository.DataDirectory;
            var backupsDir = Path.Combine(dataDir, "backups");
            Directory.CreateDirectory(backupsDir);

            foreach (var name in new[] { "apps.json", "settings.json" })
            {
                var src = Path.Combine(dataDir, name);
                if (!File.Exists(src)) continue;

                var dst = Path.Combine(backupsDir,
                    $"{Path.GetFileNameWithoutExtension(name)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                File.Copy(src, dst, true);
            }

            _repository.CleanupOldBackups(10); // 备份轮转，防止 backups 无限增长

            MessageBox.Show(string.Format(UiText.T("app.backup.done"), backupsDir),
                UiText.T("main.title.info"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Backup failed");
            MessageBox.Show(string.Format(UiText.T("app.backup.failed"), ex.Message),
                UiText.T("main.title.error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>托盘"退出"：真正关闭主窗口（绕过 Closing 拦截）后退出</summary>
    private void ExitApplication()
    {
        try
        {
            _mainWindow?.ForceClose();
            Shutdown();
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Exit failed");
        }
    }

    /// <summary>拼音补全：启动加载白名单后对所有项补一次（只填空值）</summary>
    private async Task EnsurePinyinAsync(List<AppItem> apps, bool degraded)
    {
        try
        {
            var changed = false;
            foreach (var app in apps)
            {
                if (string.IsNullOrEmpty(app.Pinyin) || string.IsNullOrEmpty(app.PinyinInitials))
                {
                    PinyinProvider.Instance.EnsurePinyin(app);
                    changed = true;
                }
            }

            // 存量数据一次性补齐后落盘（降级模式不动用户文件）
            if (changed && !degraded && _repository != null)
            {
                await _repository.SaveAllAsync(apps);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Fill pinyin failed");
        }
    }

    /// <summary>
    /// 应用主题（dark/light/system；system 按系统实际生效主题选择字典）
    /// </summary>
    private void ApplyTheme(string theme)
    {
        try
        {
            if (System.Windows.Application.Current.Resources.MergedDictionaries.Count == 0) return;
            var themeDict = System.Windows.Application.Current.Resources.MergedDictionaries[0];

            var resolved = ThemeResolver.Resolve(theme);
            var source = resolved == "light" ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml";

            if (themeDict.Source == null ||
                !themeDict.Source.ToString().EndsWith(source, StringComparison.OrdinalIgnoreCase))
            {
                themeDict.Source = new Uri(source, UriKind.Relative);
                _logger?.Information("Theme applied: {Theme} -> {Resolved}", theme, resolved);
            }

            // 同步窗口与托盘图标到对应主题版本（theme=system 时按实际生效主题选图标）
            _mainWindow?.ApplyIconTheme(theme);
            _trayService?.SetTheme(theme);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to apply theme: {Theme}", theme);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Information("=== 轻启退出 ===");

        _fileSearchManager?.Dispose();
        _hotkeyService?.Dispose();
        _foregroundTracker?.Dispose();
        _trayService?.Dispose();
        _instanceLock?.Dispose();

        // 退出时关闭并落盘日志，避免最后几行丢失
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
