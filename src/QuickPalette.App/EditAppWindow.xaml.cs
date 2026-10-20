namespace QuickPalette.App;

using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using QuickPalette.App.Services;
using QuickPalette.Domain.Models;
using QuickPalette.Domain.Services;
using QuickPalette.Infrastructure.Storage;

/// <summary>
/// 编辑应用窗口（XAML 化，使用主题资源）。
/// 编辑过程在草稿副本上进行：点"取消"对传入的 AppItem 零副作用；
/// 点"保存"校验通过后才回写并（可选）异步持久化，不阻塞 UI。
/// </summary>
public partial class EditAppWindow : Window
{
    private readonly AppItem _app;
    private readonly AppItem _draft;
    private readonly JsonRepository? _repository;

    /// <summary>保存成功后的编辑结果（与传入对象内容一致，供调用方选用）</summary>
    public AppItem? Result { get; private set; }

    /// <summary>是否已由本窗口完成持久化（调用方无需再保存）</summary>
    public bool SavedToRepository { get; private set; }

    /// <summary>
    /// 构造函数；repository 传入时保存由本窗口异步完成，否则由调用方在 ShowDialog 返回后保存
    /// （保持旧签名 EditAppWindow(AppItem) 兼容）
    /// </summary>
    public EditAppWindow(AppItem app, JsonRepository? repository = null)
    {
        InitializeComponent();

        _app = app;
        _repository = repository;
        _draft = CloneItem(app);

        ApplyUiText();
        LoadDraft();
    }

    /// <summary>
    /// 界面文案本地化
    /// </summary>
    private void ApplyUiText()
    {
        Title = UiText.T("Edit.Title");
        TitleText.Text = UiText.T("Edit.Title");
        NameLabel.Text = UiText.T("Edit.Name");
        PathLabel.Text = UiText.T("Edit.Path");
        ArgsLabel.Text = UiText.T("Edit.Args");
        WorkDirLabel.Text = UiText.T("Edit.WorkDir");
        AliasesLabel.Text = UiText.T("Edit.Aliases");
        TagsLabel.Text = UiText.T("Edit.Tags");
        IconLabel.Text = UiText.T("Edit.Icon");
        AdminCheckBox.Content = UiText.T("Edit.Admin");
        EnabledCheckBox.Content = UiText.T("Edit.Enabled");
        BrowseButton.Content = UiText.T("Common.Browse");
        IconBrowseButton.Content = UiText.T("Common.Browse");
        CancelButton.Content = UiText.T("Common.Cancel");
        SaveButton.Content = UiText.T("Common.Save");
    }

    private void LoadDraft()
    {
        NameBox.Text = _draft.Name;
        PathBox.Text = _draft.Path;
        ArgsBox.Text = _draft.Args;
        WorkDirBox.Text = _draft.WorkDir;
        AliasesBox.Text = string.Join(", ", _draft.Aliases);
        TagsBox.Text = string.Join(", ", _draft.Tags);
        IconBox.Text = _draft.IconPath;
        AdminCheckBox.IsChecked = _draft.RunAsAdmin;
        EnabledCheckBox.IsChecked = _draft.Enabled;
    }

    #region 浏览

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = UiText.T("AddApp.OpenFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private void IconBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = UiText.T("Edit.IconFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            IconBox.Text = dialog.FileName;
        }
    }

    #endregion

    #region 保存 / 取消

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 取消 = 零副作用：草稿直接丢弃，不回写 _app
        DialogResult = false;
        Close();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(UiText.T("Edit.MsgNameRequired"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(PathBox.Text))
        {
            MessageBox.Show(UiText.T("Edit.MsgPathRequired"),
                UiText.T("Common.Hint"), MessageBoxButton.OK, MessageBoxImage.Warning);
            PathBox.Focus();
            return;
        }

        // 路径存在性校验：文件类路径不存在时提示确认（允许先保存、稍后修复）
        var path = PathBox.Text.Trim();
        if ((_draft.Type is AppType.Exe or AppType.Lnk or AppType.Folder)
            && !File.Exists(path) && !Directory.Exists(path))
        {
            var confirm = MessageBox.Show(
                UiText.Format("Edit.MsgPathMissing", path),
                UiText.T("Edit.MsgPathMissingTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                PathBox.Focus();
                return;
            }
        }

        // 校验通过 → 草稿回写到编辑结果
        _draft.Name = NameBox.Text.Trim();
        _draft.Path = path;
        _draft.Args = ArgsBox.Text.Trim();
        _draft.WorkDir = WorkDirBox.Text.Trim();
        _draft.Aliases = SplitList(AliasesBox.Text);
        _draft.Tags = SplitList(TagsBox.Text);
        _draft.IconPath = IconBox.Text.Trim();
        _draft.RunAsAdmin = AdminCheckBox.IsChecked == true;
        _draft.Enabled = EnabledCheckBox.IsChecked == true;
        _draft.UpdatedAt = DateTime.UtcNow;

        // 名称/别名变化后补全拼音
        PinyinProvider.Instance.EnsurePinyin(_draft);

        // 此刻才污染传入对象（保存语义），调用方旧式 UpdateAsync(app) 依旧有效
        CopyItem(_draft, _app);
        Result = CloneItem(_draft);

        // 可选：由本窗口异步持久化，避免调用方在 UI 线程同步等待
        if (_repository != null)
        {
            SaveButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            try
            {
                await _repository.UpdateAsync(_app);
                SavedToRepository = true;
            }
            catch (Exception ex)
            {
                SaveButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                MessageBox.Show(UiText.Format("Settings.SaveFailed", ex.Message),
                    UiText.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private static List<string> SplitList(string text)
    {
        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim())
            .Where(a => !string.IsNullOrEmpty(a))
            .ToList();
    }

    /// <summary>
    /// 深拷贝 AppItem（编辑草稿，避免污染原对象）
    /// </summary>
    private static AppItem CloneItem(AppItem source)
    {
        var copy = new AppItem();
        CopyItem(source, copy);
        return copy;
    }

    /// <summary>
    /// 字段级拷贝（含列表），用于草稿回写
    /// </summary>
    private static void CopyItem(AppItem from, AppItem to)
    {
        to.Id = from.Id;
        to.Name = from.Name;
        to.DisplayName = from.DisplayName;
        to.Aliases = new List<string>(from.Aliases);
        to.Pinyin = from.Pinyin;
        to.PinyinInitials = from.PinyinInitials;
        to.Type = from.Type;
        to.Path = from.Path;
        to.Args = from.Args;
        to.WorkDir = from.WorkDir;
        to.Aumid = from.Aumid;
        to.IconPath = from.IconPath;
        to.Source = from.Source;
        to.Tags = new List<string>(from.Tags);
        to.Favorite = from.Favorite;
        to.LaunchCount = from.LaunchCount;
        to.LastLaunched = from.LastLaunched;
        to.RunAsAdmin = from.RunAsAdmin;
        to.Enabled = from.Enabled;
        to.SortWeight = from.SortWeight;
        to.CustomFields = new Dictionary<string, object>(from.CustomFields);
        to.CreatedAt = from.CreatedAt;
        to.UpdatedAt = from.UpdatedAt;
        to.SchemaVersion = from.SchemaVersion;
    }

    #endregion
}
