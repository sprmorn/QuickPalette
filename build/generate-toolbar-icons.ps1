# 由 svg/ 目录生成 ToolbarIcons.cs（WPF 矢量图标数据，48×48 viewBox）
$ErrorActionPreference = 'Stop'
$svgDir = 'D:\Desktop\轻启\svg'
$outFile = 'D:\Desktop\轻启\src\QuickPalette.App\Screenshot\ToolbarIcons.cs'

# 文件名 → 图标键
$map = @{
    '选择.svg'            = 'select'
    '矩形1_rectangle-one.svg' = 'rect'
    '圆形_round.svg'      = 'ellipse'
    '箭头左下_arrow-left-down.svg' = 'arrow'
    '铅笔_pencil.svg'     = 'brush'
    '高亮_high-light.svg' = 'highlight'
    '文字_text.svg'       = 'text'
    '马赛克_mosaic.svg'   = 'mosaic'
    '擦除_erase.svg'      = 'eraser'
    '撤销_undo.svg'       = 'undo'
    '前进_go-ahead.svg'   = 'redo'
    '复制_copy.svg'       = 'copy'
    '保存硬盘_save-one.svg' = 'save'
    '大头钉_pin.svg'      = 'pin'
    '长截图_screenshot-one.svg' = 'long'
    '关闭_close.svg'      = 'exit'
}

function Convert-RectToPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$rx) {
    if ($rx -le 0) { return "M$x $y H$($x+$w) V$($y+$h) H$x Z" }
    $x2 = $x + $w; $y2 = $y + $h
    return "M$($x+$rx) $y H$($x2-$rx) A$rx $rx 0 0 1 $x2 $($y+$rx) V$($y2-$rx) A$rx $rx 0 0 1 $($x2-$rx) $y2 H$($x+$rx) A$rx $rx 0 0 1 $x $($y2-$rx) V$($y+$rx) A$rx $rx 0 0 1 $($x+$rx) $y Z"
}

function Convert-CircleToPath([double]$cx, [double]$cy, [double]$r) {
    return "M$($cx-$r) $cy A$r $r 0 1 0 $($cx+$r) $cy A$r $r 0 1 0 $($cx-$r) $cy Z"
}

$entries = @()
foreach ($name in $map.Keys) {
    $key = $map[$name]
    $xml = [xml](Get-Content (Join-Path $svgDir $name) -Raw -Encoding UTF8)
    $paths = @()

    foreach ($p in $xml.svg.path) {
        $filled = ($p.fill -and $p.fill -ne 'none')
        $paths += [pscustomobject]@{ D = $p.d; Filled = $filled }
    }
    foreach ($r in $xml.svg.rect) {
        $rx = if ($r.rx) { [double]$r.rx } else { 0 }
        $paths += [pscustomobject]@{ D = Convert-RectToPath ([double]$r.x) ([double]$r.y) ([double]$r.width) ([double]$r.height) $rx; Filled = ($r.fill -and $r.fill -ne 'none') }
    }
    foreach ($c in $xml.svg.circle) {
        $paths += [pscustomobject]@{ D = Convert-CircleToPath ([double]$c.cx) ([double]$c.cy) ([double]$c.r); Filled = ($c.fill -and $c.fill -ne 'none') }
    }

    $entries += [pscustomobject]@{ Key = $key; Paths = $paths }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// 由 svg/ 目录生成（build 工具生成，勿手改）：WPF 矢量图标数据，48×48 viewBox，描边宽 4。')
[void]$sb.AppendLine('namespace QuickPalette.App.Screenshot;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('using System.Windows;')
[void]$sb.AppendLine('using System.Windows.Controls;')
[void]$sb.AppendLine('using System.Windows.Data;')
[void]$sb.AppendLine('using System.Windows.Media;')
[void]$sb.AppendLine('using System.Windows.Shapes;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('/// <summary>')
[void]$sb.AppendLine('/// 工具栏矢量图标：路径数据取自 svg/ 目录（iconfont 风格 48×48）；')
[void]$sb.AppendLine('/// 着色跟随按钮前景色（描边/填充自动区分）。')
[void]$sb.AppendLine('/// </summary>')
[void]$sb.AppendLine('internal static class ToolbarIcons')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    private sealed record IconPath(string D, bool Filled);')
[void]$sb.AppendLine()
[void]$sb.AppendLine('    private static readonly Dictionary<string, IconPath[]> Data = new()')
[void]$sb.AppendLine('    {')
foreach ($e in $entries) {
    [void]$sb.AppendLine("        [""$($e.Key)""] = new IconPath[]")
    [void]$sb.AppendLine('        {')
    foreach ($p in $e.Paths) {
        $filledStr = if ($p.Filled) { 'true' } else { 'false' }
        $dEscaped = $p.D -replace '"', '\"'
        [void]$sb.AppendLine("            new(""$dEscaped"", $filledStr),")
    }
    [void]$sb.AppendLine('        },')
}
[void]$sb.AppendLine('    };')
[void]$sb.AppendLine()
[void]$sb.AppendLine('    /// <summary>创建图标（Viewbox 内 48×48 画布，描边随缩放保持比例）</summary>')
[void]$sb.AppendLine('    public static FrameworkElement Create(string key, double size = 20)')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        var canvas = new Canvas { Width = 48, Height = 48, IsHitTestVisible = false };')
[void]$sb.AppendLine('        foreach (var p in Data[key])')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            var path = new Path')
[void]$sb.AppendLine('            {')
[void]$sb.AppendLine('                Data = Geometry.Parse(p.D),')
[void]$sb.AppendLine('                StrokeThickness = 4,')
[void]$sb.AppendLine('                StrokeLineJoin = PenLineJoin.Round,')
[void]$sb.AppendLine('                StrokeStartLineCap = PenLineCap.Round,')
[void]$sb.AppendLine('                StrokeEndLineCap = PenLineCap.Round,')
[void]$sb.AppendLine('                Stretch = Stretch.None,')
[void]$sb.AppendLine('                IsHitTestVisible = false,')
[void]$sb.AppendLine('            };')
[void]$sb.AppendLine()
[void]$sb.AppendLine('            // 跟随按钮前景色（选中/悬停状态自动变色）')
[void]$sb.AppendLine('            var binding = new Binding(Control.ForegroundProperty.Name)')
[void]$sb.AppendLine('            {')
[void]$sb.AppendLine('                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Controls.Primitives.ButtonBase), 1),')
[void]$sb.AppendLine('            };')
[void]$sb.AppendLine('            path.SetBinding(p.Filled ? Shape.FillProperty : Shape.StrokeProperty, binding);')
[void]$sb.AppendLine('            canvas.Children.Add(path);')
[void]$sb.AppendLine('        }')
[void]$sb.AppendLine()
[void]$sb.AppendLine('        return new Viewbox')
[void]$sb.AppendLine('        {')
[void]$sb.AppendLine('            Width = size,')
[void]$sb.AppendLine('            Height = size,')
[void]$sb.AppendLine('            Stretch = Stretch.Uniform,')
[void]$sb.AppendLine('            Child = canvas,')
[void]$sb.AppendLine('            IsHitTestVisible = false,')
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

Set-Content $outFile $sb.ToString() -Encoding UTF8
"已生成 $outFile（$($entries.Count) 个图标）"
foreach ($e in $entries) { "  $($e.Key): $($e.Paths.Count) 条路径" }
