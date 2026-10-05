using System.Diagnostics;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Import;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // 작업 공간 UI: 메뉴(작업 공간 ▸ …, 보기 ▸ 작업 공간 탐색기)·툴바 버튼·탐색기 도킹·SQL 편집기 창·마법사 연결·뷰 만들기/이름 바꾸기/제거.
    // 오래 걸리는 일은 모두 RunWorkspaceUiAsync로 감싼다: 탐색기 아래에 진행 표시와 취소 버튼이 나오고, 오류는 한 번에 안내한다.
    public partial class Form1
    {
        private ToolStripMenuItem? _wsMenu, _viewExplorerMenu, _wsExplorerMenu, _wsNewQueryMenu, _wsAddFilesMenu, _wsAddFolderMenu, _wsRefreshAllMenu,
            _wsJoinMenu, _wsAppendMenu, _wsCompareMenu, _wsGroupMenu, _wsSaveRelationMenu, _wsRefreshViewMenu, _wsOpenSourcesMenu;
        private ToolStripButton? _wsToolButton;
        private WorkspaceExplorer? _explorer;
        private readonly List<SqlEditorForm> _sqlEditors = new();
        private CancellationTokenSource? _wsOpCts;
        private bool _autoRegistering, _syncingExplorerToggle;

        // 테스트가 대화상자 없이 흐름을 검증하게 하는 이음매. null이면 실제 MessageBox를 띄운다.
        internal Action<string>? WorkspaceMessageSink;
        internal Func<string, bool>? WorkspaceConfirmSink;

        /// <summary>최상위 "작업 공간" 메뉴. 다른 기능(작업 공간 저장·열기)이 항목을 덧붙일 수 있다.</summary>
        internal ToolStripMenuItem WorkspaceMenu => _wsMenu!;

        internal WorkspaceExplorer? Explorer => _explorer;

        internal IReadOnlyList<SqlEditorForm> SqlEditors => _sqlEditors;

        // ---------------------------------------------------------------- 메뉴 · 툴바

        private void BuildWorkspaceFeatures()
        {
            _wsMenu = new ToolStripMenuItem { Name = "workspaceMenu" };
            RegisterLabel(_wsMenu, "Workspace", "작업 공간");
            _wsExplorerMenu = MakeItem("Workspace Explorer", "작업 공간 탐색기", (_, _) => SetWorkspaceExplorerVisible(!WorkspaceDockVisible));
            _wsExplorerMenu.CheckOnClick = false;
            _wsNewQueryMenu = MakeItem("New Query…", "새 질의…", (_, _) => NewQueryCommand(null, null));
            _wsNewQueryMenu.ShortcutKeys = Keys.Control | Keys.Alt | Keys.Q; // Ctrl+Shift+Q는 "품질 프로파일 실행"이 이미 쓴다
            _wsAddFilesMenu = MakeItem("Add Files to Workspace…", "작업 공간에 파일 추가…", (_, _) => AddFilesCommand());
            _wsAddFolderMenu = MakeItem("Add Folder to Workspace…", "작업 공간에 폴더 추가…", (_, _) => AddFolderCommand());
            _wsRefreshAllMenu = MakeItem("Re-read Changed Files", "바뀐 파일 다시 읽기", (_, _) => RefreshWorkspaceCommand());
            _wsJoinMenu = MakeItem("Join Tables…", "표 조인…", (_, _) => _ = RunWizardAsync(WizardKind.Join, ActiveTabPreselect()));
            _wsAppendMenu = MakeItem("Append Tables…", "표 이어 붙이기…", (_, _) => _ = RunWizardAsync(WizardKind.Append, ActiveTabPreselect()));
            _wsCompareMenu = MakeItem("Compare Tables…", "표 비교…", (_, _) => _ = RunWizardAsync(WizardKind.Compare, ActiveTabPreselect()));
            _wsGroupMenu = MakeItem("Group & Aggregate…", "그룹 집계…", (_, _) => _ = RunWizardAsync(WizardKind.Group, ActiveTabPreselect()));
            _wsSaveRelationMenu = MakeItem("Save Current Table as File…", "현재 표를 파일로 저장…", (_, _) => _ = SaveActiveTabRelationAsync());
            _wsRefreshViewMenu = MakeItem("Refresh View Tab", "뷰 탭 새로 고침", (_, _) => _ = RefreshActiveViewTabAsync());
            _wsOpenSourcesMenu = MakeItem("Open Source Tabs of View", "뷰의 원본 탭 열기", (_, _) => _ = OpenActiveViewSourcesAsync());

            _wsMenu.DropDownItems.AddRange(new ToolStripItem[]
            {
                _wsExplorerMenu, new ToolStripSeparator(), _wsNewQueryMenu, _wsAddFilesMenu, _wsAddFolderMenu, _wsRefreshAllMenu, new ToolStripSeparator(),
                _wsJoinMenu, _wsAppendMenu, _wsCompareMenu, _wsGroupMenu, new ToolStripSeparator(),
                _wsSaveRelationMenu, _wsRefreshViewMenu, _wsOpenSourcesMenu,
            });
            int at = _analysisMenu is null ? menuStrip1.Items.IndexOf(helpToolStripMenuItem) : menuStrip1.Items.IndexOf(_analysisMenu);
            if (at < 0) at = menuStrip1.Items.Count;
            menuStrip1.Items.Insert(at, _wsMenu);
            _wsMenu.DropDownOpening += (_, _) => UpdateWorkspaceMenuState();

            // 보기 ▸ 작업 공간 탐색기 (Ctrl+Shift+W)
            _viewExplorerMenu = MakeItem("Workspace Explorer", "작업 공간 탐색기", (_, _) => SetWorkspaceExplorerVisible(!WorkspaceDockVisible));
            _viewExplorerMenu.ShortcutKeys = Keys.Control | Keys.Shift | Keys.W;
            viewToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            viewToolStripMenuItem.DropDownItems.Add(_viewExplorerMenu);

            _wsToolButton = new ToolStripButton
            {
                CheckOnClick = false, DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, ImageScaling = ToolStripItemImageScaling.None,
                Image = WorkspaceIcon(), Name = "workspaceToolButton",
            };
            _wsToolButton.Click += (_, _) => SetWorkspaceExplorerVisible(!WorkspaceDockVisible);
            toolStrip1.Items.Add(_wsToolButton);

            TabsChanged += OnWorkspaceTabsChanged;
            workspaceDockHost.VisibleChanged += (_, _) => { if (workspaceDockHost.Visible) EnsureExplorer(); UpdateWorkspaceCheckStates(); };
            WorkspaceChanged += () => _explorer?.ScheduleRefresh();
            Activated += (_, _) => _explorer?.ScheduleRefresh(); // 다른 프로그램에서 파일이 바뀌었을 수 있다(⚠ 표시 갱신)
        }

        private static Bitmap WorkspaceIcon()
        {
            var bmp = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(70, 120, 190), 1.4f);
            using var fill = new SolidBrush(Color.FromArgb(70, 120, 190));
            g.DrawRectangle(pen, 1.5f, 2.5f, 12, 11);
            g.FillRectangle(fill, 1.5f, 2.5f, 4, 11);
            g.DrawLine(pen, 7, 6, 13, 6);
            g.DrawLine(pen, 7, 9, 13, 9);
            return bmp;
        }

        // 메뉴 항목 텍스트·툴팁. 엔진 쓸 수 없음 → 엔진이 필요한 항목을 끄고 이유를 툴팁으로.
        private void UpdateWorkspaceMenuState()
        {
            string? reason = WorkspaceUnavailableReason;
            bool ok = reason is null;
            var tab = ActiveTab;
            void Set(ToolStripMenuItem? item, bool enabled)
            {
                if (item is null) return;
                item.Enabled = ok && enabled;
                item.ToolTipText = ok ? "" : LT("The query engine is unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + reason;
            }
            Set(_wsNewQueryMenu, true);
            Set(_wsAddFilesMenu, true);
            Set(_wsAddFolderMenu, true);
            Set(_wsRefreshAllMenu, true);
            Set(_wsJoinMenu, true);
            Set(_wsAppendMenu, true);
            Set(_wsCompareMenu, true);
            Set(_wsGroupMenu, true);
            Set(_wsSaveRelationMenu, tab is { Kind: TabKind.File or TabKind.Sheet or TabKind.View });
            Set(_wsRefreshViewMenu, tab is { Kind: TabKind.View });
            Set(_wsOpenSourcesMenu, tab is { Kind: TabKind.View });
            bool shown = WorkspaceDockVisible;
            if (_wsExplorerMenu is not null) _wsExplorerMenu.Checked = shown;
            if (_viewExplorerMenu is not null) _viewExplorerMenu.Checked = shown;
            if (_wsToolButton is not null)
            {
                _wsToolButton.Checked = shown;
                _wsToolButton.Text = LT("Workspace", "작업 공간");
                _wsToolButton.ToolTipText = ok
                    ? LT("Show or hide the workspace explorer (Ctrl+Shift+W)", "작업 공간 탐색기 표시/숨김 (Ctrl+Shift+W)")
                    : LT("The query engine is unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + reason;
            }
        }

        // 언어 전환 때 호출(LocalizeFeatureMenus 끝). 메뉴 글자는 RegisterLabel이 처리하므로 탐색기·열린 편집기만 다시 현지화.
        private void LocalizeWorkspaceUi()
        {
            if (_wsToolButton is not null) _wsToolButton.Text = LT("Workspace", "작업 공간");
            _explorer?.Relocalize();
            foreach (var f in _sqlEditors) f.Relocalize();
        }

        private void ApplyWorkspaceTheme()
        {
            _explorer?.ApplyPalette(_palette);
            foreach (var f in _sqlEditors) f.ApplyPalette(_palette);
        }

        private void DisposeWorkspaceUi()
        {
            _wsOpCts?.Cancel();
            foreach (var f in _sqlEditors.ToArray()) { try { f.Close(); f.Dispose(); } catch { /* 닫는 중 */ } }
            _sqlEditors.Clear();
            _explorer?.Dispose();
            _explorer = null;
            DisposeWorkspaceEngine();
        }

        private void OnWorkspaceTabsChanged()
        {
            CleanupClosedGeneratedTabs();
            _explorer?.ScheduleRefresh();
            // 활성 탭이 원본이 바뀐 뷰라면 알려 준다(탭 오른쪽 클릭 ▸ 뷰 새로 고침, 또는 작업 공간 ▸ 뷰 탭 새로 고침).
            if (ActiveTab is { Kind: TabKind.View } vt && _workspace is { } engine && ViewOfTab(vt) is { } shown
                && (engine.IsStale(shown) || shown.ResultPath is not null && !SamePath(vt.Path, shown.ResultPath)))
                statusLabel.Text = LT($"The source of view '{shown.Name}' changed — refresh the view tab to recompute it.",
                                      $"뷰 '{shown.Name}'의 원본이 바뀌었습니다. 뷰 탭을 새로 고치면 다시 계산합니다.");
            // SQL 편집기가 열려 있으면 새로 연 파일도 곧바로 표로 쓸 수 있게 올려 둔다(자동완성에 보이도록).
            if (_sqlEditors.Count > 0 && _workspace is not null && !_autoRegistering && !_closing) _ = RegisterOpenTabsQuietAsync();
        }

        private async Task RegisterOpenTabsQuietAsync()
        {
            _autoRegistering = true;
            try { await RegisterOpenTabsAsync(CancellationToken.None); }
            catch (Exception ex) { Debug.WriteLine($"[Workspace] auto-register: {ex.Message}"); }
            finally { _autoRegistering = false; }
        }

        // 탭 오른쪽 클릭 메뉴에 작업 공간 항목을 덧붙인다(ShowTabMenu가 부른다).
        internal void AddWorkspaceTabMenuItems(ContextMenuStrip menu, DocumentTab tab)
        {
            bool engineOk = WorkspaceUnavailableReason is null;
            if (tab.Kind == TabKind.View)
            {
                menu.Items.Add(new ToolStripSeparator());
                var refresh = menu.Items.Add(LT("Refresh View", "뷰 새로 고침"), null, (_, _) => _ = RunWorkspaceUiAsync(LT("Refreshing view…", "뷰 새로 고치는 중…"), ct => RefreshViewTabAsync(tab, true, ct)));
                refresh.Enabled = engineOk;
                var sources = menu.Items.Add(LT("Open Source Tabs", "원본 탭 열기"), null, (_, _) => { if (ViewOfTab(tab) is { } v) _ = OpenSourceTabsCommandAsync(v); });
                sources.Enabled = engineOk;
            }
            else if (tab.Kind is TabKind.File or TabKind.Sheet)
            {
                menu.Items.Add(new ToolStripSeparator());
                var add = menu.Items.Add(LT("Add to Workspace", "작업 공간에 추가"), null, (_, _) => _ = RegisterTabCommandAsync(tab));
                add.Enabled = engineOk && !IsTabRegistered(tab);
            }
        }

        // ---------------------------------------------------------------- 탐색기 표시

        // 도킹 영역이 (우리 명령이든 작업 공간 파일 열기든) 보이게 되면 탐색기를 채운다.
        private void EnsureExplorer()
        {
            if (_explorer is not null || IsDisposed) return;
            _explorer = new WorkspaceExplorer(this, _palette) { Dock = DockStyle.Fill };
            workspaceDockHost.Controls.Add(_explorer);
        }

        internal void SetWorkspaceExplorerVisible(bool visible)
        {
            if (visible) EnsureExplorer();
            WorkspaceDockVisible = visible;
            if (visible) _explorer?.RefreshTree();
            UpdateWorkspaceCheckStates();
        }

        private void UpdateWorkspaceCheckStates()
        {
            bool shown = WorkspaceDockVisible;
            if (_wsExplorerMenu is not null) _wsExplorerMenu.Checked = shown;
            if (_viewExplorerMenu is not null) _viewExplorerMenu.Checked = shown;
            if (_wsToolButton is not null) _wsToolButton.Checked = shown;
        }

        // ---------------------------------------------------------------- 작업 실행 도우미

        internal event Action<string?>? WorkspaceBusyChanged;

        internal bool WorkspaceOperationRunning => _wsOpCts is not null;

        internal void CancelWorkspaceOperation() => _wsOpCts?.Cancel();

        internal void ShowWorkspaceMessage(string text, MessageBoxIcon icon = MessageBoxIcon.Warning)
        {
            if (WorkspaceMessageSink is { } sink) { sink(text); return; }
            if (IsDisposed) return;
            MessageBox.Show(ActiveForm ?? this, text, LT("Workspace", "작업 공간"), MessageBoxButtons.OK, icon);
        }

        private bool ConfirmWorkspace(string text)
        {
            if (WorkspaceConfirmSink is { } sink) return sink(text);
            return MessageBox.Show(ActiveForm ?? this, text, LT("Workspace", "작업 공간"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private static string ErrorText(Exception ex) => ex is AggregateException a && a.InnerException is { } inner ? inner.Message : ex.Message;

        /// <summary>
        /// 작업 공간 작업 하나를 취소 가능하게 돌린다(한 번에 하나). 탐색기를 펼쳐 진행 표시·취소 버튼을 보이고, 취소는 조용히, 오류는 대화상자로 안내한다.
        /// 끝까지 성공하면 true.
        /// </summary>
        internal async Task<bool> RunWorkspaceUiAsync(string busyText, Func<CancellationToken, Task> work, bool showExplorer = true)
        {
            if (_wsOpCts is not null)
            {
                statusLabel.Text = LT("Another workspace operation is still running — wait or cancel it first.", "다른 작업 공간 작업이 실행 중입니다. 끝나길 기다리거나 먼저 취소하세요.");
                return false;
            }
            string? unavailable = WorkspaceUnavailableReason;
            if (unavailable is not null)
            {
                ShowWorkspaceMessage(LT("The query engine is unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + unavailable);
                return false;
            }
            if (showExplorer && !WorkspaceDockVisible) SetWorkspaceExplorerVisible(true);
            var cts = new CancellationTokenSource();
            _wsOpCts = cts;
            statusLabel.Text = busyText;
            _explorer?.SetBusy(busyText);
            WorkspaceBusyChanged?.Invoke(busyText);
            try
            {
                await work(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) statusLabel.Text = LT("Cancelled", "취소했습니다");
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!IsDisposed) { statusLabel.Text = LT("Failed", "실패"); ShowWorkspaceMessage(ErrorText(ex)); }
                return false;
            }
            finally
            {
                _wsOpCts = null;
                cts.Dispose();
                if (!IsDisposed) _explorer?.SetBusy(null);
                WorkspaceBusyChanged?.Invoke(null);
            }
        }

        // ---------------------------------------------------------------- 파일 추가

        private static readonly string[] CsvLikeExtensions = { ".csv", ".tsv", ".tab", ".txt" };

        internal static bool IsWorkspaceFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return CsvLikeExtensions.Contains(ext) || TabularImporter.IsImportable(path);
        }

        // 폴더는 지원되는 파일(바로 아래 것만)로 펼친다.
        private static List<string> ExpandDropped(IEnumerable<string> paths)
        {
            var list = new List<string>();
            foreach (string p in paths)
            {
                try
                {
                    if (Directory.Exists(p)) list.AddRange(Directory.EnumerateFiles(p).Where(IsWorkspaceFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                    else if (File.Exists(p)) list.Add(p);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine($"[Workspace] expand {p}: {ex.Message}"); }
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>파일(또는 폴더)을 작업 공간에 올린다. 파일마다 실패는 모아서 한 번에 안내한다. 올린 원본 목록을 돌려준다.</summary>
        internal async Task<IReadOnlyList<WorkspaceSource>> AddFilesUiAsync(IEnumerable<string> paths)
        {
            var files = ExpandDropped(paths);
            var added = new List<WorkspaceSource>();
            if (files.Count == 0)
            {
                statusLabel.Text = LT("No supported files to add.", "추가할 수 있는 파일이 없습니다.");
                return added;
            }
            var errors = new List<string>();
            await RunWorkspaceUiAsync(LT("Adding files…", "파일 추가 중…"), async ct =>
            {
                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try { added.AddRange(await AddSourcesAsync(new[] { file }, ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { errors.Add(Path.GetFileName(file) + ": " + ErrorText(ex)); }
                }
            });
            if (errors.Count > 0) ShowWorkspaceMessage(LT("Some files could not be added:\n", "추가하지 못한 파일이 있습니다:\n") + string.Join("\n", errors));
            else if (added.Count > 0) statusLabel.Text = LT($"Added {added.Count} source(s) to the workspace.", $"원본 {added.Count}개를 작업 공간에 추가했습니다.");
            return added;
        }

        internal void AddFilesCommand()
        {
            using var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Title = LT("Add files to the workspace", "작업 공간에 파일 추가"),
                Filter = LT("Data files|*.csv;*.tsv;*.tab;*.txt;*.xlsx;*.xlsm;*.xls;*.sas7bdat;*.sav;*.db;*.sqlite;*.sqlite3|All files|*.*",
                            "데이터 파일|*.csv;*.tsv;*.tab;*.txt;*.xlsx;*.xlsm;*.xls;*.sas7bdat;*.sav;*.db;*.sqlite;*.sqlite3|모든 파일|*.*"),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _ = AddFilesUiAsync(dlg.FileNames);
        }

        internal void AddFolderCommand()
        {
            using var dlg = new FolderBrowserDialog { Description = LT("Add every CSV / Excel / SAS / SPSS / SQLite file in this folder", "이 폴더의 CSV·엑셀·SAS·SPSS·SQLite 파일을 모두 추가합니다"), UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var files = ExpandDropped(new[] { dlg.SelectedPath });
            if (files.Count > 10 && !ConfirmWorkspace(LT($"Add {files.Count} files to the workspace?", $"파일 {files.Count}개를 작업 공간에 추가할까요?"))) return;
            _ = AddFilesUiAsync(files);
        }

        /// <summary>바뀐 원본을 다시 읽고 탐색기를 갱신한다.</summary>
        internal void RefreshWorkspaceCommand()
        {
            if (_workspace is null) { _explorer?.RefreshTree(); return; }
            _ = RunWorkspaceUiAsync(LT("Re-reading changed files…", "바뀐 파일 다시 읽는 중…"), async ct =>
            {
                int n = await RefreshChangedSourcesAsync(ct);
                statusLabel.Text = n == 0 ? LT("No changed files.", "바뀐 파일이 없습니다.") : LT($"Re-read {n} changed file(s).", $"바뀐 파일 {n}개를 다시 읽었습니다.");
            }, showExplorer: false).ContinueWith(_ => _explorer?.RefreshTree(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------------------------------------------------------- 탭 · 관계 열기 명령

        internal Task OpenRelationCommandAsync(IWorkspaceRelation rel)
            => RunWorkspaceUiAsync(LT($"Opening {rel.DisplayName}…", $"{rel.DisplayName} 여는 중…"), ct => OpenRelationTabAsync(rel, ct));

        internal Task OpenSourceCommandAsync(WorkspaceSource src)
            => src.Kind == WorkspaceSourceKind.Csv
                ? OpenRelationCommandAsync(src.Tables[0])
                : RunWorkspaceUiAsync(LT($"Opening {src.Name}…", $"{src.Name} 여는 중…"), async ct =>
                {
                    if (src.Path.Length == 0) throw new InvalidOperationException(LT("This database has no original file to open.", "이 DB는 열 수 있는 원본 파일이 없습니다."));
                    await OpenFileTabAsync(src.Path);
                });

        internal Task RegisterTabCommandAsync(DocumentTab tab)
            => RunWorkspaceUiAsync(LT($"Adding {tab.Title}…", $"{tab.Title} 추가 중…"), ct => RegisterTabAsync(tab, ct));

        internal Task RefreshSourceCommandAsync(WorkspaceSource src)
            => RunWorkspaceUiAsync(LT($"Re-reading {src.Name}…", $"{src.Name} 다시 읽는 중…"), ct => RefreshSourceAsync(src, ct));

        internal Task RefreshViewCommandAsync(WorkspaceView view)
            => RunWorkspaceUiAsync(LT($"Recomputing {view.Name}…", $"{view.Name} 다시 계산 중…"), async ct =>
            {
                var ws = RequireWorkspace();
                if (FindViewTab(view) is not null) await OpenViewTabAsync(ws, view, force: true, ct);
                else
                {
                    var progress = new Progress<long>(n => { if (!IsDisposed) statusLabel.Text = LT($"Recomputing {view.Name}… {n:N0} row(s)", $"{view.Name} 다시 계산 중… {n:N0}행"); });
                    await ws.MaterializeViewAsync(view, progress, ct, force: true);
                    statusLabel.Text = LT($"View {view.Name}: {view.ResultRowCount:N0} row(s)", $"뷰 {view.Name}: {view.ResultRowCount:N0}행");
                }
            });

        internal Task OpenSourceTabsCommandAsync(WorkspaceView view)
            => RunWorkspaceUiAsync(LT("Opening source tabs…", "원본 탭 여는 중…"), ct => OpenSourceTabsOfViewAsync(view, ct));

        private Task RefreshActiveViewTabAsync()
            => ActiveTab is { Kind: TabKind.View } tab
                ? RunWorkspaceUiAsync(LT("Refreshing view…", "뷰 새로 고치는 중…"), ct => RefreshViewTabAsync(tab, true, ct))
                : Task.CompletedTask;

        private Task OpenActiveViewSourcesAsync()
            => ActiveTab is { Kind: TabKind.View } tab && ViewOfTab(tab) is { } view ? OpenSourceTabsCommandAsync(view) : Task.CompletedTask;

        // ---------------------------------------------------------------- 탭 ↔ 관계

        /// <summary>탭이 보여 주는 표·뷰. 파일·워크북 탭은 아직 올리지 않았으면 올린다. 결과 탭이면 null.</summary>
        internal async Task<IWorkspaceRelation?> RelationOfTabAsync(DocumentTab tab, CancellationToken ct)
        {
            if (tab.Kind == TabKind.View) return ViewOfTab(tab);
            if (tab.Kind is not (TabKind.File or TabKind.Sheet)) return null;
            var src = await RegisterTabAsync(tab, ct);
            return src is null ? null : TableOfTab(src, tab);
        }

        private IWorkspaceRelation? TableOfTab(WorkspaceSource src, DocumentTab tab)
        {
            if (src.Kind == WorkspaceSourceKind.Csv || src.Tables.Count == 0) return src.Tables.FirstOrDefault();
            int i = CurrentSheetOf(tab);
            return i >= 0 && i < src.Tables.Count ? src.Tables[i] : src.Tables[0];
        }

        // 마법사에 미리 골라 줄 이름: 활성 탭이 이미 올라가 있거나 뷰 탭일 때만(올리지 않는다).
        private string? ActiveTabPreselect()
        {
            var tab = ActiveTab;
            if (tab is null || _workspace is not { } ws) return null;
            if (tab.Kind == TabKind.View) return ViewOfTab(tab)?.DisplayName;
            if (tab.Kind is TabKind.File or TabKind.Sheet && FindSourceByPath(ws, tab.Path) is { } src) return TableOfTab(src, tab)?.DisplayName;
            return null;
        }

        private async Task SaveActiveTabRelationAsync()
        {
            if (ActiveTab is not { } tab) return;
            IWorkspaceRelation? rel = null;
            await RunWorkspaceUiAsync(LT("Preparing…", "준비 중…"), async ct => rel = await RelationOfTabAsync(tab, ct));
            if (rel is not null) await SaveRelationCommandAsync(rel);
        }

        // ---------------------------------------------------------------- 파일로 저장

        internal async Task SaveRelationCommandAsync(IWorkspaceRelation rel)
        {
            using var dlg = new SaveFileDialog
            {
                Title = LT("Save as file", "파일로 저장"),
                FileName = rel.DisplayName.Replace('.', '_'),
                Filter = LT("CSV (UTF-8)|*.csv|Excel workbook|*.xlsx", "CSV (UTF-8)|*.csv|엑셀 통합 문서|*.xlsx"),
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string path = dlg.FileName;
            await RunWorkspaceUiAsync(LT($"Saving {Path.GetFileName(path)}…", $"{Path.GetFileName(path)} 저장 중…"), ct => SaveRelationAsAsync(rel, path, ct));
        }

        // ---------------------------------------------------------------- 형 변환 보고

        internal async Task ShowCastReportCommandAsync(WorkspaceTable table)
        {
            IReadOnlyList<ColumnCastReport>? reports = null;
            bool ok = await RunWorkspaceUiAsync(LT($"Checking {table.DisplayName}…", $"{table.DisplayName} 점검 중…"),
                async ct => reports = await RequireWorkspace().CheckTypedColumnsAsync(table, ct));
            if (!ok || reports is null) return;
            using var dlg = new CastReportDialog(LT($"Type conversion report — {table.DisplayName}", $"형 변환 보고 — {table.DisplayName}"), reports, _palette);
            dlg.ShowDialog(this);
        }

        // ---------------------------------------------------------------- SQL 편집기

        /// <summary>SQL 편집기 창을 연다(새 질의, <paramref name="editing"/>이 있으면 그 뷰 편집). 열려 있는 파일 탭은 먼저 표로 올린다.</summary>
        internal async Task<SqlEditorForm?> NewQueryAsync(string? sql, WorkspaceView? editing)
        {
            string? reason = WorkspaceUnavailableReason;
            if (reason is not null)
            {
                ShowWorkspaceMessage(LT("The query engine is unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + reason);
                return null;
            }
            if (_tabs.Any(t => t.Kind is TabKind.File or TabKind.Sheet && !IsTabRegistered(t)))
            {
                await RunWorkspaceUiAsync(LT("Adding open files to the workspace…", "열린 파일을 작업 공간에 추가하는 중…"), async ct =>
                {
                    if (await RegisterOpenTabsAsync(ct) is { } err) ShowWorkspaceMessage(ErrorText(err));
                }, showExplorer: false);
            }
            var ws = Workspace;
            if (ws is null || IsDisposed) return null;
            var form = new SqlEditorForm(this, ws, _palette, sql, editing);
            _sqlEditors.Add(form);
            form.Disposed += (_, _) => _sqlEditors.Remove(form);
            if (Visible) form.Show(this); else _ = form.Handle; // 메인 창이 보이지 않을 때(자동화·테스트)는 창을 띄우지 않고 만들기만 한다
            return form;
        }

        internal void NewQueryCommand(string? sql, WorkspaceView? editing) => _ = NewQueryAsync(sql, editing);

        // 편집기가 실행·미리보기 직전에 부른다: 새로 연 탭을 올리고, 바뀐 파일을 다시 읽는다.
        internal async Task PrepareForQueryAsync(CancellationToken ct)
        {
            await RegisterOpenTabsAsync(ct);
            await RefreshChangedSourcesAsync(ct);
        }

        internal void OnQueryResult(SqlRunResultEventArgs e) => OpenResultTab(e.Info, NextQueryTitle());

        // ---------------------------------------------------------------- 뷰 만들기 · 바꾸기

        /// <summary>뷰 이름 검사: 쓸 수 있으면 null, 아니면 이유. <paramref name="current"/>는 이름 바꾸기에서 자기 이름을 허용하려는 값.</summary>
        internal string? ValidateViewName(DataWorkspace ws, string name, string? current = null)
        {
            if (name.Length == 0) return LT("Enter a name.", "이름을 입력하세요.");
            if (current is not null && string.Equals(name, current, StringComparison.Ordinal)) return null;
            string clean = SqlNames.Sanitize(name, "view");
            if (!string.Equals(clean, name, StringComparison.Ordinal))
                return LT($"Use letters, digits and underscores only (try '{clean}').", $"문자·숫자·밑줄만 쓸 수 있습니다('{clean}' 등).");
            if (!string.Equals(ws.SuggestName(name, "view"), name, StringComparison.Ordinal))
                return LT($"The name '{name}' is already used.", $"'{name}' 이름은 이미 사용 중입니다.");
            return null;
        }

        /// <summary>뷰를 만들고 View 탭으로 연다. 실패(이름 중복·SQL 오류)는 안내하고 null.</summary>
        internal async Task<WorkspaceView?> CreateAndOpenViewAsync(string name, string sql, bool includeUnsavedEdits)
        {
            var ws = Workspace;
            if (ws is null) { ShowWorkspaceMessage(WorkspaceUnavailableReason ?? ""); return null; }
            WorkspaceView view;
            try { view = ws.CreateView(name, sql, includeUnsavedEdits); }
            catch (Exception ex) when (ex is WorkspaceQueryException or ArgumentException or InvalidOperationException)
            {
                ShowWorkspaceMessage(ErrorText(ex));
                return null;
            }
            SetWorkspaceExplorerVisible(true);
            await RunWorkspaceUiAsync(LT($"Computing view {view.Name}…", $"뷰 {view.Name} 계산 중…"), ct => OpenRelationTabAsync(view, ct));
            return view;
        }

        // 이름 + "저장 안 한 편집 포함" 묻기. 취소면 null.
        private (string Name, bool IncludeEdits)? PromptViewName(DataWorkspace ws, string title, string prompt, string initial, string? current, string okText)
        {
            bool edits = AnyTabHasUnsavedEdits();
            using var dlg = new NamePromptDialog(title, prompt, initial, _palette, n => ValidateViewName(ws, n, current),
                showIncludeEditsOption: true, editsAvailable: edits, okText: okText);
            return dlg.ShowDialog(ActiveForm ?? this) == DialogResult.OK ? (dlg.Value, dlg.IncludeUnsavedEdits) : null;
        }

        /// <summary>SQL을 뷰로 저장하는 흐름(이름 묻기 → 만들기 → 탭 열기).</summary>
        internal async Task<WorkspaceView?> SaveSqlAsViewAsync(string sql, string suggestedName, string? summary)
        {
            var ws = Workspace;
            if (ws is null) { ShowWorkspaceMessage(WorkspaceUnavailableReason ?? ""); return null; }
            var a = ws.Analyze(sql);
            if (!a.IsValid) { ShowWorkspaceMessage((a.Error ?? "") + WorkspaceQueryException.Where(a.Line, a.Column)); return null; }
            string prompt = LT("Name for the new view table:", "새 뷰 테이블의 이름:") + (string.IsNullOrWhiteSpace(summary) ? "" : "\n" + summary);
            if (PromptViewName(ws, LT("Save as view", "뷰로 저장"), prompt, ws.SuggestName(suggestedName, "view"), null, LT("Create", "만들기")) is not { } pick) return null;
            return await CreateAndOpenViewAsync(pick.Name, sql, pick.IncludeEdits);
        }

        // 편집기의 "뷰로 저장…"
        internal async Task SaveEditorSqlAsViewAsync(string sql, WorkspaceView? editing)
        {
            var ws = Workspace;
            if (ws is null) return;
            if (editing is null)
            {
                var analysis = ws.Analyze(sql);
                string first = analysis.Tables.FirstOrDefault()?.Name ?? "query";
                await SaveSqlAsViewAsync(sql, first + "_view", null);
                return;
            }
            var a = ws.Analyze(sql);
            if (!a.IsValid) { ShowWorkspaceMessage((a.Error ?? "") + WorkspaceQueryException.Where(a.Line, a.Column)); return; }
            if (!ws.Views.Contains(editing)) { ShowWorkspaceMessage(LT("This view no longer exists.", "이 뷰는 더 이상 없습니다.")); return; }
            string prompt = LT($"Update view '{editing.Name}' with this SQL. You can also rename it. Tabs showing the old result will be marked for refresh.",
                               $"뷰 '{editing.Name}'을(를) 이 SQL로 갱신합니다. 이름도 바꿀 수 있습니다. 이전 결과를 보여 주는 탭은 새로 고침 대상이 됩니다.");
            if (PromptViewName(ws, LT("Update view", "뷰 갱신"), prompt, editing.Name, editing.Name, LT("Update", "갱신")) is not { } pick) return;
            await UpdateViewAndOpenAsync(editing, pick.Name, sql, pick.IncludeEdits);
        }

        /// <summary>뷰의 SQL(과 이름)을 바꾸고 View 탭을 새 결과로 연다.</summary>
        internal async Task<bool> UpdateViewAndOpenAsync(WorkspaceView view, string newName, string sql, bool includeUnsavedEdits)
        {
            var ws = Workspace;
            if (ws is null) return false;
            try
            {
                ws.UpdateView(view, sql, includeUnsavedEdits);
                if (!string.Equals(newName, view.Name, StringComparison.Ordinal)) ws.RenameView(view, newName);
            }
            catch (Exception ex) when (ex is WorkspaceQueryException or ArgumentException or InvalidOperationException)
            {
                ShowWorkspaceMessage(ErrorText(ex));
                return false;
            }
            if (FindViewTab(view) is { } tab) { tab.Title = view.Name; NotifyTabsChanged(); }
            return await RunWorkspaceUiAsync(LT($"Computing view {view.Name}…", $"뷰 {view.Name} 계산 중…"), ct => OpenRelationTabAsync(view, ct));
        }

        // ---------------------------------------------------------------- 마법사

        /// <summary>조인·이어 붙이기·비교·그룹 마법사를 열고, 결과 SQL을 뷰로 저장해 연다. 열린 파일 탭은 먼저 표로 올린다.</summary>
        internal async Task<WorkspaceView?> RunWizardAsync(WizardKind kind, string? preselect)
        {
            string? reason = WorkspaceUnavailableReason;
            if (reason is not null)
            {
                ShowWorkspaceMessage(LT("The query engine is unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + reason);
                return null;
            }
            Exception? registerError = null;
            await RunWorkspaceUiAsync(LT("Adding open files to the workspace…", "열린 파일을 작업 공간에 추가하는 중…"),
                async ct => registerError = await RegisterOpenTabsAsync(ct));
            if (registerError is not null) ShowWorkspaceMessage(ErrorText(registerError));
            var ws = Workspace;
            if (ws is null) return null;
            if (ws.Sources.Count + ws.Views.Count == 0)
            {
                ShowWorkspaceMessage(LT("Open or add at least one file first.", "먼저 파일을 하나 이상 열거나 추가하세요."), MessageBoxIcon.Information);
                return null;
            }
            WizardResult? result = kind switch
            {
                WizardKind.Join => WorkspaceWizards.ShowJoin(this, ws, _palette, preselect),
                WizardKind.Append => WorkspaceWizards.ShowAppend(this, ws, _palette, preselect),
                WizardKind.Compare => WorkspaceWizards.ShowCompare(this, ws, _palette, preselect),
                _ => WorkspaceWizards.ShowGroup(this, ws, _palette, preselect),
            };
            if (result is null) return null;
            return await SaveSqlAsViewAsync(result.Sql, result.SuggestedViewName, result.Summary);
        }

        // ---------------------------------------------------------------- 이름 바꾸기 · 제거

        internal void RenameCommand(object target)
        {
            var ws = Workspace;
            if (ws is null) return;
            string current = target switch { WorkspaceSource s => s.Name, WorkspaceView v => v.Name, WorkspaceTable t => t.Source.Name, _ => "" };
            if (current.Length == 0) return;
            using var dlg = new NamePromptDialog(LT("Rename", "이름 바꾸기"), LT($"New name for '{current}':", $"'{current}'의 새 이름:"), current, _palette,
                n => ValidateViewName(ws, n, current), okText: LT("Rename", "이름 바꾸기"));
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Value == current) return;
            RenameTo(target, dlg.Value);
        }

        /// <summary>원본·뷰 이름을 바꾼다. 이 이름을 쓰는 다른 뷰가 있으면 엔진이 거부하고 그 이유를 안내한다.</summary>
        internal bool RenameTo(object target, string newName)
        {
            var ws = Workspace;
            if (ws is null) return false;
            try
            {
                switch (target)
                {
                    case WorkspaceSource s: ws.Rename(s, newName); break;
                    case WorkspaceTable t: ws.Rename(t.Source, newName); break;
                    case WorkspaceView v:
                        ws.RenameView(v, newName);
                        if (FindViewTab(v) is { } tab) { tab.Title = v.Name; NotifyTabsChanged(); }
                        break;
                    default: return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is WorkspaceQueryException or ArgumentException or InvalidOperationException)
            {
                ShowWorkspaceMessage(ErrorText(ex));
                return false;
            }
        }

        internal Task<bool> RemoveCommandAsync(object target)
        {
            var ws = Workspace;
            if (ws is null) return Task.FromResult(false);
            string name = target switch { WorkspaceSource s => s.Name, WorkspaceView v => v.Name, WorkspaceTable t => t.Source.Name, _ => "" };
            if (name.Length == 0) return Task.FromResult(false);
            var dependents = ws.DependentViews(name);
            string text = target is WorkspaceView
                ? LT($"Remove view '{name}'? Its result tab will be closed.", $"뷰 '{name}'을(를) 제거할까요? 그 결과 탭은 닫힙니다.")
                : LT($"Remove '{name}' from the workspace? The file itself is not deleted.", $"'{name}'을(를) 작업 공간에서 제거할까요? 파일 자체는 지워지지 않습니다.");
            if (dependents.Count > 0)
                text += "\n\n" + LT("These views use it and will be removed too:\n", "이 이름을 쓰는 다음 뷰도 함께 제거됩니다:\n") + string.Join(", ", dependents.Select(d => d.Name));
            if (!ConfirmWorkspace(text)) return Task.FromResult(false);
            return Task.FromResult(RemoveConfirmed(target, cascade: dependents.Count > 0));
        }

        /// <summary>확인 없이 제거(함께 제거될 뷰의 결과 탭도 닫는다). 엔진이 거부하면 안내하고 false.</summary>
        internal bool RemoveConfirmed(object target, bool cascade)
        {
            var ws = Workspace;
            if (ws is null) return false;
            string name = target switch { WorkspaceSource s => s.Name, WorkspaceView v => v.Name, WorkspaceTable t => t.Source.Name, _ => "" };
            var gone = new List<WorkspaceView>(ws.DependentViews(name));
            try
            {
                switch (target)
                {
                    case WorkspaceSource s: ws.Remove(s, cascade); break;
                    case WorkspaceTable t: ws.Remove(t.Source, cascade); break;
                    case WorkspaceView v: gone.Add(v); ws.RemoveView(v, cascade); break;
                    default: return false;
                }
            }
            catch (Exception ex) when (ex is WorkspaceQueryException or ArgumentException or InvalidOperationException)
            {
                ShowWorkspaceMessage(ErrorText(ex));
                return false;
            }
            foreach (var v in gone)
                if (FindViewTab(v) is { } tab) CloseTab(tab, askUnsaved: false);
            return true;
        }
    }
}
