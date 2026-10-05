using System.Diagnostics;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Import;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // 작업 공간(v3): DuckDB 엔진(DataWorkspace)과 탭을 잇는 계약. 모두 UI 스레드에서 부른다.
    //  · 엔진은 처음 쓸 때 만든다(Workspace). 열린 탭은 등록할 때까지 엔진에 올라가지 않는다 — 인코딩 변환(UTF-8 사본)·형 추론은 등록 시점에 한 번.
    //  · 엑셀·SAS·SPSS·SQLite는 워크북의 시트별 임시 CSV를 "자기 소유 복사본"으로 등록한다(탭을 닫아도 표가 유지되도록).
    //  · 뷰·질의 결과는 읽기 전용 탭(TabKind.View / TabKind.Result)으로 열린다.
    public partial class Form1
    {
        private DataWorkspace? _workspace;
        private string? _workspaceUnavailable;
        private readonly SemaphoreSlim _workspaceGate = new(1, 1);
        private readonly Dictionary<Guid, WorkbookHold> _workbookHolds = new();
        private readonly List<string> _snapshotFiles = new();
        private readonly Dictionary<DocumentTab, Guid> _viewTabs = new();
        private readonly Dictionary<DocumentTab, string> _resultTabFiles = new();
        private int _queryCounter;

        /// <summary>작업 공간이 바뀌었다(원본·뷰 추가/제거/이름 변경/갱신, 엔진 생성). UI 스레드에서 발생.</summary>
        internal event Action? WorkspaceChanged;

        /// <summary>질의 결과 CSV를 두는 폴더.</summary>
        internal static string QueryResultDirectory { get; } = Path.Combine(Path.GetTempPath(), "NanumCsvViewer", "duck", "results");

        private sealed class WorkbookHold : IDisposable
        {
            public WorkbookSession? Session;
            public string? Dir;
            public void Dispose()
            {
                try { Session?.Dispose(); } catch { /* 임시 폴더 정리 실패는 무시 */ }
                if (Dir is not null) { try { Directory.Delete(Dir, true); } catch { /* 무시 */ } }
            }
        }

        // ---------------------------------------------------------------- 엔진 수명

        /// <summary>작업 공간 엔진. 처음 부를 때 만든다. 엔진을 쓸 수 없으면 null이고 이유는 <see cref="WorkspaceUnavailableReason"/>.</summary>
        internal DataWorkspace? Workspace
        {
            get
            {
                if (_workspace is not null) return _workspace;
                if (_workspaceUnavailable is not null || _closing || IsDisposed) return null;
                if (!DataWorkspace.IsEngineAvailable(out string? reason))
                {
                    _workspaceUnavailable = reason ?? LT("The query engine (DuckDB) is not available.", "질의 엔진(DuckDB)을 쓸 수 없습니다.");
                    return null;
                }
                try
                {
                    var ws = new DataWorkspace();
                    ws.EditSnapshotProvider = new TabSnapshotProvider(this);
                    ws.Changed += OnEngineChanged;
                    _workspace = ws;
                }
                catch (Exception ex)
                {
                    _workspaceUnavailable = ex.Message;
                    return null;
                }
                RaiseWorkspaceChanged();
                return _workspace;
            }
        }

        /// <summary>이미 만들어진 엔진(없으면 null — 만들지 않는다). 탐색기가 "아직 아무것도 안 올렸다"를 구분할 때 쓴다.</summary>
        internal DataWorkspace? ExistingWorkspace => _workspace;

        /// <summary>엔진을 쓸 수 없는 이유(쓸 수 있으면 null). 엔진을 만들지는 않는다.</summary>
        internal string? WorkspaceUnavailableReason
        {
            get
            {
                if (_workspace is not null) return null;
                if (_workspaceUnavailable is not null) return _workspaceUnavailable;
                return DataWorkspace.IsEngineAvailable(out string? reason) ? null : reason ?? LT("The query engine (DuckDB) is not available.", "질의 엔진(DuckDB)을 쓸 수 없습니다.");
            }
        }

        private DataWorkspace RequireWorkspace()
            => Workspace ?? throw new InvalidOperationException(WorkspaceUnavailableReason ?? LT("The query engine is not available.", "질의 엔진을 쓸 수 없습니다."));

        private void OnEngineChanged(object? sender, EventArgs e)
        {
            if (IsDisposed || _closing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(HandleEngineChanged)); } catch (InvalidOperationException) { /* 창이 닫히는 중 */ }
            }
            else HandleEngineChanged();
        }

        private void HandleEngineChanged()
        {
            if (IsDisposed || _closing) return;
            ReleaseOrphanHolds();
            RaiseWorkspaceChanged();
        }

        private void RaiseWorkspaceChanged()
        {
            try { WorkspaceChanged?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine($"[Workspace] changed handler: {ex}"); }
        }

        // 제거된 DB 원본이 쥐고 있던 임시 CSV 복사본을 정리한다.
        private void ReleaseOrphanHolds()
        {
            if (_workspace is null || _workbookHolds.Count == 0) return;
            var live = _workspace.Sources.Select(s => s.Id).ToHashSet();
            foreach (var id in _workbookHolds.Keys.ToArray())
            {
                if (live.Contains(id)) continue;
                _workbookHolds[id].Dispose();
                _workbookHolds.Remove(id);
            }
        }

        private void DisposeWorkspaceEngine()
        {
            var ws = _workspace;
            _workspace = null;
            if (ws is not null)
            {
                ws.Changed -= OnEngineChanged;
                try { ws.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[Workspace] dispose: {ex.Message}"); }
            }
            foreach (var hold in _workbookHolds.Values) hold.Dispose();
            _workbookHolds.Clear();
            lock (_snapshotFiles)
            {
                foreach (string f in _snapshotFiles) { try { File.Delete(f); } catch { /* 무시 */ } }
                _snapshotFiles.Clear();
            }
            foreach (var f in _resultTabFiles.Values) { try { File.Delete(f); } catch { /* 열려 있으면 다음 청소 때 */ } }
            _resultTabFiles.Clear();
        }

        // ---------------------------------------------------------------- 경로 도우미

        internal static bool SamePath(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>이 경로(CSV 원본·통합 문서 원본)로 이미 등록된 원본. 없으면 null.</summary>
        internal static WorkspaceSource? FindSourceByPath(DataWorkspace ws, string path) => FindSourceByPath(ws.Sources, path);

        internal static WorkspaceSource? FindSourceByPath(IEnumerable<WorkspaceSource> sources, string path)
            => sources.FirstOrDefault(s => SamePath(s.Path, path) || (s.Kind == WorkspaceSourceKind.Csv && s.Tables.Any(t => SamePath(t.FilePath, path))));

        /// <summary>탭이 가리키는 원본 파일(워크북이면 통합 문서)이 이미 등록돼 있는가.</summary>
        internal bool IsTabRegistered(DocumentTab tab)
            => _workspace is { } ws && tab.Kind is TabKind.File or TabKind.Sheet && FindSourceByPath(ws, tab.Path) is not null;

        // ---------------------------------------------------------------- 원본 추가

        /// <summary>
        /// 파일을 작업 공간에 올린다(CSV → 표, 엑셀·SAS·SPSS·SQLite → DB). 이미 올린 파일이면 그 원본을 돌려주고, 이미 탭으로 열려 있으면 탭이 감지한 인코딩·구분자·타입을 쓴다.
        /// 실패하면(없는 파일·빈 파일 …) 예외 — 앞서 올린 파일은 그대로 남는다.
        /// </summary>
        internal async Task<IReadOnlyList<WorkspaceSource>> AddSourcesAsync(IEnumerable<string> paths, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            var result = new List<WorkspaceSource>();
            foreach (string path in paths.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(await AddSourceCoreAsync(ws, path, null, ct));
            }
            return result;
        }

        /// <summary>탭의 파일을 작업 공간에 올린다(이미 올렸으면 그 원본). 뷰·질의 결과 탭은 올릴 대상이 아니라 null.</summary>
        internal async Task<WorkspaceSource?> RegisterTabAsync(DocumentTab tab, CancellationToken ct)
        {
            if (tab is null || tab.IsClosed || tab.Kind is TabKind.View or TabKind.Result) return null;
            var ws = RequireWorkspace();
            return await AddSourceCoreAsync(ws, tab.Path, tab, ct);
        }

        /// <summary>열려 있는 파일·워크북 탭을 모두 올린다(마법사·SQL 편집기가 모든 표를 보게 할 때). 실패한 탭은 건너뛰고 첫 오류를 돌려준다.</summary>
        internal async Task<Exception?> RegisterOpenTabsAsync(CancellationToken ct)
        {
            Exception? first = null;
            foreach (var tab in _tabs.Where(t => t.Kind is TabKind.File or TabKind.Sheet).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                try { await RegisterTabAsync(tab, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { first ??= ex; }
            }
            return first;
        }

        private async Task<WorkspaceSource> AddSourceCoreAsync(DataWorkspace ws, string path, DocumentTab? tab, CancellationToken ct)
        {
            string full = Path.GetFullPath(path);
            await _workspaceGate.WaitAsync(ct);
            try
            {
                if (await Task.Run(() => FindSourceByPath(ws, full), ct) is { } have) return have; // 엔진 잠금을 잡는 조회라 UI 스레드 밖에서
                if (!File.Exists(full))
                    throw new FileNotFoundException(LT("File not found: ", "파일을 찾을 수 없습니다: ") + full, full);
                tab ??= FindTab(full);
                string label = Path.GetFileName(full);
                var progress = new Progress<int>(p => { if (!IsDisposed) statusLabel.Text = LT($"Adding {label}… {p}%", $"{label} 추가 중… {p}%"); });
                statusLabel.Text = LT($"Adding {label}…", $"{label} 추가 중…");
                return TabularImporter.IsImportable(full)
                    ? await AddWorkbookAsync(ws, full, tab, progress, ct)
                    : await ws.AddCsvAsync(full, CsvOptionsFromTab(tab), progress, ct);
            }
            finally { _workspaceGate.Release(); }
        }

        // 탭이 이미 알아낸 구분자·인코딩(·편집이 없을 때 타입)을 엔진에 넘긴다. 타입은 편집이 있으면 파일과 달라지므로 엔진이 저장된 파일에서 다시 추론한다.
        private static CsvSourceOptions? CsvOptionsFromTab(DocumentTab? tab)
        {
            if (tab is null || tab.IsClosed || tab.Document is not { } doc) return null;
            IReadOnlyList<ColumnValueType>? types = null;
            var inferred = tab.InferredColumnTypes;
            if (inferred.Count > 0 && inferred.Count == doc.RawColumnCount && doc.Edits.IsEmpty) types = inferred;
            return new CsvSourceOptions { Delimiter = doc.Delimiter, EncodingName = doc.EncodingName, ColumnTypes = types };
        }

        private static CsvSourceOptions? HintOptions(IReadOnlyList<ColumnTypeHint?>? hints)
            => hints is { Count: > 0 } && hints.All(h => h is not null)
                ? new CsvSourceOptions { ColumnTypes = hints.Select(h => h!.Type).ToArray() }
                : null;

        private async Task<WorkspaceSource> AddWorkbookAsync(DataWorkspace ws, string full, DocumentTab? tab, IProgress<int> progress, CancellationToken ct)
        {
            var hold = new WorkbookHold();
            try
            {
                List<DbTableInput> inputs;
                if (tab is { IsClosed: false } && tab.Workbook is { } wb)
                {
                    // 탭의 임시 CSV를 자기 폴더로 복사(파싱을 다시 하지 않는다) — 탭을 닫아도 표가 남는다.
                    string dir = Path.Combine(Path.GetTempPath(), "ncv_wsdb_" + Guid.NewGuid().ToString("N"));
                    hold.Dir = dir;
                    var names = wb.SheetNames.ToArray();
                    var sources = names.Select((_, i) => wb.CsvPath(i)).ToArray();
                    var hints = names.Select((_, i) => wb.ColumnHints(i)).ToArray();
                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(dir);
                        for (int i = 0; i < sources.Length; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            File.Copy(sources[i], Path.Combine(dir, $"{i}.csv"), overwrite: true);
                        }
                    }, ct);
                    inputs = names.Select((n, i) => new DbTableInput(n, Path.Combine(dir, $"{i}.csv"), HintOptions(hints[i]))).ToList();
                }
                else
                {
                    bool labels = _settings.ShowFieldLabels;
                    var session = await Task.Run(() => WorkbookSession.Create(full, labels), ct);
                    hold.Session = session;
                    inputs = session.SheetNames.Select((n, i) => new DbTableInput(n, session.CsvPath(i), HintOptions(session.ColumnHints(i)))).ToList();
                }
                var src = await ws.AddDatabaseAsync(Path.GetFileNameWithoutExtension(full), inputs, full, progress, ct);
                _workbookHolds[src.Id] = hold;
                return src;
            }
            catch
            {
                hold.Dispose();
                throw;
            }
        }

        // ---------------------------------------------------------------- 원본 다시 읽기

        /// <summary>원본 파일을 다시 읽는다(CSV: 다시 등록·추론, DB: 통합 문서를 다시 가져와 표를 통째로 교체). 뷰는 오래된 것이 된다.</summary>
        internal async Task RefreshSourceAsync(WorkspaceSource source, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            await _workspaceGate.WaitAsync(ct);
            try
            {
                if (source.Kind == WorkspaceSourceKind.Csv)
                {
                    await Task.Run(() => ws.Reload(source, null, ct), ct);
                    return;
                }
                string origin = source.Path;
                if (origin.Length == 0 || !File.Exists(origin))
                    throw new FileNotFoundException(LT("File not found: ", "파일을 찾을 수 없습니다: ") + origin, origin);
                bool labels = _settings.ShowFieldLabels;
                var session = await Task.Run(() => WorkbookSession.Create(origin, labels), ct);
                var inputs = session.SheetNames.Select((n, i) => new DbTableInput(n, session.CsvPath(i), HintOptions(session.ColumnHints(i)))).ToList();
                try { await Task.Run(() => ws.ReplaceDatabaseTables(source, inputs, origin, null, ct), ct); }
                catch { session.Dispose(); throw; }
                if (_workbookHolds.TryGetValue(source.Id, out var old)) old.Dispose();
                _workbookHolds[source.Id] = new WorkbookHold { Session = session };
            }
            finally { _workspaceGate.Release(); }
        }

        /// <summary>등록한 뒤 원본 파일이 바뀐 원본을 모두 다시 읽는다(질의 실행 전 호출). 읽기 실패는 질의가 알려 주므로 삼킨다. 다시 읽은 원본 수를 돌려준다.</summary>
        internal async Task<int> RefreshChangedSourcesAsync(CancellationToken ct)
        {
            if (_workspace is not { } ws) return 0;
            int count = 0;
            foreach (var s in ws.Sources)
            {
                ct.ThrowIfCancellationRequested();
                bool changed;
                try { changed = ws.IsSourceChanged(s); } catch (IOException) { continue; }
                if (!changed) continue;
                try { await RefreshSourceAsync(s, ct); count++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or WorkspaceQueryException or ArgumentException)
                {
                    Debug.WriteLine($"[Workspace] reload {s.Name}: {ex.Message}");
                }
            }
            return count;
        }

        // ---------------------------------------------------------------- 탭 열기

        /// <summary>표 → 그 파일의 탭(없으면 열고, 워크북이면 그 시트로 전환). 뷰 → 결과가 없거나 오래됐으면 계산한 뒤 읽기 전용 View 탭. 실패하면 예외, 파일을 열지 못해 안내했으면 null.</summary>
        internal async Task<DocumentTab?> OpenRelationTabAsync(IWorkspaceRelation relation, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            return relation switch
            {
                WorkspaceTable t => await OpenTableTabAsync(t, ct),
                WorkspaceView v => await OpenViewTabAsync(ws, v, force: false, ct),
                _ => throw new ArgumentException("Unknown relation type.", nameof(relation)),
            };
        }

        private async Task<DocumentTab?> OpenTableTabAsync(WorkspaceTable t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (t.Source.Kind == WorkspaceSourceKind.Csv) return await OpenFileTabAsync(t.FilePath);

            string origin = t.Source.Path;
            if (origin.Length == 0)
                throw new InvalidOperationException(LT("This database table has no original file to open.", "이 DB 표는 열 수 있는 원본 파일이 없습니다."));
            var tab = await OpenFileTabAsync(origin);
            if (tab is null) return null;
            int index = IndexOfTable(t);
            if (tab.Kind == TabKind.Sheet && index >= 0 && CurrentSheetOf(tab) != index)
            {
                await ActivateTabAsync(tab);
                if (ReferenceEquals(ActiveTab, tab)) await SwitchSheetAsync(index);
            }
            else await ActivateTabAsync(tab);
            return tab;
        }

        private static int IndexOfTable(WorkspaceTable t)
        {
            var tables = t.Source.Tables;
            for (int i = 0; i < tables.Count; i++) if (ReferenceEquals(tables[i], t)) return i;
            for (int i = 0; i < tables.Count; i++) if (string.Equals(tables[i].Name, t.Name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private int CurrentSheetOf(DocumentTab tab) => tab.IsActive ? _currentSheetIndex : tab.SheetIndex;

        private async Task<DocumentTab?> OpenViewTabAsync(DataWorkspace ws, WorkspaceView view, bool force, CancellationToken ct)
        {
            if (view.Error is { } err) throw new InvalidOperationException(LT($"View '{view.Name}' cannot run: ", $"뷰 '{view.Name}'을(를) 실행할 수 없습니다: ") + err);
            var progress = new Progress<long>(n => { if (!IsDisposed) statusLabel.Text = LT($"Computing view {view.Name}… {n:N0} row(s)", $"뷰 {view.Name} 계산 중… {n:N0}행"); });
            statusLabel.Text = LT($"Computing view {view.Name}…", $"뷰 {view.Name} 계산 중…");
            await ws.MaterializeViewAsync(view, progress, ct, force);
            ct.ThrowIfCancellationRequested();
            string result = view.ResultPath ?? throw new InvalidOperationException(LT("The view has no result.", "뷰 결과가 없습니다."));

            var existing = FindViewTab(view);
            if (existing is not null)
            {
                if (SamePath(existing.Path, result)) { await ActivateTabAsync(existing); return existing; }
                return await SwapViewTabAsync(existing, view, result);
            }
            var tab = OpenGeneratedTab(result, view.Name, TabKind.View, view.Name, readOnly: true);
            _viewTabs[tab] = view.Id;
            statusLabel.Text = LT($"View {view.Name}: {view.ResultRowCount:N0} row(s)", $"뷰 {view.Name}: {view.ResultRowCount:N0}행");
            return tab;
        }

        /// <summary>이 뷰를 보여 주는 열린 View 탭(없으면 null).</summary>
        internal DocumentTab? FindViewTab(WorkspaceView view)
            => _viewTabs.FirstOrDefault(kv => kv.Value == view.Id && !kv.Key.IsClosed && _tabs.Contains(kv.Key)).Key;

        /// <summary>View 탭이 보여 주는 뷰(엔진에 없으면 null).</summary>
        internal WorkspaceView? ViewOfTab(DocumentTab tab)
            => _viewTabs.TryGetValue(tab, out var id) ? _workspace?.Views.FirstOrDefault(v => v.Id == id) : null;

        // 새 결과 문서로 탭을 바꾼다: 새 탭을 같은 자리에 두고 옛 탭(읽기 전용이라 저장 안 한 편집이 없다)을 닫는다. 필터·정렬은 새 결과 기준으로 다시 시작한다.
        private async Task<DocumentTab> SwapViewTabAsync(DocumentTab old, WorkspaceView view, string resultPath)
        {
            int index = _tabs.IndexOf(old);
            bool wasActive = old.IsActive;
            var fresh = OpenGeneratedTab(resultPath, view.Name, TabKind.View, view.Name, readOnly: true);
            _viewTabs[fresh] = view.Id;
            if (wasActive) await ActivateTabAsync(fresh);
            if (index >= 0) MoveTab(fresh, index);
            CloseTab(old, askUnsaved: false);
            statusLabel.Text = LT($"View {view.Name} refreshed: {view.ResultRowCount:N0} row(s)", $"뷰 {view.Name}을(를) 새로 고쳤습니다: {view.ResultRowCount:N0}행");
            return fresh;
        }

        /// <summary>View 탭을 다시 계산해(원본이 바뀌었으면) 새 결과로 바꾼다. 항상 다시 계산하려면 force.</summary>
        internal async Task<DocumentTab?> RefreshViewTabAsync(DocumentTab tab, bool force, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            var view = ViewOfTab(tab) ?? throw new InvalidOperationException(LT("This tab does not show a workspace view.", "이 탭은 작업 공간의 뷰를 보여 주고 있지 않습니다."));
            return await OpenViewTabAsync(ws, view, force, ct);
        }

        /// <summary>뷰가 (직·간접으로) 읽는 원본 파일을 탭으로 연다. 열린 탭 수를 돌려준다.</summary>
        internal async Task<int> OpenSourceTabsOfViewAsync(WorkspaceView view, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<WorkspaceSource>();
            void Visit(string name)
            {
                if (!seen.Add(name)) return;
                if (ws.Views.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) is { } dep)
                    foreach (string d in dep.Dependencies) Visit(d);
                else if (ws.Sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } s && !sources.Contains(s))
                    sources.Add(s);
            }
            foreach (string d in view.Dependencies) Visit(d);

            int opened = 0;
            DocumentTab? last = null;
            foreach (var s in sources)
            {
                ct.ThrowIfCancellationRequested();
                string path = s.Kind == WorkspaceSourceKind.Csv ? s.Tables[0].FilePath : s.Path;
                if (path.Length == 0) continue;
                var tab = await OpenFileTabAsync(path);
                if (tab is not null) { opened++; last = tab; }
            }
            if (last is not null) await ActivateTabAsync(last);
            return opened;
        }

        // ---------------------------------------------------------------- 질의 결과 탭

        /// <summary>다음 질의 결과 탭 제목("Query N").</summary>
        internal string NextQueryTitle() => "Query " + (++_queryCounter);

        /// <summary>SQL을 실행해 결과를 임시 CSV로 쓰고 읽기 전용 Result 탭으로 연다. 실패·취소는 예외(오류 위치는 <see cref="WorkspaceQueryException"/>).</summary>
        internal async Task<DocumentTab> OpenQueryResultTabAsync(string sql, string title, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            Directory.CreateDirectory(QueryResultDirectory);
            string path = Path.Combine(QueryResultDirectory, $"query-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.csv");
            var progress = new Progress<long>(n => { if (!IsDisposed) statusLabel.Text = LT($"Running… {n:N0} row(s)", $"실행 중… {n:N0}행"); });
            var info = await ws.RunToCsvAsync(sql, path, progress, ct);
            return OpenResultTab(info, title);
        }

        /// <summary>이미 만든 질의 결과 CSV를 Result 탭으로 연다(SQL 편집기가 쓴다).</summary>
        internal DocumentTab OpenResultTab(QueryResultInfo info, string title)
        {
            var tab = OpenGeneratedTab(info.OutputPath, title, TabKind.Result, null, readOnly: true);
            _resultTabFiles[tab] = tab.Path;
            statusLabel.Text = LT($"{title}: {info.RowCount:N0} row(s), {info.Columns.Count} column(s)", $"{title}: {info.RowCount:N0}행, {info.Columns.Count}컬럼");
            return tab;
        }

        // 닫힌 Result/View 탭의 기록을 정리하고 Result 임시 파일을 지운다(문서가 해제된 뒤).
        private void CleanupClosedGeneratedTabs()
        {
            foreach (var tab in _viewTabs.Keys.Where(t => t.IsClosed).ToArray()) _viewTabs.Remove(tab);
            foreach (var (tab, file) in _resultTabFiles.Where(kv => kv.Key.IsClosed).ToArray())
            {
                _resultTabFiles.Remove(tab);
                _ = tab.DisposeCompletion.ContinueWith(_ =>
                {
                    try { File.Delete(file); } catch { /* 다음 청소 때 */ }
                }, TaskScheduler.Default);
            }
        }

        // ---------------------------------------------------------------- 파일로 저장 (실체화)

        /// <summary>
        /// 표·뷰를 사용자가 고른 CSV(.csv, UTF-8 BOM)나 단일 시트 엑셀(.xlsx)로 저장한다. 표는 원문 그대로(<c>T__raw</c> — 형 변환·서식 손실 없음),
        /// 뷰는 계산한 결과(오래됐으면 다시 계산). 열린 원본·작업 공간 원본·뷰 결과 파일은 절대 덮어쓰지 않는다. 저장한 전체 경로를 돌려준다.
        /// </summary>
        internal async Task<string> SaveRelationAsAsync(IWorkspaceRelation relation, string path, CancellationToken ct)
        {
            var ws = RequireWorkspace();
            string full = Path.GetFullPath(path);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not (".csv" or ".xlsx"))
                throw new ArgumentException(LT("Choose a .csv or .xlsx file name.", ".csv 또는 .xlsx 파일 이름을 고르세요."), nameof(path));
            EnsureNotProtected(ws, full);
            if (Directory.Exists(full)) throw new IOException(LT("That path is a folder.", "그 경로는 폴더입니다."));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            string stage = Path.Combine(Path.GetTempPath(), "ncv_save_" + Guid.NewGuid().ToString("N") + ".csv");
            var progress = new Progress<long>(n => { if (!IsDisposed) statusLabel.Text = LT($"Saving… {n:N0} row(s)", $"저장 중… {n:N0}행"); });
            try
            {
                if (relation is WorkspaceView view)
                {
                    if (view.Error is { } err) throw new InvalidOperationException(err);
                    await ws.MaterializeViewAsync(view, progress, ct);
                    string result = view.ResultPath ?? throw new InvalidOperationException(LT("The view has no result.", "뷰 결과가 없습니다."));
                    await Task.Run(() => File.Copy(result, stage, overwrite: true), ct);
                }
                else if (relation is WorkspaceTable table)
                    await ws.RunToCsvAsync("SELECT * FROM " + table.RawSqlReference, stage, progress, ct);
                else throw new ArgumentException("Unknown relation type.", nameof(relation));

                ct.ThrowIfCancellationRequested();
                if (ext == ".csv") await Task.Run(() => WriteCsvWithBom(stage, full, ct), ct);
                else await Task.Run(() => WriteXlsx(stage, full, SheetNameFor(relation.DisplayName), ct), ct);
                statusLabel.Text = LT($"Saved: {full}", $"저장했습니다: {full}");
                return full;
            }
            finally { try { File.Delete(stage); } catch { /* 무시 */ } }
        }

        private static string SheetNameFor(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name) sb.Append(c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : c);
            string s = sb.ToString().Trim('\'', ' ');
            if (s.Length > 31) s = s[..31];
            return s.Length == 0 ? "Sheet1" : s;
        }

        // 엑셀에서 한글이 깨지지 않도록 UTF-8 BOM을 붙여 쓴다(앱의 다른 CSV 내보내기와 같은 방식). 임시 파일에 쓴 뒤 바꿔 넣어 실패해도 기존 파일이 남는다.
        private static void WriteCsvWithBom(string source, string dest, CancellationToken ct)
        {
            string tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    output.Write(new byte[] { 0xEF, 0xBB, 0xBF });
                    var buffer = new byte[1 << 20];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n);
                    }
                }
                File.Move(tmp, dest, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 무시 */ }
                throw;
            }
        }

        private static void WriteXlsx(string csv, string dest, string sheetName, CancellationToken ct)
        {
            using var doc = VirtualCsvDocument.Open(csv);
            doc.RunIndexingAsync(new Progress<IndexProgress>(), ct).GetAwaiter().GetResult();
            doc.SaveAsXlsx(dest, sheetName, null, ct);
        }

        /// <summary>열린 탭·작업 공간 원본·뷰 결과 파일이면 거부한다(어떤 저장도 원본을 덮어쓰지 않는다).</summary>
        private void EnsureNotProtected(DataWorkspace ws, string full)
        {
            bool hit = OpenSourcePaths().Contains(full, StringComparer.OrdinalIgnoreCase);
            if (!hit)
            {
                foreach (var s in ws.Sources)
                {
                    if (SamePath(s.Path, full) || s.Tables.Any(t => SamePath(t.FilePath, full) || SamePath(t.ReadPath, full))) { hit = true; break; }
                }
            }
            if (!hit && ws.Views.Any(v => SamePath(v.ResultPath, full))) hit = true;
            if (hit)
                throw new InvalidOperationException(LT("The original file is never overwritten. Choose a different file name.", "원본 파일은 덮어쓰지 않습니다. 다른 파일 이름을 고르세요."));
        }

        // ---------------------------------------------------------------- 저장 안 한 편집 스냅숏

        /// <summary>표에 대응하는 열린 탭(CSV: 같은 경로의 파일 탭, DB: 같은 통합 문서의 탭이 그 시트를 보고 있을 때). 없으면 null. UI 스레드.</summary>
        internal DocumentTab? FindTabForTable(WorkspaceTable table)
        {
            if (table.Source.Kind == WorkspaceSourceKind.Csv)
                return _tabs.FirstOrDefault(t => t.Kind == TabKind.File && SamePath(t.Path, table.FilePath));
            int index = IndexOfTable(table);
            return index < 0 ? null : _tabs.FirstOrDefault(t => t.Kind == TabKind.Sheet && SamePath(t.Path, table.Source.Path) && CurrentSheetOf(t) == index);
        }

        /// <summary>저장 안 한 편집이 있는 탭이 하나라도 있는가(뷰로 저장 대화상자의 "편집 포함" 선택 가능 여부).</summary>
        internal bool AnyTabHasUnsavedEdits() => _tabs.Any(t => t.HasUnsavedEdits);

        // 엔진이 백그라운드 스레드에서 부른다. 탭 조회는 UI 스레드에서, 파일 쓰기는 부른 스레드에서 한다.
        // 쓰는 동안 다른 작업이 없으면 화면을 바쁨 상태로 두어 같은 문서에 편집이 끼어들지 못하게 한다.
        internal string? CreateEditSnapshot(WorkspaceTable table, CancellationToken ct)
        {
            if (IsDisposed || _closing) return null;
            (VirtualCsvDocument Doc, bool Took)? found = OnUiThread(() =>
            {
                var tab = FindTabForTable(table);
                if (tab is not { HasUnsavedEdits: true } || tab.Document is not { } doc) return ((VirtualCsvDocument, bool)?)null;
                bool took = false;
                if (!_busy) { SetBusy(true); took = true; }
                return (doc, took);
            });
            if (found is not { } f) return null;
            try
            {
                if (!f.Doc.IndexingComplete)
                    throw new InvalidOperationException(LT("A tab with unsaved edits is still indexing. Wait for it to finish.", "저장 안 한 편집이 있는 탭이 아직 인덱싱 중입니다. 끝난 뒤 다시 시도하세요."));
                string dir = Path.Combine(Path.GetTempPath(), "NanumCsvViewer", "duck", "snapshots");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"{SqlNames.Sanitize(table.Name, "table")}-{Guid.NewGuid():N}.csv");
                f.Doc.SaveWithEdits(file, null, ct);
                lock (_snapshotFiles) _snapshotFiles.Add(file);
                return file;
            }
            finally
            {
                if (f.Took) OnUiThread(() => { if (!IsDisposed) SetBusy(false); return 0; });
            }
        }

        private T OnUiThread<T>(Func<T> func)
        {
            if (!InvokeRequired) return func();
            try { return (T)Invoke(func)!; }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return default!; }
        }
    }

    /// <summary>"저장 안 한 편집 포함" 뷰를 계산할 때 열린 탭의 편집 반영본을 임시 CSV로 내주는 통로.</summary>
    internal sealed class TabSnapshotProvider(Form1 host) : IEditSnapshotProvider
    {
        public string? GetEditedSnapshotPath(WorkspaceTable table, CancellationToken ct) => host.CreateEditSnapshot(table, ct);
    }
}
