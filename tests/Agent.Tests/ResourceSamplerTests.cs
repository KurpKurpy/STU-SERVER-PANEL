using System.Diagnostics;
using ServerAgent;
using ServerAgent.Resources;

namespace Agent.Tests;

public sealed class ResourceSamplerTests : IDisposable
{
    private readonly string _dataRoot = TempDir.Create();
    public void Dispose() => TempDir.Delete(_dataRoot);

    [Fact]
    public async Task Sample_reports_process_memory_and_bounded_cpu()
    {
        var sampler = new ResourceSampler(new Areas(new AgentOptions { DataRoot = _dataRoot }, []));
        var id = Guid.NewGuid();
        var running = new Dictionary<Guid, Process> { [id] = Process.GetCurrentProcess() };

        sampler.Sample(running);
        await Task.Delay(500);
        var s = sampler.Sample(running);

        var p = Assert.Single(s.Processes);
        Assert.Equal(id, p.ProfileId);
        Assert.InRange(p.CpuPercent, 0, 100);
        Assert.True(p.MemoryBytes > 0);
        Assert.InRange(s.HostCpuPercent, 0, 100);
        Assert.True(s.NetInBytesPerSec >= 0 && s.NetOutBytesPerSec >= 0);
        var disk = Assert.Single(s.Disks, d => d.Root.Equals(Path.GetPathRoot(_dataRoot), StringComparison.OrdinalIgnoreCase));
        Assert.True(disk.TotalBytes > disk.FreeBytes && disk.FreeBytes > 0);
        Assert.Equal(2, sampler.Recent.Length);
    }
}
