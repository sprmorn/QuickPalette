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

        // ---- 设置页导航（svg/set）----

        // 应用管理
        ["nav-apps"] = new IconPath[]
        {
            new("M41 13.9997L24 4L7 13.9997V33.9998L24 44L41 33.9998V13.9997Z", false),
            new("M16 18.9976L23.9932 24.0002L31.9951 18.9976", false),
            new("M24 24V33", false),
        },

        // 快捷键
        ["nav-hotkey"] = new IconPath[]
        {
            new("M42 7H6C4.89543 7 4 7.89543 4 9V37C4 38.1046 4.89543 39 6 39H42C43.1046 39 44 38.1046 44 37V9C44 7.89543 43.1046 7 42 7Z", false),
            new("M12 19H14", false),
            new("M21 19H23", false),
            new("M29 19H36", false),
            new("M12 28H36", false),
        },

        // 截图
        ["nav-screenshot"] = new IconPath[]
        {
            new("M10 42C13.3137 42 16 39.3137 16 36C16 32.6863 13.3137 30 10 30C6.68629 30 4 32.6863 4 36C4 39.3137 6.68629 42 10 42Z", false),
            new("M40.0615 8C24 28.4331 15.8047 38.6805 14.2426 40.2426C11.8995 42.5858 8.10047 42.5858 5.75732 40.2426", false),
            new("M38 42C41.3137 42 44 39.3137 44 36C44 32.6863 41.3137 30 38 30C34.6863 30 32 32.6863 32 36C32 39.3137 34.6863 42 38 42Z", false),
            new("M42.2424 40.2426C39.8993 42.5858 36.1003 42.5858 33.7571 40.2426C32.195 38.6805 23.9998 28.446 8.00049 8", false),
        },

        // 工具箱
        ["nav-toolbox"] = new IconPath[]
        {
            new("M8 25V38C8 39.6569 9.34315 41 11 41H37C38.6569 41 40 39.6569 40 38V25", false),
            new("M5 15C5 13.8954 5.89543 13 7 13H41C42.1046 13 43 13.8954 43 15V23C43 24.1046 42.1046 25 41 25H7C5.89543 25 5 24.1046 5 23V15Z", false),
            new("M31 13V9C31 7.89543 30.1046 7 29 7H19C17.8954 7 17 7.89543 17 9V13", false),
            new("M15 23V29", false),
            new("M33 23V29", false),
        },

        // 通用
        ["nav-general"] = new IconPath[]
        {
            new("M11 16V42", false),
            new("M24 29V42", false),
            new("M24 19V6", false),
            new("M37 6V32", false),
            new("M11 16C13.7614 16 16 13.7614 16 11C16 8.23858 13.7614 6 11 6C8.23858 6 6 8.23858 6 11C6 13.7614 8.23858 16 11 16Z", false),
            new("M24 29C26.7614 29 29 26.7614 29 24C29 21.2386 26.7614 19 24 19C21.2386 19 19 21.2386 19 24C19 26.7614 21.2386 29 24 29Z", false),
            new("M37 42C39.7614 42 42 39.7614 42 37C42 34.2386 39.7614 32 37 32C34.2386 32 32 34.2386 32 37C32 39.7614 34.2386 42 37 42Z", false),
        },

        // 过滤规则
        ["nav-filters"] = new IconPath[]
        {
            new("M8 44L8 6C8 4.89543 8.89543 4 10 4H38C39.1046 4 40 4.89543 40 6V44L24 35.7273L8 44Z", false),
            new("M23.9497 13.9497L23.9497 25.9497", false),
            new("M17.9497 19.9497L29.9497 19.9497", false),
        },

        // 备份
        ["nav-backup"] = new IconPath[]
        {
            new("M39.3 6H8.7C7.20883 6 6 7.20883 6 8.7V39.3C6 40.7912 7.20883 42 8.7 42H39.3C40.7912 42 42 40.7912 42 39.3V8.7C42 7.20883 40.7912 6 39.3 6Z", false),
            new("M32 6V24H15V6H32Z", false),
            new("M26 13V17", false),
            new("M10.9971 6H35.9986", false),
        },

        // 关于
        ["nav-about"] = new IconPath[]
        {
            new("M24 44C29.5228 44 34.5228 41.7614 38.1421 38.1421C41.7614 34.5228 44 29.5228 44 24C44 18.4772 41.7614 13.4772 38.1421 9.85786C34.5228 6.23858 29.5228 4 24 4C18.4772 4 13.4772 6.23858 9.85786 9.85786C6.23858 13.4772 4 18.4772 4 24C4 29.5228 6.23858 34.5228 9.85786 38.1421C13.4772 41.7614 18.4772 44 24 44Z", false),
            new("M24 11C25.3807 11 26.5 12.1193 26.5 13.5C26.5 14.8807 25.3807 16 24 16C22.6193 16 21.5 14.8807 21.5 13.5C21.5 12.1193 22.6193 11 24 11Z", true),
            new("M24.5 34V20H23.5H22.5", false),
            new("M21 34H28", false),
        },

        // 设置（齿轮）
        ["nav-setting"] = new IconPath[]
        {
            new("M18.2838 43.1713C14.9327 42.1736 11.9498 40.3213 9.58787 37.867C10.469 36.8227 11 35.4734 11 34.0001C11 30.6864 8.31371 28.0001 5 28.0001C4.79955 28.0001 4.60139 28.01 4.40599 28.0292C4.13979 26.7277 4 25.3803 4 24.0001C4 21.9095 4.32077 19.8938 4.91579 17.9995C4.94381 17.9999 4.97188 18.0001 5 18.0001C8.31371 18.0001 11 15.3138 11 12.0001C11 11.0488 10.7786 10.1493 10.3846 9.35011C12.6975 7.1995 15.5205 5.59002 18.6521 4.72314C19.6444 6.66819 21.6667 8.00013 24 8.00013C26.3333 8.00013 28.3556 6.66819 29.3479 4.72314C32.4795 5.59002 35.3025 7.1995 37.6154 9.35011C37.2214 10.1493 37 11.0488 37 12.0001C37 15.3138 39.6863 18.0001 43 18.0001C43.0281 18.0001 43.0562 17.9999 43.0842 17.9995C43.6792 19.8938 44 21.9095 44 24.0001C44 25.3803 43.8602 26.7277 43.594 28.0292C43.3986 28.01 43.2005 28.0001 43 28.0001C39.6863 28.0001 37 30.6864 37 34.0001C37 35.4734 37.531 36.8227 38.4121 37.867C36.0502 40.3213 33.0673 42.1736 29.7162 43.1713C28.9428 40.752 26.676 39.0001 24 39.0001C21.324 39.0001 19.0572 40.752 18.2838 43.1713Z", false),
            new("M24 31C27.866 31 31 27.866 31 24C31 20.134 27.866 17 24 17C20.134 17 17 20.134 17 24C17 27.866 20.134 31 24 31Z", false),
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
                // 跟随宿主控件前景色（按钮/导航项的选中、悬停状态自动变色）
                var binding = new Binding(Control.ForegroundProperty.Name)
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
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
