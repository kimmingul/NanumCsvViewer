using System.Text.Json;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Import;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    /// <summary>작업 공간에 원본을 올리는 호스트 쪽(Form1). 탭은 열지 않는다.</summary>
    internal interface IChatAttachHost
    {
        /// <summary>질의 엔진을 쓸 수 없으면 그 이유, 쓸 수 있으면 null(엔진을 만들지는 않는다).</summary>
        string? UnavailableReason { get; }

        /// <summary>
        /// 파일을 작업 공간 원본으로 올린다(이미 올린 파일이면 그 원본 — 칩은 다시 만든다). 파일마다 따로 시도해 실패는 Failed에 모으고,
        /// 다른 작업 공간 작업이 돌고 있어 시작하지 못하면 Refused에 이유를 담는다.
        /// </summary>
        Task<ChatAttachOutcome> AddAsync(IReadOnlyList<string> files, CancellationToken cancellation);
    }

    internal sealed record ChatAttachOutcome(IReadOnlyList<ChatAttachment> Added, IReadOnlyList<(string File, string Error)> Failed, string? Refused = null);

    // 입력창 + 버튼과 끌어놓기: 파일·폴더를 작업 공간 원본으로 올리고(탭은 열지 않는다) 입력창 칩으로 돌려준다. 칩은 다음 전송에 파일·표 이름 머리말이 된다.
    public sealed partial class ChatController
    {
        /// <summary>폴더에서 이 개수를 넘게 모이면 추가 전에 묻는다.</summary>
        internal const int FolderConfirmAbove = 10;
        private const int MaxNamesInNotice = 8;

        private static readonly string[] CsvLikeExtensions = { ".csv", ".tsv", ".tab", ".txt" };

        private CancellationTokenSource? _attachCts;
        private bool _attaching;

        /// <summary>작업 공간 원본 추가를 맡는 호스트. 없으면 + 메뉴는 안내만 한다.</summary>
        internal IChatAttachHost? AttachHost { get; set; }

        /// <summary>작업 공간에 올릴 수 있는 데이터 파일(CSV/TSV/TXT, 엑셀, SAS, SPSS, SQLite)인가.</summary>
        internal static bool IsAttachable(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return CsvLikeExtensions.Contains(ext) || TabularImporter.IsImportable(path);
        }

        private static IReadOnlyList<ChatAttachment> ParseAttachments(JsonElement items)
        {
            var list = new List<ChatAttachment>();
            foreach (var item in items.Items())
            {
                if (!item.IsObject()) continue;
                string name = item.Str("name").Trim();
                var tables = item.Child("tables").Items()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => (t.GetString() ?? "").Trim())
                    .Where(t => t.Length > 0 && !t.Any(char.IsWhiteSpace))
                    .ToList();
                if (name.Length == 0 || name.Any(char.IsControl) || tables.Count == 0) continue;
                list.Add(new ChatAttachment(name, "", tables));   // 경로는 받지 않는다: 머리말에는 이름만 쓴다
                if (list.Count >= AttachmentContext.MaxAttachments) break;
            }
            return list;
        }

        // ---- 진입점 ----------------------------------------------------------------------------------------------

        /// <summary>페이지의 + 메뉴: kind는 "files"(파일 대화 상자, 여러 개) 또는 "folder"(폴더 대화 상자).</summary>
        private void AttachFromDialog(string kind)
        {
            if (!CanAttach()) return;
            if (kind == "folder")
            {
                string? dir = Dialogs.PickFolder(T(
                    "Add the data files directly in this folder (subfolders are not included)",
                    "이 폴더 바로 아래의 데이터 파일을 추가합니다(하위 폴더는 제외)"));
                if (!string.IsNullOrEmpty(dir)) AttachPaths(new[] { dir });
                return;
            }
            const string exts = "*.csv;*.tsv;*.tab;*.txt;*.xlsx;*.xlsm;*.xls;*.sas7bdat;*.sav;*.db;*.sqlite;*.sqlite3";
            var files = Dialogs.PickFiles(T("Add files to the workspace", "작업 공간에 파일 추가"),
                T($"Data files|{exts}|All files|*.*", $"데이터 파일|{exts}|모든 파일|*.*"));
            if (files is { Count: > 0 }) AttachPaths(files);
        }

        /// <summary>
        /// 고른 또는 끌어놓은 경로를 올린다. 폴더는 바로 아래의 데이터 파일만(하위 폴더 제외), 파일은 지원하는 형식만 올리고
        /// 나머지 파일은 올리지 않은 채 "지원하지 않는 형식" 알림에 이름을 적는다. 폴더에서 10개를 넘게 모이면 먼저 묻는다.
        /// </summary>
        internal void AttachPaths(IReadOnlyList<string> paths)
        {
            if (!CanAttach()) return;
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unsupported = new List<string>();
            var missing = new List<string>();
            bool sawFolder = false, emptyFolder = false;
            foreach (string raw in paths)
            {
                string path = (raw ?? "").Trim().Trim('"');
                if (path.Length == 0) continue;
                try
                {
                    if (Directory.Exists(path))
                    {
                        sawFolder = true;
                        var inFolder = Directory.EnumerateFiles(path).Where(IsAttachable).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                        if (inFolder.Count == 0) emptyFolder = true;
                        foreach (string f in inFolder) if (seen.Add(Path.GetFullPath(f))) files.Add(f);
                    }
                    else if (File.Exists(path))
                    {
                        if (!IsAttachable(path)) unsupported.Add(Path.GetFileName(path));
                        else if (seen.Add(Path.GetFullPath(path))) files.Add(path);
                    }
                    else missing.Add(Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    missing.Add(Path.GetFileName(path.TrimEnd('\\', '/')) + ": " + ex.Message);
                }
            }

            if (unsupported.Count > 0)
                _stream.Emit(ChatPageMessages.Notice("warn", T("Unsupported file type: ", "지원하지 않는 형식: ") + NameList(unsupported)));
            if (missing.Count > 0)
                _stream.Emit(ChatPageMessages.Notice("warn", T("Could not read: ", "읽을 수 없는 항목: ") + NameList(missing)));
            if (files.Count == 0)
            {
                if (emptyFolder) _stream.Emit(ChatPageMessages.Notice("info", T("There are no supported data files directly in that folder.", "그 폴더 바로 아래에는 추가할 수 있는 데이터 파일이 없습니다.")));
                return;
            }
            if (sawFolder && files.Count > FolderConfirmAbove &&
                !Dialogs.Confirm(T("Add files", "파일 추가"), T($"Add {files.Count} files?", $"{files.Count}개 파일을 추가할까요?")))
                return;
            _ = AttachCoreAsync(files);
        }

        private bool CanAttach()
        {
            if (_disposed) return false;
            if (AttachHost is not { } host)
            {
                _stream.Emit(ChatPageMessages.Notice("error", T("Adding files is not available here.", "여기서는 파일을 추가할 수 없습니다.")));
                return false;
            }
            if (_attaching)
            {
                _stream.Emit(ChatPageMessages.Notice("warn", T("Files are still being added. Wait until they finish.", "파일을 추가하는 중입니다. 끝날 때까지 기다리세요.")));
                return false;
            }
            if (host.UnavailableReason is { Length: > 0 } reason)
            {
                _stream.Emit(ChatPageMessages.Notice("error",
                    T("Cannot add files: the query engine is unavailable — ", "파일을 추가할 수 없습니다. 질의 엔진을 쓸 수 없습니다: ") + reason));
                return false;
            }
            return true;
        }

        private async Task AttachCoreAsync(IReadOnlyList<string> files)
        {
            var host = AttachHost!;
            _attaching = true;
            var cts = _attachCts = new CancellationTokenSource();
            try
            {
                var outcome = await host.AddAsync(files, cts.Token);
                if (_disposed) return;
                if (outcome.Added.Count > 0) _page.Post(ChatPageMessages.Attached(outcome.Added));
                if (outcome.Refused is { Length: > 0 } refused)
                    _stream.Emit(ChatPageMessages.Notice("warn", T("Files were not added: ", "파일을 추가하지 못했습니다: ") + refused));
                if (outcome.Failed.Count > 0)
                    _stream.Emit(ChatPageMessages.Notice("error",
                        T("These files could not be added:\n", "추가하지 못한 파일:\n") + string.Join("\n", outcome.Failed.Select(f => Path.GetFileName(f.File) + ": " + f.Error))));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Note("attach failed: " + ex);
                if (!_disposed) _stream.Emit(ChatPageMessages.Notice("error", T("Could not add the files: ", "파일을 추가하지 못했습니다: ") + ex.Message));
            }
            finally
            {
                _attaching = false;
                if (ReferenceEquals(_attachCts, cts)) _attachCts = null;
                cts.Dispose();
            }
        }

        private static string NameList(IReadOnlyList<string> names) =>
            names.Count <= MaxNamesInNotice
                ? string.Join(", ", names)
                : string.Join(", ", names.Take(MaxNamesInNotice)) + $", … (+{names.Count - MaxNamesInNotice})";
    }
}
