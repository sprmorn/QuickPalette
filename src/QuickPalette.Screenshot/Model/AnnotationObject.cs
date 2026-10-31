namespace QuickPalette.Screenshot.Model;

using System.Drawing;

/// <summary>
/// 标注对象（对象模型而非像素涂抹）：
/// 形状类用 <see cref="Frame"/>（矩形/椭圆/箭头/马赛克/文字），
/// 笔画类用 <see cref="Points"/>（画笔/荧光笔），文字内容放 <see cref="Text"/>。
/// 序列化友好、可撤销、可二次编辑。
/// </summary>
public sealed class AnnotationObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public AnnotationKind Kind { get; init; }

    /// <summary>样式（替换式更新，配合 ChangeStyleCommand 进撤销历史）</summary>
    public AnnotationStyle Style { get; set; } = AnnotationStyle.Default;

    /// <summary>形状类标注的外接框（画笔类未用）</summary>
    public RectangleF Frame { get; set; }

    /// <summary>笔画类标注的采样点（形状类未用）</summary>
    public List<PointF> Points { get; init; } = new();

    /// <summary>文字内容（仅 Text 类使用）</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>深拷贝（命令历史保存前后状态用）</summary>
    public AnnotationObject Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Style = Style,
        Frame = Frame,
        Points = new List<PointF>(Points),
        Text = Text
    };

    /// <summary>把另一个对象的可变状态复制过来（同 Id）</summary>
    public void CopyStateFrom(AnnotationObject other)
    {
        Style = other.Style;
        Frame = other.Frame;
        Points.Clear();
        Points.AddRange(other.Points);
        Text = other.Text;
    }

    /// <summary>
    /// 命中测试：点是否落在对象上（对象橡皮擦/点选用）。
    /// tolerance 为命中容差（DIP）。
    /// </summary>
    public bool HitTest(PointF point, float tolerance)
    {
        if (Kind is AnnotationKind.Brush or AnnotationKind.Highlight)
        {
            var hitRadius = (float)(Style.StrokeWidth / 2) + tolerance;
            for (var i = 1; i < Points.Count; i++)
            {
                if (DistanceToSegment(point, Points[i - 1], Points[i]) <= hitRadius)
                {
                    return true;
                }
            }

            return Points.Count == 1 && Distance(point, Points[0]) <= hitRadius;
        }

        var frame = RectangleF.Inflate(Frame, tolerance, tolerance);
        return frame.Contains(point);
    }

    private static float Distance(PointF a, PointF b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-6f)
        {
            return Distance(p, a);
        }

        var t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq;
        t = Math.Clamp(t, 0f, 1f);
        return Distance(p, new PointF(a.X + t * abx, a.Y + t * aby));
    }
}
