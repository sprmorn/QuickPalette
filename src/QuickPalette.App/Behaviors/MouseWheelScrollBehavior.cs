using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickPalette.App.Behaviors;

/// <summary>
/// 滚轮滚动修复（附加行为，挂到各窗口根元素）。
///
/// 背景缺陷：WPF 把 MouseWheel 沿"焦点路径"路由——焦点在搜索框等与列表无关的
/// 元素上时，鼠标下的 ScrollViewer 收不到滚轮事件，表现为"某些界面滚轮没用"
/// （实测：结果列表可滚动但滚轮前后滚动百分比不变）。
///
/// 修复：在根元素 PreviewMouseWheel（隧道）阶段按"鼠标命中元素"向上找最近的
/// 可垂滚 ScrollViewer 代为滚动并标记 Handled；鼠标下没有可滚容器时不拦截，
/// 保留平台默认行为。对所有界面（主面板/设置/添加/编辑/向导）通用。
/// </summary>
public static class MouseWheelScrollBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(MouseWheelScrollBehavior),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    /// <summary>每档滚轮滚动像素（约一行结果的高度）</summary>
    private const double LineHeight = 48.0;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement root)
        {
            return;
        }

        var hit = root.InputHitTest(e.GetPosition(root)) as DependencyObject;
        for (var d = hit; d != null; d = GetParent(d))
        {
            if (d is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 120.0 * LineHeight);
                e.Handled = true;
                return;
            }
        }

        // 鼠标下没有可垂滚容器：不拦截，保留平台默认行为
    }

    private static DependencyObject? GetParent(DependencyObject d)
    {
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(d);
        }
        return LogicalTreeHelper.GetParent(d);
    }
}
