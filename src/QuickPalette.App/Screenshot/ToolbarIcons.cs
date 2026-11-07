// 图标数据取自 svg/ 目录（iconfont 风格 48×48 viewBox、描边宽 4）；
// 如需重新生成请更新 svg/ 后同步本文件。
namespace QuickPalette.App.Screenshot;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

/// <summary>
/// 工具栏矢量图标：48×48 画布放进 Viewbox，描边随缩放保持比例；
/// 着色跟随按钮前景色（描边/填充自动区分）。
/// </summary>
internal static class ToolbarIcons
{
    private sealed record IconPath(string D, bool Filled);

    private static readonly Dictionary<string, IconPath[]> Data = new()
    {
        // 选择（SmartSelect 十字 + 光标）
        ["select"] = new IconPath[]
        {
            new("M24 4V12", false),
            new("M22 22L42 26L36 30L42 36L36 42L30 36L26 42L22 22Z", false),
            new("M38.1421 9.85789L32.4853 15.5147", false),
            new("M9.85787 38.1421L15.5147 32.4853", false),
            new("M4 24H12", false),
            new("M9.85795 9.85787L15.5148 15.5147", false),
        },

        // 矩形
        ["rect"] = new IconPath[]
        {
            new("M42 8H6C4.89543 8 4 8.89543 4 10V38C4 39.1046 4.89543 40 6 40H42C43.1046 40 44 39.1046 44 38V10C44 8.89543 43.1046 8 42 8Z", false),
        },

        // 圆形
        ["ellipse"] = new IconPath[]
        {
            new("M4 24A20 20 0 1 0 44 24A20 20 0 1 0 4 24Z", false),
        },

        // 箭头
        ["arrow"] = new IconPath[]
        {
            new("M29 37H11V19", false),
            new("M11 36.9998L36.4559 11.5439", false),
        },

        // 画笔（铅笔）
        ["brush"] = new IconPath[]
        {
            new("M30.9995 8.99902L38.9995 16.999", false),
            new("M7.99953 31.999L35.9994 4L43.9995 11.999L15.9995 39.999L5.99951 41.999L7.99953 31.999Z", false),
            new("M8.99951 31.999L15.9995 38.999", false),
            new("M12.9995 34.999L34.9995 12.999", false),
        },

        // 荧光笔
        ["highlight"] = new IconPath[]
        {
            new("M6 44L6 25H12V17H36V25H42V44H6Z", false),
            new("M17 17V8L31 4V17", false),
        },

        // 文字
        ["text"] = new IconPath[]
        {
            new("M9 6H39A3 3 0 0 1 42 9V39A3 3 0 0 1 39 42H9A3 3 0 0 1 6 39V9A3 3 0 0 1 9 6Z", false),
            new("M16 19V16H32V19", false),
            new("M22 34H26", false),
            new("M24 18L24 34", false),
        },

        // 马赛克（实心方块）
        ["mosaic"] = new IconPath[]
        {
            new("M44 36H36V44H44V36Z", true),
            new("M28 36H20V44H28V36Z", true),
            new("M12 36H4V44H12V36Z", true),
            new("M44 20H36V28H44V20Z", true),
            new("M28 20H20V28H28V20Z", true),
            new("M12 20H4V28H12V20Z", true),
            new("M44 4H36V12H44V4Z", true),
            new("M28 4H20V12H28V4Z", true),
            new("M12 4H4V12H12V4Z", true),
            new("M20 12H12V20H20V12Z", true),
            new("M20 28H12V36H20V28Z", true),
            new("M36 12H28V20H36V12Z", true),
            new("M36 28H28V36H36V28Z", true),
        },

        // 橡皮擦
        ["eraser"] = new IconPath[]
        {
            new("M4 42H44", false),
            new("M31 4L7 28L13 34H21L41 14L31 4Z", false),
        },

        // 撤销
        ["undo"] = new IconPath[]
        {
            new("M11.2721 36.7279C14.5294 39.9853 19.0294 42 24 42C33.9411 42 42 33.9411 42 24C42 14.0589 33.9411 6 24 6C19.0294 6 14.5294 8.01472 11.2721 11.2721C9.61407 12.9301 6 17 6 17", false),
            new("M6 9V17H14", false),
        },

        // 重做（前进）
        ["redo"] = new IconPath[]
        {
            new("M38 33C38 25.7011 33.897 19.4168 28 16.5919C25.8653 15.5693 23.4954 15 21 15C11.6112 15 4 23.0589 4 33", false),
            new("M30 28L38 33L44 25", false),
        },

        // 复制
        ["copy"] = new IconPath[]
        {
            new("M13 12.4316V7.8125C13 6.2592 14.2592 5 15.8125 5H40.1875C41.7408 5 43 6.2592 43 7.8125V32.1875C43 33.7408 41.7408 35 40.1875 35H35.5163", false),
            new("M32.1875 13H7.8125C6.2592 13 5 14.2592 5 15.8125V40.1875C5 41.7408 6.2592 43 7.8125 43H32.1875C33.7408 43 35 41.7408 35 40.1875V15.8125C35 14.2592 33.7408 13 32.1875 13Z", false),
        },

        // 保存
        ["save"] = new IconPath[]
        {
            new("M39.3 6H8.7C7.20883 6 6 7.20883 6 8.7V39.3C6 40.7912 7.20883 42 8.7 42H39.3C40.7912 42 42 40.7912 42 39.3V8.7C42 7.20883 40.7912 6 39.3 6Z", false),
            new("M32 6V24H15V6H32Z", false),
            new("M26 13V17", false),
            new("M10.9971 6H35.9986", false),
        },

        // 钉图（大头钉）
        ["pin"] = new IconPath[]
        {
            new("M10.6963 17.5042C13.3347 14.8657 16.4701 14.9387 19.8781 16.8076L32.62 9.74509L31.8989 4.78683L43.2126 16.1005L38.2656 15.3907L31.1918 28.1214C32.9752 31.7589 33.1337 34.6647 30.4953 37.3032C30.4953 37.3032 26.235 33.0429 22.7171 29.525L6.44305 41.5564L18.4382 25.2461C14.9202 21.7281 10.6963 17.5042 10.6963 17.5042Z", false),
        },

        // 长截图
        ["long"] = new IconPath[]
        {
            new("M16 6H8C6.89543 6 6 6.89543 6 8V16", false),
            new("M16 42H8C6.89543 42 6 41.1046 6 40V32", false),
            new("M32 42H40C41.1046 42 42 41.1046 42 40V32", false),
            new("M32 6H40C41.1046 6 42 6.89543 42 8V16", false),
            new("M16 14H32A2 2 0 0 1 34 16V32A2 2 0 0 1 32 34H16A2 2 0 0 1 14 32V16A2 2 0 0 1 16 14Z", false),
        },

        // 关闭
        ["exit"] = new IconPath[]
        {
            new("M8 8L40 40", false),
            new("M8 40L40 8", false),
        },

        // U盘（svg/new）
        ["usb"] = new IconPath[]
        {
            new("M8 22C8 20.8954 8.89543 20 10 20H38C39.1046 20 40 20.8954 40 22V42C40 43.1046 39.1046 44 38 44H10C8.89543 44 8 43.1046 8 42V22Z", false),
            new("M15 4H33V20H15V4Z", false),
            new("M21 10V12", false),
            new("M27 10V12", false),
        },

        // 弹出（svg/new）
        ["usb-eject"] = new IconPath[]
        {
            new("M9 6H39A3 3 0 0 1 42 9V39A3 3 0 0 1 39 42H9A3 3 0 0 1 6 39V9A3 3 0 0 1 9 6Z", false),
            new("M19 14L29 24L19 34", false),
        },

        // 文件夹（svg/new）
        ["usb-folder"] = new IconPath[]
        {
            new("M4 9V41L9 21H39.5V15C39.5 13.8954 38.6046 13 37.5 13H24L19 7H6C4.89543 7 4 7.89543 4 9Z", false),
            new("M40 41L44 21H8.8125L4 41H40Z", false),
        },

        // 移动硬盘（svg/new）
        ["usb-disk"] = new IconPath[]
        {
            new("M39 6H9C7.34315 6 6 7.34315 6 9V39C6 40.6569 7.34315 42 9 42H39C40.6569 42 42 40.6569 42 39V9C42 7.34315 40.6569 6 39 6Z", false),
            new("M32 6H16V16H32V6Z", false),
            new("M17 36H31", false),
        },
    };

    /// <summary>创建图标（Viewbox 内 48×48 画布，描边随缩放保持比例）。
    /// brush 为空时跟随按钮前景色；按钮之外（弹窗/面板）请显式传色。</summary>
    public static FrameworkElement Create(string key, double size = 20, Brush? brush = null)
    {
        var canvas = new Canvas { Width = 48, Height = 48, IsHitTestVisible = false };
        foreach (var p in Data[key])
        {
            var path = new Path
            {
                Data = Geometry.Parse(p.D),
                StrokeThickness = 4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.None,
                IsHitTestVisible = false,
            };

            if (brush != null)
            {
                // 显式着色（按钮之外的场景：FindAncestor 找不到 Button 会渲染成隐形）
                if (p.Filled) path.Fill = brush; else path.Stroke = brush;
            }
            else
            {
                // 跟随按钮前景色（选中/悬停状态自动变色）
                var binding = new Binding(Control.ForegroundProperty.Name)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ButtonBase), 1),
                };
                path.SetBinding(p.Filled ? Shape.FillProperty : Shape.StrokeProperty, binding);
            }

            canvas.Children.Add(path);
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
            IsHitTestVisible = false,
        };
    }
}
