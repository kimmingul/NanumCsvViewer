using System.Buffers;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 바이트 단위 CSV 레코드 경계 스캐너(상태기계). 따옴표로 감싼 필드 안의 줄바꿈/구분자를
    /// 무시하고, 인용 밖의 CR/LF에서만 레코드를 분리합니다. CRLF·단독 CR·단독 LF를 모두 처리하며,
    /// 버퍼 경계를 가로질러 상태가 유지됩니다(여러 번 ProcessBuffer 호출 가능).
    ///
    /// EUC-KR/CP949/UTF-8에서 구조 바이트(0x0A/0x0D/구분자/따옴표)는 멀티바이트 문자 중간에
    /// 절대 나타나지 않으므로, 이 스캔은 인코딩과 무관하게 안전합니다.
    /// </summary>
    public sealed class CsvRecordIndexer
    {
        private const byte CR = 0x0D;
        private const byte LF = 0x0A;

        private enum State : byte { FieldStart, InUnquoted, InQuoted, QuoteInQuoted }

        private readonly RecordIndex _index;
        private readonly long _fileLength;
        private readonly byte _delim;
        private readonly byte _quote;
        // 인용 밖에서 의미 있는 바이트 집합(구분자/따옴표/CR/LF). 그 외 일반 문자는 벡터화로 건너뜀.
        private readonly SearchValues<byte> _structural;

        private State _state = State.FieldStart;
        private bool _awaitingLfAfterCr;

        public CsvRecordIndexer(RecordIndex index, long fileLength, byte delimiter, long firstRecordStart, byte quote = (byte)'"')
        {
            _index = index;
            _fileLength = fileLength;
            _delim = delimiter;
            _quote = quote;
            _structural = SearchValues.Create(new[] { quote, delimiter, CR, LF });
            // 첫 레코드(헤더) 시작 = BOM 다음 위치.
            if (fileLength > firstRecordStart) _index.Add(firstRecordStart);
        }

        /// <param name="buffer">파일에서 순차적으로 읽은 바이트 청크.</param>
        /// <param name="baseOffset">buffer[0]의 파일 내 절대 오프셋.</param>
        public void ProcessBuffer(ReadOnlySpan<byte> buffer, long baseOffset)
        {
            int n = buffer.Length;
            State state = _state;
            bool awaiting = _awaitingLfAfterCr;
            byte delim = _delim, quote = _quote;
            long fileLength = _fileLength;
            var structural = _structural;

            int i = 0;
            while (i < n)
            {
                // 직전이 CR로 레코드를 끝냈을 때, 뒤따르는 LF(=CRLF) 여부 해소.
                if (awaiting)
                {
                    awaiting = false;
                    if (buffer[i] == LF)
                    {
                        AddStart(baseOffset + i + 1, fileLength);
                        state = State.FieldStart;
                        i++;
                        continue;
                    }
                    // 단독 CR: 새 레코드는 현재 바이트에서 시작 → 등록 후 이 바이트를 아래에서 재처리.
                    AddStart(baseOffset + i, fileLength);
                    state = State.FieldStart;
                }

                // 인용 안에서는 다음 따옴표까지의 모든 데이터(쉼표·줄바꿈 포함)를 한 번에 건너뜀.
                if (state == State.InQuoted)
                {
                    int q = buffer.Slice(i).IndexOf(quote);
                    if (q < 0) break;            // 버퍼 끝까지 인용 데이터
                    i += q;                       // 따옴표 위치
                    state = State.QuoteInQuoted;
                    i++;
                    continue;
                }

                // FieldStart / InUnquoted / QuoteInQuoted: 다음 구조 바이트로 벡터화 점프.
                int rel = buffer.Slice(i).IndexOfAny(structural);
                if (rel < 0)
                {
                    // 남은 건 전부 일반 문자 → 필드 진입 상태만 갱신하고 종료.
                    if (state == State.FieldStart || state == State.QuoteInQuoted) state = State.InUnquoted;
                    break;
                }
                if (rel > 0)
                {
                    // 건너뛴 일반 문자들로 인해 필드 시작/닫는따옴표 뒤 상태는 InUnquoted가 됨.
                    if (state == State.FieldStart || state == State.QuoteInQuoted) state = State.InUnquoted;
                    i += rel;
                }

                byte c = buffer[i];
                switch (state)
                {
                    case State.FieldStart:
                        if (c == quote) state = State.InQuoted;
                        else if (c == delim) state = State.FieldStart;
                        else if (c == CR) { awaiting = true; i++; continue; }
                        else { /* LF */ AddStart(baseOffset + i + 1, fileLength); state = State.FieldStart; i++; continue; }
                        break;

                    case State.InUnquoted:
                        if (c == delim) state = State.FieldStart;
                        else if (c == CR) { awaiting = true; i++; continue; }
                        else if (c == LF) { AddStart(baseOffset + i + 1, fileLength); state = State.FieldStart; i++; continue; }
                        // 비인용 필드 내부의 따옴표는 리터럴(관대 모드) → 상태 유지.
                        break;

                    case State.QuoteInQuoted:
                        if (c == quote) state = State.InQuoted;            // "" → 이스케이프된 따옴표
                        else if (c == delim) state = State.FieldStart;     // 필드 종료
                        else if (c == CR) { awaiting = true; i++; continue; }
                        else { /* LF */ AddStart(baseOffset + i + 1, fileLength); state = State.FieldStart; i++; continue; }
                        break;
                }
                i++;
            }

            _state = state;
            _awaitingLfAfterCr = awaiting;
        }

        private void AddStart(long offset, long fileLength)
        {
            // 파일 끝(== 길이)에 닿는 위치는 다음 레코드가 없으므로 등록하지 않음(말미 줄바꿈 유령행 방지).
            if (offset < fileLength) _index.Add(offset);
        }
    }
}
