using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Panel.Data;
using Panel.Features.Auth;
using Panel.Features.Missions;
using Panel.Features.Mods;
using Panel.Features.Profiles;
using Panel.Hubs;

namespace Panel.Features.Operations;

// Phase 6a Rulings 8–10: each enabled checklist item for one operation, from the database and the agent as they are now.
// Anything the panel can't see right now is "unknown", never a pass; "apply" means Apply to server will set it.
public static class Readiness
{
    public const string Pass = "pass", Fail = "fail", Unknown = "unknown", Apply = "apply";

    private sealed record State(Operation Op, Mission? Mission, MissionVersion? Version, Profile? Profile, ServerInstall? Install, ResolvedMods? Mods,
        bool AgentConnected, Guid[] Ticked);

    public static async Task<CheckResult[]> EvaluateAsync(AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents, Operation op)
    {
        var items = await db.ChecklistItems.Where(i => i.Enabled).OrderBy(i => i.Order).ToListAsync();
        var mission = op.MissionId is { } m ? await db.Missions.FindAsync(m) : null;
        var version = op.MissionVersionId is { } v ? await db.MissionVersions.FindAsync(v) : null;
        var profile = op.ProfileId is { } p ? await db.Profiles.FindAsync(p) : null;
        var install = profile is null ? null : await db.Installs.FindAsync(profile.ServerInstallId);
        var mods = op.ModCollectionId is { } l ? await ModLaunch.ResolveAsync(db, l) : null;
        var ticked = await db.OperationChecks.Where(c => c.OperationId == op.Id && c.Done).Select(c => c.ChecklistItemId).ToArrayAsync();
        var s = new State(op, mission, version, profile, install, mods, install is not null && registry.Hello(install.AgentId) is not null, ticked); // connected and reported in (final review I-1)

        var results = new List<CheckResult>();
        foreach (var item in items)
        {
            (string State, string? Detail, string? Fix) r = item.AutoKey switch
            {
                null => ticked.Contains(item.Id) ? (Pass, "Ticked.", null) : (Fail, "Not ticked yet.", null),
                "mission.uploaded" => Uploaded(s),
                "mission.matches" => await MatchesAsync(db, s),
                "mission.deployed" => await DeployedAsync(db, s),
                "profile.selected" => s.Profile is null ? (Fail, op.ProfileId is null ? "No server profile chosen." : "The server profile was deleted.", null)
                    : (Pass, s.Profile.Name, null),
                "mods.installed" => ModsInstalled(s),
                "mods.updated" => await ModsUpdatedAsync(db, s),
                "keys.deployed" => await KeysAsync(db, agents, s),
                "rotation.set" => await RotationAsync(db, store, s),
                "server.ready" => await ServerAsync(db, registry, s),
                "zeus.assigned" => await ZeusAsync(db, s),
                _ => (Unknown, "This panel version doesn't know how to check this item.", (string?)null),
            };
            results.Add(new CheckResult(item.Id, item.Label, r.State, r.Detail, r.Fix));
        }
        return results.ToArray();
    }

    private static (string, string?, string?) Uploaded(State s) => s switch
    {
        { Mission: null } => (Fail, "No mission chosen.", null),
        { Mission.IsArchived: true } => (Fail, $"{s.Mission!.DisplayName} is archived.", $"/missions/{s.Mission.Id}"),
        { Version: null } => (Fail, "No mission version chosen.", $"/missions/{s.Mission!.Id}"),
        _ => (Pass, $"{s.Mission!.DisplayName} v{s.Version!.Number}", null),
    };

    private static async Task<(string, string?, string?)> MatchesAsync(AppDb db, State s)
    {
        if (s.Mission is null || s.Version is null || s.Install is null || s.Op.ModCollectionId is null)
            return (Unknown, "Needs a mission, a server profile and a mod list.", null);
        var required = await db.MissionVersionAddons.Where(a => a.VersionId == s.Version.Id).Select(a => new RequiredAddon(a.Addon, a.ModName)).ToListAsync();
        var r = Compatibility.Check(required, s.Mission.Map, await Compatibility.ProvidedAsync(db, s.Install.Id, s.Op.ModCollectionId));
        var fix = $"/server/missions?p={s.Profile!.Id}";
        return r.Status switch
        {
            Compatibility.Ready => (Pass, $"Everything {s.Mission.FileName} needs is provided.", null),
            Compatibility.Missing => (Fail, "Missing: " + string.Join(", ", r.Missing.Select(x => x.Addon).Concat(r.MissingMap is { } map ? [$"the map {map}"] : []).Take(8)) + ".", fix),
            _ => (Unknown, "Couldn't fully read: " + string.Join(", ", r.UnreadableMods.Take(5)) + ".", fix),
        };
    }

    private static async Task<(string, string?, string?)> DeployedAsync(AppDb db, State s)
    {
        if (s.Install is null) return (Unknown, "Needs a server profile.", null);
        if (s.Mission is null || s.Version is null) return (Fail, "No mission version chosen.", null);
        var fix = $"/missions/{s.Mission.Id}";
        var on = (await ServerMissionsEndpoints.OnServerAsync(db, s.Install.Id, s.Mission.Id)).FirstOrDefault();
        if (on is null) return (Fail, $"{s.Mission.FileName} isn't on {s.Install.Name} yet.", fix);
        return on.Version.Id == s.Version.Id
            ? (Pass, $"v{s.Version.Number} is on {s.Install.Name}.", null)
            : (Fail, $"{s.Install.Name} has v{on.Version.Number}; this operation uses v{s.Version.Number}. Choose v{on.Version.Number} here, or restore v{s.Version.Number} on the mission page.", fix);
    }

    private static (string, string?, string?) ModsInstalled(State s) => s switch
    {
        { Op.ModCollectionId: null } => (Fail, "No mod list chosen.", null),
        { Mods: null } => (Fail, "The mod list was deleted.", null),
        { Mods.NotInstalled.Length: > 0 } => (Fail, "Not installed: " + string.Join(", ", s.Mods!.NotInstalled.Take(8)) + ".", "/mods"),
        _ => (Pass, $"{s.Mods!.ListName}: every mod is installed.", null),
    };

    // Only as fresh as the last "Check for updates" (Ruling 8): a mod Steam wasn't asked about in the last day can't be called up to date.
    private const int UpdateCheckHours = 24;

    private static async Task<(string, string?, string?)> ModsUpdatedAsync(AppDb db, State s)
    {
        if (s.Op.ModCollectionId is not { } listId || s.Mods is null) return (Unknown, "Needs a mod list.", null);
        var mods = await (from i in db.ModCollectionItems join m in db.Mods on i.ModId equals m.Id
                          where i.CollectionId == listId && i.Enabled && m.WorkshopId != null select m).ToListAsync();
        if (await db.Jobs.AnyAsync(j => j.Type == JobKinds.WorkshopDownload && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.NeedsInput)))
            return (Fail, "A mod download is still running.", "/mods");
        var behind = mods.Where(m => m.LatestWorkshopUpdate is { } latest && (m.WorkshopUpdatedAt is null || latest > m.WorkshopUpdatedAt)).Select(m => m.Name).ToList();
        if (behind.Count > 0) return (Fail, "Updates waiting: " + string.Join(", ", behind.Take(8)) + ".", "/mods");
        var cutoff = DateTime.UtcNow.AddHours(-UpdateCheckHours);
        var stale = mods.Where(m => m.WorkshopCheckedAt is not { } at || at < cutoff).Select(m => m.Name).ToList();
        if (stale.Count > 0)
            return (Unknown, $"Not checked for updates in the last {UpdateCheckHours} hours: " + string.Join(", ", stale.Take(8)) + ". Use Check for updates on the Mods page.", "/mods");
        return (Pass, mods.Count == 0 ? "The list has no Workshop mods." : $"Up to date (checked in the last {UpdateCheckHours} hours).", null);
    }

    // Start deploys missing keys of the list's installed mods, so missing ones are "apply" (Ruling 8).
    private static async Task<(string, string?, string?)> KeysAsync(AppDb db, AgentCalls agents, State s)
    {
        if (s.Install is null || s.Op.ModCollectionId is not { } listId) return (Unknown, "Needs a server profile and a mod list.", null);
        var wanted = (await (from i in db.ModCollectionItems join m in db.Mods on i.ModId equals m.Id
                             where i.CollectionId == listId && i.Enabled && m.Status == ModStatus.Installed select m.KeyFilesJson).ToListAsync())
            .SelectMany(j => JsonSerializer.Deserialize<string[]>(j) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return (Pass, "The list's mods carry no keys.", null);
        if (!s.AgentConnected) return (Unknown, "The server agent isn't connected, so the keys folder can't be read.", null);
        KeyFile[] present;
        try { present = await agents.CallAsync(s.Install.AgentId, c => c.ListKeys(s.Install.InstallPath)); }
        catch (Exception e) when (e is AgentUnavailableException or Microsoft.AspNetCore.SignalR.HubException) { return (Unknown, "The agent couldn't list the keys folder.", null); }
        var missing = wanted.Except(present.Select(k => k.Name), StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count == 0 ? (Pass, $"All {wanted.Count} keys are in the keys folder.", null)
            : (Apply, "Missing: " + string.Join(", ", missing.Take(8)) + ". Apply to server deploys them.", null);
    }

    private static async Task<(string, string?, string?)> RotationAsync(AppDb db, ProfileStore store, State s)
    {
        if (s.Profile is null || s.Mission is null) return (Unknown, "Needs a server profile and a mission.", null);
        if (s.Profile.AppliedRevisionId is not { } revId || await db.ProfileRevisions.FindAsync(revId) is not { } revision)
            return (Apply, "Nothing is applied to the server yet. Apply to server sets it.", null);
        var applied = store.Load(revision).Snapshot;
        var template = s.Mission.FileName[..^4];
        var rotationOk = applied.Rotation is [var only] && only.Template.Equals(template, StringComparison.OrdinalIgnoreCase);
        return rotationOk && applied.ModCollectionId == s.Op.ModCollectionId
            ? (Pass, $"{s.Profile.Name} runs {template} with this mod list.", null)
            : (Apply, $"{s.Profile.Name} is set to {(applied.Rotation.Length == 0 ? "no mission" : string.Join(", ", applied.Rotation.Select(r => r.Template)))}. Apply to server sets it.", null);
    }

    private static async Task<(string, string?, string?)> ServerAsync(AppDb db, AgentRegistry registry, State s)
    {
        if (s.Profile is null || s.Install is null) return (Unknown, "Needs a server profile.", null);
        if (!s.AgentConnected)
            return (Unknown, registry.IsConnected(s.Install.AgentId) ? "The server agent hasn't reported its servers yet. Prepare again in a few seconds."
                : "The server agent isn't connected, so the server's state is unknown.", null);
        var installId = s.Install.Id.ToString();
        if (await db.Jobs.AnyAsync(j => j.Type == JobKinds.ServerInstall && j.TargetId == installId
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.NeedsInput)))
            return (Fail, $"SteamCMD is updating {s.Install.Name}.", "/server/setup");
        // The agent reports every running server on hello (checked above), so no status for it means it isn't running.
        var state = registry.Status(s.Profile.Id)?.State ?? ServerState.Offline;
        return state switch
        {
            ServerState.Offline => (Pass, "Stopped; Apply to server starts it.", null),
            ServerState.Online => (Pass, "Running; Apply to server restarts it.", null),
            ServerState.Error => (Fail, $"In error: {registry.Status(s.Profile.Id)?.Error}", $"/server?p={s.Profile.Id}"),
            _ => (Fail, $"The server is {state.ToString().ToLowerInvariant()}; wait for it.", $"/server?p={s.Profile.Id}"),
        };
    }

    private static async Task<(string, string?, string?)> ZeusAsync(AppDb db, State s)
    {
        var ids = await db.OperationZeus.Where(z => z.OperationId == s.Op.Id).Select(z => z.UserId).ToListAsync();
        var active = (await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync()).Where(u => !AuthEndpoints.IsDisabled(u)).Select(u => u.UserName).ToList();
        return active.Count > 0 ? (Pass, string.Join(", ", active), null) : (Fail, ids.Count > 0 ? "Every Zeus assigned is disabled." : "No Zeus assigned.", null);
    }

    // Prepare: evaluate and store with the time (Ruling 9).
    public static async Task<CheckResult[]> PrepareAsync(AppDb db, ProfileStore store, AgentRegistry registry, AgentCalls agents, Operation op)
    {
        var results = await EvaluateAsync(db, store, registry, agents, op);
        Store(op, results);
        await db.SaveChangesAsync();
        return results;
    }

    // Apply's gate: nothing Not ready or Can't tell ("Apply does this" is fine; an unticked manual item is Not ready).
    public static bool PassesGate(IEnumerable<CheckResult> results) => !results.Any(r => r.State is Fail or Unknown);

    public static void Store(Operation op, CheckResult[] results, DateTime? at = null)
    {
        (op.LastCheckJson, op.LastCheckAt) = (JsonSerializer.Serialize(results, OperationEndpoints.Json), at ?? DateTime.UtcNow);
        (op.ReadyCount, op.CheckCount) = (results.Count(r => r.State == Pass), results.Length);
    }
}
