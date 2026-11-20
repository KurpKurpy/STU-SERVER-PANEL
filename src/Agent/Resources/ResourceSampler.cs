using System.Diagnostics;
using System.Net.NetworkInformation;
using Contracts;

namespace ServerAgent.Resources;

// Per-server CPU and memory, host CPU, host network (Windows has no cheap per-process figure, so the UI says "host"),
// and free disk on the drives the agent uses. Keeps the last 15 minutes (450 samples at 2 s) for the panel's charts.
public sealed class ResourceSampler
{
    private const int Keep = 450;
    private readonly string[] _driveRoots;
    private readonly HostCpu _hostCpu = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _lastCpu = [];
    private readonly Queue<ResourceSample> _recent = new();
    private readonly Lock _lock = new();
    private (long In, long Out) _lastNet;
    private DateTime _lastNetAt;

    public ResourceSampler(Areas areas)
    {
        _driveRoots = areas.Describe().Select(a => Path.GetPathRoot(a.Path)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _lastNet = NetTotals();
        _lastNetAt = DateTime.UtcNow;
    }

    public ResourceSample[] Recent
    {
        get { lock (_lock) return _recent.ToArray(); }
    }

    public ResourceSample Sample(IReadOnlyDictionary<Guid, Process> running)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var processes = running.Select(kv => SampleProcess(kv.Key, kv.Value, now)).OfType<ProcessSample>().ToArray();

            var net = NetTotals();
            var seconds = Math.Max(0.001, (now - _lastNetAt).TotalSeconds);
            var netIn = (long)(Math.Max(0, net.In - _lastNet.In) / seconds);
            var netOut = (long)(Math.Max(0, net.Out - _lastNet.Out) / seconds);
            (_lastNet, _lastNetAt) = (net, now);

            var sample = new ResourceSample(now, _hostCpu.Sample(), netIn, netOut, Disks(), processes);
            _recent.Enqueue(sample);
            while (_recent.Count > Keep) _recent.Dequeue();
            return sample;
        }
    }

    private ProcessSample? SampleProcess(Guid profileId, Process p, DateTime now)
    {
        try
        {
            p.Refresh();
            var cpu = p.TotalProcessorTime;
            // First sight: average since the process started, so the first figure is real rather than 0.
            var (lastCpu, lastAt) = _lastCpu.TryGetValue(p.Id, out var last) ? last : (TimeSpan.Zero, p.StartTime.ToUniversalTime());
            _lastCpu[p.Id] = (cpu, now);
            var wall = (now - lastAt).TotalMilliseconds * Environment.ProcessorCount;
            var percent = wall <= 0 ? 0 : Math.Clamp((cpu - lastCpu).TotalMilliseconds / wall * 100, 0, 100);
            return new ProcessSample(profileId, Math.Round(percent, 1), p.WorkingSet64);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private DiskSample[] Disks() => _driveRoots.Select(root =>
    {
        try
        {
            var d = new DriveInfo(root);
            return new DiskSample(root, d.AvailableFreeSpace, d.TotalSize);
        }
        catch (IOException) { return null; }
    }).OfType<DiskSample>().ToArray();

    private static (long In, long Out) NetTotals()
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var s = nic.GetIPStatistics();
            rx += s.BytesReceived;
            tx += s.BytesSent;
        }
        return (rx, tx);
    }
}
