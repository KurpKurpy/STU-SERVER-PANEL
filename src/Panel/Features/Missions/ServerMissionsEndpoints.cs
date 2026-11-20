using Microsoft.EntityFrameworkCore;
using Panel.Api;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Profiles;

namespace Panel.Features.Missions;

public sealed record ListRef(Guid Id, string Name);
public sealed record ServerMissionRow(Guid MissionId, string DisplayName, string FileName, int Version, string Status, string? MissingMap,
    MissingAddon[] Missing, string[] UnreadableMods);
public sealed record ServerMissionsDto(Guid? ModListId, Guid? AppliedModListId, ListRef[] ModLists, ServerMissionRow[] Missions, string[] NotInstalled);
public sealed record ListCompat(Guid Id, string Name, string Status, string? MissingMap, MissingAddon[] Missing, string[] UnreadableMods);
public sealed record InstallCompat(Guid InstallId, string InstallName, int Version, ListCompat[] Lists);

// Phase 5b: the Server Missions tab (which of a server's missions a mod list can run) and the mission page's mod-list compatibility.
public static class ServerMissionsEndpoints
{
    public static void MapServerMissions(this WebApplication app)
    {
        app.MapGet("/api/servers/{id:guid}/missions", List).RequirePermission(Permissions.MissionsView);
        app.MapGet("/api/missions/{id:guid}/compatibility", ForMission).RequirePermission(Permissions.MissionsView);
    }

    // The version of each library mission last written (Done) to the install (Ruling 9).
    internal sealed record OnServer(Mission Mission, MissionVersion Version, RequiredAddon[] Required);

    internal static async Task<List<OnServer>> OnServerAsync(AppDb db, Guid installId, Guid? missionId = null)
    {
        var done = await db.Deployments.Where(d => d.ServerInstallId == installId && d.Status == DeploymentStatus.Done && (missionId == null || d.MissionId == missionId))
            .Select(d => new { d.MissionId, d.MissionVersionId, d.DeployedAt }).ToListAsync();
        var latest = done.GroupBy(d => d.MissionId).Select(g => g.OrderByDescending(d => d.DeployedAt).First()).ToList();
        var missionIds = latest.Select(d => d.MissionId).ToList();
        var versionIds = latest.Select(d => d.MissionVersionId).ToList();
        var missions = await db.Missions.Where(m => missionIds.Contains(m.Id) && !m.IsArchived).ToDictionaryAsync(m => m.Id);
        var versions = await db.MissionVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        var addons = (await db.MissionVersionAddons.Where(a => versionIds.Contains(a.VersionId)).ToListAsync()).ToLookup(a => a.VersionId);
        return latest.Where(d => missions.ContainsKey(d.MissionId) && versions.ContainsKey(d.MissionVersionId))
            .Select(d => new OnServer(missions[d.MissionId], versions[d.MissionVersionId],
                addons[d.MissionVersionId].Select(a => new RequiredAddon(a.Addon, a.ModName)).ToArray()))
            .OrderBy(o => o.Mission.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The mod list the applied revision loads (what the server runs), else the head's.
    internal static async Task<Guid?> AppliedListAsync(AppDb db, ProfileStore store, Profile p) =>
        p.AppliedRevisionId is { } revId && await db.ProfileRevisions.FindAsync(revId) is { } applied ? store.Load(applied).Snapshot.ModCollectionId : p.ModCollectionId;

    private static async Task<IResult> List(Guid id, Guid? modListId, AppDb db, ProfileStore store)
    {
        if (await db.Profiles.FindAsync(id) is not { } p) return ApiError.Of(404, "not_found", "No such server.");
        var lists = await db.ModCollections.OrderBy(c => c.Name).Select(c => new ListRef(c.Id, c.Name)).ToArrayAsync();
        if (modListId is { } wanted && lists.All(l => l.Id != wanted)) return ApiError.Field("modListId", "No such mod list.");
        var applied = await AppliedListAsync(db, store, p);
        var listId = modListId ?? applied;
        var provided = await Compatibility.ProvidedAsync(db, p.ServerInstallId, listId);
        var rows = (await OnServerAsync(db, p.ServerInstallId)).Select(o =>
        {
            var r = Compatibility.Check(o.Required, o.Mission.Map, provided);
            return new ServerMissionRow(o.Mission.Id, o.Mission.DisplayName, o.Mission.FileName, o.Version.Number, r.Status, r.MissingMap, r.Missing, r.UnreadableMods);
        }).ToArray();
        return Results.Ok(new ServerMissionsDto(listId, applied, lists, rows, [.. provided.NotInstalled ?? []]));
    }

    private static async Task<IResult> ForMission(Guid id, AppDb db)
    {
        if (await db.Missions.FindAsync(id) is null) return ApiError.Of(404, "not_found", "No such mission.");
        var lists = await db.ModCollections.OrderBy(c => c.Name).ToListAsync();
        var installIds = await db.Deployments.Where(d => d.MissionId == id && d.Status == DeploymentStatus.Done).Select(d => d.ServerInstallId).Distinct().ToListAsync();
        var result = new List<InstallCompat>();
        foreach (var install in await db.Installs.Where(i => installIds.Contains(i.Id)).OrderBy(i => i.CreatedAt).ToListAsync())
        {
            if ((await OnServerAsync(db, install.Id, id)).FirstOrDefault() is not { } o) continue;
            var rows = new List<ListCompat>();
            foreach (var list in lists)
            {
                var r = Compatibility.Check(o.Required, o.Mission.Map, await Compatibility.ProvidedAsync(db, install.Id, list.Id));
                rows.Add(new ListCompat(list.Id, list.Name, r.Status, r.MissingMap, r.Missing, r.UnreadableMods));
            }
            result.Add(new InstallCompat(install.Id, install.Name, o.Version.Number, rows.ToArray()));
        }
        return Results.Ok(result);
    }
}
