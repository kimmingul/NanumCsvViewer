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
            fileToolStripMenuItem = new ToolStripMenuItem();
            openToolStripMenuItem = new ToolStripMenuItem();
            quitToolStripMenuItem = new ToolStripMenuItem();
            viewToolStripMenuItem = new ToolStripMenuItem();
            clearFilterToolStripMenuItem = new ToolStripMenuItem();
            clearSortToolStripMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            aboutToolStripMenuItem = new ToolStripMenuItem();

            toolStrip1 = new ToolStrip();
            openToolStripButton = new ToolStripButton();
            toolStripSeparatorA = new ToolStripSeparator();
            encodingLabel = new ToolStripLabel();
            encodingCombo = new ToolStripComboBox();
            toolStripSeparatorB = new ToolStripSeparator();
            findLabel = new ToolStripLabel();
            findTextBox = new ToolStripTextBox();
            findNextButton = new ToolStripButton();
            toolStripSeparatorC = new ToolStripSeparator();
            filterColumnLabel = new ToolStripLabel();
            filterColumnCombo = new ToolStripComboBox();
            filterTextBox = new ToolStripTextBox();
            applyFilterButton = new ToolStripButton();
            clearFilterButton = new ToolStripButton();

            outerSplit = new SplitContainer();
            splitContainer1 = new SplitContainer();
            cellAddressLabel = new Label();
            cellValueTextBox = new TextBox();
            detailHeaderLabel = new Label();
            detailRichText = new RichTextBox();
            detailToggleButton = new ToolStripButton();
            toolStripSeparatorD = new ToolStripSeparator();
            detailPanelMenuItem = new ToolStripMenuItem();
            grid = new DataGridView();
            statusStrip1 = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            progressLabel = new ToolStripStatusLabel();
            progressBar = new ToolStripProgressBar();
            openFileDialog1 = new OpenFileDialog();

            menuStrip1.SuspendLayout();
            toolStrip1.SuspendLayout();
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

            // menuStrip1
            menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, viewToolStripMenuItem, helpToolStripMenuItem });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(1008, 24);
            menuStrip1.Text = "menuStrip1";
            // File
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { openToolStripMenuItem, quitToolStripMenuItem });
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(37, 20);
            fileToolStripMenuItem.Text = "File";
            openToolStripMenuItem.Name = "openToolStripMenuItem";
            openToolStripMenuItem.Size = new Size(180, 22);
            openToolStripMenuItem.Text = "Open...";
            openToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.O;
            openToolStripMenuItem.Click += OnOpenClick;
            quitToolStripMenuItem.Name = "quitToolStripMenuItem";
            quitToolStripMenuItem.Size = new Size(180, 22);
            quitToolStripMenuItem.Text = "Quit";
            quitToolStripMenuItem.Click += OnQuitClick;
            // View
            viewToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { detailPanelMenuItem, clearFilterToolStripMenuItem, clearSortToolStripMenuItem });
            viewToolStripMenuItem.Name = "viewToolStripMenuItem";
            viewToolStripMenuItem.Size = new Size(45, 20);
            viewToolStripMenuItem.Text = "View";
            detailPanelMenuItem.Name = "detailPanelMenuItem";
            detailPanelMenuItem.Size = new Size(180, 22);
            detailPanelMenuItem.Text = "상세 패널";
            detailPanelMenuItem.CheckOnClick = true;
            detailPanelMenuItem.ShortcutKeys = Keys.F4;
            detailPanelMenuItem.CheckedChanged += OnDetailMenuChanged;
            clearFilterToolStripMenuItem.Name = "clearFilterToolStripMenuItem";
            clearFilterToolStripMenuItem.Size = new Size(180, 22);
            clearFilterToolStripMenuItem.Text = "Clear Filter";
            clearFilterToolStripMenuItem.Click += OnClearFilterClick;
            clearSortToolStripMenuItem.Name = "clearSortToolStripMenuItem";
            clearSortToolStripMenuItem.Size = new Size(180, 22);
            clearSortToolStripMenuItem.Text = "Clear Sort";
            clearSortToolStripMenuItem.Click += OnClearSortClick;
            // Help
            helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { aboutToolStripMenuItem });
            helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            helpToolStripMenuItem.Size = new Size(44, 20);
            helpToolStripMenuItem.Text = "Help";
            aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            aboutToolStripMenuItem.Size = new Size(180, 22);
            aboutToolStripMenuItem.Text = "About";
            aboutToolStripMenuItem.Click += OnAboutClick;

            // toolStrip1
            toolStrip1.Items.AddRange(new ToolStripItem[]
            {
                openToolStripButton, toolStripSeparatorA,
                encodingLabel, encodingCombo, toolStripSeparatorB,
                findLabel, findTextBox, findNextButton, toolStripSeparatorC,
                filterColumnLabel, filterColumnCombo, filterTextBox, applyFilterButton, clearFilterButton,
                toolStripSeparatorD, detailToggleButton
            });
            toolStrip1.Location = new Point(0, 24);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(1008, 25);
            toolStrip1.GripStyle = ToolStripGripStyle.Hidden;

            openToolStripButton.Name = "openToolStripButton";
            openToolStripButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            openToolStripButton.Text = "Open";
            openToolStripButton.Click += OnOpenClick;

            toolStripSeparatorA.Name = "toolStripSeparatorA";

            encodingLabel.Name = "encodingLabel";
            encodingLabel.Text = "Encoding:";

            encodingCombo.Name = "encodingCombo";
            encodingCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            encodingCombo.Size = new Size(140, 25);
            encodingCombo.SelectedIndexChanged += OnEncodingChanged;

            toolStripSeparatorB.Name = "toolStripSeparatorB";

            findLabel.Name = "findLabel";
            findLabel.Text = "Find:";

            findTextBox.Name = "findTextBox";
            findTextBox.Size = new Size(140, 25);
            findTextBox.KeyDown += OnFindKeyDown;

            findNextButton.Name = "findNextButton";
            findNextButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            findNextButton.Text = "Find Next";
            findNextButton.Click += OnFindNextClick;

            toolStripSeparatorC.Name = "toolStripSeparatorC";

            filterColumnLabel.Name = "filterColumnLabel";
            filterColumnLabel.Text = "Filter:";

            filterColumnCombo.Name = "filterColumnCombo";
            filterColumnCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            filterColumnCombo.Size = new Size(130, 25);

            filterTextBox.Name = "filterTextBox";
            filterTextBox.Size = new Size(140, 25);
            filterTextBox.KeyDown += OnFilterKeyDown;

            applyFilterButton.Name = "applyFilterButton";
            applyFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            applyFilterButton.Text = "Apply";
            applyFilterButton.Click += OnApplyFilterClick;

            clearFilterButton.Name = "clearFilterButton";
            clearFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            clearFilterButton.Text = "Clear";
            clearFilterButton.Click += OnClearFilterClick;

            toolStripSeparatorD.Name = "toolStripSeparatorD";

            detailToggleButton.Name = "detailToggleButton";
            detailToggleButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            detailToggleButton.Text = "⊟ 상세 패널 (F4)";
            detailToggleButton.CheckOnClick = true;
            detailToggleButton.ToolTipText = "선택한 행의 전체 내용을 우측 패널에 표시";
            detailToggleButton.CheckedChanged += OnDetailToggleChanged;

            // outerSplit (좌: 기존 화면 / 우: 상세 패널) — 세로 분할, 폭 확장, 기본 숨김
            outerSplit.Name = "outerSplit";
            outerSplit.Dock = DockStyle.Fill;
            outerSplit.Orientation = Orientation.Vertical;
            outerSplit.SplitterWidth = 4;
            outerSplit.Panel1MinSize = 200;
            outerSplit.Panel2MinSize = 150;
            outerSplit.Panel1.Controls.Add(splitContainer1);
            outerSplit.Panel2.Controls.Add(detailRichText);
            outerSplit.Panel2.Controls.Add(detailHeaderLabel);
            outerSplit.Panel2Collapsed = true;

            // detailHeaderLabel (패널 머리글: 행 번호)
            detailHeaderLabel.Name = "detailHeaderLabel";
            detailHeaderLabel.Dock = DockStyle.Top;
            detailHeaderLabel.Height = 22;
            detailHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
            detailHeaderLabel.Padding = new Padding(5, 0, 0, 0);
            detailHeaderLabel.BackColor = SystemColors.Control;
            detailHeaderLabel.BorderStyle = BorderStyle.FixedSingle;
            detailHeaderLabel.Text = "행 상세";

            // detailRichText (선택 행 전체: 컬럼명 + 값, 멀티라인 읽기 전용)
            detailRichText.Name = "detailRichText";
            detailRichText.Dock = DockStyle.Fill;
            detailRichText.ReadOnly = true;
            detailRichText.BorderStyle = BorderStyle.None;
            detailRichText.BackColor = SystemColors.Window;
            detailRichText.WordWrap = true;
            detailRichText.ScrollBars = RichTextBoxScrollBars.Both;
            detailRichText.DetectUrls = false;

            // splitContainer1 (위: 선택 셀 값 표시줄 / 아래: 그리드) — 가로 분할, 스플리터로 높이 확장
            splitContainer1.Name = "splitContainer1";
            splitContainer1.Dock = DockStyle.Fill;
            splitContainer1.Orientation = Orientation.Horizontal;
            splitContainer1.SplitterWidth = 4;
            splitContainer1.Panel1MinSize = 22;
            splitContainer1.Panel2MinSize = 60;
            splitContainer1.Panel1.Controls.Add(cellValueTextBox);
            splitContainer1.Panel1.Controls.Add(cellAddressLabel);
            splitContainer1.Panel2.Controls.Add(grid);

            // cellAddressLabel (Excel 이름 상자 느낌)
            cellAddressLabel.Name = "cellAddressLabel";
            cellAddressLabel.Dock = DockStyle.Left;
            cellAddressLabel.Width = 150;
            cellAddressLabel.AutoSize = false;
            cellAddressLabel.TextAlign = ContentAlignment.MiddleLeft;
            cellAddressLabel.BorderStyle = BorderStyle.Fixed3D;
            cellAddressLabel.Padding = new Padding(5, 0, 0, 0);
            cellAddressLabel.BackColor = SystemColors.Control;

            // cellValueTextBox (선택 셀 전체 값, 읽기 전용, 여러 줄)
            cellValueTextBox.Name = "cellValueTextBox";
            cellValueTextBox.Dock = DockStyle.Fill;
            cellValueTextBox.Multiline = true;
            cellValueTextBox.ReadOnly = true;
            cellValueTextBox.WordWrap = true;
            cellValueTextBox.ScrollBars = ScrollBars.Vertical;
            cellValueTextBox.BorderStyle = BorderStyle.FixedSingle;
            cellValueTextBox.BackColor = SystemColors.Window;

            // grid
            grid.Name = "grid";
            grid.Dock = DockStyle.Fill;
            grid.VirtualMode = true;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = true;
            grid.RowHeadersWidth = 70;
            grid.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            grid.BackgroundColor = SystemColors.Window;
            grid.CellValueNeeded += OnCellValueNeeded;
            grid.RowHeightInfoNeeded += OnRowHeightInfoNeeded;
            grid.RowPostPaint += OnRowPostPaint;
            grid.CurrentCellChanged += OnCurrentCellChanged;
            grid.ColumnHeaderMouseClick += OnColumnHeaderMouseClick;

            // statusStrip1
            statusStrip1.Items.AddRange(new ToolStripItem[] { statusLabel, progressLabel, progressBar });
            statusStrip1.Location = new Point(0, 707);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(1008, 22);
            statusStrip1.Text = "statusStrip1";

            statusLabel.Name = "statusLabel";
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Text = "Ready.";

            progressLabel.Name = "progressLabel";
            progressLabel.AutoSize = false;
            progressLabel.Size = new Size(50, 17);
            progressLabel.TextAlign = ContentAlignment.MiddleRight;
            progressLabel.Visible = false;

            progressBar.Name = "progressBar";
            progressBar.Size = new Size(200, 16);
            progressBar.Visible = false;

            // openFileDialog1
            openFileDialog1.Filter = "CSV / Text File (*.csv;*.txt)|*.csv;*.txt|All Files (*.*)|*.*";
            openFileDialog1.FilterIndex = 1;
            openFileDialog1.RestoreDirectory = true;

            // Form1
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1008, 729);
            Controls.Add(outerSplit);
            Controls.Add(statusStrip1);
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
            ((System.ComponentModel.ISupportInitialize)grid).EndInit();
            splitContainer1.Panel1.ResumeLayout(false);
            splitContainer1.Panel1.PerformLayout();
            splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainer1).EndInit();
            splitContainer1.ResumeLayout(false);
            outerSplit.Panel1.ResumeLayout(false);
            outerSplit.Panel2.ResumeLayout(false);
            outerSplit.Panel2.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)outerSplit).EndInit();
            outerSplit.ResumeLayout(false);
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private MenuStrip menuStrip1;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem openToolStripMenuItem;
        private ToolStripMenuItem quitToolStripMenuItem;
        private ToolStripMenuItem viewToolStripMenuItem;
        private ToolStripMenuItem clearFilterToolStripMenuItem;
        private ToolStripMenuItem clearSortToolStripMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStripMenuItem aboutToolStripMenuItem;

        private ToolStrip toolStrip1;
        private ToolStripButton openToolStripButton;
        private ToolStripSeparator toolStripSeparatorA;
        private ToolStripLabel encodingLabel;
        private ToolStripComboBox encodingCombo;
        private ToolStripSeparator toolStripSeparatorB;
        private ToolStripLabel findLabel;
        private ToolStripTextBox findTextBox;
        private ToolStripButton findNextButton;
        private ToolStripSeparator toolStripSeparatorC;
        private ToolStripLabel filterColumnLabel;
        private ToolStripComboBox filterColumnCombo;
        private ToolStripTextBox filterTextBox;
        private ToolStripButton applyFilterButton;
        private ToolStripButton clearFilterButton;

        private SplitContainer outerSplit;
        private SplitContainer splitContainer1;
        private Label cellAddressLabel;
        private TextBox cellValueTextBox;
        private Label detailHeaderLabel;
        private RichTextBox detailRichText;
        private ToolStripButton detailToggleButton;
        private ToolStripSeparator toolStripSeparatorD;
        private ToolStripMenuItem detailPanelMenuItem;
        private DataGridView grid;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel progressLabel;
        private ToolStripProgressBar progressBar;
        private OpenFileDialog openFileDialog1;
    }
}
