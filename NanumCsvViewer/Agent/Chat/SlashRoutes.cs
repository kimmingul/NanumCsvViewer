namespace NanumCsvViewer.Agent.Chat
{
    internal enum SlashKind
    {
        /// <summary>슬래시 명령이 아닌 일반 입력.</summary>
        Prompt,
        /// <summary>omp가 처리하는 슬래시 명령: 원문 그대로 prompt로 보낸다.</summary>
        PassThrough,
        NewSession, Abort, ListModels, SetModel, Fast, Thinking,
        Clear, Restart, Copy, Settings, Queue, Version, Login,
        /// <summary>omp 터미널 UI에서만 동작하는 명령.</summary>
        TerminalOnly,
    }

    internal readonly record struct SlashRoute(SlashKind Kind, string Name, string Arg1, string Arg2, string Args)
    {
        public static readonly SlashRoute Plain = new(SlashKind.Prompt, "", "", "", "");
    }

    /// <summary>
    /// 슬래시 명령 중 앱이 직접 처리하는 것. omp는 RPC로 알려 준 명령(get_available_commands)만 실행하고, 터미널 전용 명령은
    /// 그대로 보내면 모델에게 일반 글로 가 버리므로 RPC 기능이나 앱 창으로 옮기거나 "터미널에서만"이라고 알려 준다.
    /// </summary>
    internal static class SlashRoutes
    {
        private static readonly (string Name, SlashKind Kind, string Hint)[] Local =
        {
            ("new", SlashKind.NewSession, ""),
            ("abort", SlashKind.Abort, ""),
            ("clear", SlashKind.Clear, ""),
            ("restart", SlashKind.Restart, ""),
            ("copy", SlashKind.Copy, ""),
            ("settings", SlashKind.Settings, ""),
            ("queue", SlashKind.Queue, "<message>"),
            ("version", SlashKind.Version, ""),
            ("login", SlashKind.Login, "[provider]"),
        };

        private static readonly string[] TerminalOnlyNames =
        {
            "delete", "resume", "tree", "branch", "rewind", "fork", "extensions", "status", "agents", "plan", "hotkeys",
            "hub", "exit", "quit", "q",
            // omp 18.4.4 터미널 전용 내장 명령(RPC 대응 없음)
            "goal", "guided-goal", "loop", "vibe", "tan", "omfg", "cleanse", "plan-review", "collab", "join", "leave",
            "pause", "live", "record", "git", "debug", "setup", "skills", "logout", "open",
            // RPC 목록에는 있지만 RPC에서는 아무것도 열지 않는 명령
            "annotate",
        };

        /// <summary>/ 메뉴에 omp 목록과 합칠 앱 자체 명령.</summary>
        public static IEnumerable<SlashCommandInfo> LocalCommands(bool korean)
        {
            foreach (var (name, kind, hint) in Local)
                yield return new SlashCommandInfo(name, Describe(kind, korean), hint);
        }

        private static string Describe(SlashKind kind, bool ko) => kind switch
        {
            SlashKind.NewSession => ko ? "새 대화 시작" : "Start a new conversation",
            SlashKind.Abort => ko ? "진행 중인 작업 중지" : "Stop the running turn",
            SlashKind.Clear => ko ? "대화를 비우고 새로 시작" : "Clear and start over",
            SlashKind.Restart => ko ? "에이전트(omp) 다시 시작" : "Restart the agent (omp)",
            SlashKind.Copy => ko ? "마지막 답변 복사" : "Copy the last answer",
            SlashKind.Settings => ko ? "에이전트 설정 열기" : "Open the agent settings",
            SlashKind.Queue => ko ? "작업이 끝난 뒤 보낼 메시지 예약" : "Queue a message for after the turn",
            SlashKind.Version => ko ? "버전 정보" : "Show versions",
            SlashKind.Login => ko ? "모델 제공자에 로그인" : "Log in to a model provider",
            _ => "",
        };

        /// <param name="rpcNames">omp가 RPC로 알려 준 명령 이름(앞 '/' 없음).</param>
        public static SlashRoute Route(string text, IReadOnlyCollection<string> rpcNames)
        {
            string body = (text ?? "").Trim();
            if (!body.StartsWith('/')) return SlashRoute.Plain;

            int space = body.IndexOf(' ');
            string name = (space < 0 ? body[1..] : body[1..space]).ToLowerInvariant();
            string args = space < 0 ? "" : body[(space + 1)..].Trim();
            if (name.Length == 0) return SlashRoute.Plain;

            // RPC 기능으로 옮겨 처리하는 명령(omp가 목록에 갖고 있어도 앱이 확인창/선택창을 띄운다)
            switch (name)
            {
                case "new": return new SlashRoute(SlashKind.NewSession, name, "", "", args);
                case "abort": return new SlashRoute(SlashKind.Abort, name, "", "", args);
                case "model":
                    if (args.Length == 0) return new SlashRoute(SlashKind.ListModels, name, "", "", args);
                    {
                        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2) return new SlashRoute(SlashKind.SetModel, name, parts[0], parts[1], args);
                        if (parts.Length == 1)
                        {
                            int slash = parts[0].IndexOf('/');
                            if (slash > 0 && slash < parts[0].Length - 1)
                                return new SlashRoute(SlashKind.SetModel, name, parts[0][..slash], parts[0][(slash + 1)..], args);
                        }
                    }
                    return new SlashRoute(SlashKind.PassThrough, name, "", "", args);
                case "fast":
                    if (args.Length == 0 || args.Equals("on", StringComparison.OrdinalIgnoreCase) || args.Equals("off", StringComparison.OrdinalIgnoreCase))
                        return new SlashRoute(SlashKind.Fast, name, args.ToLowerInvariant(), "", args);
                    return new SlashRoute(SlashKind.PassThrough, name, "", "", args);
                case "thinking":
                case "effort":
                    if (args.Length == 0 || args.IndexOf(' ') < 0)
                        return new SlashRoute(SlashKind.Thinking, name, args, "", args);
                    return new SlashRoute(SlashKind.PassThrough, name, "", "", args);
            }

            if (rpcNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) && name != "annotate")
                return new SlashRoute(SlashKind.PassThrough, name, "", "", args);

            foreach (var (localName, kind, _) in Local)
                if (localName == name) return new SlashRoute(kind, name, "", "", args);

            if (Array.IndexOf(TerminalOnlyNames, name) >= 0)
                return new SlashRoute(SlashKind.TerminalOnly, name, "", "", args);

            return new SlashRoute(SlashKind.PassThrough, name, "", "", args);
        }
    }
}
