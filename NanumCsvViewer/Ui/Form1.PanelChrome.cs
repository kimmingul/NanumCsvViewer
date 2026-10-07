using System.Drawing;

namespace NanumCsvViewer
{
    // 패널 머리글·분할선 규격(Ui/PanelChrome.cs)을 메인 창에 적용한다.
    //   머리글 = 탐색기 도구 띠 · 표 위 셀 주소/값 줄 · 행 상세 머리글 · (채팅 페이지 상단 바는 테마 메시지의 CSS 변수로).
    //   분할선 = 탐색기|표(workspaceSplitter) · 셀 줄|표(splitContainer1) · 표|행 상세(outerSplit) · 본문|AI(_agentSplit): 모두 경계색 1px 선 + 끌 수 있는 면.
    public partial class Form1
    {
        private bool _chromeHooked;

        private int L(int logical) => LogicalToDeviceUnits(logical);

        /// <summary>
        /// DPI에 따라 달라지는 크기(머리글 높이·분할 면 폭·셀 줄 안쪽 여백)를 현재 배율로 정한다. 생성 직후·처음 표시·모니터 이동(DPI 변경) 때 부른다.
        /// 셀 줄은 머리글 높이로 되돌아간다(값 줄을 끌어 키운 높이는 유지하지 않는다).
        /// </summary>
        private void ApplyPanelChromeMetrics()
        {
            int header = L(PanelChrome.HeaderHeight);
            int sw = L(PanelChrome.SplitterWidth);

            workspaceSplitter.Width = sw;
            foreach (var sc in new[] { outerSplit, splitContainer1, _agentSplit })
            {
                if (sc is null) continue;
                try { sc.SplitterWidth = sw; } catch (ArgumentException) { /* 아직 크기가 작으면 다음 기회에 */ }
            }

            detailHeaderLabel.Height = header;

            // 셀 줄: 머리글 = 위 칸(header - sw) + 분할 면(sw, 맨 아래 1px이 머리글의 경계선). 주소·값 상자는 위 칸 안에 세로로 꽉 차게 놓는다.
            int bar = Math.Max(1, header - sw);
            splitContainer1.Panel1.Padding = new Padding(L(4), L(3), L(4), 0);
            cellAddressHost.Width = L(190) + L(4);
            cellAddressHost.GapRight = L(4);
            cellAddressHost.TextInset = L(2);
            try
            {
                splitContainer1.Panel1MinSize = bar;
                splitContainer1.SplitterDistance = bar;
            }
            catch (InvalidOperationException) { /* 초기 크기에 따라 무시 */ }
            catch (ArgumentException) { }
        }

        /// <summary>테마 색을 머리글·분할선에 적용한다(ThemeManager.Apply 뒤에 부른다).</summary>
        private void ApplyPanelChromeColors()
        {
            var p = _palette;
            if (!_chromeHooked)
            {
                _chromeHooked = true;
                detailHeaderLabel.Paint += (_, e) => PanelChrome.PaintHeaderBottomLine(e.Graphics, detailHeaderLabel.ClientRectangle, _palette);
            }
            PanelChrome.StyleSplitter(splitContainer1, p, PanelChrome.DividerLine.Far);   // 셀 줄의 분할 면은 머리글의 일부: 선이 맨 아래
            splitContainer1.Panel1.BackColor = PanelChrome.HeaderBackground(p);
            detailHeaderLabel.BackColor = PanelChrome.HeaderBackground(p);
            detailHeaderLabel.ForeColor = PanelChrome.HeaderText(p);
            detailHeaderLabel.Invalidate();
        }
    }
}
