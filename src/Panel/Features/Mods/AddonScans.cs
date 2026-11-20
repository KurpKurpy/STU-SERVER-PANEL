using Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Panel.Audit;
using Panel.Data;
using Panel.Hubs;

namespace Panel.Features.Mods;

// Phase 5b (Rulings 5–6): asks the agent which addons and maps its mods and installs provide, and stores the answers. A scan
// replaces that target's rows; unreadable files make it Partial, an error Failed. Never called from inside an agent's own hub
// invocation (that would deadlock): the hub runs RescanAsync after Hello and JobFinished return.
public sealed class AddonScans(AppDb db, AgentCalls agents, ILogger<AddonScans> log)
{
    public static ModRef RefOf(Mod m) => new(m.WorkshopId, m.WorkshopId is null ? m.LocalFolder : null);

    // Null when the agent took the request; else why not.
    public async Task<string?> RequestAsync(Guid agentId, IReadOnlyCollection<Mod> mods, IReadOnlyCollection<ServerInstall> installs)
    {
        if (mods.Count + installs.Count == 0) return null;
        try
        {
            var r = await agents.CallAsync(agentId, c => c.ScanAddons(new AddonScanRequest(mods.Select(RefOf).ToArray(), installs.Select(i => i.InstallPath).ToArray())));
            return r.Ok ? null : r.Error ?? "The agent refused the scan.";
        }
        catch (AgentUnavailableException) { return "The server agent isn't connected."; }
        catch (HubException e) { return e.Message; }
    }

    // Everything of this agent that needs a scan: its installs, and the library's installed mods when it's the mod agent.
    public async Task RescanAsync(Guid agentId)
    {
        var installs = await db.Installs.Where(i => i.AgentId == agentId && i.ScanStatus == ScanStatus.NotScanned).ToListAsync();
        var mods = await ModLibrary.AgentAsync(db) == agentId
            ? await db.Mods.Where(m => m.Status == ModStatus.Installed && m.ScanStatus == ScanStatus.NotScanned).ToListAsync()
            : [];
        if (await RequestAsync(agentId, mods, installs) is { } problem) log.LogWarning("Couldn't ask agent {AgentId} for addon scans: {Problem}", agentId, problem);
    }

    public async Task ApplyAsync(Guid agentId, AddonScan scan)
    {
        var (status, note) = scan.Error is { } error ? (ScanStatus.Failed, Clip(error))
            : scan.Folders.Any(f => f.Unreadable.Length > 0)
                ? (ScanStatus.Partial, Clip(string.Join("\n", scan.Folders.SelectMany(f => f.Unreadable.Select(u => f.Folder.Length == 0 ? u : $"{f.Folder}/{u}")))))
                : (ScanStatus.Scanned, null);
        if (scan.Mod is { } r)
        {
            if (await ModLibrary.AgentAsync(db) != agentId) return; // mods live with the mod agent
            var mod = r.WorkshopId is { } id ? await db.Mods.FirstOrDefaultAsync(m => m.WorkshopId == id)
                : await db.Mods.FirstOrDefaultAsync(m => m.WorkshopId == null && m.LocalFolder == r.LocalFolder);
            if (mod is null) return;
            db.ModAddons.RemoveRange(await db.ModAddons.Where(a => a.ModId == mod.Id).ToListAsync());
            db.ModAddons.AddRange(Rows(scan.Folders).Select(x => new ModAddon { ModId = mod.Id, Kind = x.Kind, Name = x.Name })
                .DistinctBy(a => (a.Kind, a.Name.ToUpperInvariant())));
            (mod.ScanStatus, mod.ScannedAt, mod.ScanNote) = (status, DateTime.UtcNow, note);
        }
        else if (scan.InstallRoot is { } root)
        {
            var install = (await db.Installs.Where(i => i.AgentId == agentId).ToListAsync())
                .FirstOrDefault(i => i.InstallPath.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (install is null) return;
            db.InstallAddons.RemoveRange(await db.InstallAddons.Where(a => a.ServerInstallId == install.Id).ToListAsync());
            db.InstallAddons.AddRange(scan.Folders.SelectMany(f => Rows([f]).Select(x => new InstallAddon
            {
                ServerInstallId = install.Id, Folder = Clip(f.Folder, 64), Kind = x.Kind, Name = x.Name,
            })).DistinctBy(a => (a.Folder.ToUpperInvariant(), a.Kind, a.Name.ToUpperInvariant())));
            (install.ScanStatus, install.ScannedAt, install.ScanNote) = (status, DateTime.UtcNow, note);
        }
        else return;
        await db.SaveChangesAsync();
    }

    private static IEnumerable<(AddonKind Kind, string Name)> Rows(IEnumerable<ScannedFolder> folders) =>
        folders.SelectMany(f => f.Addons.Select(a => (AddonKind.Addon, a)).Concat(f.Worlds.Select(w => (AddonKind.World, w))))
            .Where(x => x.Item2.Length is > 0 and <= 200);

    private static string Clip(string s, int max = 2000) => s.Length > max ? s[..max] : s;
}

// A finished server install or update changes the install's addons; the hub's rescan after JobFinished picks it up.
public sealed class InstallScanHandler : Panel.Features.Jobs.IJobResultHandler
{
    public bool Handles(string jobType) => jobType == JobKinds.ServerInstall;

    public async Task HandleAsync(Job job, JobResult result, AppDb db)
    {
        if (!result.Ok || !Guid.TryParse(job.TargetId, out var installId) || await db.Installs.FindAsync(installId) is not { } install) return;
        install.ScanStatus = ScanStatus.NotScanned;
        await db.SaveChangesAsync();
    }
}
