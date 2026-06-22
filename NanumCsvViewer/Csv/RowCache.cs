namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 파싱된 행(string[])의 LRU 캐시. 최근 사용 행만 유지하여 파일 크기와 무관하게
    /// 메모리를 일정하게 유지합니다. UI 스레드와 프리페치 스레드가 함께 접근하므로 잠금으로 보호.
    /// </summary>
    public sealed class RowCache
    {
        private readonly int _capacity;
        private readonly Dictionary<int, LinkedListNode<(int row, string[] fields)>> _map;
        private readonly LinkedList<(int row, string[] fields)> _lru = new();
        private readonly object _gate = new();

        public RowCache(int capacity)
        {
            _capacity = Math.Max(16, capacity);
            _map = new Dictionary<int, LinkedListNode<(int, string[])>>(_capacity);
        }

        public bool TryGet(int row, out string[] fields)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(row, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    fields = node.Value.fields;
                    return true;
                }
            }
            fields = Array.Empty<string>();
            return false;
        }

        public void Add(int row, string[] fields)
        {
            lock (_gate)
            {
                if (_map.ContainsKey(row)) return;
                var node = new LinkedListNode<(int, string[])>((row, fields));
                _lru.AddFirst(node);
                _map[row] = node;
                if (_map.Count > _capacity)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.row);
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _lru.Clear();
            }
        }
    }
}
