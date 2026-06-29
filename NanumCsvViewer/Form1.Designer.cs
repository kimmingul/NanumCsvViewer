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
            editToolStripMenuItem = new ToolStripMenuItem();
            findMenuItem = new ToolStripMenuItem();
            findNextMenuItem = new ToolStripMenuItem();
            editSeparator1 = new ToolStripSeparator();
            applyFilterMenuItem = new ToolStripMenuItem();
            editFilterByCellMenuItem = new ToolStripMenuItem();
            clearFilterToolStripMenuItem = new ToolStripMenuItem();
            editSeparator2 = new ToolStripSeparator();
            sortAscMenuItem = new ToolStripMenuItem();
            sortDescMenuItem = new ToolStripMenuItem();
            clearSortToolStripMenuItem = new ToolStripMenuItem();
            viewToolStripMenuItem = new ToolStripMenuItem();
            encodingMenuItem = new ToolStripMenuItem();
            viewSeparator1 = new ToolStripSeparator();
            detailPanelMenuItem = new ToolStripMenuItem();
            languageMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            usageMenuItem = new ToolStripMenuItem();
            aboutToolStripMenuItem = new ToolStripMenuItem();
            toolStrip1 = new ToolStrip();
            openToolStripButton = new ToolStripButton();
            toolStripSeparatorA = new ToolStripSeparator();
            sortAscButton = new ToolStripButton();
            sortDescButton = new ToolStripButton();
            clearSortButton = new ToolStripButton();
            toolStripSeparator1 = new ToolStripSeparator();
            findLabel = new ToolStripLabel();
            findTextBox = new ToolStripTextBox();
            findNextButton = new ToolStripButton();
            toolStripSeparatorC = new ToolStripSeparator();
            filterByCellButton = new ToolStripButton();
            filterColumnLabel = new ToolStripLabel();
            filterColumnCombo = new ToolStripComboBox();
            filterTextBox = new ToolStripTextBox();
            applyFilterButton = new ToolStripButton();
            clearFilterButton = new ToolStripButton();
            toolStripSeparatorE = new ToolStripSeparator();
            detailToggleButton = new ToolStripButton();
            themeToggleButton = new ToolStripButton();
            gridContextMenu = new ContextMenuStrip(components);
            filterByCellMenuItem = new ToolStripMenuItem();
            outerSplit = new SplitContainer();
            splitContainer1 = new SplitContainer();
            cellValueTextBox = new TextBox();
            cellAddressLabel = new Label();
            grid = new DataGridView();
            detailRichText = new RichTextBox();
            detailHeaderLabel = new Label();
            toolStripSeparatorD = new ToolStripSeparator();
            statusStrip1 = new StatusStrip();
            statusLabel = new ToolStripStatusLabel();
            progressLabel = new ToolStripStatusLabel();
            progressBar = new ToolStripProgressBar();
            encodingStatusButton = new ToolStripDropDownButton();
            signalLabel = new ToolStripStatusLabel();
            openFileDialog1 = new OpenFileDialog();
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
            // menuStrip1
            // 
            menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, editToolStripMenuItem, viewToolStripMenuItem, helpToolStripMenuItem });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(1008, 24);
            menuStrip1.TabIndex = 4;
            menuStrip1.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { openToolStripMenuItem, quitToolStripMenuItem });
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(37, 20);
            fileToolStripMenuItem.Text = "File";
            // 
            // openToolStripMenuItem
            // 
            openToolStripMenuItem.Name = "openToolStripMenuItem";
            openToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.O;
            openToolStripMenuItem.Size = new Size(155, 22);
            openToolStripMenuItem.Text = "Open...";
            openToolStripMenuItem.Click += OnOpenClick;
            // 
            // quitToolStripMenuItem
            // 
            quitToolStripMenuItem.Name = "quitToolStripMenuItem";
            quitToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.Q;
            quitToolStripMenuItem.Size = new Size(155, 22);
            quitToolStripMenuItem.Text = "Quit";
            quitToolStripMenuItem.Click += OnQuitClick;
            // 
            // editToolStripMenuItem
            // 
            editToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { findMenuItem, findNextMenuItem, editSeparator1, applyFilterMenuItem, editFilterByCellMenuItem, clearFilterToolStripMenuItem, editSeparator2, sortAscMenuItem, sortDescMenuItem, clearSortToolStripMenuItem });
            editToolStripMenuItem.Name = "editToolStripMenuItem";
            editToolStripMenuItem.Size = new Size(39, 20);
            editToolStripMenuItem.Text = "Edit";
            // 
            // findMenuItem
            // 
            findMenuItem.Name = "findMenuItem";
            findMenuItem.ShortcutKeys = Keys.Control | Keys.F;
            findMenuItem.Size = new Size(211, 22);
            findMenuItem.Text = "찾기...";
            findMenuItem.Click += OnFindMenuClick;
            // 
            // findNextMenuItem
            // 
            findNextMenuItem.Name = "findNextMenuItem";
            findNextMenuItem.ShortcutKeys = Keys.F3;
            findNextMenuItem.Size = new Size(211, 22);
            findNextMenuItem.Text = "다음 찾기";
            findNextMenuItem.Click += OnFindNextClick;
            // 
            // editSeparator1
            // 
            editSeparator1.Name = "editSeparator1";
            editSeparator1.Size = new Size(208, 6);
            // 
            // applyFilterMenuItem
            // 
            applyFilterMenuItem.Name = "applyFilterMenuItem";
            applyFilterMenuItem.Size = new Size(211, 22);
            applyFilterMenuItem.Text = "필터 적용";
            applyFilterMenuItem.Click += OnApplyFilterClick;
            // 
            // editFilterByCellMenuItem
            // 
            editFilterByCellMenuItem.Name = "editFilterByCellMenuItem";
            editFilterByCellMenuItem.ShortcutKeys = Keys.Control | Keys.B;
            editFilterByCellMenuItem.Size = new Size(211, 22);
            editFilterByCellMenuItem.Text = "이 셀 값으로 필터";
            editFilterByCellMenuItem.Click += OnFilterByCellClick;
            // 
            // clearFilterToolStripMenuItem
            // 
            clearFilterToolStripMenuItem.Name = "clearFilterToolStripMenuItem";
            clearFilterToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.L;
            clearFilterToolStripMenuItem.Size = new Size(211, 22);
            clearFilterToolStripMenuItem.Text = "Clear Filter";
            clearFilterToolStripMenuItem.Click += OnClearFilterClick;
            // 
            // editSeparator2
            // 
            editSeparator2.Name = "editSeparator2";
            editSeparator2.Size = new Size(208, 6);
            // 
            // sortAscMenuItem
            // 
            sortAscMenuItem.Name = "sortAscMenuItem";
            sortAscMenuItem.Size = new Size(211, 22);
            sortAscMenuItem.Text = "오름차순 정렬 (현재 열)";
            sortAscMenuItem.Click += OnSortAscMenuClick;
            // 
            // sortDescMenuItem
            // 
            sortDescMenuItem.Name = "sortDescMenuItem";
            sortDescMenuItem.Size = new Size(211, 22);
            sortDescMenuItem.Text = "내림차순 정렬 (현재 열)";
            sortDescMenuItem.Click += OnSortDescMenuClick;
            // 
            // clearSortToolStripMenuItem
            // 
            clearSortToolStripMenuItem.Name = "clearSortToolStripMenuItem";
            clearSortToolStripMenuItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.S;
            clearSortToolStripMenuItem.Size = new Size(211, 22);
            clearSortToolStripMenuItem.Text = "Clear Sort";
            clearSortToolStripMenuItem.Click += OnClearSortClick;
            // 
            // viewToolStripMenuItem
            // 
            viewToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { encodingMenuItem, languageMenuItem, viewSeparator1, detailPanelMenuItem });
            viewToolStripMenuItem.Name = "viewToolStripMenuItem";
            viewToolStripMenuItem.Size = new Size(45, 20);
            viewToolStripMenuItem.Text = "View";
            // 
            // encodingMenuItem
            // 
            encodingMenuItem.Name = "encodingMenuItem";
            encodingMenuItem.Size = new Size(146, 22);
            encodingMenuItem.Text = "인코딩";
            // 
            // viewSeparator1
            // 
            viewSeparator1.Name = "viewSeparator1";
            viewSeparator1.Size = new Size(143, 6);
            // 
            // detailPanelMenuItem
            // 
            detailPanelMenuItem.CheckOnClick = true;
            detailPanelMenuItem.Name = "detailPanelMenuItem";
            detailPanelMenuItem.ShortcutKeys = Keys.F4;
            detailPanelMenuItem.Size = new Size(146, 22);
            detailPanelMenuItem.Text = "상세 패널";
            detailPanelMenuItem.CheckedChanged += OnDetailMenuChanged;
            //
            // languageMenuItem (하위 항목은 BuildLanguageMenu에서 채움)
            //
            languageMenuItem.Name = "languageMenuItem";
            languageMenuItem.Size = new Size(146, 22);
            languageMenuItem.Text = "Language";
            //
            // helpToolStripMenuItem
            //
            helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { usageMenuItem, aboutToolStripMenuItem });
            helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            helpToolStripMenuItem.Size = new Size(44, 20);
            helpToolStripMenuItem.Text = "Help";
            //
            // usageMenuItem
            //
            usageMenuItem.Name = "usageMenuItem";
            usageMenuItem.ShortcutKeys = Keys.F1;
            usageMenuItem.Size = new Size(107, 22);
            usageMenuItem.Text = "How to Use";
            usageMenuItem.Click += OnUsageClick;
            //
            // aboutToolStripMenuItem
            //
            aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            aboutToolStripMenuItem.Size = new Size(107, 22);
            aboutToolStripMenuItem.Text = "About";
            aboutToolStripMenuItem.Click += OnAboutClick;
            // 
            // toolStrip1
            // 
            toolStrip1.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip1.Items.AddRange(new ToolStripItem[] { openToolStripButton, toolStripSeparatorA, sortAscButton, sortDescButton, clearSortButton, toolStripSeparator1, findLabel, findTextBox, findNextButton, toolStripSeparatorC, filterByCellButton, filterColumnLabel, filterColumnCombo, filterTextBox, applyFilterButton, clearFilterButton, toolStripSeparatorE, detailToggleButton, themeToggleButton });
            toolStrip1.Location = new Point(0, 24);
            toolStrip1.Name = "toolStrip1";
            toolStrip1.Size = new Size(1008, 25);
            toolStrip1.TabIndex = 3;
            // 
            // openToolStripButton
            // 
            openToolStripButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            openToolStripButton.Name = "openToolStripButton";
            openToolStripButton.Size = new Size(23, 22);
            openToolStripButton.Text = "Open";
            openToolStripButton.Click += OnOpenClick;
            // 
            // toolStripSeparatorA
            // 
            toolStripSeparatorA.Name = "toolStripSeparatorA";
            toolStripSeparatorA.Size = new Size(6, 25);
            // 
            // sortAscButton
            // 
            sortAscButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            sortAscButton.Name = "sortAscButton";
            sortAscButton.Size = new Size(23, 22);
            sortAscButton.Text = "Sort ▲";
            sortAscButton.ToolTipText = "현재 열을 오름차순으로 정렬";
            sortAscButton.Click += OnSortAscMenuClick;
            // 
            // sortDescButton
            // 
            sortDescButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            sortDescButton.Name = "sortDescButton";
            sortDescButton.Size = new Size(23, 22);
            sortDescButton.Text = "Sort ▼";
            sortDescButton.ToolTipText = "현재 열을 내림차순으로 정렬";
            sortDescButton.Click += OnSortDescMenuClick;
            // 
            // clearSortButton
            // 
            clearSortButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            clearSortButton.Name = "clearSortButton";
            clearSortButton.Size = new Size(23, 22);
            clearSortButton.Text = "Clear Sort";
            clearSortButton.ToolTipText = "정렬 해제(파일 순서로 복원)";
            clearSortButton.Click += OnClearSortClick;
            // 
            // toolStripSeparator1
            // 
            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(6, 25);
            // 
            // findLabel
            // 
            findLabel.Name = "findLabel";
            findLabel.Size = new Size(33, 22);
            findLabel.Text = "Find:";
            // 
            // findTextBox
            // 
            findTextBox.Name = "findTextBox";
            findTextBox.Size = new Size(140, 25);
            findTextBox.KeyDown += OnFindKeyDown;
            // 
            // findNextButton
            // 
            findNextButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            findNextButton.Name = "findNextButton";
            findNextButton.Size = new Size(36, 22);
            findNextButton.Text = "Next";
            findNextButton.Click += OnFindNextClick;
            // 
            // toolStripSeparatorC
            // 
            toolStripSeparatorC.Name = "toolStripSeparatorC";
            toolStripSeparatorC.Size = new Size(6, 25);
            // 
            // filterByCellButton
            // 
            filterByCellButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            filterByCellButton.Name = "filterByCellButton";
            filterByCellButton.Size = new Size(78, 22);
            filterByCellButton.Text = "Filter by Cell";
            filterByCellButton.ToolTipText = "선택한 셀의 열을 그 값으로 필터(기존 필터에 AND 누적)";
            filterByCellButton.Click += OnFilterByCellClick;
            // 
            // filterColumnLabel
            // 
            filterColumnLabel.Name = "filterColumnLabel";
            filterColumnLabel.Size = new Size(36, 22);
            filterColumnLabel.Text = "Filter:";
            // 
            // filterColumnCombo
            // 
            filterColumnCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            filterColumnCombo.Name = "filterColumnCombo";
            filterColumnCombo.Size = new Size(130, 25);
            // 
            // filterTextBox
            // 
            filterTextBox.Name = "filterTextBox";
            filterTextBox.Size = new Size(140, 25);
            filterTextBox.KeyDown += OnFilterKeyDown;
            // 
            // applyFilterButton
            // 
            applyFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            applyFilterButton.Name = "applyFilterButton";
            applyFilterButton.Size = new Size(42, 22);
            applyFilterButton.Text = "Apply";
            applyFilterButton.Click += OnApplyFilterClick;
            // 
            // clearFilterButton
            // 
            clearFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            clearFilterButton.Name = "clearFilterButton";
            clearFilterButton.Size = new Size(38, 22);
            clearFilterButton.Text = "Clear";
            clearFilterButton.Click += OnClearFilterClick;
            // 
            // toolStripSeparatorE
            // 
            toolStripSeparatorE.Name = "toolStripSeparatorE";
            toolStripSeparatorE.Size = new Size(6, 25);
            // 
            // detailToggleButton
            // 
            detailToggleButton.Alignment = ToolStripItemAlignment.Right;
            detailToggleButton.CheckOnClick = true;
            detailToggleButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            detailToggleButton.Name = "detailToggleButton";
            detailToggleButton.Size = new Size(72, 22);
            detailToggleButton.Text = "Details (F4)";
            detailToggleButton.ToolTipText = "선택한 행의 전체 내용을 우측 패널에 표시";
            detailToggleButton.CheckedChanged += OnDetailToggleChanged;
            //
            // themeToggleButton (우측 정렬, 아이콘은 ApplyTheme에서 설정)
            //
            themeToggleButton.Alignment = ToolStripItemAlignment.Right;
            themeToggleButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            themeToggleButton.ImageScaling = ToolStripItemImageScaling.None;
            themeToggleButton.Name = "themeToggleButton";
            themeToggleButton.Size = new Size(23, 22);
            themeToggleButton.Click += OnThemeToggleClick;
            //
            // gridContextMenu
            //
            gridContextMenu.Items.AddRange(new ToolStripItem[] { filterByCellMenuItem });
            gridContextMenu.Name = "gridContextMenu";
            gridContextMenu.Size = new Size(171, 26);
            // 
            // filterByCellMenuItem
            // 
            filterByCellMenuItem.Name = "filterByCellMenuItem";
            filterByCellMenuItem.Size = new Size(170, 22);
            filterByCellMenuItem.Text = "이 셀 값으로 필터";
            filterByCellMenuItem.Click += OnFilterByCellClick;
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
            splitContainer1.Panel1.Controls.Add(cellAddressLabel);
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
            // cellValueTextBox
            // 
            cellValueTextBox.BackColor = SystemColors.Window;
            cellValueTextBox.BorderStyle = BorderStyle.FixedSingle;
            cellValueTextBox.Dock = DockStyle.Fill;
            cellValueTextBox.Location = new Point(150, 0);
            cellValueTextBox.Multiline = true;
            cellValueTextBox.Name = "cellValueTextBox";
            cellValueTextBox.ReadOnly = true;
            cellValueTextBox.ScrollBars = ScrollBars.Vertical;
            cellValueTextBox.Size = new Size(858, 329);
            cellValueTextBox.TabIndex = 0;
            // 
            // cellAddressLabel
            // 
            cellAddressLabel.BackColor = SystemColors.Control;
            cellAddressLabel.BorderStyle = BorderStyle.Fixed3D;
            cellAddressLabel.Dock = DockStyle.Left;
            cellAddressLabel.Location = new Point(0, 0);
            cellAddressLabel.Name = "cellAddressLabel";
            cellAddressLabel.Padding = new Padding(5, 0, 0, 0);
            cellAddressLabel.Size = new Size(150, 329);
            cellAddressLabel.TabIndex = 1;
            cellAddressLabel.TextAlign = ContentAlignment.MiddleLeft;
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
            detailHeaderLabel.BorderStyle = BorderStyle.FixedSingle;
            detailHeaderLabel.Dock = DockStyle.Top;
            detailHeaderLabel.Location = new Point(0, 0);
            detailHeaderLabel.Name = "detailHeaderLabel";
            detailHeaderLabel.Padding = new Padding(5, 0, 0, 0);
            detailHeaderLabel.Size = new Size(96, 22);
            detailHeaderLabel.TabIndex = 1;
            detailHeaderLabel.Text = "행 상세";
            detailHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // toolStripSeparatorD
            // 
            toolStripSeparatorD.Name = "toolStripSeparatorD";
            toolStripSeparatorD.Size = new Size(6, 6);
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
            openFileDialog1.Filter = "CSV / Text File (*.csv;*.txt)|*.csv;*.txt|All Files (*.*)|*.*";
            openFileDialog1.RestoreDirectory = true;
            // 
            // Form1
            // 
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
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem openToolStripMenuItem;
        private ToolStripMenuItem quitToolStripMenuItem;
        private ToolStripMenuItem editToolStripMenuItem;
        private ToolStripMenuItem findMenuItem;
        private ToolStripMenuItem findNextMenuItem;
        private ToolStripSeparator editSeparator1;
        private ToolStripMenuItem applyFilterMenuItem;
        private ToolStripMenuItem editFilterByCellMenuItem;
        private ToolStripMenuItem clearFilterToolStripMenuItem;
        private ToolStripSeparator editSeparator2;
        private ToolStripMenuItem sortAscMenuItem;
        private ToolStripMenuItem sortDescMenuItem;
        private ToolStripMenuItem clearSortToolStripMenuItem;
        private ToolStripMenuItem viewToolStripMenuItem;
        private ToolStripMenuItem encodingMenuItem;
        private ToolStripSeparator viewSeparator1;
        private ToolStripMenuItem languageMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStripMenuItem usageMenuItem;
        private ToolStripMenuItem aboutToolStripMenuItem;

        private ToolStrip toolStrip1;
        private ToolStripButton openToolStripButton;
        private ToolStripSeparator toolStripSeparatorA;
        private ToolStripLabel findLabel;
        private ToolStripTextBox findTextBox;
        private ToolStripButton findNextButton;
        private ToolStripSeparator toolStripSeparatorC;
        private ToolStripLabel filterColumnLabel;
        private ToolStripComboBox filterColumnCombo;
        private ToolStripTextBox filterTextBox;
        private ToolStripButton applyFilterButton;
        private ToolStripButton clearFilterButton;
        private ToolStripButton filterByCellButton;
        private ToolStripSeparator toolStripSeparatorE;
        private ToolStripButton sortAscButton;
        private ToolStripButton sortDescButton;
        private ToolStripButton clearSortButton;
        private ContextMenuStrip gridContextMenu;
        private ToolStripMenuItem filterByCellMenuItem;

        private SplitContainer outerSplit;
        private SplitContainer splitContainer1;
        private Label cellAddressLabel;
        private TextBox cellValueTextBox;
        private Label detailHeaderLabel;
        private RichTextBox detailRichText;
        private ToolStripButton detailToggleButton;
        private ToolStripButton themeToggleButton;
        private ToolStripSeparator toolStripSeparatorD;
        private ToolStripMenuItem detailPanelMenuItem;
        private DataGridView grid;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel statusLabel;
        private ToolStripStatusLabel progressLabel;
        private ToolStripProgressBar progressBar;
        private ToolStripDropDownButton encodingStatusButton;
        private ToolStripStatusLabel signalLabel;
        private OpenFileDialog openFileDialog1;
        private ToolStripSeparator toolStripSeparator1;
    }
}
