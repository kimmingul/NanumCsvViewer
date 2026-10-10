using System.Runtime.InteropServices;

namespace NanumCsvViewer.Csv;

/// <summary>
/// 메모리 안에서 계산하는 모든 분석(기본 분석·차트·그룹별/중복/카이제곱·피벗·품질 참조 집합·고급 통계)이 함께 쓰는 예산 공급자.
/// 자동 = 이 PC 물리 메모리의 50%(최소 512 MB, 상한 없음). 수동 = 설정의 GB 값(물리 메모리를 넘지 못함).
/// 남길 메모리 = 읽을 때의 가용 물리 메모리에서 지정한 GB를 뺀 값(하한 없음). 남길 메모리가 켜져 있으면 자동·수동보다 우선하며,
/// 남는 양이 0이면(또는 가용 메모리를 읽지 못하면) <see cref="AnalysisMemoryLimitException"/>으로 안전하게 멈춘다.
/// 프로세스 전체의 하드 보장이나 동시 분석 간 예약은 아니다.
/// DuckDB 작업 공간의 메모리 상한은 별개이며 여기와 무관하다.
/// </summary>
internal static class AnalysisMemoryBudget
{
    public const long MinimumBytes = 512L * 1024 * 1024;
    public const double BytesPerGb = 1024.0 * 1024 * 1024;
    /// <summary>수동 상한의 최솟값(GB). 자동 하한(512 MB)과 같다.</summary>
    public const double MinimumManualGb = 0.5;
    /// <summary>.NET 배열 하나의 최대 요소 수. 예산이 아주 커져도 이보다 큰 행렬은 만들 수 없으므로 예산 초과로 취급한다.</summary>
    public const long MaxArrayElements = 0x7FFFFFC7;

    private sealed record Config(bool Auto, double ManualGb, bool Reserve, double ReserveGb);

    private static volatile Config s_config = new(true, 0, false, 0);
    private static readonly AsyncLocal<long?> s_override = new();

    /// <summary>설정을 반영한다(앱 시작·설정 저장 때). manualGb가 0 이하이면 수동이라도 자동으로 계산한다. reserve가 켜져 있으면 auto·manualGb보다 우선한다.</summary>
    public static void Configure(bool auto, double manualGb, bool reserve, double reserveGb)
        => s_config = new Config(auto, double.IsFinite(manualGb) ? manualGb : 0, reserve, double.IsFinite(reserveGb) ? reserveGb : 0);

    /// <summary>이 PC의 물리 메모리 총량(바이트). 읽지 못하면 .NET이 보는 가용 총량.</summary>
    public static long PhysicalBytes => SystemMemory.TotalPhysicalBytes;

    /// <summary>지금 사용할 수 있는 물리 메모리(바이트). 읽지 못하면 0.</summary>
    public static long AvailableBytes => SystemMemory.AvailablePhysicalBytes;

    /// <summary>자동 예산: 물리 메모리의 50%, 최소 512 MB, 상한 없음.</summary>
    public static long ForPhysicalMemory(long physicalBytes)
        => Math.Max(MinimumBytes, physicalBytes / 2);

    /// <summary>수동 예산: 지정한 GB(최소 512 MB)를 물리 메모리 총량으로 제한한다.</summary>
    public static long ForManual(double gb, long physicalBytes)
    {
        double bytes = Math.Max(gb * BytesPerGb, MinimumBytes);
        return bytes >= physicalBytes ? physicalBytes : (long)bytes;
    }

    /// <summary>남길 메모리 예산: 가용 메모리에서 reserveGb를 뺀 값. 하한 없음, 가용 메모리 이상을 남기면 0.</summary>
    public static long ForReserve(double reserveGb, long availableBytes)
    {
        if (availableBytes <= 0) return 0;
        if (!(reserveGb > 0)) return availableBytes;
        double reserved = reserveGb * BytesPerGb;
        return reserved >= availableBytes ? 0 : availableBytes - (long)reserved;
    }

    public static long Resolve(bool auto, double manualGb, long physicalBytes)
        => auto || !(manualGb > 0) ? ForPhysicalMemory(physicalBytes) : ForManual(manualGb, physicalBytes);

    /// <summary>설정에 따른 현재 예산(바이트). 남길 메모리 정책에서 쓸 양이 없으면 <see cref="AnalysisMemoryLimitException"/>.</summary>
    public static long Current
    {
        get
        {
            if (s_override.Value is long fixedBytes) return fixedBytes;
            var c = s_config;
            if (c.Reserve)
            {
                long room = ForReserve(c.ReserveGb, AvailableBytes);
                return room > 0 ? room : throw new AnalysisMemoryLimitException();
            }
            return Resolve(c.Auto, c.ManualGb, PhysicalBytes);
        }
    }

    /// <summary>테스트가 예산을 고정하는 이음매(현재 비동기 흐름에만 적용). 앱은 쓰지 않는다.</summary>
    internal static IDisposable Override(long bytes)
    {
        var previous = s_override.Value;
        s_override.Value = bytes;
        return new Restore(previous);
    }

    private sealed class Restore(long? previous) : IDisposable
    {
        public void Dispose() => s_override.Value = previous;
    }
}

/// <summary>Win32 GlobalMemoryStatusEx로 읽은 물리 메모리. 실패하면 GC가 보는 가용 총량으로 대신한다.</summary>
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool TryRead(out MemoryStatusEx status)
    {
        status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        try { return GlobalMemoryStatusEx(ref status); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public static long TotalPhysicalBytes
    {
        get
        {
            if (TryRead(out var s) && s.ullTotalPhys > 0) return (long)Math.Min(s.ullTotalPhys, long.MaxValue);
            long gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return gc > 0 ? gc : 8L << 30;
        }
    }

    public static long AvailablePhysicalBytes
        => TryRead(out var s) ? (long)Math.Min(s.ullAvailPhys, long.MaxValue) : 0;
}
