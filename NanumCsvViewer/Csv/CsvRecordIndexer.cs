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

        private State _state = State.FieldStart;
        private bool _awaitingLfAfterCr;

        public CsvRecordIndexer(RecordIndex index, long fileLength, byte delimiter, long firstRecordStart, byte quote = (byte)'"')
        {
            _index = index;
            _fileLength = fileLength;
            _delim = delimiter;
            _quote = quote;
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

            int i = 0;
            while (i < n)
            {
                byte b = buffer[i];

                // 직전 버퍼/바이트가 CR로 레코드를 끝냈을 때, 뒤따르는 LF(=CRLF) 여부 해소.
                if (awaiting)
                {
                    awaiting = false;
                    if (b == LF)
                    {
                        AddStart(baseOffset + i + 1, fileLength);
                        state = State.FieldStart;
                        i++;
                        continue;
                    }
                    // 단독 CR: 새 레코드는 현재 바이트에서 시작 → 등록 후 이 바이트를 재처리.
                    AddStart(baseOffset + i, fileLength);
                    state = State.FieldStart;
                }

                switch (state)
                {
                    case State.FieldStart:
                        if (b == quote) state = State.InQuoted;
                        else if (b == delim) state = State.FieldStart;
                        else if (b == CR) { awaiting = true; i++; continue; }
                        else if (b == LF) { AddStart(baseOffset + i + 1, fileLength); state = State.FieldStart; i++; continue; }
                        else state = State.InUnquoted;
                        break;

                    case State.InUnquoted:
                        if (b == delim) state = State.FieldStart;
                        else if (b == CR) { awaiting = true; i++; continue; }
                        else if (b == LF) { AddStart(baseOffset + i + 1, fileLength); state = State.FieldStart; i++; continue; }
                        // 비인용 필드 내부의 따옴표는 리터럴로 간주(관대 모드).
                        break;

                    case State.InQuoted:
                        if (b == quote) state = State.QuoteInQuoted;
                        // 그 외(CR/LF/구분자 포함)는 모두 데이터.
                        break;

                    case State.QuoteInQuoted:
                        if (b == quote) state = State.InQuoted;            // "" → 이스케이프된 따옴표
                        else if (b == delim) state = State.FieldStart;     // 필드 종료
                        else if (b == CR) { awaiting = true; i++; continue; }
                        else if (b == LF) { AddStart(baseOffset + i + 1, fileLength); state = State.FieldStart; i++; continue; }
                        else state = State.InUnquoted;                     // 관대 모드
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
