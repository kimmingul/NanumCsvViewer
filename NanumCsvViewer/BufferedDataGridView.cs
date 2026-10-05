namespace NanumCsvViewer
{
    /// <summary>
    /// 이중 버퍼링을 켠 DataGridView. 기본 DataGridView는 화면에 바로 그려 스크롤 한 단계(휠 3행)에 약 20 ms가 들어
    /// 60 fps 한 프레임(16.7 ms)을 넘겨 끊겨 보였다. 이중 버퍼링이면 같은 단계가 약 4 ms(30열·30만 행 측정)다.
    /// </summary>
    internal sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView() => DoubleBuffered = true;
    }
}
