using System.Runtime.InteropServices;

namespace ServerAgent.Resources;

// Whole-machine CPU from kernel32 GetSystemTimes deltas (kernel time includes idle time).
public sealed class HostCpu
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _idle, _total;

    public HostCpu() => Read(out _idle, out _total);

    public double Sample()
    {
        Read(out var idle, out var total);
        var busy = (total - _total) - (idle - _idle);
        var span = total - _total;
        (_idle, _total) = (idle, total);
        return span <= 0 ? 0 : Math.Clamp(busy * 100.0 / span, 0, 100);
    }

    private static void Read(out long idle, out long total)
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out idle, out var kernel, out var user)) { idle = 0; total = 0; return; }
        total = kernel + user;
    }
}
