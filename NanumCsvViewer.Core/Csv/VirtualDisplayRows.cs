using System.Collections;

namespace NanumCsvViewer.Csv;

/// <summary>
/// 현재 표시 순서의 지연 행 목록. Count와 인덱서만 전체 행을 만들지 않는다.
/// 그리드가 전체를 열거하면 실패한다. 내보내기는 <see cref="ReadAll"/>을 명시적으로 호출한다.
/// </summary>
public sealed class VirtualDisplayRows : IList<string[]>, IList
{
    private readonly VirtualCsvDocument _document;
    public int IndexReads { get; private set; }
    public int EnumerationAttempts { get; private set; }

    public VirtualDisplayRows(VirtualCsvDocument document)
        => _document = document ?? throw new ArgumentNullException(nameof(document));

    public int Count => _document.DisplayRowCount;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public string[] this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            IndexReads++;
            return _document.GetDisplayRow(index);
        }
        set => throw new NotSupportedException("Edit through CellEdits, not the display list.");
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException("Edit through CellEdits, not the display list.");
    }

    public IEnumerable<string[]> ReadAll()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    public void CopyTo(string[][] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < array.Length - arrayIndex; i++)
            array[arrayIndex + i] = this[i];
    }

    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (int i = 0; i < array.Length - index; i++)
            array.SetValue(this[i], index + i);
    }

    public int IndexOf(string[] item) => throw NotIndexed();
    public int IndexOf(object? value) => throw NotIndexed();
    public bool Contains(string[] item) => throw NotIndexed();
    public bool Contains(object? value) => throw NotIndexed();
    public void Add(string[] item) => throw new NotSupportedException();
    public int Add(object? value) => throw new NotSupportedException();
    public void Insert(int index, string[] item) => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public bool Remove(string[] item) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();

    public IEnumerator<string[]> GetEnumerator()
    {
        EnumerationAttempts++;
        throw NotIndexed();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static NotSupportedException NotIndexed()
        => new("Display rows are index-only. Enumerating them would read the whole file.");
}
