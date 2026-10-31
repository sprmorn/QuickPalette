namespace QuickPalette.Screenshot.Model;

/// <summary>标注命令（撤销/重做的最小单元）</summary>
public interface IAnnotationCommand
{
    void Undo(AnnotationDocument document);
    void Redo(AnnotationDocument document);
}

/// <summary>新增标注</summary>
public sealed class AddAnnotationCommand : IAnnotationCommand
{
    private readonly AnnotationObject _annotation;
    private readonly int _index;

    public AddAnnotationCommand(AnnotationObject annotation, int index)
    {
        _annotation = annotation;
        _index = index;
    }

    public void Redo(AnnotationDocument document) => document.Insert(_index, _annotation);

    public void Undo(AnnotationDocument document) => document.Remove(_annotation);
}

/// <summary>删除标注（保留原下标，撤销时回插原位）</summary>
public sealed class RemoveAnnotationCommand : IAnnotationCommand
{
    private readonly AnnotationObject _annotation;
    private int _index;

    public RemoveAnnotationCommand(AnnotationObject annotation, int index)
    {
        _annotation = annotation;
        _index = index;
    }

    public void Redo(AnnotationDocument document)
    {
        var removedIndex = document.Remove(_annotation);
        if (removedIndex >= 0)
        {
            _index = removedIndex;
        }
    }

    public void Undo(AnnotationDocument document) => document.Insert(_index, _annotation);
}

/// <summary>改样式（前后状态快照）</summary>
public sealed class ChangeStyleCommand : IAnnotationCommand
{
    private readonly Guid _id;
    private readonly AnnotationStyle _before;
    private readonly AnnotationStyle _after;

    public ChangeStyleCommand(Guid id, AnnotationStyle before, AnnotationStyle after)
    {
        _id = id;
        _before = before;
        _after = after;
    }

    public void Redo(AnnotationDocument document)
    {
        if (document.Find(_id) is { } target)
        {
            target.Style = _after;
        }
    }

    public void Undo(AnnotationDocument document)
    {
        if (document.Find(_id) is { } target)
        {
            target.Style = _before;
        }
    }
}

/// <summary>
/// 标注命令栈：Ctrl+Z / Ctrl+Y 的历史。
/// 深度超限丢最旧的一条（防长会话内存无界增长）。
/// </summary>
public sealed class CommandStack
{
    /// <summary>默认历史深度上限</summary>
    public const int DefaultMaxDepth = 200;

    private readonly Stack<IAnnotationCommand> _undo = new();
    private readonly Stack<IAnnotationCommand> _redo = new();
    private readonly int _maxDepth;

    public CommandStack(int maxDepth = DefaultMaxDepth)
    {
        _maxDepth = Math.Max(1, maxDepth);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;

    /// <summary>执行命令并入历史（清空重做栈）</summary>
    public void Execute(IAnnotationCommand command, AnnotationDocument document)
    {
        command.Redo(document);
        _undo.Push(command);
        _redo.Clear();

        // 超限：重建栈丢弃最旧条目（Stack 无法从底部移除）
        if (_undo.Count > _maxDepth)
        {
            var kept = _undo.Take(_maxDepth).ToArray();
            _undo.Clear();
            for (var i = kept.Length - 1; i >= 0; i--)
            {
                _undo.Push(kept[i]);
            }
        }
    }

    public void Undo(AnnotationDocument document)
    {
        if (_undo.Count == 0) return;
        var command = _undo.Pop();
        command.Undo(document);
        _redo.Push(command);
    }

    public void Redo(AnnotationDocument document)
    {
        if (_redo.Count == 0) return;
        var command = _redo.Pop();
        command.Redo(document);
        _undo.Push(command);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
