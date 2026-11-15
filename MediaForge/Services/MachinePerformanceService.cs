using MediaForge.Models;
using System.Runtime.InteropServices;

namespace MediaForge.Services;

public sealed class MachinePerformanceService : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _gpu;
    private long _idle, _kernel, _user;
    private bool _hasCpuSample, _disposed;

    public MachinePerformance Sample()
    {
        lock (_gate)
        {
            var memory = ReadMemory();
            if (_disposed) return memory;
            double? cpu = null;
            if (GetSystemTimes(out long idle, out long kernel, out long user))
            {
                long total = kernel - _kernel + user - _user;
                if (_hasCpuSample && total > 0)
                    cpu = Math.Clamp(100d * (total - (idle - _idle)) / total, 0, 100);
                _idle = idle; _kernel = kernel; _user = user; _hasCpuSample = true;
            }
            var (encode, decode) = ReadGpu();
            return memory with { Cpu = cpu, GpuEncode = encode, GpuDecode = decode };
        }
    }

    public static MachinePerformance ReadMemory()
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref state)) return new(null, 0, 0, 0, null, null);
        const double gib = 1024d * 1024 * 1024;
        return new(null, state.Load, (state.TotalPhysical - state.AvailablePhysical) / gib,
            state.TotalPhysical / gib, null, null);
    }

    private (double?, double?) ReadGpu()
    {
        if (_query == IntPtr.Zero)
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return (null, null);
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu) != 0)
            {
                PdhCloseQuery(_query); _query = IntPtr.Zero;
                return (null, null);
            }
            PdhCollectQueryData(_query);
            return (null, null);
        }
        if (PdhCollectQueryData(_query) != 0) return (null, null);
        uint bytes = 0, count = 0;
        const uint formatDouble = 0x200;
        uint status = PdhGetFormattedCounterArrayW(_gpu, formatDouble, ref bytes, ref count, IntPtr.Zero);
        if (status != 0x800007D2 || bytes == 0 || bytes > 32 * 1024 * 1024) return (null, null);
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (PdhGetFormattedCounterArrayW(_gpu, formatDouble, ref bytes, ref count, buffer) != 0)
                return (null, null);
            int size = Marshal.SizeOf<CounterItem>();
            if ((ulong)count * (uint)size > bytes) return (null, null);
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, i * size));
                if (item.Value.Status is not (0 or 1) || !double.IsFinite(item.Value.Value)) continue;
                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                int luid = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                if (luid < 0) continue;
                string key = name[luid..];
                engines[key] = engines.GetValueOrDefault(key) + Math.Max(0, item.Value.Value);
            }
            double? Peak(string type)
            {
                var values = engines.Where(e => e.Key.Contains(type, StringComparison.OrdinalIgnoreCase))
                    .Select(e => Math.Clamp(e.Value, 0, 100)).ToArray();
                return values.Length == 0 ? null : values.Max();
            }
            return (Peak("engtype_VideoEncode"), Peak("engtype_VideoDecode"));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes,
        ref uint count, IntPtr items);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
