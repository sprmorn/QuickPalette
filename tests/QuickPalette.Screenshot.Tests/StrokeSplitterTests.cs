namespace QuickPalette.Screenshot.Tests;

using System.Drawing;
using QuickPalette.Screenshot.Model;

/// <summary>
/// 橡皮擦点擦：笔画切分 + 切分命令撤销/重做
/// </summary>
public class StrokeSplitterTests
{
    private static AnnotationObject MakeStroke(params PointF[] points)
    {
        var stroke = new AnnotationObject { Kind = AnnotationKind.Brush };
        stroke.Points.AddRange(points);
        return stroke;
    }

    [Fact]
    public void Split_Middle_Removes_Points_And_Yields_Two_Pieces()
    {
        // 0-1-2-3-4-5-6 直线；擦掉中间 3
        var stroke = MakeStroke(
            new PointF(0, 0), new PointF(10, 0), new PointF(20, 0), new PointF(30, 0),
            new PointF(40, 0), new PointF(50, 0), new PointF(60, 0));

        var pieces = StrokeSplitter.Split(stroke, new PointF(30, 0), 5);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(3, pieces[0].Points.Count); // 0,10,20
        Assert.Equal(3, pieces[1].Points.Count); // 40,50,60
    }

    [Fact]
    public void Split_End_Trims_Without_Splitting()
    {
        var stroke = MakeStroke(new PointF(0, 0), new PointF(10, 0), new PointF(20, 0), new PointF(30, 0));

        var pieces = StrokeSplitter.Split(stroke, new PointF(30, 0), 5);

        var piece = Assert.Single(pieces);
        Assert.Equal(3, piece.Points.Count);
    }

    [Fact]
    public void Split_All_Points_Erased_Yields_Empty()
    {
        var stroke = MakeStroke(new PointF(0, 0), new PointF(5, 0), new PointF(10, 0));

        Assert.Empty(StrokeSplitter.Split(stroke, new PointF(5, 0), 100));
        Assert.True(StrokeSplitter.IsFullyErased(stroke, new PointF(5, 0), 100));
    }

    [Fact]
    public void Split_Miss_Keeps_Stroke_Intact()
    {
        var stroke = MakeStroke(new PointF(0, 0), new PointF(10, 0), new PointF(20, 0));

        var pieces = StrokeSplitter.Split(stroke, new PointF(100, 100), 5);

        var piece = Assert.Single(pieces);
        Assert.Equal(3, piece.Points.Count);
        Assert.NotEqual(stroke.Id, piece.Id); // 副本，不改动原对象
    }

    [Fact]
    public void Split_Does_Not_Mutate_Source()
    {
        var stroke = MakeStroke(new PointF(0, 0), new PointF(10, 0), new PointF(20, 0));

        StrokeSplitter.Split(stroke, new PointF(10, 0), 5);

        Assert.Equal(3, stroke.Points.Count);
    }

    [Fact]
    public void Split_NonStroke_Returns_Clone()
    {
        var rect = new AnnotationObject { Kind = AnnotationKind.Rectangle, Frame = new RectangleF(0, 0, 10, 10) };

        var pieces = StrokeSplitter.Split(rect, new PointF(5, 5), 100);

        Assert.Single(pieces);
    }

    [Fact]
    public void SplitStrokeCommand_RoundTrip()
    {
        var doc = new AnnotationDocument(800, 600);
        var stroke = MakeStroke(
            new PointF(0, 0), new PointF(10, 0), new PointF(20, 0),
            new PointF(30, 0), new PointF(40, 0), new PointF(50, 0));
        doc.Add(stroke);

        var pieces = StrokeSplitter.Split(stroke, new PointF(25, 0), 6);
        var stack = new CommandStack();
        stack.Execute(new SplitStrokeCommand(stroke, pieces, 0), doc);

        // 切分后：原笔画消失，两条子笔画在场
        Assert.Equal(2, doc.Annotations.Count);
        Assert.DoesNotContain(doc.Annotations, a => a.Id == stroke.Id);

        // 撤销：原样恢复
        stack.Undo(doc);
        var restored = Assert.Single(doc.Annotations);
        Assert.Equal(stroke.Id, restored.Id);
        Assert.Equal(6, restored.Points.Count);

        // 重做：回到切分后
        stack.Redo(doc);
        Assert.Equal(2, doc.Annotations.Count);
    }

    [Fact]
    public void SplitStrokeCommand_FullyErased_RoundTrip()
    {
        var doc = new AnnotationDocument(800, 600);
        var stroke = MakeStroke(new PointF(0, 0), new PointF(5, 0), new PointF(10, 0));
        doc.Add(stroke);

        var pieces = StrokeSplitter.Split(stroke, new PointF(5, 0), 100);
        var stack = new CommandStack();
        stack.Execute(new SplitStrokeCommand(stroke, pieces, 0), doc);
        Assert.Empty(doc.Annotations);

        stack.Undo(doc);
        Assert.Single(doc.Annotations);
    }
}
