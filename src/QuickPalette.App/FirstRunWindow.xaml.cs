namespace QuickPalette.App;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickPalette.App.Services;
using QuickPalette.Domain.Contracts;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.Scanners;
using QuickPalette.Infrastructure.Storage;
using Serilog;

/// <summary>
/// 首次运行引导窗口：
/// - 用 ScannerManager.ScanAllAsync(settings, ct) 扫描（覆盖 uwp/process 来源），可取消；
/// - 候选支持"不再推荐 / 隐藏此类"（HiddenItemStore 持久化，防复活）、搜索过滤、勾选计数实时刷新；
/// - 导入时补拼音，UWP 项填 Aumid/Type=Uwp；
/// - 完成/跳过时写 firstrun.done 标记到数据目录。
/// </summary>
public partial class FirstRunWindow : Window
{
    /// <summary>App 侧约定的完成标记文件名</summary>
    private const string FirstRunMarkerFileName = "firstrun.done";

    private readonly JsonRepository _repository;
    private readonly FilterService _filterService;
    private readonly ILogger _logger;
    private readonly string _hotkey;

    private List<CandidateDisplayItem> _allCandidates = new();
    private HiddenItemStore? _hiddenItemStore;
    private CancellationTokenSource? _scanCts;
    private bool _scanning;

    public FirstRunWindow(JsonRepository repository, FilterService filterService, ILogger logger, string hotkey)
    {
        InitializeComponent();

        _repository = repository;
        _filterService = filterService;
        _logger = logger;
        _hotkey = hotkey;

        ApplyUiText();

        // 显示实际热键
        HotkeyRun.Text = hotkey;

        // 开始扫描
        Loaded += async (s, e) => await ScanCandidatesAsync();
    }

    /// <summary>
    /// 界面文案本地化
    /// </summary>
    private void ApplyUiText()
    {
        Title = UiText.T("FirstRun.Title");
        WelcomeTitle.Text = UiText.T("FirstRun.Welcome");
        ScanStatusText.Text = UiText.T("FirstRun.Scanning");
        ScanToggleButton.Content = UiText.T("FirstRun.CancelScan");
        SelectAllCheckBox.Content = UiText.T("FirstRun.SelectAll");
        ImportButton.Content = UiText.T("FirstRun.Import");
        SkipButton.Content = UiText.T("FirstRun.Skip");
        DoneButton.Content = UiText.T("FirstRun.Done");

        DoneTitle.Text = UiText.T("FirstRun.ImportedTitle");
        DoneSubtitle.Text = UiText.T("FirstRun.DoneSubtitle");
        HowToOpenTitle.Text = UiText.T("FirstRun.HowToOpen");
        HowToOpenRun1.Text = UiText.T("FirstRun.HowToOpenBody1");
        HowToOpenRun2.Text = UiText.T("FirstRun.HowToOpenBody2");
        HowToOpenRun3.Text = UiText.T("FirstRun.HowToOpenBody3");
        FeaturesTitle.Text = UiText.T("FirstRun.Features");
        Feature1Text.Text = UiText.T("FirstRun.Feature1");
        Feature2Text.Text = UiText.T("FirstRun.Feature2");
        Feature3Text.Text = UiText.T("FirstRun.Feature3");
        Feature4Text.Text = UiText.T("FirstRun.Feature4");
        Feature5Text.Text = UiText.T("FirstRun.Feature5");

        UpdateCountText();
    }

    #region 扫描

    private async Task ScanCandidatesAsync()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        SetScanningState(true);
        ScanStatusText.Text = UiText.T("FirstRun.Scanning");

        try
        {
            // 按设置过滤来源（含 uwp / process），统一扫描 + 过滤 + 去重
            var settings = await _repository.GetAsync();
            UiText.SetLocale(settings.Locale);
            ApplyUiText();

            var manager = new ScannerManager(_filterService, _logger);
            var rawApps = await manager.ScanAllAsync(settings, ct);
            ct.ThrowIfCancellationRequested();

            // 过滤"不再推荐 / 隐藏此类"的记录（hidden.json 防复活）
            var hidden = GetHiddenItemStore();
            _allCandidates = rawApps
                .Where(r => !hidden.IsHidden(MakeHiddenKey(r)))
                .Select(r => new CandidateDisplayItem
                {
                    Name = r.Name,
                    Path = r.Path,
                    Source = r.SourceId,
                    Aumid = r.Aumid ?? string.Empty,
                    Type = r.Type,
                    HiddenKey = MakeHiddenKey(r),
                    KindKey = (r.Name ?? string.Empty).Trim().ToLowerInvariant(),
                    IsSelected = true // 默认全选
                })
                .OrderBy(c => c.Name)
                .ToList();

            CandidatesList.ItemsSource = _allCandidates;
            ScanStatusText.Text = UiText.Format("FirstRun.ScanDone", _allCandidates.Count);
            UpdateCountText();

            _logger.Information("Found {Count} candidates", _allCandidates.Count);
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = UiText.T("FirstRun.ScanCanceled");
            _logger.Information("Candidate scan canceled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to scan candidates");
            ScanStatusText.Text = UiText.T("FirstRun.ScanFailed");
            MessageBox.Show(UiText.T("FirstRun.ScanFailed"),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetScanningState(false);
        }
    }

    /// <summary>
    /// 扫描中显示进度、可取消；空闲时该按钮变为"重新扫描"
    /// </summary>
    private void SetScanningState(bool scanning)
    {
        _scanning = scanning;
        ScanProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanToggleButton.Content = scanning
            ? UiText.T("FirstRun.CancelScan")
            : UiText.T("FirstRun.Rescan");
        ImportButton.IsEnabled = !scanning;
    }

    private async void ScanToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_scanning)
        {
            _scanCts?.Cancel();
            return;
        }

        await ScanCandidatesAsync();
    }

    #endregion

    #region 候选交互

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(query))
        {
            CandidatesList.ItemsSource = _allCandidates;
        }
        else
        {
            CandidatesList.ItemsSource = _allCandidates
                .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            c.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var isChecked = SelectAllCheckBox.IsChecked == true;
        foreach (var item in _allCandidates)
        {
            item.IsSelected = isChecked;
        }

        CandidatesList.Items.Refresh();
        UpdateCountText();
    }

    /// <summary>
    /// 勾选状态变化 → 计数实时刷新
    /// </summary>
    private void CandidateCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateCountText();
    }

    /// <summary>
    /// 不再推荐：仅隐藏当前项（写入 hidden.json，防复活）
    /// </summary>
    private void NeverRecommend_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CandidateDisplayItem item)
        {
            return;
        }

        GetHiddenItemStore().Hide(item.HiddenKey);
        _allCandidates.Remove(item);
        RefreshCandidateView();
        _logger.Information("Candidate hidden: {Name}", item.Name);
    }

    /// <summary>
    /// 隐藏此类：隐藏同名的全部候选项（如同一软件的多个快捷方式）
    /// </summary>
    private void HideKind_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CandidateDisplayItem item)
        {
            return;
        }

        var hidden = GetHiddenItemStore();
        var sameKind = _allCandidates
            .Where(c => string.Equals(c.KindKey, item.KindKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var candidate in sameKind)
        {
            hidden.Hide(candidate.HiddenKey);
        }

        _allCandidates.RemoveAll(c => sameKind.Contains(c));
        RefreshCandidateView();
        _logger.Information("Candidate kind hidden: {Name} ({Count} items)", item.Name, sameKind.Count);
    }

    private void RefreshCandidateView()
    {
        CandidatesList.ItemsSource = null;
        CandidatesList.ItemsSource = _allCandidates;
        UpdateCountText();
    }

    private void UpdateCountText()
    {
        var count = _allCandidates.Count(c => c.IsSelected);
        CountText.Text = UiText.Format("FirstRun.Count", count);
    }

    #endregion

    #region 导入 / 完成

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = _allCandidates.Where(c => c.IsSelected).ToList();

        if (selectedItems.Count == 0)
        {
            MessageBox.Show(UiText.T("FirstRun.MsgSelectOne"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 禁用按钮防止重复点击
        ImportButton.IsEnabled = false;
        ImportButton.Content = UiText.T("FirstRun.Importing");

        try
        {
            foreach (var item in selectedItems)
            {
                var app = BuildAppItem(item);

                // 补全拼音（搜索需要）
                PinyinProvider.Instance.EnsurePinyin(app);

                await _repository.AddAsync(app);
            }

            _logger.Information("Imported {Count} apps", selectedItems.Count);

            // 切换到底部"完成"按钮（隐藏跳过，导入完成后再给"跳过"按钮不合逻辑）
            ImportButton.Visibility = Visibility.Collapsed;
            SkipButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Visible;

            // 切换到完成/指引页
            CandidatePanel.Visibility = Visibility.Collapsed;
            DonePanel.Visibility = Visibility.Visible;
            DoneTitle.Text = UiText.Format("FirstRun.ImportedTitle", selectedItems.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to import apps");
            MessageBox.Show(UiText.Format("FirstRun.ImportFailed", ex.Message),
                UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportButton.IsEnabled = true;
            ImportButton.Content = UiText.T("FirstRun.Import");
        }
    }

    /// <summary>
    /// 候选项 → AppItem（UWP 项填 Aumid/Type=Uwp）
    /// </summary>
    private static AppItem BuildAppItem(CandidateDisplayItem item)
    {
        var isUwp = item.Type == AppType.Uwp;
        return new AppItem
        {
            Name = item.Name,
            Path = item.Path,
            Aumid = isUwp ? item.Aumid : string.Empty,
            Type = isUwp ? AppType.Uwp : DetermineAppType(item.Path),
            Source = item.Source switch
            {
                "StartMenu" => AppSource.StartMenu,
                "Registry" => AppSource.Registry,
                "Uwp" => AppSource.Uwp,
                "Process" => AppSource.Process,
                _ => AppSource.Import
            },
            Enabled = true
        };
    }

    /// <summary>
    /// 按扩展名推断应用类型（扫描来源都是系统登记的应用，默认按可执行文件处理）
    /// </summary>
    private static AppType DetermineAppType(string path)
    {
        var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".lnk" => AppType.Lnk,
            ".url" => AppType.Url,
            ".cmd" or ".bat" => AppType.Command,
            _ => AppType.Exe
        };
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        MarkFirstRunDone();
        DialogResult = false;
        Close();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        MarkFirstRunDone();
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 写 firstrun.done 标记（App 侧据此判断是否需要再看向导）
    /// </summary>
    private void MarkFirstRunDone()
    {
        try
        {
            var dir = GetDataDirectory();
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, FirstRunMarkerFileName),
                DateTime.Now.ToString("O"));
            _logger.Information("First-run marker written: {Dir}", dir);
        }
        catch (Exception ex)
        {
            // 标记写失败不阻塞用户流程
            _logger.Warning(ex, "Failed to write firstrun.done marker");
        }
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

    private HiddenItemStore GetHiddenItemStore()
    {
        return _hiddenItemStore ??= new HiddenItemStore(GetDataDirectory(), _logger);
    }

    /// <summary>
    /// 隐藏键：(Path ?? Aumid ?? Name).Trim().ToLowerInvariant()
    /// </summary>
    private static string MakeHiddenKey(RawApp raw)
    {
        var primary = !string.IsNullOrWhiteSpace(raw.Path) ? raw.Path
            : !string.IsNullOrWhiteSpace(raw.Aumid) ? raw.Aumid
            : raw.Name;
        return (primary ?? string.Empty).Trim().ToLowerInvariant();
    }

    #endregion
}

/// <summary>
/// 候选项显示类
/// </summary>
public class CandidateDisplayItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    /// <summary>UWP 应用用户模型 ID</summary>
    public string Aumid { get; set; } = string.Empty;

    /// <summary>扫描来源给出的应用类型（UWP 项为 AppType.Uwp）</summary>
    public AppType Type { get; set; } = AppType.Exe;

    /// <summary>hidden.json 的隐藏键（防复活）</summary>
    public string HiddenKey { get; set; } = string.Empty;

    /// <summary>"隐藏此类"的同类键（按名称归类）</summary>
    public string KindKey { get; set; } = string.Empty;

    public bool IsSelected { get; set; }
}
