using System.Drawing;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // v3.2: 패널(행 상세·AI·탐색기·패싯·검사 결과·셀 값 표시줄)의 표시 상태를 한 곳에서 다룬다.
    //  - 시작 상태: 설정의 "시작할 때 보이는 패널"(또는 '마지막 상태 기억'이 켜져 있으면 지난번 종료 때의 상태).
    //  - 작업 공간 파일(.ncvws)이 열리면 그 파일에 저장된 레이아웃이 앱 기본을 덮어쓴다(저장할 때마다 함께 저장).
    //  - 창 위치·크기·최대화, 패널 폭은 설정에 저장한다.
    public partial class Form1
    {
        // ---- 변경 알림 ---------------------------------------------------------------------------------------

        /// <summary>패널 표시가 바뀌면(시작·작업 공간 복원·사용자 조작 모두) UI 스레드에서 발생한다. 메뉴 체크·도구 모음 토글이 이걸로 동기화한다.</summary>
        internal event Action<PanelKind, bool>? PanelVisibilityChanged;

        private readonly bool?[] _panelNotified = new bool?[Enum.GetValues<PanelKind>().Length];
        private int _placingSplitters;   // >0이면 코드가 분할선을 옮기는 중이므로 폭을 설정에 저장하지 않는다

        private void RaisePanelChanged(PanelKind kind)
        {
            bool now = IsPanelVisible(kind);
            if (_panelNotified[(int)kind] == now) return;
            _panelNotified[(int)kind] = now;
            PanelVisibilityChanged?.Invoke(kind, now);
        }

        internal bool IsPanelVisible(PanelKind kind) => kind switch
        {
            PanelKind.Detail => !outerSplit.Panel2Collapsed,
            PanelKind.Agent => AgentPanelVisible,
            PanelKind.Explorer => WorkspaceDockVisible,
            PanelKind.Facets => _facetsVisible,
            PanelKind.Findings => _findingsShown,
            _ => !splitContainer1.Panel1Collapsed,
        };

        internal void SetPanelVisible(PanelKind kind, bool visible)
        {
            switch (kind)
            {
                case PanelKind.Detail: SetDetailPanelVisible(visible); break;
                case PanelKind.Agent: SetAgentPanelVisible(visible); break;
                case PanelKind.Explorer: SetWorkspaceExplorerVisible(visible); break;
                case PanelKind.Facets: SetFacetsVisible(visible); break;
                case PanelKind.Findings: SetFindingsVisible(visible); break;
                default: SetCellBarVisible(visible); break;
            }
        }

        internal void SetCellBarVisible(bool visible)
        {
            splitContainer1.Panel1Collapsed = !visible;
            RaisePanelChanged(PanelKind.CellBar);
        }

        // ---- 폭 ----------------------------------------------------------------------------------------------

        private const int DefaultDetailWidth = 360, DefaultExplorerWidth = 260;
        private int _explorerWidthOverride;

        private int DetailWidthLogical => _detailWidthOverride > 0 ? _detailWidthOverride
            : _settings.DetailPanelWidth > 0 ? _settings.DetailPanelWidth : DefaultDetailWidth;

        private int ExplorerWidthLogical => _explorerWidthOverride > 0 ? _explorerWidthOverride
            : _settings.ExplorerPanelWidth > 0 ? _settings.ExplorerPanelWidth : DefaultExplorerWidth;

        private int ToLogical(int deviceUnits) => (int)Math.Round(deviceUnits * 96.0 / DeviceDpi);

        private void PlaceDetailSplitter()
        {
            try
            {
                int want = outerSplit.Width - LogicalToDeviceUnits(DetailWidthLogical) - outerSplit.SplitterWidth;
                int min = outerSplit.Panel1MinSize;
                int max = outerSplit.Width - outerSplit.Panel2MinSize - outerSplit.SplitterWidth;
                if (max > min) outerSplit.SplitterDistance = Math.Clamp(want, min, max);
            }
            catch (InvalidOperationException) { /* 아직 크기가 없으면 다음 표시 때 다시 */ }
        }

        // 폼 생성 때 한 번: 사용자가 분할선을 끌면 그 폭을 설정에 저장한다(AI 패널은 EnsureAgentPanel이 같은 일을 한다).
        private void HookLayoutPersistence()
        {
            outerSplit.FixedPanel = FixedPanel.Panel2;   // 창 크기를 바꿔도 행 상세 폭은 그대로, 표가 늘고 준다
            outerSplit.SplitterMoved += (_, _) =>
            {
                if (_placingSplitters > 0 || outerSplit.Panel2Collapsed) return;
                int logical = ToLogical(outerSplit.Panel2.Width);
                if (logical < PanelLayout.MinWidth) return;
                _detailWidthOverride = 0;
                _settings.DetailPanelWidth = logical;
                _settings.Save();
            };
            workspaceSplitter.SplitterMoved += (_, _) =>
            {
                if (_placingSplitters > 0 || !WorkspaceDockVisible) return;
                int logical = ToLogical(workspaceDockHost.Width);
                if (logical < PanelLayout.MinWidth) return;
                _explorerWidthOverride = 0;
                _settings.ExplorerPanelWidth = logical;
                _settings.Save();
            };
        }

        // ---- 레이아웃 적용·캡처 --------------------------------------------------------------------------------

        /// <summary>시작 레이아웃(또는 폼이 처음 표시된 뒤)이 적용되었는가. 그 전(테스트의 보이지 않는 창)에는 작업 공간 파일이 AI 패널 같은 무거운 패널을 만들지 않는다.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool LayoutReady { get; set; }

        /// <summary>앱 시작 때 보일 패널: '마지막 상태 기억'이 켜져 있고 저장된 상태가 있으면 그것, 아니면 설정의 시작 패널. 폭은 앱 설정의 폭을 따른다.</summary>
        internal PanelLayout StartupLayout()
        {
            var source = _settings.RememberLastPanels && _settings.LastPanels is { } last ? last : _settings.StartupPanels;
            var l = source.Clone();
            l.DetailWidth = l.ExplorerWidth = l.AgentWidth = 0;
            return l;
        }

        private void ApplyStartupLayout()
        {
            ApplyLayout(StartupLayout());
            LayoutReady = true;
        }

        /// <summary>지금 화면의 패널 상태(표시 여부와 폭). 작업 공간 파일 저장·마지막 상태 기억이 쓴다.</summary>
        internal PanelLayout CaptureLayout() => new PanelLayout
        {
            Agent = IsPanelVisible(PanelKind.Agent),
            Detail = IsPanelVisible(PanelKind.Detail),
            Explorer = IsPanelVisible(PanelKind.Explorer),
            Facets = _facetsVisible,
            Findings = _findingsPinned && IsPanelVisible(PanelKind.Findings),
            CellBar = IsPanelVisible(PanelKind.CellBar),
            DetailWidth = !outerSplit.Panel2Collapsed ? ToLogical(outerSplit.Panel2.Width) : DetailWidthLogical,
            ExplorerWidth = WorkspaceDockVisible ? ToLogical(workspaceDockHost.Width) : ExplorerWidthLogical,
            AgentWidth = _agentSplit is { Panel2Collapsed: false } ? ToLogical(_agentSplit.Panel2.Width) : AgentWidthLogical,
        }.Normalized();

        /// <summary>레이아웃을 화면에 적용한다. 폭이 0이 아니면 그 폭으로(작업 공간 파일), 0이면 앱 설정의 폭으로 되돌린다. AI 패널로 포커스를 옮기지 않는다.</summary>
        internal void ApplyLayout(PanelLayout layout)
        {
            var l = layout.Clone().Normalized();
            _detailWidthOverride = l.DetailWidth;
            _explorerWidthOverride = l.ExplorerWidth;
            _agentWidthOverride = l.AgentWidth;
            _placingSplitters++;
            try
            {
                // AI 패널을 먼저 정한다: 본문(outerSplit)의 폭이 AI 패널을 켜고 끄는 데 따라 바뀌므로, 그 뒤에 행 상세 폭을 잡아야 어긋나지 않는다.
                SetAgentPanelVisible(l.Agent, focus: false);
                SetWorkspaceExplorerVisible(l.Explorer);
                SetCellBarVisible(l.CellBar);
                SetDetailPanelVisible(l.Detail);
                SetFacetsVisible(l.Facets);
                if (l.Findings || _qualityPanel is not null) SetFindingsVisible(l.Findings);
                if (l.Detail) PlaceDetailSplitter();   // 패싯·검사 결과를 정한 뒤 최종 폭 기준으로 한 번 더
            }
            finally { _placingSplitters--; }
        }

        // ---- 작업 공간 파일의 레이아웃 ---------------------------------------------------------------------------

        /// <summary>작업 공간을 열거나 저장한 직후의 레이아웃. 닫을 때 지금과 다르면(사용자가 패널을 바꿨으면) 작업 공간 파일에 다시 적는다.</summary>
        private PanelLayout? _wfAppliedLayout;

        /// <summary>
        /// 열린 작업 공간 파일의 레이아웃을 화면에 적용한다. layout 절이 없는 옛 파일(v1·v2)은 앱 기본 레이아웃을 쓰되, 옛 explorerVisible이 켜져 있으면 탐색기는 보인다.
        /// </summary>
        private void ApplyWorkspaceLayout(WorkspaceFileModel model)
        {
            PanelLayout layout;
            if (model.Layout is { } saved) layout = saved.Clone();
            else
            {
                layout = StartupLayout();
                layout.Explorer |= model.ExplorerVisible;
            }
            _wfAppliedLayout = layout.Clone();
            if (LayoutReady) ApplyLayout(layout);
            else WorkspaceDockVisible = layout.Explorer;   // 아직 표시 전: 탐색기만(예전 동작)
        }

        /// <summary>
        /// 닫을 때: 작업 공간 파일이 있고 저장 확인이 없었는데(내용 변경 없음) 패널 배치만 바뀌었으면, 파일의 layout 절만 고쳐 적는다.
        /// 파일을 다시 만들지 않고 읽어서 layout·explorerVisible만 바꾸므로 열 때 건너뛴 원본·뷰가 파일에서 사라지지 않는다. 실패하면 조용히 넘어간다.
        /// </summary>
        private void SaveWorkspaceLayoutOnClose()
        {
            if (_wfFilePath is null || !LayoutReady || _wfAppliedLayout is null) return;
            var now = CaptureLayout();
            if (LayoutsMatch(now, _wfAppliedLayout)) return;
            try
            {
                var model = WorkspaceFile.Load(_wfFilePath);
                model.Layout = now;
                model.ExplorerVisible = now.Explorer;
                WorkspaceFile.Save(_wfFilePath, model);
                _wfAppliedLayout = now.Clone();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceFileException) { }
        }

        /// <summary>표시 여부가 같고, 보이는 패널의 폭이 2 논리 픽셀 안이면 같은 레이아웃(DPI 반올림 오차 허용).</summary>
        internal static bool LayoutsMatch(PanelLayout a, PanelLayout b)
        {
            static bool Close(bool visible, int x, int y) => !visible || x == 0 || y == 0 || Math.Abs(x - y) <= 2;
            return a.SameVisibility(b)
                && Close(a.Detail, a.DetailWidth, b.DetailWidth)
                && Close(a.Explorer, a.ExplorerWidth, b.ExplorerWidth)
                && Close(a.Agent, a.AgentWidth, b.AgentWidth);
        }

        // ---- 창 위치·크기와 종료 때 상태 -----------------------------------------------------------------------

        private void RestoreWindowGeometry()
        {
            if (!_settings.RememberWindow) return;
            var areas = Screen.AllScreens.Select(s => s.WorkingArea).ToList();
            if (WindowGeometry.Fit(_settings.Window, areas) is not { } r) return;
            StartPosition = FormStartPosition.Manual;
            Bounds = r;
            if (_settings.Window!.Maximized) WindowState = FormWindowState.Maximized;
        }

        internal WindowGeometry CaptureWindow()
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            return new WindowGeometry { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = WindowState == FormWindowState.Maximized };
        }

        /// <summary>종료 직전: 창 위치·크기, 마지막 패널 상태, 열려 있던 작업 공간을 설정에 저장한다.</summary>
        private void SaveSessionState()
        {
            try
            {
                if (_settings.RememberWindow && Visible) _settings.Window = CaptureWindow();
                // 작업 공간 파일이 열려 있으면 패널 상태는 그 파일의 것이다 — 앱의 '마지막 상태'로 덮어쓰지 않는다.
                if (LayoutReady && _wfFilePath is null) _settings.LastPanels = CaptureLayout();
                _settings.LastWorkspace = _wfFilePath;
                _settings.Save();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SaveSessionState] {ex}"); }
        }
    }
}
