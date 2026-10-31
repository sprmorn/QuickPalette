namespace QuickPalette.App;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickPalette.App.Services;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.Storage;
using Serilog;

/// <summary>
/// 设置窗口：
/// - 主题单选按 _settings.Theme 回显，切换即时预览；
/// - 热键由 HotkeyRecorderControl 录制（Alt 组合 / Oem / NumPad 正确处理、即时冲突探测），
///   保存时统一注册 _settings.Hotkey 并持久化，取消/关闭时若注册已改动则恢复原注册；
/// - MaxResults / 过滤规则（关键词、路径、白名单）/ 过滤系统组件开关均可编辑；
/// - 备份区：导出 / 导入 / 立即备份 / 从最近备份恢复 / 打开数据目录。
/// </summary>
public partial class SettingsWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "QuickPalette";

    private readonly JsonRepository _repository;
    private readonly ILogger _logger;
    private readonly HotkeyService _hotkeyService;
    private readonly Action _onHotkeyPressed;

    /// <summary>批量导入完成后回调（刷新主面板应用池）</summary>
    private readonly Action? _onAppsImported;
    private readonly FileSearchManager? _fileSearchManager;
    private readonly DispatcherTimer _fileStatusTimer;

    private Settings _settings = new();
    private List<AppItem> _allApps = new();
    private List<AppItem> _filteredApps = new();

    /// <summary>窗口构造时的原热键快照（取消/关闭时恢复原注册用）</summary>
    private string _originalHotkey = string.Empty;

    /// <summary>窗口构造时的原主题快照（取消/关闭时恢复预览用）</summary>
    private string _originalTheme = "system";

    /// <summary>本次会话中注册是否已改动（未持久化前关闭需回滚）</summary>
    private bool _registrationChanged;

    /// <summary>是否已成功保存（保存后关闭不再回滚主题/热键）</summary>
    private bool _saved;

    /// <summary>控件回显期间抑制事件副作用</summary>
    private bool _loading;

    private BackupService? _backupService;

    public SettingsWindow(JsonRepository repository, ILogger logger, HotkeyService hotkeyService,
        Action onHotkeyPressed, FileSearchManager? fileSearchManager = null, Action? onAppsImported = null)
    {
        InitializeComponent();

        _repository = repository;
        _logger = logger;
        _hotkeyService = hotkeyService;
        _onHotkeyPressed = onHotkeyPressed;
        _onAppsImported = onAppsImported;
        _fileSearchManager = fileSearchManager;

        // 定期刷新文件搜索状态（索引构建期间可见进度）
        _fileStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fileStatusTimer.Tick += (s, e) => UpdateFileSearchStatus();
        _fileStatusTimer.Start();

        // 热键录制完成 → 只更新待保存的设置对象，不立即注册
        HotkeyRecorder.HotkeyChanged += (s, hotkey) =>
        {
            _settings.Hotkey = hotkey;
            _logger.Information("Hotkey recorded: {Hotkey}", hotkey);
        };

        // 窗口关闭：停表（修 DispatcherTimer 泄漏）+ 回滚未保存的注册/主题
        Closed += (s, e) => OnWindowClosed();

        Loaded += async (s, e) => await LoadDataAsync();
    }

    #region 加载 / 回显

    private async Task LoadDataAsync()
    {
        try
        {
            // 加载设置
            _settings = await _repository.GetAsync();

            _originalHotkey = _settings.Hotkey;
            _originalTheme = _settings.Theme;

            UiText.SetLocale(_settings.Locale);
            ApplyUiText();
            ApplySettingsToUI();

            // 加载应用列表
            _allApps = await _repository.GetAllAsync();
            _filteredApps = new List<AppItem>(_allApps);
            AppsDataGrid.ItemsSource = _filteredApps;
            StatusText.Text = UiText.Format("Settings.StatusTotal", _allApps.Count);

            UpdateFileSearchStatus();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load settings");
            MessageBox.Show(UiText.T("Settings.LoadFailed"),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 界面文案本地化（按钮 / 标题）
    /// </summary>
    private void ApplyUiText()
    {
        Title = UiText.T("Settings.Title");
        TitleText.Text = UiText.T("Settings.WindowTitle");

        TabAppsItem.Header = UiText.T("Settings.TabApps");
        TabHotkeyItem.Header = UiText.T("Settings.TabHotkey");
        TabScreenshotItem.Header = UiText.T("Settings.TabScreenshot");
        TabGeneralItem.Header = UiText.T("Settings.TabGeneral");
        TabFiltersItem.Header = UiText.T("Settings.TabFilters");
        TabBackupItem.Header = UiText.T("Settings.TabBackup");

        AddAppButton.Content = UiText.T("Settings.AddApp");
        ImportAppsButton.Content = UiText.T("Settings.ImportApps");
        CancelButton.Content = UiText.T("Common.Cancel");
        SaveButton.Content = UiText.T("Common.Save");

        HotkeySectionTitle.Text = UiText.T("Settings.HotkeySection");
        HotkeyUsageTitle.Text = UiText.T("Settings.HotkeyUsage");
        HotkeyTriggerHint.Text = UiText.T("Settings.HotkeyTrigger");

        GeneralTitle.Text = UiText.T("Settings.GeneralSection");
        AppearanceTitle.Text = UiText.T("Settings.Appearance");
        ThemeLight.Content = UiText.T("Settings.ThemeLight");
        ThemeDark.Content = UiText.T("Settings.ThemeDark");
        ThemeSystem.Content = UiText.T("Settings.ThemeSystem");
        ThemeHintText.Text = UiText.T("Settings.ThemePreviewHint");
        StartupTitle.Text = UiText.T("Settings.Startup");
        AutoStartCheckBox.Content = UiText.T("Settings.AutoStart");
        MaxResultsTitle.Text = UiText.T("Settings.MaxResultsLabel");
        PreviewTitle.Text = UiText.T("Settings.PreviewSection");
        PreviewDesc.Text = UiText.T("Settings.PreviewDesc");
        PreviewCheckBox.Content = UiText.T("Settings.PreviewEnable");
        DeepSeekTitle.Text = UiText.T("Settings.DeepSeekSection");
        DeepSeekDesc.Text = UiText.T("Settings.DeepSeekDesc");
        DeepSeekCheckBox.Content = UiText.T("Settings.DeepSeekEnable");
        DeepSeekHint.Text = UiText.T("Settings.DeepSeekHint");
        FileSearchTitle.Text = UiText.T("Settings.FileSearchSection");
        FileSearchDesc.Text = UiText.T("Settings.FileSearchDesc");
        FileSearchCheckBox.Content = UiText.T("Settings.FileSearchEnable");
        RebuildIndexButton.Content = UiText.T("Settings.RebuildIndex");
        ResetTitle.Text = UiText.T("Settings.ResetSection");
        ResetDesc.Text = UiText.T("Settings.ResetDesc");
        ResetButton.Content = UiText.T("Settings.ResetButton");

        // 截图
        ScreenshotSectionTitle.Text = UiText.T("Settings.ScreenshotSection");
        ScreenshotHotkeyTitle.Text = UiText.T("Settings.ScreenshotHotkey");
        ScreenshotHotkeyHint.Text = UiText.T("Settings.ScreenshotHotkeyHint");
        ScreenshotSmartCheckBox.Content = UiText.T("Settings.ScreenshotSmartSelect");
        ScreenshotDefaultToolTitle.Text = UiText.T("Settings.ScreenshotDefaultTool");
        ScreenshotColorFormatTitle.Text = UiText.T("Settings.ScreenshotColorFormat");
        ScreenshotMosaicTitle.Text = UiText.T("Settings.ScreenshotMosaicGranularity");
        ScreenshotSaveDirTitle.Text = UiText.T("Settings.ScreenshotSaveDir");
        ScreenshotSaveDirHint.Text = UiText.T("Settings.ScreenshotSaveDirHint");
        ScreenshotBrowseButton.Content = UiText.T("Settings.ScreenshotBrowse");
        ScreenshotTemplateTitle.Text = UiText.T("Settings.ScreenshotFileNameTemplate");
        ScreenshotTemplateHint.Text = UiText.T("Settings.ScreenshotFileNameTemplateHint");
        FillScreenshotCombos();

        FiltersTitle.Text = UiText.T("Settings.FiltersSection");
        FiltersDesc.Text = UiText.T("Settings.FiltersDesc");
        FilterSystemComponentsCheckBox.Content = UiText.T("Settings.FilterSystemComponents");
        FilterKeywordsTitle.Text = UiText.T("Settings.FilterKeywords");
        FilterPathsTitle.Text = UiText.T("Settings.FilterPaths");
        WhitelistTitle.Text = UiText.T("Settings.Whitelist");
        KeywordsAddButton.Content = UiText.T("Common.Add");
        KeywordsRemoveButton.Content = UiText.T("Common.Delete");
        PathsAddButton.Content = UiText.T("Common.Add");
        PathsRemoveButton.Content = UiText.T("Common.Delete");
        WhitelistAddButton.Content = UiText.T("Common.Add");
        WhitelistRemoveButton.Content = UiText.T("Common.Delete");

        BackupTitle.Text = UiText.T("Settings.BackupSection");
        BackupDesc.Text = UiText.T("Settings.BackupDesc");
        BackupNowButton.Content = UiText.T("Settings.BackupNow");
        ExportBackupButton.Content = UiText.T("Settings.ExportBackup");
        ImportBackupButton.Content = UiText.T("Settings.ImportBackup");
        RestoreLatestButton.Content = UiText.T("Settings.RestoreLatest");
        OpenDataDirButton.Content = UiText.T("Settings.OpenDataDir");
    }

    /// <summary>
    /// 所有控件按 _settings 回显（含主题单选，修"保存即覆盖成 dark"）
    /// </summary>
    private void ApplySettingsToUI()
    {
        _loading = true;
        try
        {
            // 热键
            HotkeyRecorder.Hotkey = _settings.Hotkey;
            HotkeyRecorder.RegisteredHotkey = _originalHotkey;

            // 截图
            ScreenshotHotkeyBox.Text = _settings.ScreenshotHotkey;
            ScreenshotSmartCheckBox.IsChecked = _settings.ScreenshotSmartSelect;
            ScreenshotSaveDirBox.Text = _settings.ScreenshotSaveDir;
            ScreenshotTemplateBox.Text = _settings.ScreenshotFileNameTemplate;
            ScreenshotMosaicSlider.Value = Math.Clamp(_settings.ScreenshotMosaicGranularity, 4, 48);
            FillScreenshotCombos();

            // 主题单选回显
            var theme = _settings.Theme?.Trim().ToLowerInvariant();
            ThemeLight.IsChecked = theme == "light";
            ThemeDark.IsChecked = theme == "dark";
            ThemeSystem.IsChecked = theme is not ("light" or "dark");

            // 结果条数
            MaxResultsSlider.Value = Math.Clamp(_settings.MaxResults, 5, 20);
            MaxResultsHint.Text = UiText.Format("Settings.MaxResultsHint", (int)MaxResultsSlider.Value);

            // 启动 / 文档预览 / DeepSeek 搜索 / 文件搜索 / 过滤开关
            AutoStartCheckBox.IsChecked = IsAutoStartEnabled();
            PreviewCheckBox.IsChecked = _settings.PreviewEnabled;
            DeepSeekCheckBox.IsChecked = _settings.DeepSeekSearchEnabled;
            FileSearchCheckBox.IsChecked = _settings.FileSearchEnabled;
            FilterSystemComponentsCheckBox.IsChecked = _settings.FilterSystemComponents;

            // 过滤规则列表
            RefreshFilterLists();
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshFilterLists()
    {
        KeywordsList.ItemsSource = null;
        KeywordsList.ItemsSource = _settings.FilterKeywords;
        PathsList.ItemsSource = null;
        PathsList.ItemsSource = _settings.FilterPaths;
        WhitelistItems.ItemsSource = null;
        WhitelistItems.ItemsSource = _settings.Whitelist;
    }

    #endregion

    #region 应用管理

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(query))
        {
            _filteredApps = new List<AppItem>(_allApps);
        }
        else
        {
            _filteredApps = _allApps
                .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            a.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            a.Aliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        AppsDataGrid.ItemsSource = _filteredApps;
        StatusText.Text = UiText.Format("Settings.StatusFiltered", _filteredApps.Count, _allApps.Count);
    }

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var addWindow = new AddAppWindow { Owner = this };
        if (addWindow.ShowDialog() == true && addWindow.Result != null)
        {
            await _repository.AddAsync(addWindow.Result);
            _allApps.Add(addWindow.Result);
            SearchBox_TextChanged(SearchBox, null!);
            _logger.Information("Added app: {Name}", addWindow.Result.Name);
        }
    }

    /// <summary>批量导入：复用首次向导的扫描/勾选/导入界面（开始菜单/注册表/UWP），导入后刷新列表与主面板</summary>
    private async void ImportApps_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wizard = new FirstRunWindow(
                _repository, new FilterService(_settings), _logger, _settings.Hotkey, importMode: true)
            {
                Owner = this
            };
            wizard.ShowDialog();

            // 刷新本页列表 + 通知主面板刷新应用池
            _allApps = await _repository.GetAllAsync();
            SearchBox_TextChanged(SearchBox, null!);
            _onAppsImported?.Invoke();
            _logger.Information("Import wizard closed");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Open import wizard failed");
        }
    }

    private async void EditApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not AppItem app)
        {
            return;
        }

        // 编辑窗口在草稿上工作：取消 = 对 app 零副作用；
        // 保存由编辑窗口异步完成（SavedToRepository），否则此处补存
        var editWindow = new EditAppWindow(app, _repository) { Owner = this };
        if (editWindow.ShowDialog() == true)
        {
            if (!editWindow.SavedToRepository)
            {
                await _repository.UpdateAsync(app);
            }

            AppsDataGrid.Items.Refresh();
            _logger.Information("Updated app: {Name}", app.Name);
        }
    }

    private async void DeleteApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not AppItem app)
        {
            return;
        }

        var result = MessageBox.Show(
            UiText.Format("Settings.DeleteConfirm", app.Name),
            UiText.T("Settings.DeleteConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _repository.DeleteAsync(app.Id);
            _allApps.Remove(app);
            SearchBox_TextChanged(SearchBox, null!);
            _logger.Information("Deleted app: {Name}", app.Name);
        }
    }

    #endregion

    #region 主题（即时预览 + 回显）

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var theme = GetSelectedTheme();
        _settings.Theme = theme;
        ApplyThemePreview(theme);
        _logger.Information("Theme preview: {Theme}", theme);
    }

    private string GetSelectedTheme()
    {
        if (ThemeLight.IsChecked == true)
        {
            return "light";
        }

        return ThemeDark.IsChecked == true ? "dark" : "system";
    }

    /// <summary>
    /// 立即应用主题（不等保存）：替换应用级主题字典
    /// </summary>
    private void ApplyThemePreview(string theme)
    {
        var resolved = string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase)
            ? GetSystemTheme()
            : theme;

        var uri = new Uri(
            string.Equals(resolved, "light", StringComparison.OrdinalIgnoreCase)
                ? "Themes/LightTheme.xaml"
                : "Themes/DarkTheme.xaml",
            UriKind.Relative);

        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString ?? string.Empty;
            if (source.Contains("Theme", StringComparison.OrdinalIgnoreCase))
            {
                merged[i] = new ResourceDictionary { Source = uri };
                return;
            }
        }

        merged.Insert(0, new ResourceDictionary { Source = uri });
    }

    /// <summary>
    /// 读取系统应用主题（AppsUseLightTheme）
    /// </summary>
    private static string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 1 ? "light" : "dark";
        }
        catch (Exception)
        {
            return "dark";
        }
    }

    #endregion

    #region MaxResults

    private void MaxResultsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || MaxResultsHint == null)
        {
            return;
        }

        var value = (int)MaxResultsSlider.Value;
        _settings.MaxResults = value;
        MaxResultsHint.Text = UiText.Format("Settings.MaxResultsHint", value);
    }

    #endregion

    #region 过滤规则

    private void KeywordsAdd_Click(object sender, RoutedEventArgs e)
    {
        AddFilterEntry(_settings.FilterKeywords, KeywordsInput, KeywordsList);
    }

    private void KeywordsRemove_Click(object sender, RoutedEventArgs e)
    {
        RemoveFilterEntry(_settings.FilterKeywords, KeywordsList);
    }

    private void PathsAdd_Click(object sender, RoutedEventArgs e)
    {
        AddFilterEntry(_settings.FilterPaths, PathsInput, PathsList);
    }

    private void PathsRemove_Click(object sender, RoutedEventArgs e)
    {
        RemoveFilterEntry(_settings.FilterPaths, PathsList);
    }

    private void WhitelistAdd_Click(object sender, RoutedEventArgs e)
    {
        AddFilterEntry(_settings.Whitelist, WhitelistInput, WhitelistItems);
    }

    private void WhitelistRemove_Click(object sender, RoutedEventArgs e)
    {
        RemoveFilterEntry(_settings.Whitelist, WhitelistItems);
    }

    /// <summary>
    /// 向过滤列表添加条目（去空、去重）
    /// </summary>
    private void AddFilterEntry(List<string> target, TextBox input, ListBox list)
    {
        var value = input.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            MessageBox.Show(UiText.T("Settings.FilterEmptyInput"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (target.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(UiText.T("Settings.FilterDuplicate"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        target.Add(value);
        input.Clear();
        list.ItemsSource = null;
        list.ItemsSource = target;
    }

    /// <summary>
    /// 从过滤列表删除所选条目
    /// </summary>
    private void RemoveFilterEntry(List<string> target, ListBox list)
    {
        if (list.SelectedItem is not string selected)
        {
            return;
        }

        target.Remove(selected);
        list.ItemsSource = null;
        list.ItemsSource = target;
    }

    #endregion

    #region 备份与恢复

    private BackupService GetBackupService()
    {
        return _backupService ??= new BackupService(GetDataDirectory(), _logger);
    }

    /// <summary>
    /// 数据目录（优先 JsonRepository.DataDirectory，缺失时回退 %APPDATA%\QuickPalette）
    /// </summary>
    private string GetDataDirectory()
    {
        var dir = _repository.DataDirectory;
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPalette")
            : dir;
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await GetBackupService().CreateBackupAsync();
            var backupsDir = Path.Combine(GetDataDirectory(), "backups");
            BackupStatusText.Text = UiText.Format("Settings.BackupOk", backupsDir);
            MessageBox.Show(UiText.Format("Settings.BackupOk", backupsDir),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Manual backup failed");
            BackupStatusText.Text = UiText.Format("Settings.BackupFailed", ex.Message);
            MessageBox.Show(UiText.Format("Settings.BackupFailed", ex.Message),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                FileName = "quickpalette-backup.json",
                Filter = UiText.T("Settings.ExportFilter"),
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await GetBackupService().ExportAsync(dialog.FileName);
            BackupStatusText.Text = UiText.Format("Settings.ExportOk", dialog.FileName);
            MessageBox.Show(UiText.Format("Settings.ExportOk", dialog.FileName),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Export backup failed");
            BackupStatusText.Text = UiText.Format("Settings.ExportFailed", ex.Message);
            MessageBox.Show(UiText.Format("Settings.ExportFailed", ex.Message),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = UiText.T("Settings.ExportFilter"),
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            // 导入前提示：将覆盖当前数据；BackupService.ImportAsync 内部会自动备份
            var confirm = MessageBox.Show(
                UiText.T("Settings.ImportConfirm"),
                UiText.T("Settings.ImportConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            await GetBackupService().ImportAsync(dialog.FileName);
            BackupStatusText.Text = UiText.T("Settings.ImportOk");
            MessageBox.Show(UiText.T("Settings.ImportOk"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);

            // 导入后重载设置与列表
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Import backup failed");
            BackupStatusText.Text = UiText.Format("Settings.ImportFailed", ex.Message);
            MessageBox.Show(UiText.Format("Settings.ImportFailed", ex.Message),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestoreLatest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var latest = await GetBackupService().GetLatestBackupAsync();
            if (string.IsNullOrWhiteSpace(latest))
            {
                MessageBox.Show(UiText.T("Settings.RestoreNoBackup"),
                    UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 二次确认（展示备份文件路径）
            var confirm = MessageBox.Show(
                UiText.Format("Settings.RestoreConfirm", latest),
                UiText.T("Settings.RestoreConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            await GetBackupService().RestoreFromBackupAsync(latest);
            BackupStatusText.Text = UiText.T("Settings.RestoreOk");
            MessageBox.Show(UiText.T("Settings.RestoreOk"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);

            // 恢复后重载设置与列表
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Restore from latest backup failed");
            BackupStatusText.Text = UiText.Format("Settings.RestoreFailed", ex.Message);
            MessageBox.Show(UiText.Format("Settings.RestoreFailed", ex.Message),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = GetDataDirectory();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\""));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Open data directory failed");
            MessageBox.Show(ex.Message, UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region 文件搜索

    private void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_fileSearchManager == null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            UiText.T("Settings.RebuildIndexConfirm"),
            UiText.T("Settings.RebuildIndexTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _fileSearchManager.RequestRebuild();
            UpdateFileSearchStatus();
        }
    }

    /// <summary>
    /// 刷新文件搜索状态文字
    /// </summary>
    private void UpdateFileSearchStatus()
    {
        if (FileSearchStatusText == null)
        {
            return;
        }

        if (_fileSearchManager == null)
        {
            FileSearchStatusText.Text = UiText.T("Settings.FileSearchUnavailable");
            RebuildIndexButton.IsEnabled = false;
            return;
        }

        var status = _fileSearchManager.Status;
        var text = status switch
        {
            FileIndexStatus.Disabled => UiText.T("Settings.FileSearchDisabled"),
            FileIndexStatus.Starting => UiText.T("Settings.FileSearchStarting"),
            FileIndexStatus.Ready => UiText.Format("Settings.FileSearchReady", _fileSearchManager.IndexedCount),
            FileIndexStatus.NeedsElevation => UiText.T("Settings.FileSearchElevation"),
            FileIndexStatus.NoIndex => UiText.T("Settings.FileSearchNoIndex"),
            FileIndexStatus.Error => UiText.T("Settings.FileSearchError"),
            _ => string.Empty
        };
        FileSearchStatusText.Text = text;
        RebuildIndexButton.IsEnabled = status is FileIndexStatus.Ready or FileIndexStatus.Error;
    }

    #endregion

    #region 通用设置

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            UiText.T("Settings.ResetConfirm"),
            UiText.T("Settings.ResetConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        // 完整重置：设置对象 + 所有控件 + 主题预览 + 热键注册，全部回到默认值
        _settings = new Settings();
        ApplySettingsToUI();
        ApplyThemePreview(_settings.Theme);

        if (_hotkeyService.RegisterHotkey(_settings.Hotkey, _onHotkeyPressed))
        {
            _registrationChanged = true;
            HotkeyRecorder.RegisteredHotkey = _settings.Hotkey;
        }

        _logger.Information("Settings reset to defaults");
        MessageBox.Show(UiText.Format("Settings.ResetDone", _settings.Hotkey),
            UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 读取注册表判断当前是否已启用开机自启
    /// </summary>
    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch (Exception ex)
        {
            // 读取失败按未启用处理
            Debug.WriteLine(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 写入/删除注册表 Run 键，实现开机自启
    /// </summary>
    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    _logger.Warning("Cannot enable autostart: exe path unavailable");
                    return;
                }

                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }

            _logger.Information("AutoStart {State}", enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update autostart");
            MessageBox.Show(ex.Message, UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region 窗口操作

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 保存：收集所有控件状态
            _settings.AutoStart = AutoStartCheckBox.IsChecked == true;
            _settings.FileSearchEnabled = FileSearchCheckBox.IsChecked == true;
            _settings.PreviewEnabled = PreviewCheckBox.IsChecked == true;
            _settings.DeepSeekSearchEnabled = DeepSeekCheckBox.IsChecked == true;
            _settings.MaxResults = (int)MaxResultsSlider.Value;
            _settings.FilterSystemComponents = FilterSystemComponentsCheckBox.IsChecked == true;
            _settings.Hotkey = HotkeyRecorder.Hotkey;
            _settings.Theme = GetSelectedTheme();

            // 截图
            _settings.ScreenshotHotkey = string.IsNullOrWhiteSpace(ScreenshotHotkeyBox.Text)
                ? "Alt+D"
                : ScreenshotHotkeyBox.Text.Trim();
            _settings.ScreenshotSmartSelect = ScreenshotSmartCheckBox.IsChecked == true;
            _settings.ScreenshotSaveDir = ScreenshotSaveDirBox.Text.Trim();
            _settings.ScreenshotFileNameTemplate = string.IsNullOrWhiteSpace(ScreenshotTemplateBox.Text)
                ? QuickPalette.Screenshot.Output.FileNameTemplate.DefaultTemplate
                : ScreenshotTemplateBox.Text.Trim();
            _settings.ScreenshotDefaultTool = (ScreenshotToolBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "rect";
            _settings.ScreenshotColorFormat = (ScreenshotFormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "hex";
            _settings.ScreenshotMosaicGranularity = (int)ScreenshotMosaicSlider.Value;

            // 热键一致性：保存时统一注册 _settings.Hotkey；
            // 注册失败则中止保存（避免运行注册与 settings.json 不一致）
            if (!_hotkeyService.RegisterHotkey(_settings.Hotkey, _onHotkeyPressed))
            {
                MessageBox.Show(UiText.T("Hotkey.RegisterFailed"),
                    UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _registrationChanged = true;
            HotkeyRecorder.RegisteredHotkey = _settings.Hotkey;

            await _repository.SaveAsync(_settings);

            // 已持久化，关闭时无需回滚
            _registrationChanged = false;
            _saved = true;

            // 应用开机自启（写入/删除注册表 Run 键）
            ApplyAutoStart(_settings.AutoStart);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save settings");
            MessageBox.Show(UiText.Format("Settings.SaveFailed", ex.Message),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>重建截图下拉项（保持当前选择；文案随语言）</summary>
    private void FillScreenshotCombos()
    {
        var tools = new (string Value, string Key)[]
        {
            ("rect", "Settings.Screenshot.Tool.Rectangle"),
            ("ellipse", "Settings.Screenshot.Tool.Ellipse"),
            ("arrow", "Settings.Screenshot.Tool.Arrow"),
            ("brush", "Settings.Screenshot.Tool.Brush"),
            ("highlight", "Settings.Screenshot.Tool.Highlight"),
            ("text", "Settings.Screenshot.Tool.Text"),
            ("mosaic", "Settings.Screenshot.Tool.Mosaic")
        };
        var currentTool = (ScreenshotToolBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.ScreenshotDefaultTool;
        ScreenshotToolBox.Items.Clear();
        foreach (var (value, key) in tools)
        {
            var item = new ComboBoxItem { Content = UiText.T(key), Tag = value };
            ScreenshotToolBox.Items.Add(item);
            if (value == currentTool)
            {
                item.IsSelected = true;
            }
        }

        var formats = new (string Value, string Label)[]
        {
            ("hex", "HEX  #FF8800"),
            ("rgb", "RGB  rgb(255, 136, 0)"),
            ("hsl", "HSL  hsl(32, 100%, 50%)")
        };
        var currentFormat = (ScreenshotFormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.ScreenshotColorFormat;
        ScreenshotFormatBox.Items.Clear();
        foreach (var (value, label) in formats)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            ScreenshotFormatBox.Items.Add(item);
            if (value == currentFormat)
            {
                item.IsSelected = true;
            }
        }
    }

    /// <summary>浏览截图保存目录（系统文件夹选择对话框）</summary>
    private void ScreenshotBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = UiText.T("Settings.ScreenshotSaveDir")
            };
            if (dialog.ShowDialog(this) == true)
            {
                ScreenshotSaveDirBox.Text = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Browse screenshot folder failed");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// 窗口关闭：停掉 DispatcherTimer（修窗口泄漏）；
    /// 未保存时恢复原主题预览，若注册已改动则恢复原热键注册
    /// </summary>
    private void OnWindowClosed()
    {
        _fileStatusTimer.Stop();

        if (_saved)
        {
            return;
        }

        // 主题预览回滚
        ApplyThemePreview(_originalTheme);

        // 热键注册回滚（仅当会话中改过注册才需要）
        if (_registrationChanged)
        {
            if (_hotkeyService.RegisterHotkey(_originalHotkey, _onHotkeyPressed))
            {
                _logger.Information("Hotkey registration restored to: {Hotkey}", _originalHotkey);
            }
            else
            {
                _logger.Warning("Failed to restore original hotkey: {Hotkey}", _originalHotkey);
            }

            _registrationChanged = false;
        }
    }

    #endregion
}
