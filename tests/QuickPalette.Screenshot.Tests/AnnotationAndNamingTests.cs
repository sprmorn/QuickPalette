namespace QuickPalette.Screenshot.Tests;

using System.Drawing;
using QuickPalette.Screenshot.Model;
using QuickPalette.Screenshot.Output;

/// <summary>
/// 标注对象命中测试 + 文件命名模板测试
/// </summary>
public class AnnotationAndNamingTests
{
    // ---------- 命中测试 ----------

    [Fact]
    public void HitTest_Shape_Inside_Frame()
    {
        var obj = new AnnotationObject
        {
            Kind = AnnotationKind.Rectangle,
            Frame = new RectangleF(10, 10, 100, 50)
        };

        Assert.True(obj.HitTest(new PointF(50, 30), 4));
        Assert.False(obj.HitTest(new PointF(200, 30), 4));
    }

    [Fact]
    public void HitTest_Shape_Respects_Tolerance()
    {
        var obj = new AnnotationObject
        {
            Kind = AnnotationKind.Ellipse,
            Frame = new RectangleF(0, 0, 100, 100)
        };

        Assert.False(obj.HitTest(new PointF(103, 50), 0));
        Assert.True(obj.HitTest(new PointF(103, 50), 4));
    }

    [Fact]
    public void HitTest_Brush_Uses_Stroke_Width_And_Tolerance()
    {
        var obj = new AnnotationObject
        {
            Kind = AnnotationKind.Brush,
            Style = AnnotationStyle.Default with { StrokeWidth = 10 }
        };
        obj.Points.AddRange(new[] { new PointF(0, 0), new PointF(100, 0) });

        Assert.False(obj.HitTest(new PointF(50, 6), 0)); // 距线段 6 > 半宽 5
        Assert.True(obj.HitTest(new PointF(50, 6), 2));  // 加容差 2 后命中
    }

    [Fact]
    public void HitTest_Brush_Point_Within_Half_Width()
    {
        var obj = new AnnotationObject
        {
            Kind = AnnotationKind.Highlight,
            Style = AnnotationStyle.HighlighterDefault with { StrokeWidth = 10 }
        };
        obj.Points.AddRange(new[] { new PointF(0, 0), new PointF(100, 0) });

        Assert.True(obj.HitTest(new PointF(50, 4), 0));   // 4 ≤ 5
        Assert.False(obj.HitTest(new PointF(50, 9), 0));  // 9 > 5
    }

    [Fact]
    public void Document_HitTest_Prefers_Topmost()
    {
        var doc = new AnnotationDocument(800, 600);
        var bottom = new AnnotationObject { Kind = AnnotationKind.Rectangle, Frame = new RectangleF(0, 0, 100, 100) };
        var top = new AnnotationObject { Kind = AnnotationKind.Rectangle, Frame = new RectangleF(0, 0, 100, 100) };
        doc.Add(bottom);
        doc.Add(top);

        Assert.Equal(top.Id, doc.HitTest(new PointF(50, 50))?.Id);
    }

    [Fact]
    public void Clone_And_CopyState_Are_Independent()
    {
        var obj = new AnnotationObject
        {
            Kind = AnnotationKind.Brush,
            Text = "hello",
            Frame = new RectangleF(1, 2, 3, 4)
        };
        obj.Points.Add(new PointF(9, 9));

        var clone = obj.Clone();
        clone.Text = "changed";
        clone.Points.Add(new PointF(1, 1));

        Assert.Equal("hello", obj.Text);
        Assert.Single(obj.Points);
        Assert.Equal(obj.Id, clone.Id);
    }

    // ---------- 命名模板 ----------

    [Fact]
    public void FileNameTemplate_Default_Replaces_Tokens()
    {
        var t = new DateTime(2026, 10, 31, 9, 5, 7);
        Assert.Equal("截图_20261031_090507", FileNameTemplate.Build(null, t));
        Assert.Equal("截图_20261031_090507", FileNameTemplate.Build("   ", t));
    }

    [Fact]
    public void FileNameTemplate_Custom_And_Invalid_Chars()
    {
        var t = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal("snap-2026-01-02", FileNameTemplate.Build("snap-yyyy-MM-dd", t));
        Assert.Equal("a_b_c_030405", FileNameTemplate.Build(@"a/b\c_HHmmss", t));
    }

    [Fact]
    public void FileNameTemplate_Falls_Back_When_All_Sanitized()
    {
        var t = new DateTime(2026, 10, 31, 9, 5, 7);
        Assert.Equal("截图_20261031_090507", FileNameTemplate.Build("////", t));
    }
}
