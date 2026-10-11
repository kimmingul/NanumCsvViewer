using Eto.Forms;
using Eto.Drawing;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Eto;

public sealed class ViewerForm : Form
{
    private readonly GridView _grid = new() { ShowHeader = true, AllowMultipleSelection = true };
    private readonly TextBox _filter = new() { PlaceholderText = "필터 또는 검색" };
    private readonly Label _status = new() { Text = "파일을 여세요." };
    private VirtualCsvDocument? _document;
    private VirtualDisplayRows? _rows;
    private CancellationTokenSource? _work;
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    public ViewerForm()
    {
        Title = "Nanum CSV Viewer";
        ClientSize = new Size(960, 640);
        Menu = new MenuBar
        {
            Items =
            {
                new SubMenuItem
                {
                    Text = "파일",
                    Items =
                    {
                        new ButtonMenuItem { Text = "열기…", Command = new Command((_, _) => OpenDialog()) },
                        new ButtonMenuItem { Text = "CSV로 내보내기…", Command = new Command((_, _) => ExportCsv()) },
                    }
                }
            },
            QuitItem = new ButtonMenuItem { Text = "끝내기" }
        };
        var filterButton = new Button { Text = "필터" };
        filterButton.Click += (_, _) => _ = ApplyFilterAsync();
        var clear = new Button { Text = "해제" };
        clear.Click += (_, _) => _ = ClearFilterAsync();
        var find = new Button { Text = "다음 찾기" };
        find.Click += (_, _) => FindNext();
        _grid.ColumnHeaderClick += (_, e) => _ = SortAsync(e.Column.DisplayIndex);
        Content = new TableLayout
        {
            Spacing = new Size(6, 6),
            Padding = 8,
            Rows =
            {
                new TableRow(new TableCell(_filter, true), filterButton, clear, find),
                new TableRow(_grid) { ScaleHeight = true },
                _status
            }
        };
        Closed += (_, _) => CloseDocument();
    }
    public void AttachIndexed(VirtualCsvDocument document)
    {
        CloseDocument();
        _document = document;
        _work = new CancellationTokenSource();
        Bind();
        _status.Text = $"{document.DisplayRowCount:N0}행 · {document.ColumnCount}열 · {document.EncodingName}";
    }
    public string CheckState()
    {
        string first = _rows is { Count: > 0 } ? _rows[0][0] : "";
        string last = _rows is { Count: > 0 } ? _rows[_rows.Count - 1][1] : "";
        return $"{_rows?.Count ?? 0}|{_grid.Columns.Count}|{first}|{last}";
    }



    public async Task OpenAsync(string path)
    {
        CloseDocument();
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        _status.Text = "인덱싱…";
        var doc = VirtualCsvDocument.Open(path);
        _document = doc;
        await doc.RunIndexingAsync(new Progress<IndexProgress>(p =>
            Application.Instance.AsyncInvoke(() => _status.Text = $"인덱싱 {p.Percent}%")), ct);
        if (ct.IsCancellationRequested) return;
        Bind();
        Title = "Nanum CSV Viewer — " + Path.GetFileName(path);
        _status.Text = $"{doc.DisplayRowCount:N0}행 · {doc.ColumnCount}열 · {doc.EncodingName} · {(doc.InMemory ? "RAM" : "Disk")}";
    }

    private void Bind()
    {
        var doc = _document ?? throw new InvalidOperationException();
        _rows = new VirtualDisplayRows(doc);
        _grid.Columns.Clear();
        for (int i = 0; i < doc.ColumnCount; i++)
        {
            int column = i;
            string header = column < doc.Header.Length && doc.Header[column].Length > 0 ? doc.Header[column] : $"Column {column + 1}";
            _grid.Columns.Add(new GridColumn
            {
                HeaderText = header,
                DataCell = new TextBoxCell(column),
                Editable = false,
                Width = 140
            });
        }
        _grid.DataStore = _rows;
    }

    private void OpenDialog()
    {
        using var dialog = new OpenFileDialog { Title = "CSV 열기" };
        dialog.Filters.Add(new FileFilter("CSV", ".csv", ".txt"));
        if (dialog.ShowDialog(this) == DialogResult.Ok && dialog.FileName is { Length: > 0 } path)
            _ = OpenAsync(path);
    }

    private async Task ApplyFilterAsync()
    {
        if (_document is null || _work is null) return;
        var query = CsvSearchQuery.FromUserInput(_filter.Text, null);
        if (query is null) { await ClearFilterAsync(); return; }
        var matcher = new CsvSearchMatcher(query);
        _status.Text = "필터…";
        await _document.ApplyFilterAsync(row => matcher.FirstMatch(row) is not null, null, _work.Token);
        Bind();
        _status.Text = $"{_document.DisplayRowCount:N0}행 일치";
    }

    private async Task ClearFilterAsync()
    {
        if (_document is null || _work is null) return;
        await _document.ApplyFilterAsync(_ => true, null, _work.Token);
        Bind();
        _status.Text = $"{_document.DisplayRowCount:N0}행";
    }

    private async Task SortAsync(int column)
    {
        if (_document is null || _work is null || column < 0) return;
        _sortAscending = _sortColumn == column ? !_sortAscending : true;
        _sortColumn = column;
        _status.Text = "정렬…";
        await _document.SortAsync(column, _sortAscending, null, _work.Token);
        Bind();
        _status.Text = $"{_document.DisplayRowCount:N0}행 · {( _sortAscending ? "오름차순" : "내림차순")}";
    }

    private void FindNext()
    {
        if (_rows is null) return;
        var query = CsvSearchQuery.FromUserInput(_filter.Text, null);
        if (query is null) return;
        var matcher = new CsvSearchMatcher(query);
        int start = _grid.SelectedRows.Cast<int>().DefaultIfEmpty(-1).Max() + 1;
        for (int i = start; i < _rows.Count; i++)
        {
            if (matcher.FirstMatch(_rows[i]) is null) continue;
            _grid.SelectedRows = new[] { i };
            _grid.ScrollToRow(i);
            _status.Text = $"{i + 1:N0}행";
            return;
        }
        _status.Text = "더 이상 없습니다.";
    }

    private void ExportCsv()
    {
        if (_document is null || _rows is null) return;
        using var dialog = new SaveFileDialog { Title = "CSV로 내보내기" };
        dialog.Filters.Add(new FileFilter("CSV", ".csv"));
        if (dialog.ShowDialog(this) != DialogResult.Ok || dialog.FileName is not { Length: > 0 } path) return;
        CsvExporter.Export(ExportFormat.Csv, path, _document.Header, _rows.ReadAll());
        _status.Text = "내보냈습니다.";
    }

    private void CloseDocument()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = null;
        _document?.Dispose();
        _document = null;
        _rows = null;
    }
}
