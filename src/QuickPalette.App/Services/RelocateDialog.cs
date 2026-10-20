namespace QuickPalette.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPalette.Domain.Models;

/// <summary>
/// 路径失效"重新定位"引导对话框：重新选择路径 / 打开所在目录 / 从列表移除（三选一）。
/// 纯代码构建（无 XAML）；颜色走主题字典 DynamicResource，随 dark/light 联动。
/// </summary>
public sealed class RelocateDialog : Window
{
    /// <summary>用户选择</summary>
    public enum RelocateChoice
    {
        /// <summary>取消（不做任何处理）</summary>
        Cancel,
        /// <summary>重新选择路径</summary>
        ChangePath,
        /// <summary>打开所在目录</summary>
        OpenDirectory,
        /// <summary>从列表移除</summary>
        Remove
    }

    public RelocateChoice Choice { get; private set; } = RelocateChoice.Cancel;

    public RelocateDialog(AppItem app, Window owner)
    {
        Title = UiText.T("main.title.relocate");
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        Background = Brushes.Transparent;

        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            Margin = new Thickness(8)
        };
        root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        root.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        root.BorderThickness = new Thickness(1);

        var panel = new StackPanel();

        var message = new TextBlock
        {
            Text = string.Format(UiText.T("main.relocate.message"), app.Path),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(message);

        var name = new TextBlock
        {
            Text = app.Name,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 0, 0, 14)
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        panel.Children.Add(name);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        buttons.Children.Add(MakeButton(UiText.T("main.relocate.change"), RelocateChoice.ChangePath));
        buttons.Children.Add(MakeButton(UiText.T("main.relocate.openDir"), RelocateChoice.OpenDirectory));
        buttons.Children.Add(MakeButton(UiText.T("main.relocate.remove"), RelocateChoice.Remove, isDestructive: true));
        buttons.Children.Add(MakeButton(UiText.T("main.menu.cancel"), RelocateChoice.Cancel));

        panel.Children.Add(buttons);
        root.Child = panel;
        Content = root;
    }

    private Button MakeButton(string text, RelocateChoice choice, bool isDestructive = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = choice == RelocateChoice.Cancel
        };
        button.SetResourceReference(Button.BackgroundProperty, isDestructive ? "DangerBrush" : "SurfaceAltBrush");
        button.SetResourceReference(Button.ForegroundProperty, "TextBrush");
        button.Click += (_, _) =>
        {
            Choice = choice;
            DialogResult = choice != RelocateChoice.Cancel;
        };
        return button;
    }
}
