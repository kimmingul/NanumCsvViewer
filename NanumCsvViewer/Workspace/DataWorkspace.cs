using DuckDB.NET.Data;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    public sealed record DataWorkspaceOptions
    {
        /// <summary>DuckDB 메모리 상한(바이트). null이면 물리 메모리의 절반(최소 512MB, 최대 32GB).</summary>
        public long? MemoryLimitBytes { get; init; }
        /// <summary>DuckDB 스레드 수. null이면 (논리 코어 − 1), 최소 1 — UI가 계속 반응하도록 코어 하나를 남긴다.</summary>
        public int? Threads { get; init; }
        /// <summary>임시 파일 루트. null이면 %TEMP%\NanumCsvViewer\duck. 이 아래에 spill(디스크 넘침)·UTF-8 사본·뷰 결과를 둔다.</summary>
        public string? TempRoot { get; init; }
    }

    /// <summary>
    /// 작업 공간 엔진: 프로세스 내장 DuckDB 하나(메모리 DB)에 CSV 원본·DB 원본·뷰를 등록하고 질의한다. WinForms에 의존하지 않는다.
    /// <para><b>노출 방식</b> — CSV 파일 하나 = 표 하나. 표 <c>T</c>는 두 개의 DuckDB 뷰로 등록된다:
    /// <c>T__raw</c>(모든 컬럼 VARCHAR, 파일의 글자 그대로)와 <c>T</c>(같은 컬럼 이름·순서, 앱이 추론한 정수·실수·통화·퍼센트·불리언·날짜·시각 컬럼만 TRY_CAST).
    /// 그래서 <c>SELECT * FROM T</c>가 표 그대로이고 <c>WHERE 나이 &gt; 30</c> 같은 식이 바로 동작한다.
    /// 식별자·범주·문자열 컬럼은 VARCHAR 그대로(<c>001</c> 보존). 변환 실패는 NULL이고 <see cref="CheckTypedColumnsAsync"/>가 실패 수를 보고한다.
    /// 원문이 필요하면 <c>SELECT * FROM T__raw</c>. DB 원본(엑셀·SAS·SPSS·SQLite)은 DuckDB 스키마 하나이고 시트·표가 그 안의 표다(<c>설문.명단</c>).</para>
    /// <para><b>스레드</b> — 공개 메서드는 모두 스레드 안전하다. 질의는 호출마다 별도 연결(같은 DB)을 열어 병렬 실행·개별 취소가 된다.
    /// 동기 메서드 중 파일을 읽는 것(<see cref="AddCsv"/> 등)은 큰 파일이면 오래 걸리므로 UI 스레드가 아니라 백그라운드에서 부른다(<c>Async</c> 짝이 있다).</para>
    /// <para>이름 규칙은 <see cref="SqlNames"/>. 원본 파일은 읽기만 하며 절대 쓰지 않는다.</para>
    /// </summary>
    public sealed partial class DataWorkspace : IDisposable
    {
        /// <summary>타입을 추론할 때 앞에서부터 보는 행 수.</summary>
        public const int InferenceSampleRows = 2000;

        private readonly object _gate = new();
        private readonly DuckDBConnection _root;
        private readonly List<WorkspaceSource> _sources = new();
        private readonly List<WorkspaceView> _views = new();
        private readonly string _tempRoot, _sessionDir, _utf8Dir, _viewsDir;
        private long _memoryLimit;
        private int _threads;
        private bool _disposed;
        private long _viewFileCounter;

        /// <summary>"저장 안 한 편집 포함" 뷰의 결과를 계산할 때 앱이 편집 반영 스냅숏을 내주는 통로. null이면 저장된 파일 기준.</summary>
        public IEditSnapshotProvider? EditSnapshotProvider { get; set; }

        /// <summary>원본·뷰가 추가·제거·이름 변경·갱신되면 발생(변경한 스레드에서).</summary>
        public event EventHandler? Changed;

        public long MemoryLimitBytes => Interlocked.Read(ref _memoryLimit);
        public int Threads => _threads;

        // ---- 엔진 사용 가능 여부 ----------------------------------------------------------------

        private static readonly object AvailabilityLock = new();
        private static string? _unavailableReason;
        private static bool _availabilityChecked;

        /// <summary>
        /// DuckDB 네이티브 라이브러리를 읽어 메모리 DB를 열 수 있는가(결과는 캐시). 지원하지 않는 아키텍처이거나 네이티브 DLL이 빠졌으면
        /// false와 이유를 돌려주므로 호출 쪽은 작업 공간 기능만 끄고 계속 동작하면 된다.
        /// </summary>
        public static bool IsEngineAvailable(out string? reason)
        {
            lock (AvailabilityLock)
            {
                if (!_availabilityChecked)
                {
                    try
                    {
                        using var c = new DuckDBConnection("Data Source=:memory:");
                        c.Open();
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = "SELECT 1";
                        cmd.ExecuteScalar();
                    }
                    catch (Exception ex) // 네이티브 라이브러리 로드 실패는 형식이 다양하다(DllNotFound·BadImageFormat·TypeInitialization…). 무엇이든 "쓸 수 없음"으로 보고한다.
                    {
                        _unavailableReason = ex.GetBaseException().Message;
                    }
                    _availabilityChecked = true;
                }
                reason = _unavailableReason;
                return _unavailableReason is null;
            }
        }

        // ---- 생성·해제 --------------------------------------------------------------------------

        public DataWorkspace(DataWorkspaceOptions? options = null)
        {
            options ??= new DataWorkspaceOptions();
            if (!IsEngineAvailable(out string? reason))
                throw new WorkspaceEngineUnavailableException(
                    ViewerSupport.LT("The query engine (DuckDB) is not available on this system: ", "이 컴퓨터에서는 질의 엔진(DuckDB)을 쓸 수 없습니다: ") + reason);

            _tempRoot = options.TempRoot ?? Path.Combine(Path.GetTempPath(), "NanumCsvViewer", "duck");
            _utf8Dir = Path.Combine(_tempRoot, "utf8");
            string sessions = Path.Combine(_tempRoot, "sessions");
            SweepOrphanSessions(sessions);
            _sessionDir = Path.Combine(sessions, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
            _viewsDir = Path.Combine(_sessionDir, "views");
            Directory.CreateDirectory(_viewsDir);
            Directory.CreateDirectory(_utf8Dir);
            CsvUtf8Copy.Sweep(_utf8Dir, TimeSpan.FromDays(7));

            _memoryLimit = options.MemoryLimitBytes ?? DefaultMemoryLimit();
            _threads = Math.Max(1, options.Threads ?? Environment.ProcessorCount - 1);

            _root = new DuckDBConnection("Data Source=:memory:");
            try
            {
                _root.Open();
                Exec(_root, "SET autoinstall_known_extensions = false");
                Exec(_root, "SET autoload_known_extensions = false");
                Exec(_root, "SET temp_directory = " + SqlNames.Literal(Path.Combine(_sessionDir, "spill")));
                ApplyLimits(_root, _memoryLimit, _threads);
                try
                {
                    using var cmd = _root.CreateCommand();
                    cmd.CommandText = "SELECT keyword_name FROM duckdb_keywords() WHERE keyword_category IN ('reserved', 'type_function')";
                    using var r = cmd.ExecuteReader();
                    var words = new List<string>();
                    while (r.Read()) words.Add(r.GetString(0));
                    if (words.Count > 0) SqlNames.SetReservedKeywords(words);
                }
                catch (DuckDBException) { /* 기본 예약어 목록을 계속 쓴다. */ }
            }
            catch
            {
                _root.Dispose();
                throw;
            }
        }

        private static long DefaultMemoryLimit()
        {
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (total <= 0) total = 8L << 30;
            return Math.Clamp(total / 2, 512L << 20, 32L << 30);
        }

        private static void ApplyLimits(DuckDBConnection con, long memoryBytes, int threads)
        {
            long mib = Math.Max(128, memoryBytes >> 20);
            Exec(con, $"SET memory_limit = '{mib}MiB'");
            Exec(con, $"SET threads = {threads}");
        }

        /// <summary>DuckDB 메모리 상한을 바꾼다(이후 실행되는 질의부터).</summary>
        public void SetMemoryLimit(long bytes)
        {
            if (bytes < (128L << 20)) throw new ArgumentOutOfRangeException(nameof(bytes), "At least 128 MB.");
            lock (_gate)
            {
                ThrowIfDisposed();
                Interlocked.Exchange(ref _memoryLimit, bytes);
                ApplyLimits(_root, bytes, _threads);
            }
        }

        private static void SweepOrphanSessions(string sessions)
        {
            try
            {
                if (!Directory.Exists(sessions)) return;
                foreach (string d in Directory.EnumerateDirectories(sessions))
                {
                    try
                    {
                        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromDays(1)) Directory.Delete(d, true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _root.Dispose(); } catch (Exception) { /* 해제 중 오류는 무시 */ }
            }
            try { Directory.Delete(_sessionDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DataWorkspace));
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        // ---- 목록 -------------------------------------------------------------------------------

        /// <summary>등록된 원본의 스냅숏(추가한 순서).</summary>
        public IReadOnlyList<WorkspaceSource> Sources { get { lock (_gate) return _sources.ToArray(); } }

        /// <summary>등록된 뷰의 스냅숏(만든 순서).</summary>
        public IReadOnlyList<WorkspaceView> Views { get { lock (_gate) return _views.ToArray(); } }

        /// <summary>이름(표시 이름 "표" 또는 "스키마.표")으로 표·뷰를 찾는다. 없으면 null.</summary>
        public IWorkspaceRelation? FindRelation(string displayName)
        {
            lock (_gate)
            {
                foreach (var s in _sources)
                    foreach (var t in s.Tables)
                        if (string.Equals(t.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) return t;
                foreach (var v in _views)
                    if (string.Equals(v.Name, displayName, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }

        // ---- 이름 -------------------------------------------------------------------------------

        private HashSet<string> TopLevelNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _sources)
            {
                set.Add(s.Name);
                if (s.Kind == WorkspaceSourceKind.Csv) set.Add(s.Name + SqlNames.RawSuffix);
            }
            foreach (var v in _views) set.Add(v.Name);
            return set;
        }

        private static bool Taken(HashSet<string> names, string candidate)
            => names.Contains(candidate) || names.Contains(candidate + SqlNames.RawSuffix) ||
               (candidate.EndsWith(SqlNames.RawSuffix, StringComparison.OrdinalIgnoreCase) &&
                names.Contains(candidate[..^SqlNames.RawSuffix.Length]));

        /// <summary>원하는 이름에서 규칙에 맞고 아직 안 쓰는 최상위 이름을 만든다(UI가 기본값을 제안할 때).</summary>
        public string SuggestName(string wanted, string fallback = "data")
        {
            lock (_gate)
            {
                var names = TopLevelNames();
                return SqlNames.MakeUnique(SqlNames.Sanitize(wanted, fallback), c => Taken(names, c));
            }
        }

        // ---- CSV·DB 원본 등록 --------------------------------------------------------------------

        /// <summary>
        /// CSV 파일 하나를 표로 등록한다. 이름은 파일 이름에서 만든 식별자(겹치면 _2…). UTF-8이 아니면 UTF-8 임시 사본을 만들어 읽는다(큰 파일은 오래 걸리니 백그라운드에서).
        /// 원본 파일은 쓰지 않는다.
        /// </summary>
        public WorkspaceSource AddCsv(string path, CsvSourceOptions? options = null, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            options ??= CsvSourceOptions.Default;
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException(ViewerSupport.LT("File not found: ", "파일을 찾을 수 없습니다: ") + full, full);
            string stamp = FileStamp(full);
            var prep = CsvUtf8Copy.Prepare(full, options.EncodingName, _utf8Dir, progress, ct);
            var resolved = options with { EncodingName = prep.EncodingName };
            var info = new FileInfo(full);

            WorkspaceSource src;
            lock (_gate)
            {
                ThrowIfDisposed();
                var names = TopLevelNames();
                string name = SqlNames.MakeUnique(SqlNames.Sanitize(Path.GetFileNameWithoutExtension(full)), c => Taken(names, c));
                var cols = BuildTable(_root, "main", name, prep.ReadPath, resolved, null);
                src = new WorkspaceSource(Guid.NewGuid(), WorkspaceSourceKind.Csv, name, full) { RegisteredStamp = stamp };
                src.Tables = new[] { new WorkspaceTable(src, "main", name, full, prep.ReadPath, resolved, cols, info.LastWriteTimeUtc, info.Length) };
                _sources.Add(src);
            }
            RaiseChanged();
            return src;
        }

        public Task<WorkspaceSource> AddCsvAsync(string path, CsvSourceOptions? options = null, IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.Run(() => AddCsv(path, options, progress, ct), ct);

        /// <summary>
        /// DB형 원본(엑셀 통합 문서·SAS·SPSS·SQLite 파일 하나)을 DuckDB 스키마 하나로 등록한다. 시트·표마다 CSV 경로(앱의 WorkbookSession이 만든 임시 CSV)와
        /// 표 이름을 주면 그 스키마 안에 표 하나씩 만든다. <paramref name="originPath"/>는 원래 통합 문서 경로로, 원본 변경 감지에 쓴다(없으면 임시 CSV 기준).
        /// </summary>
        public WorkspaceSource AddDatabase(string name, IReadOnlyList<DbTableInput> tables, string? originPath = null,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (tables.Count == 0) throw new ArgumentException("A database source needs at least one table.", nameof(tables));
            var prepared = PrepareDbTables(tables, progress, ct);
            string origin = string.IsNullOrWhiteSpace(originPath) ? string.Empty : Path.GetFullPath(originPath);
            string stamp = origin.Length > 0 ? FileStamp(origin) : string.Join(",", prepared.Select(p => FileStamp(p.Full)));

            WorkspaceSource src;
            lock (_gate)
            {
                ThrowIfDisposed();
                var names = TopLevelNames();
                string schema = SqlNames.MakeUnique(SqlNames.Sanitize(name, "database"), c => Taken(names, c));
                src = new WorkspaceSource(Guid.NewGuid(), WorkspaceSourceKind.Database, schema, origin) { RegisteredStamp = stamp };
                Exec(_root, "CREATE SCHEMA " + SqlNames.Quote(schema));
                try
                {
                    src.Tables = BuildDbTables(_root, src, schema, prepared, null);
                }
                catch
                {
                    TryExec(_root, "DROP SCHEMA " + SqlNames.Quote(schema) + " CASCADE");
                    throw;
                }
                _sources.Add(src);
            }
            RaiseChanged();
            return src;
        }

        public Task<WorkspaceSource> AddDatabaseAsync(string name, IReadOnlyList<DbTableInput> tables, string? originPath = null,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => Task.Run(() => AddDatabase(name, tables, originPath, progress, ct), ct);

        private sealed record PreparedTable(string Name, string Full, CsvUtf8Copy.Result Read, CsvSourceOptions Options, DateTime LastWriteUtc, long Length);

        private List<PreparedTable> PrepareDbTables(IReadOnlyList<DbTableInput> tables, IProgress<int>? progress, CancellationToken ct)
        {
            var list = new List<PreparedTable>();
            foreach (var t in tables)
            {
                string full = Path.GetFullPath(t.CsvPath);
                if (!File.Exists(full)) throw new FileNotFoundException(ViewerSupport.LT("File not found: ", "파일을 찾을 수 없습니다: ") + full, full);
                var o = t.Options ?? CsvSourceOptions.Default;
                var read = CsvUtf8Copy.Prepare(full, o.EncodingName, _utf8Dir, progress, ct);
                var info = new FileInfo(full);
                list.Add(new PreparedTable(t.TableName, full, read, o with { EncodingName = read.EncodingName }, info.LastWriteTimeUtc, info.Length));
            }
            return list;
        }

        private IReadOnlyList<WorkspaceTable> BuildDbTables(DuckDBConnection con, WorkspaceSource src, string schema, List<PreparedTable> prepared,
            Dictionary<string, Dictionary<string, ColumnValueType>>? hintsByTable)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<WorkspaceTable>();
            foreach (var p in prepared)
            {
                string tname = SqlNames.MakeUnique(SqlNames.Sanitize(p.Name), c => Taken(used, c));
                used.Add(tname); used.Add(tname + SqlNames.RawSuffix);
                Dictionary<string, ColumnValueType>? hints = null;
                hintsByTable?.TryGetValue(p.Name, out hints);
                var cols = BuildTable(con, schema, tname, p.Read.ReadPath, p.Options, hints);
                result.Add(new WorkspaceTable(src, schema, tname, p.Full, p.Read.ReadPath, p.Options, cols, p.LastWriteUtc, p.Length));
            }
            return result;
        }

        // ---- 표(= 두 개의 DuckDB 뷰) 만들기 -----------------------------------------------------------

        private static string ReadCsvCall(string readPath, CsvSourceOptions o)
        {
            var sb = new System.Text.StringBuilder("read_csv(");
            sb.Append(SqlNames.Literal(readPath));
            sb.Append(o.HasHeader ? ", header = true" : ", header = false");
            // 따옴표·이스케이프는 앱의 CsvRowParser와 같은 RFC 4180 규칙(" 와 "")으로 고정한다 — 자동 감지에 맡기면 "" 이스케이프를 못 알아보는 경우가 있다.
            sb.Append(", all_varchar = true, strict_mode = false, null_padding = true, parallel = false, quote = '\"', escape = '\"'");
            if (o.Delimiter is char d) sb.Append(", delim = ").Append(SqlNames.Literal(d.ToString()));
            sb.Append(')');
            return sb.ToString();
        }

        private static string RawViewDdl(string schema, string name, string readPath, CsvSourceOptions o)
            => $"CREATE OR REPLACE VIEW {SqlNames.Qualified(schema, name + SqlNames.RawSuffix)} AS SELECT * FROM {ReadCsvCall(readPath, o)}";

        private static string TypedViewDdl(string schema, string name, IReadOnlyList<WorkspaceColumn> cols)
        {
            var items = new List<string>(cols.Count);
            foreach (var c in cols)
            {
                string q = SqlNames.Quote(c.Name);
                string? expr = c.IsConverted ? TypedColumnSql.Expression(c.Type, q) : null;
                items.Add(expr is null ? q : $"{expr} AS {q}");
            }
            return $"CREATE OR REPLACE VIEW {SqlNames.Qualified(schema, name)} AS SELECT {(items.Count == 0 ? "*" : string.Join(", ", items))} " +
                   $"FROM {SqlNames.Qualified(schema, name + SqlNames.RawSuffix)}";
        }

        /// <summary>표의 두 뷰를 만들고 컬럼 정보를 돌려준다. 실패하면 만든 뷰를 지우고 예외를 던진다.</summary>
        private static List<WorkspaceColumn> BuildTable(DuckDBConnection con, string schema, string name, string readPath,
            CsvSourceOptions options, Dictionary<string, ColumnValueType>? hintsByName)
        {
            try
            {
                Exec(con, RawViewDdl(schema, name, readPath, options));
                string rawRef = SqlNames.Qualified(schema, name + SqlNames.RawSuffix);
                var names = new List<string>();
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "DESCRIBE SELECT * FROM " + rawRef;
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) names.Add(r.GetString(0));
                }
                if (names.Count == 0)
                    throw new WorkspaceQueryException(ViewerSupport.LT("The file has no columns.", "파일에 컬럼이 없습니다."));

                // 타입: 주어진 목록 → 이름 힌트 → 앞쪽 표본에서 앱 추론기로.
                var types = new ColumnValueType?[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    if (options.ColumnTypes is { } given && i < given.Count) types[i] = given[i];
                    else if (options.ColumnTypes is null && hintsByName is not null && hintsByName.TryGetValue(names[i], out var h)) types[i] = h;
                }
                bool anyUnknown = options.ColumnTypes is null && types.Any(t => t is null);
                bool needSample = anyUnknown || types.Any(t => t == ColumnValueType.Boolean);
                List<string[]>? sample = needSample ? ReadSample(con, rawRef, names.Count) : null;
                ColumnStatisticsReport? inferred = null;
                if (sample is not null && anyUnknown)
                    inferred = ColumnStatisticsBuilder.Summarize(names, sample);

                var cols = new List<WorkspaceColumn>(names.Count);
                for (int i = 0; i < names.Count; i++)
                {
                    ColumnValueType t = types[i] ?? (options.ColumnTypes is not null ? ColumnValueType.String : inferred!.Columns[i].InferredType);
                    // 0/1만 있는 불리언 후보는 정수로 둔다(합계·평균을 낼 수 있게). true/false/yes/no/y/n 값이 보이면 불리언.
                    if (t == ColumnValueType.Boolean && sample is not null && !HasWordBooleanToken(sample, i)) t = ColumnValueType.Integer;
                    string? sqlType = TypedColumnSql.SqlType(t);
                    cols.Add(new WorkspaceColumn(names[i], t, sqlType ?? "VARCHAR", sqlType is not null));
                }
                Exec(con, TypedViewDdl(schema, name, cols));
                return cols;
            }
            catch
            {
                TryExec(con, "DROP VIEW IF EXISTS " + SqlNames.Qualified(schema, name));
                TryExec(con, "DROP VIEW IF EXISTS " + SqlNames.Qualified(schema, name + SqlNames.RawSuffix));
                throw;
            }
        }

        private static readonly HashSet<string> WordBooleans = new(StringComparer.OrdinalIgnoreCase) { "true", "false", "yes", "no", "y", "n" };

        private static bool HasWordBooleanToken(List<string[]> sample, int col)
        {
            foreach (var row in sample)
                if (col < row.Length && WordBooleans.Contains(row[col].Trim())) return true;
            return false;
        }

        private static List<string[]> ReadSample(DuckDBConnection con, string rawRef, int columns)
        {
            var rows = new List<string[]>();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {rawRef} LIMIT {InferenceSampleRows}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new string[columns];
                for (int i = 0; i < columns; i++) row[i] = r.IsDBNull(i) ? string.Empty : r.GetString(i);
                rows.Add(row);
            }
            return rows;
        }

        private static string FileStamp(string path)
        {
            try
            {
                var i = new FileInfo(path);
                return i.Exists ? $"{i.LastWriteTimeUtc.Ticks}/{i.Length}" : "missing";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "unreadable"; }
        }

        // ---- 원본 변경 감지·다시 읽기·이름 바꾸기·제거 -----------------------------------------------------

        /// <summary>등록한 뒤 원본 파일(DB 원본은 통합 문서)이 바뀌었는가(수정 시각·크기).</summary>
        public bool IsSourceChanged(WorkspaceSource source)
            => !string.Equals(CurrentStamp(source), source.RegisteredStamp, StringComparison.Ordinal);

        private static string CurrentStamp(WorkspaceSource s)
        {
            if (s.Kind == WorkspaceSourceKind.Csv) return FileStamp(s.Path);
            return s.Path.Length > 0 ? FileStamp(s.Path) : string.Join(",", s.Tables.Select(t => FileStamp(t.FilePath)));
        }

        /// <summary>
        /// CSV 원본을 다시 읽는다(UTF-8 사본 재생성, 컬럼·타입 재확인). 컬럼 이름이 같으면 이전 타입을 유지한다.
        /// DB 원본은 앱이 통합 문서를 다시 임포트한 뒤 <see cref="ReplaceDatabaseTables"/>를 쓴다.
        /// </summary>
        public void Reload(WorkspaceSource source, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (source.Kind != WorkspaceSourceKind.Csv) throw new InvalidOperationException("Use ReplaceDatabaseTables for database sources.");
            var old = source.Tables[0];
            string stamp = FileStamp(source.Path);
            var prep = CsvUtf8Copy.Prepare(source.Path, old.Options.EncodingName, _utf8Dir, progress, ct);
            var info = new FileInfo(source.Path);
            var resolved = old.Options with { EncodingName = prep.EncodingName, ColumnTypes = null };
            var hints = old.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
            lock (_gate)
            {
                ThrowIfDisposed();
                var cols = BuildTable(_root, "main", old.Name, prep.ReadPath, resolved, hints);
                source.Tables = new[] { new WorkspaceTable(source, "main", old.Name, source.Path, prep.ReadPath, resolved, cols, info.LastWriteTimeUtc, info.Length) };
                source.RegisteredStamp = stamp;
                RefreshDependentMetadata(source.Name);
            }
            RaiseChanged();
        }

        /// <summary>DB 원본의 표를 새로 임포트한 CSV로 통째로 바꾼다(스키마 이름은 유지). 이전 타입은 같은 이름의 컬럼에 이어진다.</summary>
        public void ReplaceDatabaseTables(WorkspaceSource source, IReadOnlyList<DbTableInput> tables, string? originPath = null,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (source.Kind != WorkspaceSourceKind.Database) throw new InvalidOperationException("Not a database source.");
            if (tables.Count == 0) throw new ArgumentException("A database source needs at least one table.", nameof(tables));
            var prepared = PrepareDbTables(tables, progress, ct);
            string origin = string.IsNullOrWhiteSpace(originPath) ? source.Path : Path.GetFullPath(originPath);
            var hints = source.Tables.ToDictionary(t => t.Name, t => t.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                ThrowIfDisposed();
                string schema = source.Name;
                Exec(_root, "DROP SCHEMA " + SqlNames.Quote(schema) + " CASCADE");
                Exec(_root, "CREATE SCHEMA " + SqlNames.Quote(schema));
                var byName = new Dictionary<string, Dictionary<string, ColumnValueType>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in prepared)
                {
                    string key = SqlNames.Sanitize(p.Name);
                    if (hints.TryGetValue(key, out var h)) byName[p.Name] = h;
                }
                source.Tables = BuildDbTables(_root, source, schema, prepared, byName);
                source.Path = origin;
                source.RegisteredStamp = origin.Length > 0 ? FileStamp(origin) : string.Join(",", prepared.Select(p => FileStamp(p.Full)));
                RefreshDependentMetadata(source.Name);
            }
            RaiseChanged();
        }

        /// <summary>원본을 이름만 바꾼다(규칙에 맞게 고치고, 겹치면 _2…). 이 원본을 쓰는 뷰가 있으면 SQL이 깨지므로 거부한다.</summary>
        public void Rename(WorkspaceSource source, string newName)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                RequireNoDependents(source.Name, ViewerSupport.LT("rename", "이름을 바꿀"));
                var names = TopLevelNames();
                names.Remove(source.Name);
                if (source.Kind == WorkspaceSourceKind.Csv) names.Remove(source.Name + SqlNames.RawSuffix);
                string fresh = SqlNames.MakeUnique(SqlNames.Sanitize(newName, source.Kind == WorkspaceSourceKind.Csv ? "data" : "database"), c => Taken(names, c));
                if (string.Equals(fresh, source.Name, StringComparison.Ordinal)) return;

                var oldTables = source.Tables;
                string oldName = source.Name;
                if (source.Kind == WorkspaceSourceKind.Csv)
                {
                    var t = oldTables[0];
                    ApplyTableDdl(_root, "main", fresh, t.ReadPath, t.Options, t.Columns);
                    if (!string.Equals(fresh, t.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        TryExec(_root, "DROP VIEW IF EXISTS " + SqlNames.Qualified("main", t.Name));
                        TryExec(_root, "DROP VIEW IF EXISTS " + t.RawSqlReference);
                    }
                    source.Name = fresh;
                    source.Tables = new[] { new WorkspaceTable(source, "main", fresh, t.FilePath, t.ReadPath, t.Options, t.Columns, t.LastWriteUtc, t.Length) };
                }
                else
                {
                    Exec(_root, "CREATE SCHEMA " + SqlNames.Quote(fresh));
                    var moved = new List<WorkspaceTable>();
                    try
                    {
                        foreach (var t in oldTables)
                        {
                            ApplyTableDdl(_root, fresh, t.Name, t.ReadPath, t.Options, t.Columns);
                            moved.Add(new WorkspaceTable(source, fresh, t.Name, t.FilePath, t.ReadPath, t.Options, t.Columns, t.LastWriteUtc, t.Length));
                        }
                    }
                    catch
                    {
                        TryExec(_root, "DROP SCHEMA " + SqlNames.Quote(fresh) + " CASCADE");
                        throw;
                    }
                    Exec(_root, "DROP SCHEMA " + SqlNames.Quote(oldName) + " CASCADE");
                    source.Name = fresh;
                    source.Tables = moved;
                }
            }
            RaiseChanged();
        }

        private static void ApplyTableDdl(DuckDBConnection con, string schema, string name, string readPath, CsvSourceOptions o, IReadOnlyList<WorkspaceColumn> cols)
        {
            Exec(con, RawViewDdl(schema, name, readPath, o));
            Exec(con, TypedViewDdl(schema, name, cols));
        }

        /// <summary>
        /// 원본을 제거한다. 이 원본을 쓰는 뷰가 있으면 <paramref name="cascade"/>가 거짓일 때 거부하고(예외 메시지에 뷰 이름),
        /// 참이면 그 뷰들도 함께 제거한다. 원본 파일·UTF-8 사본은 건드리지 않는다.
        /// </summary>
        public void Remove(WorkspaceSource source, bool cascade = false)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var dependents = DependentViewsCore(source.Name);
                if (dependents.Count > 0)
                {
                    if (!cascade) throw DependentsError(source.Name, dependents, ViewerSupport.LT("remove", "제거할"));
                    RemoveViewsCore(dependents);
                }
                if (source.Kind == WorkspaceSourceKind.Csv)
                {
                    TryExec(_root, "DROP VIEW IF EXISTS " + SqlNames.Qualified("main", source.Name));
                    TryExec(_root, "DROP VIEW IF EXISTS " + SqlNames.Qualified("main", source.Name + SqlNames.RawSuffix));
                }
                else TryExec(_root, "DROP SCHEMA IF EXISTS " + SqlNames.Quote(source.Name) + " CASCADE");
                _sources.Remove(source);
            }
            RaiseChanged();
        }

        // ---- 연결 도우미 -----------------------------------------------------------------------------

        internal static void Exec(DuckDBConnection con, string sql)
        {
            try
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (DuckDBException ex) { throw WorkspaceQueryException.From(ex, sql); }
        }

        internal static void TryExec(DuckDBConnection con, string sql)
        {
            try { Exec(con, sql); } catch (WorkspaceQueryException) { }
        }
    }
}
