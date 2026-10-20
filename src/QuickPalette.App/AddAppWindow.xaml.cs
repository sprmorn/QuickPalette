namespace QuickPalette.App;

using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using QuickPalette.App.Services;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;

/// <summary>
/// 添加应用窗口（支持拖拽 / 浏览 / 预填，供主面板拖拽与"运行中捕获"流程复用）
/// </summary>
public partial class AddAppWindow : Window
{
    /// <summary>允许拖入/浏览的文件扩展名</summary>
    private static readonly string[] SupportedExtensions = { ".exe", ".lnk", ".url", ".cmd", ".bat" };

    /// <summary>类型下拉选项（仅用于显示，取值为 AppType 枚举名）</summary>
    private static readonly AppType[] TypeChoices =
    {
        AppType.Exe, AppType.Lnk, AppType.Url, AppType.Folder, AppType.Command, AppType.Uwp
    };

    private readonly AppItem? _prefill;

    /// <summary>保存成功后的结果项</summary>
    public AppItem? Result { get; private set; }

    /// <summary>
    /// 构造函数；prefill 用于拖拽入面板 / 捕获前台窗口等场景预填路径、名称、类型
    /// </summary>
    public AddAppWindow(AppItem? prefill = null)
    {
        InitializeComponent();

        _prefill = prefill;

        ApplyUiText();

        TypeBox.ItemsSource = TypeChoices;
        TypeBox.SelectedItem = AppType.Exe;

        if (prefill != null)
        {
            PathBox.Text = prefill.Path;
            NameBox.Text = string.IsNullOrWhiteSpace(prefill.Name)
                ? Path.GetFileNameWithoutExtension(prefill.Path ?? string.Empty)
                : prefill.Name;
            if (TypeChoices.Contains(prefill.Type))
            {
                TypeBox.SelectedItem = prefill.Type;
            }

            ArgsBox.Text = prefill.Args;
            WorkDirBox.Text = prefill.WorkDir;
            AdminCheckBox.IsChecked = prefill.RunAsAdmin;
        }
    }

    /// <summary>
    /// 界面文案本地化（轻量：按钮 / 标题 / 提示）
    /// </summary>
    private void ApplyUiText()
    {
        Title = UiText.T("AddApp.Title");
        TitleText.Text = UiText.T("AddApp.Title");
        NameLabel.Text = UiText.T("AddApp.Name");
        PathLabel.Text = UiText.T("AddApp.Path");
        PathHint.Text = UiText.T("AddApp.PathHint");
        TypeLabel.Text = UiText.T("AddApp.Type");
        ArgsLabel.Text = UiText.T("AddApp.Args");
        WorkDirLabel.Text = UiText.T("AddApp.WorkDir");
        AliasesLabel.Text = UiText.T("AddApp.Aliases");
        AdminCheckBox.Content = UiText.T("AddApp.Admin");
        BrowseButton.Content = UiText.T("Common.Browse");
        CancelButton.Content = UiText.T("Common.Cancel");
        AddButton.Content = UiText.T("AddApp.Add");
    }

    #region 拖拽 / 浏览

    private void PathBox_DragOver(object sender, DragEventArgs e)
    {
        // 只有携带文件拖入时才显示"可放"
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PathBox_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        var filePath = files[0];
        if (!File.Exists(filePath))
        {
            MessageBox.Show(UiText.Format("AddApp.MsgPathMissing", filePath),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext))
        {
            MessageBox.Show(UiText.T("AddApp.MsgUnsupportedFile"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyPath(filePath);
        e.Handled = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = UiText.T("AddApp.OpenFilter"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApplyPath(dialog.FileName);
        }
    }

    /// <summary>
    /// 填充路径并自动推断名称与类型
    /// </summary>
    private void ApplyPath(string filePath)
    {
        PathBox.Text = filePath;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = Path.GetFileNameWithoutExtension(filePath);
        }

        TypeBox.SelectedItem = DetermineAppType(filePath);
    }

    #endregion

    #region 保存

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // 校验必填项：名称
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(UiText.T("AddApp.MsgNameRequired"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        // 校验必填项：路径
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(UiText.T("AddApp.MsgPathRequired"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            PathBox.Focus();
            return;
        }

        var type = TypeBox.SelectedItem is AppType selected ? selected : DetermineAppType(path);

        // 按类型校验路径/内容合法性
        switch (type)
        {
            case AppType.Exe:
            case AppType.Lnk:
                if (!File.Exists(path))
                {
                    MessageBox.Show(UiText.Format("AddApp.MsgPathMissing", path),
                        UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    PathBox.Focus();
                    return;
                }

                break;

            case AppType.Folder:
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(UiText.Format("AddApp.MsgPathMissing", path),
                        UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    PathBox.Focus();
                    return;
                }

                break;

            case AppType.Url:
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp))
                {
                    MessageBox.Show(UiText.T("AddApp.MsgBadUrl"),
                        UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    PathBox.Focus();
                    return;
                }

                break;

            case AppType.Uwp:
                // UWP 项允许只填 AUMID（来自捕获/预填），此时路径可为空
                if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(_prefill?.Aumid))
                {
                    MessageBox.Show(UiText.T("AddApp.MsgPathRequired"),
                        UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    PathBox.Focus();
                    return;
                }

                break;

            default:
                // Command：路径是命令行文本，要求非空（上面已校验）
                break;
        }

        // 解析别名
        var aliases = new List<string>();
        if (!string.IsNullOrWhiteSpace(AliasesBox.Text))
        {
            aliases = AliasesBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => !string.IsNullOrEmpty(a))
                .ToList();
        }

        // 创建 AppItem（不改动传入的 prefill 对象）
        var item = new AppItem
        {
            Name = NameBox.Text.Trim(),
            Path = path,
            Args = ArgsBox.Text.Trim(),
            WorkDir = WorkDirBox.Text.Trim(),
            Aliases = aliases,
            Type = type,
            Aumid = type == AppType.Uwp ? _prefill?.Aumid ?? string.Empty : string.Empty,
            Source = _prefill is { Source: not AppSource.Manual } ? _prefill.Source : AppSource.Manual,
            RunAsAdmin = AdminCheckBox.IsChecked == true,
            Enabled = true
        };

        // 补全拼音（搜索需要）
        PinyinProvider.Instance.EnsurePinyin(item);

        Result = item;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 应用类型推断：按扩展名识别；未知扩展名不盲目判 Exe ——
    /// 目录判 Folder、网址判 Url、其余兜底 Command
    /// </summary>
    private static AppType DetermineAppType(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AppType.Exe;
        }

        if (Directory.Exists(path))
        {
            return AppType.Folder;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".exe":
            case ".com":
                return AppType.Exe;
            case ".lnk":
                return AppType.Lnk;
            case ".url":
                return AppType.Url;
            case ".cmd":
            case ".bat":
                return AppType.Command;
        }

        // 内容判断：形如 URL 的按 Url 处理
        if (path.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            return AppType.Url;
        }

        // 未知扩展名兜底 Command（而非盲目 Exe）
        return AppType.Command;
    }

    #endregion
}
