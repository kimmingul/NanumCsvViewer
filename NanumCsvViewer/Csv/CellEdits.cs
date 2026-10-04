using System.Collections.Concurrent;
using System.Text;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 시트 편집으로 추가한 행. Anchor = 이 행 바로 위에 오는 행의 번호(-1이면 맨 위), Values = 추가 시점의 빈 값(컬럼 수).
    /// 번호(id)는 BaseRows + 추가 순서이며 삭제해도 재사용하지 않는다.
    /// </summary>
    public sealed record AddedRow(int Anchor, string[] Values);

    /// <summary>덮개 전체의 값 스냅샷(크래시 복구 저널용). 행 번호는 모두 "행 id"(원본 0..BaseRows-1, 추가 행은 그 뒤)다.</summary>
    public sealed record EditSnapshot(
        int BaseRows,
        (int Row, int Col, string Value)[] Cells,
        (int Col, string Name)[] Headers,
        int[] Deleted,
        AddedRow[] Added,
        string[]? AppendedColumns = null,
        int AppendBase = -1,
        int[]? DeletedColumns = null);

    /// <summary>
    /// 편집 덮개(overlay). 원본 파일은 절대 바꾸지 않고 다음을 보관한다:
    ///  - (행 id, 컬럼) → 문자열 셀 편집 — 값은 항상 "문자열 그대로"라 001의 선행 0이 사라지지 않는다.
    ///  - 컬럼 이름 변경(헤더 덮개)
    ///  - 행 삭제 집합 + 추가 행(앵커 기반 위치)
    ///  - 추가 컬럼(정규식 추출 등): 헤더 이름 + (행 id, 컬럼) 셀 값. 값이 비어 있지 않은 셀만 저장하므로 메모리는 O(비어 있지 않은 값)이다.
    ///    추가 컬럼의 번호는 AppendBase(원본 컬럼 수)부터 이어 붙는다. 읽기 경로는 모든 행을 전체 너비로 맞춘다(ExpandRaw).
    ///  - 컬럼 삭제: "물리 컬럼"(원본 + 추가, 삭제해도 번호가 밀리지 않는 공간)에 삭제 표시만 한다. 읽기 경로(ParseDataRow/헤더)가 삭제된 칸을 걷어내므로
    ///    문서·그리드·필터·분석·내보내기·저장은 모두 "보이는 컬럼"(삭제 제외)만 본다. 이 클래스의 공개 셀·헤더 API는 보이는 컬럼 번호를 받아
    ///    내부에서 물리 번호로 바꿔 기록하므로, 뒤에 컬럼을 삭제·복원해도 이력의 되돌리기가 어긋나지 않는다(LIFO).
    /// 모든 변경은 되돌리기/다시 실행 이력에 단계(step)로 쌓인다 — 한 번의 커밋·붙여넣기·삭제 = 한 단계.
    /// </summary>
    public sealed class CellEdits
    {
        /// <summary>되돌리기 이력 한도(단계 수 / 변경 건수). 넘으면 가장 오래된 단계부터 버린다.</summary>
        public const int MaxUndoSteps = 500;
        public const int MaxUndoChanges = 2_000_000;

        private readonly ConcurrentDictionary<(int Row, int Col), string> _cells = new();
        // 행별 편집 컬럼(읽기 경로의 빠른 판정용). 값은 컬럼 → 문자열.
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, string>> _rows = new();
        private readonly ConcurrentDictionary<int, string> _headers = new();

        private readonly object _gate = new(); // _deleted / _added / _baseRows
        private readonly HashSet<int> _deleted = new();
        private readonly List<AddedRow> _added = new();
        private int _baseRows = -1;
        private readonly List<string> _appended = new(); // 추가 컬럼 이름(_gate로 보호)
        private int _appendBase = -1;                     // 첫 추가 컬럼이 놓이는 인덱스 = 원본 컬럼 수
        private volatile int[] _delCols = Array.Empty<int>(); // 삭제된 물리 컬럼(오름차순, 복사-후-교체라 락 없이 읽는다)

        private readonly List<EditStep> _undo = new();
        private readonly List<EditStep> _redo = new();
        private long _undoChanges;
        private int _savedPos;
        private List<EditChange>? _open;
        private string? _openDescription;
        private int _depth;

        /// <summary>변경될 때마다 한 번(단계 단위로 묶어서) 호출. 구독 순서대로 실행된다.</summary>
        public event Action? Changed;

        /// <summary>편집된 셀 수(삭제된 행의 셀 포함).</summary>
        public int Count => _cells.Count;
        public int HeaderEditCount => _headers.Count;
        public int DeletedCount { get { lock (_gate) return _deleted.Count; } }
        public int AddedCount { get { lock (_gate) return _added.Count; } }
        public bool IsEmpty => _cells.IsEmpty && _headers.IsEmpty && !HasStructureEdits && !HasAppendedColumns && !HasDeletedColumns;

        /// <summary>행 삭제/추가가 있는가(행 구조가 원본과 다른가).</summary>
        public bool HasStructureEdits { get { lock (_gate) return _deleted.Count > 0 || _added.Count > 0; } }

        /// <summary>추가 컬럼 수.</summary>
        public int AppendedColumnCount { get { lock (_gate) return _appended.Count; } }
        public bool HasAppendedColumns { get { lock (_gate) return _appended.Count > 0; } }

        /// <summary>삭제한 컬럼 수 / 있는가.</summary>
        public int DeletedColumnCount => _delCols.Length;
        public bool HasDeletedColumns => _delCols.Length > 0;

        /// <summary>삭제된 물리 컬럼 번호(오름차순). 저널·테스트용.</summary>
        public int[] DeletedColumns() => (int[])_delCols.Clone();

        /// <summary>보이는 컬럼 번호 → 물리 컬럼 번호(삭제된 칸을 건너뜀).</summary>
        public int ToPhysical(int visibleColumn)
        {
            int p = visibleColumn;
            foreach (int d in _delCols) { if (d <= p) p++; else break; }
            return p;
        }

        /// <summary>물리 컬럼 번호 → 보이는 컬럼 번호. 삭제된 컬럼이면 -1.</summary>
        public int ToVisible(int physicalColumn)
        {
            int n = 0;
            foreach (int d in _delCols)
            {
                if (d == physicalColumn) return -1;
                if (d < physicalColumn) n++; else break;
            }
            return physicalColumn - n;
        }

        /// <summary>물리 너비(원본 + 추가 컬럼). rawColumnCount = 파일의 원본 컬럼 수.</summary>
        public int PhysicalWidth(int rawColumnCount) { lock (_gate) return rawColumnCount + _appended.Count; }

        /// <summary>보이는 컬럼들의 물리 번호(왼쪽부터). 그리드 컬럼 동기화용.</summary>
        public int[] VisiblePhysicalColumns(int rawColumnCount)
        {
            int width = PhysicalWidth(rawColumnCount);
            var del = _delCols;
            var result = new List<int>(Math.Max(0, width - del.Length));
            int di = 0;
            for (int p = 0; p < width; p++)
            {
                while (di < del.Length && del[di] < p) di++;
                if (di < del.Length && del[di] == p) continue;
                result.Add(p);
            }
            return result.ToArray();
        }

        /// <summary>물리 번호의 행에서 삭제된 컬럼 칸을 걷어낸다. 삭제가 없으면 입력 그대로.</summary>
        public string[] DropDeletedColumns(string[] fields)
        {
            var del = _delCols;
            if (del.Length == 0) return fields;
            int inRange = 0;
            foreach (int d in del) { if (d < fields.Length) inRange++; else break; }
            if (inRange == 0) return fields;
            var result = new string[fields.Length - inRange];
            int di = 0, w = 0;
            for (int i = 0; i < fields.Length; i++)
            {
                if (di < del.Length && del[di] == i) { di++; continue; }
                result[w++] = fields[i];
            }
            return result;
        }

        /// <summary>첫 추가 컬럼의 인덱스(= 추가 시점의 원본 컬럼 수). 추가 컬럼이 없으면 -1.</summary>
        public int AppendBase { get { lock (_gate) return _appended.Count > 0 ? _appendBase : -1; } }

        /// <summary>추가 컬럼 이름(추가한 순서). 이름 변경(헤더 덮개)은 반영하지 않은 "원래" 이름.</summary>
        public string[] AppendedColumnNames() { lock (_gate) return _appended.ToArray(); }

        /// <summary>col이 추가 컬럼의 인덱스인가.</summary>
        public bool IsAppendedColumn(int col)
        {
            int p = ToPhysical(col);
            lock (_gate) return _appended.Count > 0 && p >= _appendBase && p < _appendBase + _appended.Count;
        }

        /// <summary>모든 변경에서 증가. 분석 결과 창의 "데이터가 바뀜" 판정과 디바운스용.</summary>
        public long Version { get; private set; }
        public long StructureVersion { get; private set; }
        public long HeaderVersion { get; private set; }

        /// <summary>추가 행이 기준으로 삼은 원본 데이터 행 수(없으면 -1).</summary>
        public int BaseRowCount { get { lock (_gate) return _baseRows; } }

        // ------------------------------------------------------------ 셀

        // 공개 셀·헤더 API의 col은 "보이는 컬럼 번호"(삭제 제외). 내부 저장·이력은 물리 번호.
        public bool Contains(int dataRow, int col) => _cells.ContainsKey((dataRow, ToPhysical(col)));

        public bool HasRowEdits(int dataRow) => _rows.ContainsKey(dataRow);

        public bool TryGet(int dataRow, int col, out string value) => _cells.TryGetValue((dataRow, ToPhysical(col)), out value!);

        /// <summary>편집값 저장. original과 같으면 편집을 제거(되돌림)한다. 같은 값을 다시 넣으면 이력도 남기지 않는다.</summary>
        public void Set(int dataRow, int col, string value, string original)
        {
            if (string.Equals(value, original, StringComparison.Ordinal)) { Revert(dataRow, col); return; }
            int p = ToPhysical(col);
            _cells.TryGetValue((dataRow, p), out string? old);
            if (old is not null && string.Equals(old, value, StringComparison.Ordinal)) return;
            Record(new CellChange(dataRow, p, old, value));
        }

        public void Revert(int dataRow, int col)
        {
            int p = ToPhysical(col);
            if (!_cells.TryGetValue((dataRow, p), out string? old)) return;
            Record(new CellChange(dataRow, p, old, null));
        }
        /// <summary>편집된 행이면 복사본에 덮어쓴 새 배열(컬럼이 모자라면 확장), 아니면 원본 그대로. 삭제한 컬럼 칸은 걷어낸다(보이는 컬럼 기준 행).</summary>
        public string[] Apply(int dataRow, string[] fields)
        {
            if (_rows.IsEmpty || !_rows.TryGetValue(dataRow, out var cols)) return DropDeletedColumns(fields);
            int width = fields.Length;
            foreach (var kv in cols) if (kv.Key >= width) width = kv.Key + 1;
            var copy = new string[width];
            Array.Copy(fields, copy, fields.Length);
            for (int i = fields.Length; i < width; i++) copy[i] = string.Empty;
            foreach (var kv in cols) copy[kv.Key] = kv.Value;
            return DropDeletedColumns(copy);
        }

        // ------------------------------------------------------------ 헤더

        public bool TryGetHeader(int col, out string name) => _headers.TryGetValue(ToPhysical(col), out name!);

        /// <summary>컬럼 이름 변경. original(파일의 원래 이름)과 같으면 변경을 제거한다.</summary>
        public void SetHeader(int col, string name, string original)
        {
            int p = ToPhysical(col);
            if (string.Equals(name, original, StringComparison.Ordinal))
            {
                if (_headers.TryGetValue(p, out string? cur)) Record(new HeaderChange(p, cur, null));
                return;
            }
            _headers.TryGetValue(p, out string? old);
            if (old is not null && string.Equals(old, name, StringComparison.Ordinal)) return;
            Record(new HeaderChange(p, old, name));
        }

        /// <summary>원본 헤더에 추가 컬럼 이름과 이름 변경을 반영하고 삭제한 컬럼을 뺀 새 배열(변경이 없으면 원본 그대로).</summary>
        public string[] ApplyHeader(string[] raw)
        {
            string[] appended;
            lock (_gate) appended = _appended.ToArray();
            if (_headers.IsEmpty && appended.Length == 0 && _delCols.Length == 0) return raw;
            var copy = new string[raw.Length + appended.Length];
            Array.Copy(raw, copy, raw.Length);
            Array.Copy(appended, 0, copy, raw.Length, appended.Length);
            foreach (var kv in _headers) if (kv.Key >= 0 && kv.Key < copy.Length) copy[kv.Key] = kv.Value;
            return DropDeletedColumns(copy);
        }

        /// <summary>
        /// 원본(또는 추가 행 기본값) 행을 추가 컬럼이 있는 너비로 맞춘다(셀 편집 덮개 적용 전).
        /// 원본 행: AppendBase 위치에 빈 추가 컬럼 칸을 끼워 넣고(헤더보다 긴 행의 남는 필드는 그 뒤로 보존), 모자라면 빈 칸으로 채운다.
        /// 추가 행: 이미 전체 너비로 만들어졌으므로 모자랄 때만 채운다. 추가 컬럼이 없으면 입력 그대로.
        /// </summary>
        public string[] ExpandRaw(string[] fields, bool isAddedRow)
        {
            int n, b;
            lock (_gate) { n = _appended.Count; b = _appendBase; }
            if (n == 0) return fields;
            int width = b + n;
            if (isAddedRow || fields.Length <= b)
            {
                if (fields.Length >= width) return fields;
                var padded = new string[width];
                Array.Copy(fields, padded, fields.Length);
                for (int i = fields.Length; i < width; i++) padded[i] = string.Empty;
                return padded;
            }
            var result = new string[fields.Length + n];
            Array.Copy(fields, result, b);
            for (int i = 0; i < n; i++) result[b + i] = string.Empty;
            Array.Copy(fields, b, result, b + n, fields.Length - b);
            return result;
        }

        /// <summary>
        /// 컬럼을 맨 뒤에 추가한다(값은 전부 빈 값). rawColumnCount = 파일의 원본 컬럼 수.
        /// 같은 단계(BeginStep) 안에서 Set으로 값을 채우면 되돌리기 한 번에 컬럼과 값이 함께 사라진다. 새 컬럼의 인덱스를 돌려준다.
        /// </summary>
        public int AppendColumn(string name, int rawColumnCount, string? description = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The column name cannot be empty.", nameof(name));
            int index;
            lock (_gate)
            {
                if (_appended.Count > 0 && _appendBase != rawColumnCount) throw new InvalidOperationException("Raw column count changed.");
                index = rawColumnCount + _appended.Count;
            }
            using (BeginStep(description)) Record(new ColumnChange(name, rawColumnCount, true));
            return index - _delCols.Length; // 보이는 컬럼 번호(삭제된 컬럼은 모두 새 컬럼보다 앞)
        }

        /// <summary>
        /// 보이는 컬럼 하나를 삭제 표시한다(한 단계, 원본·추가 컬럼 모두). 그 컬럼의 셀 편집·이름 변경은 같은 단계에서 함께 버려지고
        /// 되돌리기로 모두 복원된다. 남는 컬럼이 없어지게는 못 한다. 삭제한 컬럼의 (원래) 이름을 돌려준다.
        /// visibleColumnCount = 지금 보이는 컬럼 수, rawColumnCount = 파일의 원본 컬럼 수.
        /// </summary>
        public int DeleteColumn(int visibleColumn, int visibleColumnCount, int rawColumnCount, string? description = null)
        {
            if (visibleColumn < 0 || visibleColumn >= visibleColumnCount) throw new ArgumentOutOfRangeException(nameof(visibleColumn));
            if (visibleColumnCount <= 1) throw new InvalidOperationException("The last remaining column cannot be deleted.");
            int p = ToPhysical(visibleColumn);
            lock (_gate)
            {
                if (_appended.Count > 0 && _appendBase != rawColumnCount) throw new InvalidOperationException("Raw column count changed.");
            }
            using (BeginStep(description))
            {
                foreach (var kv in _cells.ToArray())
                    if (kv.Key.Col == p) Record(new CellChange(kv.Key.Row, p, kv.Value, null));
                if (_headers.TryGetValue(p, out string? name)) Record(new HeaderChange(p, name, null));
                Record(new ColumnDeleteChange(p, true));
            }
            return p;
        }

        // ------------------------------------------------------------ 행 구조

        public bool IsDeleted(int rowId) { lock (_gate) return _deleted.Contains(rowId); }

        /// <summary>추가 행의 현재 기본값(편집 덮개 적용 전). 추가 행이 아니면 null.</summary>
        public string[]? GetAddedBase(int rowId)
        {
            lock (_gate)
            {
                if (_baseRows < 0 || rowId < _baseRows) return null;
                int i = rowId - _baseRows;
                return i < _added.Count ? (string[])_added[i].Values.Clone() : null;
            }
        }

        public bool IsAddedRow(int rowId)
        {
            lock (_gate) return _baseRows >= 0 && rowId >= _baseRows && rowId - _baseRows < _added.Count;
        }

        /// <summary>행 id 상한(원본 + 추가). baseRows는 문서의 원본 데이터 행 수.</summary>
        public int TotalRowIds(int baseRows) { lock (_gate) return baseRows + _added.Count; }

        /// <summary>행을 삭제 표시(한 단계). 이미 삭제된 행은 무시. 삭제한 행 수를 돌려준다.</summary>
        public int DeleteRows(IEnumerable<int> rowIds, string? description = null)
        {
            int n = 0;
            using (BeginStep(description))
            {
                foreach (int id in rowIds.Distinct())
                {
                    bool already;
                    lock (_gate) already = _deleted.Contains(id);
                    if (already) continue;
                    Record(new DeleteChange(id, false, true));
                    n++;
                }
            }
            return n;
        }

        /// <summary>anchorId 바로 아래(-1이면 맨 위)에 빈 행을 추가하고 새 행 id를 돌려준다.</summary>
        public int AddRow(int anchorId, int columnCount, int baseRows, string? description = null)
        {
            int id;
            lock (_gate)
            {
                if (_baseRows >= 0 && _baseRows != baseRows) throw new InvalidOperationException("Base row count changed.");
                if (anchorId < -1 || anchorId >= baseRows + _added.Count) throw new ArgumentOutOfRangeException(nameof(anchorId));
                _baseRows = baseRows;
                id = baseRows + _added.Count;
            }
            // columnCount = 보이는 컬럼 수. 행 값은 물리 너비(삭제한 컬럼 칸 포함)로 만든다 — 읽기 경로가 삭제된 칸을 걷어낸다.
            var values = new string[Math.Max(0, columnCount) + _delCols.Length];
            Array.Fill(values, string.Empty);
            using (BeginStep(description)) Record(new RowChange(new AddedRow(anchorId, values), true));
            return id;
        }

        /// <summary>
        /// 화면 순서(삭제 제외, 추가 행은 앵커 아래)의 행 id 배열. 같은 앵커에 나중에 넣은 행이 앵커에 더 가깝다.
        /// 재귀 없이 순회하므로 "아래에 계속 추가"한 긴 사슬에서도 스택이 넘치지 않는다.
        /// </summary>
        public int[] BuildLiveOrder(int baseRows)
        {
            lock (_gate)
            {
                var kids = new Dictionary<int, List<int>>();
                for (int i = 0; i < _added.Count; i++)
                {
                    int a = _added[i].Anchor;
                    if (!kids.TryGetValue(a, out var list)) kids[a] = list = new List<int>();
                    list.Add(baseRows + i); // 생성 순서. 순회는 뒤에서부터(최근 것이 앵커에 가깝다).
                }

                var result = new List<int>(Math.Max(0, baseRows + _added.Count - _deleted.Count));
                bool anyDeleted = _deleted.Count > 0;

                void Walk(int root)
                {
                    if (!kids.TryGetValue(root, out var first)) return;
                    var stack = new Stack<(List<int> List, int Index)>();
                    stack.Push((first, first.Count - 1));
                    while (stack.Count > 0)
                    {
                        var (list, idx) = stack.Pop();
                        if (idx < 0) continue;
                        stack.Push((list, idx - 1));
                        int child = list[idx];
                        if (!anyDeleted || !_deleted.Contains(child)) result.Add(child);
                        if (kids.TryGetValue(child, out var grand)) stack.Push((grand, grand.Count - 1));
                    }
                }

                Walk(-1);
                for (int id = 0; id < baseRows; id++)
                {
                    if (!anyDeleted || !_deleted.Contains(id)) result.Add(id);
                    Walk(id);
                }
                return result.ToArray();
            }
        }

        // ------------------------------------------------------------ 단계 · 되돌리기

        /// <summary>
        /// 여러 변경을 한 단계로 묶는다(using). 가장 바깥 범위가 끝날 때 변경이 있으면 이력에 한 단계로 쌓고
        /// Changed를 한 번만 호출한다. 중첩 가능.
        /// </summary>
        public IDisposable BeginStep(string? description = null)
        {
            if (_depth == 0) { _open = new List<EditChange>(); _openDescription = description; }
            _depth++;
            return new StepScope(this);
        }

        private sealed class StepScope(CellEdits owner) : IDisposable
        {
            private CellEdits? _owner = owner;
            public void Dispose()
            {
                var o = _owner;
                if (o is null) return;
                _owner = null;
                o.EndStep();
            }
        }

        private void EndStep()
        {
            if (--_depth > 0) return;
            var changes = _open!;
            string? description = _openDescription;
            _open = null;
            _openDescription = null;
            if (changes.Count == 0) return;
            Push(new EditStep(description, changes));
            Raise();
        }

        private void Record(EditChange change)
        {
            change.Apply(this, forward: true);
            if (_depth > 0) { _open!.Add(change); return; }
            Push(new EditStep(null, new List<EditChange> { change }));
            Raise();
        }

        private void Push(EditStep step)
        {
            if (_redo.Count > 0)
            {
                if (_savedPos > _undo.Count) _savedPos = -1; // 저장 시점이 버려지는 재실행 가지에 있었다
                _redo.Clear();
            }
            _undo.Add(step);
            _undoChanges += step.Changes.Count;
            while (_undo.Count > 1 && (_undo.Count > MaxUndoSteps || _undoChanges > MaxUndoChanges))
            {
                _undoChanges -= _undo[0].Changes.Count;
                _undo.RemoveAt(0);
                if (_savedPos >= 0) _savedPos--; // 저장 시점이 이력 밖으로 밀리면 -1(도달 불가)
            }
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
        public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

        public bool Undo()
        {
            if (_depth > 0 || _undo.Count == 0) return false;
            var step = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _undoChanges -= step.Changes.Count;
            for (int i = step.Changes.Count - 1; i >= 0; i--) step.Changes[i].Apply(this, forward: false);
            _redo.Add(step);
            Raise();
            return true;
        }

        public bool Redo()
        {
            if (_depth > 0 || _redo.Count == 0) return false;
            var step = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            foreach (var c in step.Changes) c.Apply(this, forward: true);
            _undo.Add(step);
            _undoChanges += step.Changes.Count;
            Raise();
            return true;
        }

        /// <summary>모든 편집(셀·이름·삭제·추가 행·추가 컬럼)을 버린다. 한 단계라 되돌릴 수 있다.</summary>
        public void Clear()
        {
            if (IsEmpty) return;
            using (BeginStep())
            {
                foreach (var kv in _cells.ToArray()) Record(new CellChange(kv.Key.Row, kv.Key.Col, kv.Value, null));
                foreach (var kv in _headers.ToArray()) Record(new HeaderChange(kv.Key, kv.Value, null));
                int[] deleted;
                AddedRow[] added;
                lock (_gate) { deleted = _deleted.ToArray(); added = _added.ToArray(); }
                foreach (int id in deleted) Record(new DeleteChange(id, true, false));
                for (int i = added.Length - 1; i >= 0; i--) Record(new RowChange(added[i], false)); // 뒤에서부터 제거
                foreach (int d in _delCols) Record(new ColumnDeleteChange(d, false)); // 추가 컬럼을 지우기 전에 삭제 표시를 푼다
                string[] columns;
                int appendBase;
                lock (_gate) { columns = _appended.ToArray(); appendBase = _appendBase; }
                for (int i = columns.Length - 1; i >= 0; i--) Record(new ColumnChange(columns[i], appendBase, false));
            }
        }

        /// <summary>지금 상태를 "저장된 상태"로 표시(저장 성공 후).</summary>
        public void MarkSaved() => _savedPos = _undo.Count;

        /// <summary>마지막 저장(또는 열기) 이후 변경이 있는가. 되돌려서 같은 상태로 오면 false.</summary>
        public bool IsDirty => _savedPos != _undo.Count;

        // ------------------------------------------------------------ 저널 스냅샷

        public EditSnapshot Snapshot()
        {
            var cells = _cells.Select(kv => (kv.Key.Row, kv.Key.Col, kv.Value)).OrderBy(t => t.Row).ThenBy(t => t.Col).ToArray();
            var headers = _headers.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Key).ToArray();
            lock (_gate)
            {
                var deleted = _deleted.ToArray();
                Array.Sort(deleted);
                return new EditSnapshot(_baseRows, cells, headers, deleted,
                    _added.Select(a => new AddedRow(a.Anchor, (string[])a.Values.Clone())).ToArray(),
                    _appended.Count > 0 ? _appended.ToArray() : null, _appended.Count > 0 ? _appendBase : -1,
                    _delCols.Length > 0 ? (int[])_delCols.Clone() : null);
            }
        }

        /// <summary>
        /// 저널 스냅샷을 빈 덮개에 되살린다. 이력은 만들지 않고(복구분은 되돌릴 수 없다) 저장 안 된 상태로 표시한다.
        /// 문서와 맞지 않는 값(범위 밖 행·컬럼)이면 아무것도 바꾸지 않고 InvalidDataException.
        /// </summary>
        public void Restore(EditSnapshot s, int baseRows, int columnCount)
        {
            if (!IsEmpty || _undo.Count > 0) throw new InvalidOperationException("Restore needs an empty overlay.");
            if (s.BaseRows >= 0 && s.BaseRows != baseRows)
                throw new InvalidDataException("Recovery data was made for a different row count.");
            int total = baseRows + s.Added.Length;
            for (int i = 0; i < s.Added.Length; i++)
            {
                var a = s.Added[i];
                if (a is null || a.Values is null || a.Anchor < -1 || a.Anchor >= baseRows + i)
                    throw new InvalidDataException("Recovery data has an invalid added row.");
            }
            foreach (var (row, col, value) in s.Cells)
                if (row < 0 || row >= total || col < 0 || col >= 1 << 20 || value is null)
                    throw new InvalidDataException("Recovery data has a cell outside the table.");
            int appendedCount = s.AppendedColumns?.Length ?? 0;
            if (appendedCount > 0)
            {
                if (s.AppendBase != columnCount)
                    throw new InvalidDataException("Recovery data was made for a different column count.");
                if (s.AppendedColumns!.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException("Recovery data has an empty appended column name.");
            }
            foreach (var (col, name) in s.Headers)
                if (col < 0 || col >= columnCount + appendedCount || name is null)
                    throw new InvalidDataException("Recovery data has a column outside the table.");
            foreach (int id in s.Deleted)
                if (id < 0 || id >= total) throw new InvalidDataException("Recovery data has an invalid deleted row.");
            int[] delCols = s.DeletedColumns is { Length: > 0 } dc ? dc.Distinct().OrderBy(x => x).ToArray() : Array.Empty<int>();
            foreach (int d in delCols)
                if (d < 0 || d >= columnCount + appendedCount) throw new InvalidDataException("Recovery data has an invalid deleted column.");
            if (delCols.Length >= columnCount + appendedCount && delCols.Length > 0)
                throw new InvalidDataException("Recovery data deletes every column.");

            lock (_gate)
            {
                if (s.Added.Length > 0 || s.Deleted.Length > 0) _baseRows = baseRows;
                foreach (var a in s.Added) _added.Add(new AddedRow(a.Anchor, (string[])a.Values.Clone()));
                foreach (int id in s.Deleted) _deleted.Add(id);
                if (s.Added.Length > 0 || s.Deleted.Length > 0) StructureVersion++;
                if (appendedCount > 0) { _appendBase = columnCount; _appended.AddRange(s.AppendedColumns!); }
            }
            _delCols = delCols;
            if (delCols.Length > 0) HeaderVersion++;
            foreach (var (row, col, value) in s.Cells) RawCell(row, col, value);
            foreach (var (col, name) in s.Headers) RawHeader(col, name);
            if (s.Headers.Length > 0 || appendedCount > 0) HeaderVersion++;
            _savedPos = -1;
            Raise();
        }

        // ------------------------------------------------------------ 내부

        private void Raise()
        {
            Version++;
            Changed?.Invoke();
        }

        private void RawCell(int row, int col, string? value)
        {
            if (value is null)
            {
                if (!_cells.TryRemove((row, col), out _)) return;
                if (_rows.TryGetValue(row, out var cols))
                {
                    cols.TryRemove(col, out _);
                    if (cols.IsEmpty) _rows.TryRemove(row, out _);
                }
                return;
            }
            _cells[(row, col)] = value;
            _rows.GetOrAdd(row, _ => new ConcurrentDictionary<int, string>())[col] = value;
        }

        private void RawHeader(int col, string? name)
        {
            if (name is null) _headers.TryRemove(col, out _);
            else _headers[col] = name;
        }

        private void RawDeleted(int row, bool deleted)
        {
            lock (_gate)
            {
                if (deleted) _deleted.Add(row); else _deleted.Remove(row);
                StructureVersion++;
            }
        }

        private void RawAddRow(AddedRow row)
        {
            lock (_gate) { _added.Add(row); StructureVersion++; }
        }

        private void RawRemoveLastRow()
        {
            lock (_gate)
            {
                if (_added.Count == 0) throw new InvalidOperationException("No added row to remove.");
                _added.RemoveAt(_added.Count - 1);
                StructureVersion++;
            }
        }

        private void RawAppendColumn(string name, int rawColumnCount)
        {
            lock (_gate)
            {
                if (_appended.Count == 0) _appendBase = rawColumnCount;
                _appended.Add(name);
            }
            HeaderVersion++;
        }

        private void RawRemoveLastColumn()
        {
            lock (_gate)
            {
                if (_appended.Count == 0) throw new InvalidOperationException("No appended column to remove.");
                _appended.RemoveAt(_appended.Count - 1);
            }
            HeaderVersion++;
        }

        private void RawSetColumnDeleted(int physical, bool deleted)
        {
            lock (_gate)
            {
                var list = new List<int>(_delCols);
                if (deleted) { if (!list.Contains(physical)) list.Add(physical); } else list.Remove(physical);
                list.Sort();
                _delCols = list.ToArray();
            }
            HeaderVersion++;
        }

        private abstract class EditChange
        {
            public abstract void Apply(CellEdits e, bool forward);
        }

        private sealed class CellChange(int row, int col, string? old, string? @new) : EditChange
        {
            public override void Apply(CellEdits e, bool forward) => e.RawCell(row, col, forward ? @new : old);
        }

        private sealed class HeaderChange(int col, string? old, string? @new) : EditChange
        {
            public override void Apply(CellEdits e, bool forward)
            {
                e.RawHeader(col, forward ? @new : old);
                e.HeaderVersion++;
            }
        }

        private sealed class DeleteChange(int row, bool old, bool @new) : EditChange
        {
            public override void Apply(CellEdits e, bool forward) => e.RawDeleted(row, forward ? @new : old);
        }

        private sealed class RowChange(AddedRow row, bool added) : EditChange
        {
            public override void Apply(CellEdits e, bool forward)
            {
                if (forward == added) e.RawAddRow(row); else e.RawRemoveLastRow();
            }
        }

        private sealed class ColumnChange(string name, int rawColumnCount, bool added) : EditChange
        {
            public override void Apply(CellEdits e, bool forward)
            {
                if (forward == added) e.RawAppendColumn(name, rawColumnCount); else e.RawRemoveLastColumn();
            }
        }

        private sealed class ColumnDeleteChange(int physical, bool deleted) : EditChange
        {
            public override void Apply(CellEdits e, bool forward) => e.RawSetColumnDeleted(physical, forward ? deleted : !deleted);
        }

        private sealed record EditStep(string? Description, List<EditChange> Changes);

        /// <summary>새 텍스트의 줄바꿈을 원래 값의 스타일(CRLF/LF)에 맞춘다. 원래 값에 CRLF가 있으면 그대로 둔다.</summary>
        public static string MatchNewlineStyle(string text, string original)
            => original.Contains("\r\n", StringComparison.Ordinal) ? text : text.Replace("\r\n", "\n");

        // ------------------------------------------------------------ CSV 직렬화

        /// <summary>
        /// CSV 필드 직렬화. 구분자·따옴표·줄바꿈이 있거나 앞뒤 공백이 있으면 따옴표로 감싼다.
        /// 값은 해석하지 않고 그대로 쓴다(선행 0 보존). 선행 0 숫자는 CSV에서 따옴표와 무관하게 텍스트로 저장되며,
        /// 엑셀이 열 때 0을 지우는 것은 엑셀의 동작이다(앱은 값 자체를 바꾸지 않는다).
        /// </summary>
        public static string QuoteField(string value, char delimiter)
        {
            bool needs = value.Length > 0 &&
                         (value.IndexOf(delimiter) >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 ||
                          value.IndexOf('\r') >= 0 || value[0] == ' ' || value[^1] == ' ');
            if (!needs) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        public static string JoinRecord(IReadOnlyList<string> fields, char delimiter)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                sb.Append(QuoteField(fields[i], delimiter));
            }
            return sb.ToString();
        }
    }
}
