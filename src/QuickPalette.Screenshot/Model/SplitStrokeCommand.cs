namespace QuickPalette.Screenshot.Model;

/// <summary>
/// 笔画切分命令：一条笔画 → 若干子笔画（橡皮擦点擦）。
/// 一次拖擦多段合并为逐条命令入栈（每条命令对应一次 SplitStroke 调用）。
/// </summary>
public sealed class SplitStrokeCommand : IAnnotationCommand
{
    private readonly AnnotationObject _before;
    private readonly List<AnnotationObject> _pieces;
    private int _index;

    public SplitStrokeCommand(AnnotationObject before, List<AnnotationObject> pieces, int index)
    {
        _before = before;
        _pieces = pieces;
        _index = index;
    }

    public void Redo(AnnotationDocument document)
    {
        var removedIndex = document.Remove(_before);
        if (removedIndex >= 0)
        {
            _index = removedIndex;
        }

        for (var i = 0; i < _pieces.Count; i++)
        {
            document.Insert(_index + i, _pieces[i]);
        }
    }

    public void Undo(AnnotationDocument document)
    {
        foreach (var piece in _pieces)
        {
            document.Remove(piece);
        }

        document.Insert(_index, _before);
    }
}
