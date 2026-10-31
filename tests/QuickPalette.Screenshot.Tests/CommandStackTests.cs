namespace QuickPalette.Screenshot.Tests;

using System.Drawing;
using QuickPalette.Screenshot.Model;

/// <summary>
/// CommandStack 标注命令栈测试（撤销/重做/历史深度）
/// </summary>
public class CommandStackTests
{
    private static AnnotationObject NewRect(float x = 10) => new()
    {
        Kind = AnnotationKind.Rectangle,
        Frame = new RectangleF(x, 10, 100, 50)
    };

    [Fact]
    public void Add_Undo_Removes_Object()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack();
        var obj = NewRect();

        stack.Execute(new AddAnnotationCommand(obj, doc.Annotations.Count), doc);
        Assert.Single(doc.Annotations);

        stack.Undo(doc);
        Assert.Empty(doc.Annotations);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void Add_Undo_Redo_Restores_Object()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack();
        var obj = NewRect();

        stack.Execute(new AddAnnotationCommand(obj, doc.Annotations.Count), doc);
        stack.Undo(doc);
        stack.Redo(doc);

        var hit = Assert.Single(doc.Annotations);
        Assert.Equal(obj.Id, hit.Id);
    }

    [Fact]
    public void Remove_Undo_Restores_At_Original_Index()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack();
        var a = NewRect(10);
        var b = NewRect(20);
        var c = NewRect(30);
        doc.Add(a);
        doc.Add(b);
        doc.Add(c);

        stack.Execute(new RemoveAnnotationCommand(b, 1), doc);
        Assert.Equal(new[] { a.Id, c.Id }, doc.Annotations.Select(x => x.Id));

        stack.Undo(doc);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, doc.Annotations.Select(x => x.Id));
    }

    [Fact]
    public void ChangeStyle_Undo_Restores_Before_State()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack();
        var obj = NewRect();
        doc.Add(obj);

        var before = obj.Style;
        var after = before with { StrokeWidth = 8, Color = RgbaColor.Blue };
        stack.Execute(new ChangeStyleCommand(obj.Id, before, after), doc);
        Assert.Equal(8, obj.Style.StrokeWidth);

        stack.Undo(doc);
        Assert.Equal(before, obj.Style);
    }

    [Fact]
    public void Execute_Clears_Redo_Stack()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack();
        stack.Execute(new AddAnnotationCommand(NewRect(1), doc.Annotations.Count), doc);
        stack.Undo(doc);
        Assert.True(stack.CanRedo);

        stack.Execute(new AddAnnotationCommand(NewRect(2), doc.Annotations.Count), doc);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void MaxDepth_Drops_Oldest_Command()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack(maxDepth: 3);

        for (var i = 0; i < 5; i++)
        {
            stack.Execute(new AddAnnotationCommand(NewRect(i), doc.Annotations.Count), doc);
        }

        Assert.Equal(3, stack.UndoCount);

        // 连撤销 3 次后应剩 2 个对象（最早的两条历史被丢弃）
        stack.Undo(doc);
        stack.Undo(doc);
        stack.Undo(doc);
        Assert.Equal(2, doc.Annotations.Count);
    }

    [Fact]
    public void Undo_On_Empty_Stack_Does_Not_Throw()
    {
        var doc = new AnnotationDocument(800, 600);
        var stack = new CommandStack();

        stack.Undo(doc);
        stack.Redo(doc);

        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }
}
