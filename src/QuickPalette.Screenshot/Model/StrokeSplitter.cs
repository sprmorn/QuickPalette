namespace QuickPalette.Screenshot.Model;

using System.Drawing;

/// <summary>
/// 笔画切分（橡皮擦"点擦"）：把画笔/荧光笔的采样点中落在擦除半径内的点去掉，
/// 剩余连续段拆成独立笔画对象。形状类标注不适用（形状仍是整对象删除）。
/// </summary>
public static class StrokeSplitter
{
    /// <summary>
    /// 切分笔画：返回切分后的子笔画（0 段 = 整条被擦除；1 段 = 只是修剪了端点/无命中）。
    /// before 本身不被修改。
    /// </summary>
    public static List<AnnotationObject> Split(AnnotationObject stroke, PointF eraseAt, float radius)
    {
        var pieces = new List<AnnotationObject>();
        if (stroke.Kind is not (AnnotationKind.Brush or AnnotationKind.Highlight))
        {
            // 非笔画类不参与切分（调用方走整对象删除）；返回副本保持类型一致
            pieces.Add(MakePiece(stroke, new List<PointF>(stroke.Points)));
            return pieces;
        }

        var radiusSq = radius * radius;
        var current = new List<PointF>();

        foreach (var point in stroke.Points)
        {
            var dx = point.X - eraseAt.X;
            var dy = point.Y - eraseAt.Y;
            if (dx * dx + dy * dy <= radiusSq)
            {
                // 命中：当前段在此截断
                if (current.Count > 1)
                {
                    pieces.Add(MakePiece(stroke, current));
                }

                current.Clear();
                continue;
            }

            current.Add(point);
        }

        if (current.Count > 1)
        {
            pieces.Add(MakePiece(stroke, current));
        }

        return pieces;
    }

    /// <summary>整条笔画是否会被一次擦除清空（用于"未命中不产生命令"的判定）</summary>
    public static bool IsFullyErased(AnnotationObject stroke, PointF eraseAt, float radius)
    {
        var pieces = Split(stroke, eraseAt, radius);
        return pieces.Count == 0;
    }

    /// <summary>子笔画 = 样式继承 + 新 Id（Clone 保留原 Id 是快照语义，切分产物必须独立）</summary>
    private static AnnotationObject MakePiece(AnnotationObject source, List<PointF> points)
        => new()
        {
            Kind = source.Kind,
            Style = source.Style,
            Frame = source.Frame,
            Text = source.Text,
            Points = new List<PointF>(points)
        };
}
