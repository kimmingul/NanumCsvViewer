namespace NanumCsvViewer
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            menuStrip1 = new MenuStrip();
            toolStrip1 = new ToolStrip();
            openToolStripButton = new ToolStripButton();
            sortAscButton = new ToolStripButton();
            sortDescButton = new ToolStripButton();
            clearSortButton = new ToolStripButton();
            findLabel = new ToolStripLabel();
            findTextBox = new ToolStripTextBox();
            findNextButton = new ToolStripButton();
            filterColumnLabel = new ToolStripLabel();
            filterColumnCombo = new ToolStripComboBox();
            filterTextBox = new ToolStripTextBox();
            applyFilterButton = new ToolStripButton();
            clearFilterButton = new ToolStripButton();
            detailToggleButton = new ToolStripButton();
            gridContextMenu = new ContextMenuStrip(components);
            outerSplit = new SplitContainer();
            splitContainer1 = new SplitContainer();
            cellValueTextBox = new TextBox();
            cellAddressBox = new TextBox();
            cellAddressHost = new ChromeAddressHost();
            grid = new BufferedDataGridView();
            detailRichText = new RichTextBox();
            detailHeaderLabel = new Label();
            statusStrip1 = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            progressLabel = new ToolStripStatusLabel();
            progressBar = new ToolStripProgressBar();
            encodingStatusButton = new ToolStripDropDownButton();
            signalLabel = new ToolStripStatusLabel();
            openFileDialog1 = new OpenFileDialog();
            tabStrip = new TabStrip();
            workspaceDockHost = new Panel();
            workspaceSplitter = new DividerSplitter();
            menuStrip1.SuspendLayout();
            toolStrip1.SuspendLayout();
            gridContextMenu.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)outerSplit).BeginInit();
            outerSplit.Panel1.SuspendLayout();
            outerSplit.Panel2.SuspendLayout();
            outerSplit.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainer1).BeginInit();
            splitContainer1.Panel1.SuspendLayout();
            splitContainer1.Panel2.SuspendLayout();
            splitContainer1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)grid).BeginInit();
            statusStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // menuStrip1 — 10개 최상위 메뉴와 항목은 Ui/Form1.MainMenu.cs의 ComposeMainMenu가 코드로 구성한다.
            // 
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(1008, 24);
            menuStrip1.TabIndex = 4;
            menuStrip1.Text = "menuStrip1";
            // 
            // toolStrip1 — 버튼 순서·아이콘·툴팁은 Ui/Form1.Toolbar.cs의 ComposeToolbar가 구성한다(여기서는 이름·동작만 정의).
            // 
            toolStrip1.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip1.Location = new Point(0, 24);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(1008, 25);
            toolStrip1.TabIndex = 3;
            openToolStripButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            openToolStripButton.Name = "openToolStripButton";
            openToolStripButton.Click += OnOpenClick;
            sortAscButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            sortAscButton.Name = "sortAscButton";
            sortAscButton.Click += OnSortAscMenuClick;
            sortDescButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            sortDescButton.Name = "sortDescButton";
            sortDescButton.Click += OnSortDescMenuClick;
            clearSortButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            clearSortButton.Name = "clearSortButton";
            clearSortButton.Click += OnClearSortClick;
            findLabel.Name = "findLabel";
            findTextBox.Name = "findTextBox";
            findTextBox.Size = new Size(140, 25);
            findTextBox.KeyDown += OnFindKeyDown;
            findNextButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            findNextButton.Name = "findNextButton";
            findNextButton.Click += OnFindNextClick;
            filterColumnLabel.Name = "filterColumnLabel";
            filterColumnCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            filterColumnCombo.Name = "filterColumnCombo";
            filterColumnCombo.Size = new Size(130, 25);
            filterTextBox.Name = "filterTextBox";
            filterTextBox.Size = new Size(140, 25);
            filterTextBox.KeyDown += OnFilterKeyDown;
            applyFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            applyFilterButton.Name = "applyFilterButton";
            applyFilterButton.Click += OnApplyFilterClick;
            clearFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            clearFilterButton.Name = "clearFilterButton";
            clearFilterButton.Click += OnClearFilterClick;
            detailToggleButton.CheckOnClick = true;
            detailToggleButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            detailToggleButton.Name = "detailToggleButton";
            detailToggleButton.CheckedChanged += OnDetailToggleChanged;
            //
            // gridContextMenu — 셀 우클릭 메뉴. 항목은 Ui/Form1.ContextMenus.cs가 열릴 때마다 구성한다.
            //
            gridContextMenu.Name = "gridContextMenu";
            gridContextMenu.Size = new Size(171, 26);
            // 
            // outerSplit
            // 
            outerSplit.Dock = DockStyle.Fill;
            outerSplit.Location = new Point(0, 49);
            outerSplit.Name = "outerSplit";
            // 
            // outerSplit.Panel1
            // 
            outerSplit.Panel1.Controls.Add(splitContainer1);
            outerSplit.Panel1MinSize = 200;
            // 
            // outerSplit.Panel2
            // 
            outerSplit.Panel2.Controls.Add(detailRichText);
            outerSplit.Panel2.Controls.Add(detailHeaderLabel);
            outerSplit.Panel2Collapsed = true;
            outerSplit.Panel2MinSize = 150;
            outerSplit.Size = new Size(1008, 658);
            outerSplit.SplitterDistance = 200;
            outerSplit.TabIndex = 1;
            // 
            // splitContainer1
            // 
            splitContainer1.Dock = DockStyle.Fill;
            splitContainer1.Location = new Point(0, 0);
            splitContainer1.Name = "splitContainer1";
            splitContainer1.Orientation = Orientation.Horizontal;
            // 
            // splitContainer1.Panel1
            // 
            splitContainer1.Panel1.Controls.Add(cellValueTextBox);
            splitContainer1.Panel1.Controls.Add(cellAddressHost);
            splitContainer1.Panel1MinSize = 22;
            // 
            // splitContainer1.Panel2
            // 
            splitContainer1.Panel2.Controls.Add(grid);
            splitContainer1.Panel2MinSize = 60;
            splitContainer1.Size = new Size(1008, 658);
            splitContainer1.SplitterDistance = 329;
            splitContainer1.TabIndex = 0;
            // 
            // cellValueTextBox — 값 줄: 입력 상자 모양 그대로
            //
            cellValueTextBox.BackColor = SystemColors.Window;
            cellValueTextBox.BorderStyle = BorderStyle.FixedSingle;
            cellValueTextBox.Dock = DockStyle.Fill;
            cellValueTextBox.Location = new Point(194, 0);
            cellValueTextBox.Multiline = true;
            cellValueTextBox.Name = "cellValueTextBox";
            cellValueTextBox.ReadOnly = true;
            cellValueTextBox.ScrollBars = ScrollBars.Vertical;
            cellValueTextBox.Size = new Size(814, 329);
            cellValueTextBox.TabIndex = 0;
            //
            // cellAddressHost — 주소 상자를 담는 머리글 칸(Ui/PanelChrome.cs): 평소엔 글자만, 포커스가 있으면 밑줄
            //
            cellAddressHost.Controls.Add(cellAddressBox);
            cellAddressHost.Dock = DockStyle.Left;
            cellAddressHost.Location = new Point(0, 0);
            cellAddressHost.Name = "cellAddressHost";
            cellAddressHost.Size = new Size(194, 23);
            cellAddressHost.TabIndex = 1;
            //
            // cellAddressBox (Excel 이름 상자처럼 편집 가능: 120 · R120C3 · C3 · 이름:120). 테두리 없음 — 포커스일 때만 칸이 밑줄을 그린다.
            //
            cellAddressBox.BackColor = SystemColors.Control;
            cellAddressBox.BorderStyle = BorderStyle.None;
            cellAddressBox.Location = new Point(2, 4);
            cellAddressBox.Name = "cellAddressBox";
            cellAddressBox.Size = new Size(188, 16);
            cellAddressBox.TabIndex = 0;
            // 
            // grid
            // 
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ContextMenuStrip = gridContextMenu;
            grid.Dock = DockStyle.Fill;
            grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            grid.Location = new Point(0, 0);
            grid.MultiSelect = true;
            grid.Name = "grid";
            grid.ReadOnly = true;
            grid.RowHeadersWidth = 80;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.Size = new Size(1008, 325);
            grid.TabIndex = 0;
            grid.VirtualMode = true;
            grid.RowHeadersWidthChanged += OnRowHeadersWidthChanged;
            grid.CellMouseDown += OnGridCellMouseDown;
            grid.CellPainting += OnGridCellPainting;
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;
            grid.CurrentCellChanged += OnCurrentCellChanged;
            grid.RowHeightInfoNeeded += OnRowHeightInfoNeeded;
            grid.RowPostPaint += OnRowPostPaint;
            // 
            // detailRichText
            // 
            detailRichText.BackColor = SystemColors.Window;
            detailRichText.BorderStyle = BorderStyle.None;
            detailRichText.DetectUrls = false;
            detailRichText.Dock = DockStyle.Fill;
            detailRichText.Location = new Point(0, 22);
            detailRichText.Name = "detailRichText";
            detailRichText.ReadOnly = true;
            detailRichText.Size = new Size(96, 78);
            detailRichText.TabIndex = 0;
            detailRichText.Text = "";
            // 
            // detailHeaderLabel
            // 
            detailHeaderLabel.BackColor = SystemColors.Control;
            detailHeaderLabel.BorderStyle = BorderStyle.None;
            detailHeaderLabel.Dock = DockStyle.Top;
            detailHeaderLabel.Location = new Point(0, 0);
            detailHeaderLabel.Name = "detailHeaderLabel";
            detailHeaderLabel.Padding = new Padding(5, 0, 0, 0);
            detailHeaderLabel.Size = new Size(96, 30);
            detailHeaderLabel.TabIndex = 1;
            detailHeaderLabel.Text = "행 상세";
            detailHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // statusStrip1
            // 
            statusStrip1.Items.AddRange(new ToolStripItem[] { statusLabel, progressLabel, progressBar, encodingStatusButton, signalLabel });
            statusStrip1.Location = new Point(0, 707);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(1008, 22);
            statusStrip1.TabIndex = 2;
            statusStrip1.Text = "statusStrip1";
            // 
            // statusLabel
            // 
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(860, 17);
            statusLabel.Spring = true;
            statusLabel.Text = "Ready.";
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // progressLabel
            // 
            progressLabel.AutoSize = false;
            progressLabel.Name = "progressLabel";
            progressLabel.Size = new Size(50, 17);
            progressLabel.TextAlign = ContentAlignment.MiddleRight;
            progressLabel.Visible = false;
            // 
            // progressBar
            // 
            progressBar.Name = "progressBar";
            progressBar.Size = new Size(200, 16);
            progressBar.Visible = false;
            // 
            // encodingStatusButton
            // 
            encodingStatusButton.ImageScaling = ToolStripItemImageScaling.None;
            encodingStatusButton.Name = "encodingStatusButton";
            encodingStatusButton.Size = new Size(13, 20);
            encodingStatusButton.ToolTipText = "텍스트 인코딩 (클릭하여 변경)";
            // 
            // signalLabel
            // 
            signalLabel.AutoSize = false;
            signalLabel.ForeColor = Color.Gray;
            signalLabel.Name = "signalLabel";
            signalLabel.Size = new Size(120, 17);
            signalLabel.Text = "● 대기";
            signalLabel.TextAlign = ContentAlignment.MiddleRight;
            // 
            // openFileDialog1
            // 
            openFileDialog1.Filter = "All Supported|*.csv;*.txt;*.xlsx;*.xlsm;*.xls;*.sas7bdat;*.sav;*.db;*.sqlite;*.sqlite3|CSV / Text (*.csv;*.txt)|*.csv;*.txt|Excel (*.xlsx;*.xls)|*.xlsx;*.xlsm;*.xls|SAS (*.sas7bdat)|*.sas7bdat|SPSS (*.sav)|*.sav|SQLite (*.db;*.sqlite)|*.db;*.sqlite;*.sqlite3|All Files (*.*)|*.*";
            openFileDialog1.RestoreDirectory = true;
            openFileDialog1.Multiselect = true;
            // 
            // tabStrip — 열린 문서 탭 띠(툴바 아래, 칩 띠·본문 위). 문서가 없으면 숨김.
            // 
            tabStrip.Dock = DockStyle.Top;
            tabStrip.Name = "tabStrip";
            tabStrip.Height = 30;
            tabStrip.Visible = false;
            // 
            // workspaceDockHost — 작업 공간 탐색기용 왼쪽 도킹 영역(Wave B가 채운다). 기본은 접힘.
            // 
            workspaceDockHost.Dock = DockStyle.Left;
            workspaceDockHost.Name = "workspaceDockHost";
            workspaceDockHost.Width = 260;
            workspaceDockHost.Visible = false;
            // 
            // workspaceSplitter — 폭·색은 Ui/Form1.PanelChrome.cs가 규격(PanelChrome)대로 정한다.
            //
            workspaceSplitter.Dock = DockStyle.Left;
            workspaceSplitter.Name = "workspaceSplitter";
            workspaceSplitter.Width = 5;
            workspaceSplitter.MinSize = 120;
            workspaceSplitter.MinExtra = 300;
            workspaceSplitter.TabStop = false;
            workspaceSplitter.Visible = false;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1008, 729);
            Controls.Add(outerSplit);
            Controls.Add(workspaceSplitter);
            Controls.Add(workspaceDockHost);
            Controls.Add(statusStrip1);
            Controls.Add(tabStrip);
            Controls.Add(toolStrip1);
            Controls.Add(menuStrip1);
            MainMenuStrip = menuStrip1;
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Nanum CSV Viewer";
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            toolStrip1.ResumeLayout(false);
            toolStrip1.PerformLayout();
            gridContextMenu.ResumeLayout(false);
            outerSplit.Panel1.ResumeLayout(false);
            outerSplit.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)outerSplit).EndInit();
            outerSplit.ResumeLayout(false);
            splitContainer1.Panel1.ResumeLayout(false);
            splitContainer1.Panel1.PerformLayout();
            splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainer1).EndInit();
            splitContainer1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)grid).EndInit();
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private MenuStrip menuStrip1;

        private ToolStrip toolStrip1;
        private ToolStripButton openToolStripButton;
        private ToolStripLabel findLabel;
        private ToolStripTextBox findTextBox;
        private ToolStripButton findNextButton;
        private ToolStripLabel filterColumnLabel;
        private ToolStripComboBox filterColumnCombo;
        private ToolStripTextBox filterTextBox;
        private ToolStripButton applyFilterButton;
        private ToolStripButton clearFilterButton;
        private ToolStripButton sortAscButton;
        private ToolStripButton sortDescButton;
        private ToolStripButton clearSortButton;
        private ContextMenuStrip gridContextMenu;

        private SplitContainer outerSplit;
        private SplitContainer splitContainer1;
        private TextBox cellAddressBox;
        private ChromeAddressHost cellAddressHost;
        private TextBox cellValueTextBox;
        private Label detailHeaderLabel;
        private RichTextBox detailRichText;
        private ToolStripButton detailToggleButton;
        private BufferedDataGridView grid;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel progressLabel;
        private ToolStripProgressBar progressBar;
        private ToolStripDropDownButton encodingStatusButton;
        private ToolStripStatusLabel signalLabel;
        private OpenFileDialog openFileDialog1;
        private TabStrip tabStrip;
        private Panel workspaceDockHost;
        private DividerSplitter workspaceSplitter;
    }
}
