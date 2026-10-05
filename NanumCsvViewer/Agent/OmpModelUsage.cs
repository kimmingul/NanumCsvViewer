using Microsoft.Data.Sqlite;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    /// <summary>
    /// omp가 직접 적어 두는 "모델 마지막 사용 시각"(agent.db의 model_usage 표)을 읽기 전용으로 읽는다. omp 내부 형식이라 언제든 바뀔 수 있으므로
    /// 표·열·값 형식을 모두 확인하고, 하나라도 다르면 <see cref="Status.SchemaChanged"/>로 돌려준다(호출자는 전체 목록으로 되돌아간다).
    /// omp 18.4.4 확인: 표 model_usage(model_key TEXT PRIMARY KEY, last_used_at INTEGER = 유닉스 초). 키는 "provider/id".
    /// RPC set_model이 이 표를 즉시 갱신한다(응답 전에 기록됨). 모델을 바꾸지 않고 기본 모델로만 보낸 프롬프트는 기록하지 않는다.
    /// 이 클래스는 UI 스레드 밖에서 부른다: 연결 열기·재시도 대기(최대 약 0.5초)가 블로킹이다.
    /// </summary>
    internal static class OmpModelUsage
    {
        public enum Status
        {
            /// <summary>읽었다(항목이 없을 수도 있다).</summary>
            Ok,
            /// <summary>agent.db가 없다(omp를 아직 안 썼거나 다른 폴더).</summary>
            NoDatabase,
            /// <summary>다른 프로세스가 잠가서 재시도 뒤에도 못 읽었다.</summary>
            Busy,
            /// <summary>표·열·값 형식이 알던 것과 다르다(omp가 바뀜).</summary>
            SchemaChanged,
            /// <summary>그 밖의 읽기 오류(깨진 파일, 권한 …).</summary>
            Error,
        }

        /// <summary>한 모델의 마지막 사용 시각(UTC).</summary>
        public readonly record struct Use(string Key, DateTime LastUsedUtc);

        public sealed record Result(Status Status, IReadOnlyList<Use> Items, string Detail)
        {
            public static Result Fail(Status status, string detail) => new(status, Array.Empty<Use>(), detail);
            public bool IsOk => Status == Status.Ok;
        }

        public const string DatabaseFileName = "agent.db";
        public const string TableName = "model_usage";
        public const string KeyColumn = "model_key";
        public const string TimeColumn = "last_used_at";

        /// <summary>채팅 선택기의 "최근 사용"에 보이는 개수.</summary>
        public const int MaxRecent = 8;

        /// <summary>유닉스 초로 보아 말이 되는 범위: 2000-01-01 ~ 2100-01-01. 밀리초(1.7e12)로 바뀌면 이 범위를 벗어난다.</summary>
        internal const long MinSeconds = 946_684_800, MaxSeconds = 4_102_444_800;

        private const int MaxRows = 2000;
        private const int BusyTimeoutMs = 250;

        public static string DatabasePath(string agentDir) => Path.Combine(agentDir, DatabaseFileName);

        /// <summary>
        /// agentDir\agent.db를 읽는다. 잠겨 있으면 <paramref name="retryDelayMs"/> 뒤에 한 번 다시 시도하고, 그래도 안 되면 Busy.
        /// 읽기 전용 연결(Mode=ReadOnly)만 쓰며, 아무것도 쓰지 않고 WAL 체크포인트도 하지 않는다(omp가 실행 중이어도 안전).
        /// </summary>
        public static Result Read(string agentDir, int retryDelayMs = 200)
        {
            if (string.IsNullOrWhiteSpace(agentDir)) return Result.Fail(Status.NoDatabase, "agent directory unknown");
            string path = DatabasePath(agentDir);
            Result result = ReadOnce(path);
            if (result.Status != Status.Busy) return result;
            Thread.Sleep(Math.Max(0, retryDelayMs));
            return ReadOnce(path);
        }

        private static Result ReadOnce(string path)
        {
            if (!File.Exists(path)) return Result.Fail(Status.NoDatabase, "agent.db not found");
            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 1,
                };
                using var conn = new SqliteConnection(csb.ConnectionString);
                conn.Open();
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout = " + BusyTimeoutMs;
                    pragma.ExecuteNonQuery();
                }

                string? schemaProblem = CheckSchema(conn);
                if (schemaProblem != null) return Result.Fail(Status.SchemaChanged, schemaProblem);
                return ReadRows(conn);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)     // SQLITE_BUSY, SQLITE_LOCKED
            {
                return Result.Fail(Status.Busy, ex.Message);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 14 && !File.Exists(path))   // 열기 직전에 지워짐
            {
                return Result.Fail(Status.NoDatabase, "agent.db not found");
            }
            catch (Exception ex)
            {
                return Result.Fail(Status.Error, ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>표와 두 열이 있고 선언 형식이 TEXT·INTEGER 계열인지. 문제가 있으면 설명, 없으면 null.</summary>
        private static string? CheckSchema(SqliteConnection conn)
        {
            string? keyType = null, timeType = null;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({TableName})";
            using (var reader = cmd.ExecuteReader())
            {
                bool any = false;
                while (reader.Read())
                {
                    any = true;
                    string name = reader.GetString(1), type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    if (string.Equals(name, KeyColumn, StringComparison.OrdinalIgnoreCase)) keyType = type;
                    else if (string.Equals(name, TimeColumn, StringComparison.OrdinalIgnoreCase)) timeType = type;
                }
                if (!any) return $"table {TableName} not found";
            }
            if (keyType is null) return $"column {KeyColumn} not found";
            if (timeType is null) return $"column {TimeColumn} not found";
            if (!HasTextAffinity(keyType)) return $"column {KeyColumn} is declared {Shown(keyType)}, expected TEXT";
            if (!timeType.Contains("INT", StringComparison.OrdinalIgnoreCase)) return $"column {TimeColumn} is declared {Shown(timeType)}, expected INTEGER";
            return null;
        }

        private static bool HasTextAffinity(string declared) =>
            declared.Contains("TEXT", StringComparison.OrdinalIgnoreCase)
            || declared.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
            || declared.Contains("CLOB", StringComparison.OrdinalIgnoreCase);

        private static string Shown(string declared) => declared.Length == 0 ? "(no type)" : declared;

        private static Result ReadRows(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {KeyColumn}, {TimeColumn}, typeof({KeyColumn}), typeof({TimeColumn}) FROM {TableName} ORDER BY {TimeColumn} DESC LIMIT {MaxRows}";
            var items = new List<Use>();
            int rows = 0;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows++;
                string keyKind = reader.GetString(2), timeKind = reader.GetString(3);
                if (keyKind != "text") return Result.Fail(Status.SchemaChanged, $"{KeyColumn} holds {keyKind}, expected text");
                if (timeKind != "integer") return Result.Fail(Status.SchemaChanged, $"{TimeColumn} holds {timeKind}, expected integer");
                long seconds = reader.GetInt64(1);
                if (seconds < MinSeconds || seconds > MaxSeconds)
                    return Result.Fail(Status.SchemaChanged, $"{TimeColumn} value {seconds} is not a Unix time in seconds");
                string key = reader.GetString(0);
                if (!IsModelKey(key)) continue;   // provider/id 모양이 아닌 행은 건너뛴다(전부 그렇다면 아래에서 형식 변경으로 본다)
                items.Add(new Use(key, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime));
            }
            if (rows > 0 && items.Count == 0) return Result.Fail(Status.SchemaChanged, $"no {KeyColumn} has the provider/id form");
            return new Result(Status.Ok, items, "");
        }

        private static bool IsModelKey(string key)
        {
            int slash = key.IndexOf('/');
            return slash > 0 && slash < key.Length - 1;
        }

        /// <summary>
        /// 지금 고를 수 있는 모델(provider/id)만 남기고 최근 순으로 최대 <paramref name="max"/>개. 같은 시각이면 키 순.
        /// </summary>
        public static List<string> Recent(IEnumerable<Use> items, IEnumerable<string> available, int max = MaxRecent)
        {
            var have = new HashSet<string>(available, StringComparer.Ordinal);
            return items.Where(u => have.Contains(u.Key))
                .OrderByDescending(u => u.LastUsedUtc)
                .ThenBy(u => u.Key, StringComparer.Ordinal)
                .Select(u => u.Key)
                .Distinct(StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }
    }

    /// <summary>
    /// omp가 쓰는 에이전트 폴더(agent.db가 있는 곳). omp 18.4.4 문서(config-usage.md): 이름 있는 프로필(--profile, OMP_PROFILE, 옛 PI_PROFILE)은
    /// ~/.omp/profiles/&lt;이름&gt;/agent, 기본 프로필은 PI_CODING_AGENT_DIR(있으면) 아니면 ~/.omp/agent. PI_CONFIG_DIR는 ~ 아래 루트 이름(.omp)을 바꾼다.
    /// 앱은 omp를 띄울 때 환경을 그대로 물려주므로 같은 환경에서 같은 폴더를 찾는다. 사용자가 "omp 추가 인자"에 --profile을 넣은 경우도 따른다.
    /// </summary>
    internal static class OmpAgentDir
    {
        /// <summary>진단·테스트용: 설정하면 최근 사용 기록만 이 폴더에서 읽는다(omp 자신은 영향 없음).</summary>
        public const string OverrideVariable = "NANUMCSV_OMP_AGENT_DIR";

        public static string? Resolve(string? extraArgs, Func<string, string?> env, string home)
        {
            string? over = Clean(env(OverrideVariable));
            if (over != null) return Expand(over, home);

            string configRoot = Clean(env("PI_CONFIG_DIR")) ?? ".omp";
            string? profile = ProfileFromArgs(extraArgs);
            if (profile == null)
            {
                // OMP_PROFILE이 정의돼 있으면(빈 값이라도) PI_PROFILE보다 우선한다.
                string? omp = env("OMP_PROFILE");
                profile = omp ?? env("PI_PROFILE");
            }
            profile = profile?.Trim();
            if (!string.IsNullOrEmpty(profile) && !string.Equals(profile, "default", StringComparison.OrdinalIgnoreCase))
            {
                if (profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile is "." or "..") return null;
                return Path.Combine(home, configRoot, "profiles", profile, "agent");
            }
            string? agent = Clean(env("PI_CODING_AGENT_DIR"));
            return agent != null ? Expand(agent, home) : Path.Combine(home, configRoot, "agent");
        }

        public static string? Resolve(string? extraArgs) =>
            Resolve(extraArgs, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string Expand(string path, string home)
        {
            if (path == "~") return home;
            if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(home, path[2..]);
            return path;
        }

        /// <summary>omp 추가 인자에서 마지막 --profile 값(--profile x 또는 --profile=x). 없으면 null.</summary>
        internal static string? ProfileFromArgs(string? extraArgs)
        {
            var tokens = OmpLaunch.SplitArguments(extraArgs);
            string? found = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == "--profile" && i + 1 < tokens.Count) found = tokens[++i];
                else if (tokens[i].StartsWith("--profile=", StringComparison.Ordinal)) found = tokens[i]["--profile=".Length..];
            }
            return found;
        }
    }
}
