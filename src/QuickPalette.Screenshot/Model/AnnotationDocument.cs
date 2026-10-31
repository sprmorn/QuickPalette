namespace QuickPalette.Screenshot.Model;

using System.Drawing;

/// <summary>
/// 一次截图选区上的标注文档：对象列表 + 命中测试。
/// 底图（截图位图）不在这里——由 UI 层持有，文档只管标注对象，
/// 因此马赛克始终是"对底图的滤镜对象"，不破坏原始像素。
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<AnnotationObject> _annotations = new();

    /// <summary>选区尺寸（DIP）</summary>
    public float Width { get; }
    public float Height { get; }

    public AnnotationDocument(float width, float height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>标注列表（下标即 z 序：越靠后越上层）</summary>
    public IReadOnlyList<AnnotationObject> Annotations => _annotations;

    public void Add(AnnotationObject annotation) => _annotations.Add(annotation);

    public void Insert(int index, AnnotationObject annotation)
        => _annotations.Insert(Math.Clamp(index, 0, _annotations.Count), annotation);

    public bool Remove(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        _annotations.RemoveAt(index);
        return true;
    }

    /// <summary>按对象移除并返回下标（撤销"删除"时按原下标回插）</summary>
    public int Remove(AnnotationObject annotation)
    {
        var index = IndexOf(annotation.Id);
        if (index >= 0)
        {
            _annotations.RemoveAt(index);
        }

        return index;
    }

    public int IndexOf(Guid id) => _annotations.FindIndex(a => a.Id == id);

    public AnnotationObject? Find(Guid id)
    {
        var index = IndexOf(id);
        return index < 0 ? null : _annotations[index];
    }

    /// <summary>自顶向下命中测试（对象橡皮擦/点选用）</summary>
    public AnnotationObject? HitTest(PointF point, float tolerance = 4f)
    {
        for (var i = _annotations.Count - 1; i >= 0; i--)
        {
            if (_annotations[i].HitTest(point, tolerance))
            {
                return _annotations[i];
            }
        }

        return null;
    }

    /// <summary>清除全部标注（新截图复用文档时）</summary>
    public void Clear() => _annotations.Clear();
}
