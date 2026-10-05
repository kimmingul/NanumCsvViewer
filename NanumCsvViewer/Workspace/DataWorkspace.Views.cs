using DuckDB.NET.Data;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    public sealed partial class DataWorkspace
    {
        // ---- 의존 관계 -----------------------------------------------------------------------------------

        private WorkspaceView? FindViewCore(string name)
            => _views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        private WorkspaceSource? FindSourceCore(string name)
        {
            foreach (var s in _sources)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            // "<표>__raw" 참조는 그 표를 쓰는 것으로 본다.
            if (name.EndsWith(SqlNames.RawSuffix, StringComparison.OrdinalIgnoreCase))
            {
                string stem = name[..^SqlNames.RawSuffix.Length];
                foreach (var s in _sources)
                    if (s.Kind == WorkspaceSourceKind.Csv && string.Equals(s.Name, stem, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        /// <summary>SQL이 참조한 표 이름을 원본·뷰의 최상위 이름으로 바꾼다.</summary>
        private List<string> ResolveDependencies(SqlAnalysis analysis)
        {
            var result = new List<string>();
            void Add(string name)
            {
                if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
            }
            foreach (var t in analysis.Tables)
            {
                if (t.Schema.Length == 0 || string.Equals(t.Schema, "main", StringComparison.OrdinalIgnoreCase))
                {
                    if (FindViewCore(t.Name) is { } v) Add(v.Name);
                    else if (FindSourceCore(t.Name) is { } s && s.Kind == WorkspaceSourceKind.Csv) Add(s.Name);
                }
                else if (FindSourceCore(t.Schema) is { Kind: WorkspaceSourceKind.Database } db) Add(db.Name);
            }
            return result;
        }

        /// <summary>이름(원본 또는 뷰)을 직·간접으로 쓰는 뷰들.</summary>
        private List<WorkspaceView> DependentViewsCore(string name)
        {
            var result = new List<WorkspaceView>();
            var queue = new Queue<string>();
            queue.Enqueue(name);
            while (queue.Count > 0)
            {
                string n = queue.Dequeue();
                foreach (var v in _views)
                {
                    if (result.Contains(v)) continue;
                    if (v.Dependencies.Any(d => string.Equals(d, n, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(v);
                        queue.Enqueue(v.Name);
                    }
                }
            }
            return result;
        }

        /// <summary>이 이름(원본 또는 뷰)을 직·간접으로 쓰는 뷰들(제거·이름 변경 전 확인용).</summary>
        public IReadOnlyList<WorkspaceView> DependentViews(string name)
        {
            lock (_gate) return DependentViewsCore(name);
        }

        private void RequireNoDependents(string name, string verb)
        {
            var d = DependentViewsCore(name);
            if (d.Count > 0) throw DependentsError(name, d, verb);
        }

        private static InvalidOperationException DependentsError(string name, List<WorkspaceView> dependents, string verb)
            => new(ViewerSupport.LT(
                $"'{name}' is used by view(s) {string.Join(", ", dependents.Select(v => "'" + v.Name + "'"))}; cannot {verb} it.",
                $"'{name}'을(를) 쓰는 뷰({string.Join(", ", dependents.Select(v => "'" + v.Name + "'"))})가 있어 {verb} 수 없습니다."));

        /// <summary>뷰의 직·간접 원본(표)과 뷰들(의존 순서, 자기 자신 포함 안 함).</summary>
        private (List<WorkspaceSource> Sources, List<WorkspaceView> Views) Closure(IEnumerable<string> roots)
        {
            var sources = new List<WorkspaceSource>();
            var views = new List<WorkspaceView>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string name)
            {
                if (!seen.Add(name)) return;
                if (FindViewCore(name) is { } v)
                {
                    foreach (var d in v.Dependencies) Visit(d);
                    views.Add(v);
                }
                else if (FindSourceCore(name) is { } s && !sources.Contains(s)) sources.Add(s);
            }
            foreach (var r in roots) Visit(r);
            return (sources, views);
        }

        // ---- 뷰 만들기·바꾸기·지우기 ------------------------------------------------------------------------

        /// <summary>
        /// 뷰 테이블을 만든다. 이름은 <see cref="SqlNames"/> 규칙에 맞아야 하고(고쳐서 쓰지 않는다 — 다르면 예외) 다른 원본·뷰와 겹치면 안 된다.
        /// SQL은 한 문장 SELECT여야 하며(아니면 <see cref="WorkspaceQueryException"/>) 다른 원본·뷰를 이름으로 쓸 수 있다(뷰 위의 뷰).
        /// <paramref name="includeUnsavedEdits"/>가 참이면 <see cref="MaterializeViewAsync"/>가 결과를 계산할 때 <see cref="EditSnapshotProvider"/>의 편집 반영본을 쓴다.
        /// <paramref name="provenance"/>는 누가 만들었는지(없으면 모름 — 작업 공간 파일 v1에서 복원한 뷰). 작업 공간 파일(.ncvws v2)에 함께 저장된다.
        /// </summary>
        public WorkspaceView CreateView(string name, string sql, bool includeUnsavedEdits = false, ViewProvenance? provenance = null)
        {
            WorkspaceView view;
            lock (_gate)
            {
                ThrowIfDisposed();
                string clean = SqlNames.Sanitize(name, "view");
                if (!string.Equals(clean, name.Trim(), StringComparison.Ordinal))
                    throw new ArgumentException(ViewerSupport.LT(
                        $"'{name}' is not a valid name. Use letters, digits and underscores only (try '{clean}').",
                        $"'{name}'은(는) 올바른 이름이 아닙니다. 문자·숫자·밑줄만 쓰세요('{clean}' 등)."), nameof(name));
                if (Taken(TopLevelNames(), clean))
                    throw new ArgumentException(ViewerSupport.LT($"The name '{clean}' is already used.", $"'{clean}' 이름은 이미 사용 중입니다."), nameof(name));

                var analysis = AnalyzeCore(_root, sql);
                string body = RequireBody(analysis, sql);
                string prefix = $"CREATE VIEW {SqlNames.Qualified("main", clean)} AS ";
                Exec(_root, SqlText.Wrap(prefix, body, ""), prefix.Length);
                view = new WorkspaceView(Guid.NewGuid(), clean, sql, includeUnsavedEdits) { Dependencies = ResolveDependencies(analysis), Provenance = provenance };
                _views.Add(view);
                RefreshViewMetadata(view);
            }
            RaiseChanged();
            return view;
        }

        private static string RequireBody(SqlAnalysis a, string sql)
        {
            if (!a.IsValid)
                throw new WorkspaceQueryException(a.Error! + WorkspaceQueryException.Where(a.Line, a.Column),
                    a.Line is not null ? QueryErrorKind.Syntax : QueryErrorKind.NotSelect, a.Line, a.Column);
            return SqlText.StripTerminator(sql);
        }

        private static void Exec(DuckDBConnection con, string sql, int prefixLength)
        {
            try
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (DuckDBException ex) { throw WorkspaceQueryException.From(ex, sql, prefixLength); }
        }

        /// <summary>
        /// 뷰의 SQL·편집 포함 여부를 바꾼다. 이 뷰를 쓰는 다른 뷰의 컬럼 정보도 다시 확인하고, 결과는 오래된 것(<see cref="IsStale"/>)이 된다.
        /// <paramref name="provenance"/>를 주면 출처를 바꾼 사람의 것으로 갈아 끼운다(정의를 다시 쓴 사람이 새 출처), 없으면 그대로 둔다.
        /// </summary>
        public void UpdateView(WorkspaceView view, string sql, bool includeUnsavedEdits, ViewProvenance? provenance = null)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var analysis = AnalyzeCore(_root, sql);
                string body = RequireBody(analysis, sql);
                var deps = ResolveDependencies(analysis);
                var (_, closureViews) = Closure(deps);
                if (deps.Contains(view.Name, StringComparer.OrdinalIgnoreCase) || closureViews.Contains(view))
                    throw new WorkspaceQueryException(ViewerSupport.LT(
                        $"View '{view.Name}' cannot use itself (directly or through another view).",
                        $"뷰 '{view.Name}'이(가) 자기 자신을(직접 또는 다른 뷰를 거쳐) 쓸 수 없습니다."));
                string prefix = $"CREATE OR REPLACE VIEW {view.SqlReference} AS ";
                Exec(_root, SqlText.Wrap(prefix, body, ""), prefix.Length);
                view.Sql = sql;
                view.IncludeUnsavedEdits = includeUnsavedEdits;
                if (provenance is not null) view.Provenance = provenance;
                view.Dependencies = deps;
                view.Version++;
                RefreshViewMetadata(view);
                RefreshDependentMetadata(view.Name);
            }
            RaiseChanged();
        }

        /// <summary>뷰 이름만 바꾼다. 이 뷰를 쓰는 다른 뷰가 있으면 SQL이 깨지므로 거부한다.</summary>
        public void RenameView(WorkspaceView view, string newName)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                RequireNoDependents(view.Name, ViewerSupport.LT("rename", "이름을 바꿀"));
                var names = TopLevelNames();
                names.Remove(view.Name);
                string fresh = SqlNames.MakeUnique(SqlNames.Sanitize(newName, "view"), c => Taken(names, c));
                if (string.Equals(fresh, view.Name, StringComparison.Ordinal)) return;
                Exec(_root, $"ALTER VIEW {view.SqlReference} RENAME TO {SqlNames.Quote(fresh)}");
                view.Name = fresh;
                view.Version++;
            }
            RaiseChanged();
        }

        /// <summary>뷰를 지운다. 이 뷰를 쓰는 다른 뷰가 있으면 <paramref name="cascade"/>가 거짓일 때 거부, 참이면 함께 지운다. 결과 임시 파일도 지운다.</summary>
        public void RemoveView(WorkspaceView view, bool cascade = false)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var dependents = DependentViewsCore(view.Name);
                if (dependents.Count > 0)
                {
                    if (!cascade) throw DependentsError(view.Name, dependents, ViewerSupport.LT("remove", "제거할"));
                    RemoveViewsCore(dependents);
                }
                RemoveViewsCore(new List<WorkspaceView> { view });
            }
            RaiseChanged();
        }

        private void RemoveViewsCore(List<WorkspaceView> views)
        {
            foreach (var v in views)
            {
                TryExec(_root, "DROP VIEW IF EXISTS " + v.SqlReference);
                DeleteResultFile(v);
                _views.Remove(v);
            }
        }

        private static void DeleteResultFile(WorkspaceView v)
        {
            if (v.ResultPath is { } p)
            {
                try { File.Delete(p); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 탭이 열고 있으면 세션 종료 때 지운다. */ }
            }
            v.ResultPath = null; v.ResultRowCount = null; v.ComputedUtc = null; v.ComputedSignature = null;
        }

        private void RefreshViewMetadata(WorkspaceView v)
        {
            try
            {
                var cols = DescribeBody(_root, "SELECT * FROM " + v.SqlReference);
                v.Columns = cols.Select(c => new WorkspaceColumn(c.Name, c.Type, c.SqlType, false)).ToList();
                v.Error = null;
            }
            catch (WorkspaceQueryException e)
            {
                v.Error = e.Message;
            }
        }

        private void RefreshDependentMetadata(string name)
        {
            foreach (var v in DependentViewsCore(name)) RefreshViewMetadata(v);
        }

        // ---- 오래됨 판정 ---------------------------------------------------------------------------------

        private string SignatureCore(WorkspaceView v)
        {
            var (sources, views) = Closure(v.Dependencies);
            var parts = new List<string>();
            foreach (var s in sources) parts.Add("s:" + s.Id.ToString("N") + ":" + CurrentStamp(s));
            foreach (var x in views) parts.Add("v:" + x.Id.ToString("N") + "#" + x.Version);
            parts.Add("v:" + v.Id.ToString("N") + "#" + v.Version);
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        /// <summary>
        /// 뷰 결과가 오래됐는가: 결과가 있는데 그 뒤로 원본 파일(직·간접)의 수정 시각·크기가 바뀌었거나 이 뷰·아래 뷰의 정의가 바뀌었다.
        /// 결과가 아직 없으면 false(<see cref="HasResult"/>로 구분). 결과 임시 파일이 사라졌어도 true.
        /// </summary>
        public bool IsStale(WorkspaceView view)
        {
            lock (_gate)
            {
                if (view.ResultPath is null || view.ComputedSignature is null) return false;
                if (!File.Exists(view.ResultPath)) return true;
                return !string.Equals(SignatureCore(view), view.ComputedSignature, StringComparison.Ordinal);
            }
        }

        // ---- 결과 계산(임시 CSV) --------------------------------------------------------------------------

        /// <summary>
        /// 뷰 결과를 임시 CSV(UTF-8, 헤더)로 계산해 <see cref="WorkspaceView.ResultPath"/>에 둔다. 결과가 있고 오래되지 않았으면 그대로 돌려준다(<paramref name="force"/>가 거짓일 때).
        /// 계산 전에 바뀐 CSV 원본은 다시 읽는다. <see cref="WorkspaceView.IncludeUnsavedEdits"/>가 참이고 <see cref="EditSnapshotProvider"/>가 있으면
        /// 표마다 편집 반영본을 받아 별도 메모리 DB에서 같은 이름으로 다시 만들어 계산한다(그때 시점의 스냅숏 — 이후 편집은 반영되지 않는다).
        /// 이 결과 파일은 앱이 읽기 전용 탭으로 열 수 있다. 세션이 끝나면 지워진다.
        /// </summary>
        public Task<WorkspaceView> MaterializeViewAsync(WorkspaceView view, IProgress<long>? rowsProgress = null, CancellationToken ct = default, bool force = false)
            => Task.Run(() =>
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (!force && view.ResultPath is not null && !IsStale(view) && File.Exists(view.ResultPath)) return view;
                }

                List<WorkspaceSource> sources;
                lock (_gate) sources = Closure(view.Dependencies).Sources;
                foreach (var s in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    if (s.Kind == WorkspaceSourceKind.Csv && IsSourceChanged(s)) Reload(s, null, ct);
                }

                string signature;
                string sql, name;
                bool withEdits;
                long version;
                lock (_gate)
                {
                    signature = SignatureCore(view);
                    sql = view.SqlReference; name = view.Name; withEdits = view.IncludeUnsavedEdits; version = view.Version;
                }
                string outPath = Path.Combine(_viewsDir, $"{SqlNames.Sanitize(name, "view")}-{Interlocked.Increment(ref _viewFileCounter)}.csv");
                QueryResultInfo info = withEdits && EditSnapshotProvider is { } provider
                    ? ComputeWithSnapshots(view, provider, outPath, rowsProgress, ct)
                    : RunAsync(con => CopyToCsv(con, "SELECT * FROM " + sql, outPath, rowsProgress, ct), ct).GetAwaiter().GetResult();

                lock (_gate)
                {
                    if (!_views.Contains(view)) { try { File.Delete(outPath); } catch (IOException) { } return view; }
                    string? old = view.ResultPath;
                    view.ResultPath = outPath;
                    view.ResultRowCount = info.RowCount;
                    view.ComputedUtc = DateTime.UtcNow;
                    view.ComputedSignature = signature;
                    if (old is not null) { try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                }
                RaiseChanged();
                return view;
            }, ct);

        private QueryResultInfo ComputeWithSnapshots(WorkspaceView view, IEditSnapshotProvider provider, string outPath, IProgress<long>? progress, CancellationToken ct)
        {
            List<WorkspaceSource> sources;
            List<WorkspaceView> views;
            lock (_gate) (sources, views) = Closure(view.Dependencies);

            using var scratch = new DuckDBConnection("Data Source=:memory:");
            scratch.Open();
            Exec(scratch, "SET autoinstall_known_extensions = false");
            Exec(scratch, "SET autoload_known_extensions = false");
            Exec(scratch, "SET temp_directory = " + SqlNames.Literal(Path.Combine(_sessionDir, "spill-scratch-" + Guid.NewGuid().ToString("N")[..8])));
            ApplyLimits(scratch, MemoryLimitBytes, _threads);
            TryExec(scratch, "SET enable_progress_bar = true");
            TryExec(scratch, "SET enable_progress_bar_print = false");
            TryExec(scratch, "SET progress_bar_time = 0");

            foreach (var s in sources)
            {
                if (s.Kind == WorkspaceSourceKind.Database) Exec(scratch, "CREATE SCHEMA " + SqlNames.Quote(s.Name));
                foreach (var t in s.Tables)
                {
                    ct.ThrowIfCancellationRequested();
                    string? snap = provider.GetEditedSnapshotPath(t, ct);
                    if (snap is null)
                    {
                        ApplyTableDdl(scratch, t.Schema, t.Name, t.ReadPath, t.Options, t.Columns);
                        continue;
                    }
                    var prep = CsvUtf8Copy.Prepare(snap, t.Options.EncodingName, _utf8Dir, null, ct);
                    var hints = t.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
                    BuildTable(scratch, t.Schema, t.Name, prep.ReadPath, t.Options with { ColumnTypes = null }, hints);
                }
            }
            foreach (var v in views)
            {
                string body = SqlText.StripTerminator(v.Sql);
                string prefix = $"CREATE VIEW {v.SqlReference} AS ";
                Exec(scratch, SqlText.Wrap(prefix, body, ""), prefix.Length);
            }

            string body2 = "SELECT * FROM " + view.SqlReference;
            // 뷰 자신의 정의도 scratch에 만든다(위 views에는 자신이 없다).
            string selfBody = SqlText.StripTerminator(view.Sql);
            string selfPrefix = $"CREATE VIEW {view.SqlReference} AS ";
            Exec(scratch, SqlText.Wrap(selfPrefix, selfBody, ""), selfPrefix.Length);

            using var guard = new InterruptGuard(scratch, ct);
            try { return CopyToCsv(scratch, body2, outPath, progress, ct); }
            catch (Exception ex) when (ct.IsCancellationRequested && (ex is DuckDBException || ex is WorkspaceQueryException))
            {
                throw new OperationCanceledException(ct);
            }
            catch (DuckDBException ex) { throw WorkspaceQueryException.From(ex); }
        }
    }
}
