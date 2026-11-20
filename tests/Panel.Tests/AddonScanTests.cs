using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class AddonScanTests
{
    private sealed record Setup(HttpClient Owner, Guid AgentId, FakeAgent Agent, Guid InstallId, Guid ModId);

    // An install, plus mod 111 in the library, installed.
    private static async Task<Setup> SetUpAsync(PanelFactory f)
    {
        var (owner, agentId, agent, installId) = await AgentKit.InstallAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        await ModTests.PasteAsync(owner, "111");
        var modId = (await ModTests.ModAsync(owner, 111)).GetProperty("id").GetGuid();
        await WithDb(f, db => db.Mods.Single(m => m.Id == modId).Status = ModStatus.Installed);
        return new Setup(owner, agentId, agent, installId, modId);
    }

    private static async Task WithDb(PanelFactory f, Action<AppDb> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        change(db);
        await db.SaveChangesAsync();
    }

    private static T Read<T>(PanelFactory f, Func<AppDb, T> query)
    {
        using var scope = f.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    private static Task<JsonElement> InstallDtoAsync(HttpClient c, Guid id) =>
        c.GetFromJsonAsync<JsonElement[]>("/api/installs").ContinueWith(t => t.Result!.Single(i => i.GetProperty("id").GetGuid() == id));

    private static AddonScan ModScan(long id, string[] addons, string[]? unreadable = null, string? error = null) =>
        new(new ModRef(id, null), null, error is null ? [new ScannedFolder("", addons, [], unreadable ?? [])] : [], error);

    private static AddonScan InstallScan(params ScannedFolder[] folders) => new(null, AgentKit.InstallPath, folders, null);

    [Fact]
    public async Task A_scan_report_stores_addons_and_worlds()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;

        await s.Agent.SendAddonsScannedAsync(ModScan(111, ["cba_main", "cba_xeh"]));
        await s.Agent.SendAddonsScannedAsync(InstallScan(new ScannedFolder("", ["A3_Core"], ["Altis", "VR"], []), new ScannedFolder("expansion", ["A3_Exp"], ["Tanoa"], [])));

        var mod = await ModTests.ModAsync(s.Owner, 111);
        Assert.Equal("Scanned", mod.GetProperty("scanStatus").GetString());
        Assert.NotEqual(JsonValueKind.Null, mod.GetProperty("scannedAt").ValueKind);
        Assert.Equal(["cba_main", "cba_xeh"], Read(f, db => db.ModAddons.Where(a => a.ModId == s.ModId && a.Kind == AddonKind.Addon).Select(a => a.Name).OrderBy(n => n).ToList()));
        Assert.Equal("Scanned", (await InstallDtoAsync(s.Owner, s.InstallId)).GetProperty("scanStatus").GetString());
        var rows = Read(f, db => db.InstallAddons.Where(a => a.ServerInstallId == s.InstallId).Select(a => $"{a.Folder}|{a.Kind}|{a.Name}").ToList());
        string[] expected = ["|Addon|A3_Core", "|World|Altis", "|World|VR", "expansion|Addon|A3_Exp", "expansion|World|Tanoa"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), rows.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_new_scan_replaces_the_rows()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        await s.Agent.SendAddonsScannedAsync(ModScan(111, ["cba_main", "cba_old"]));

        await s.Agent.SendAddonsScannedAsync(ModScan(111, ["cba_main"]));

        Assert.Equal(["cba_main"], Read(f, db => db.ModAddons.Where(a => a.ModId == s.ModId).Select(a => a.Name).ToList()));
    }

    [Fact]
    public async Task Unreadable_files_make_the_scan_partial_and_errors_failed()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;

        await s.Agent.SendAddonsScannedAsync(ModScan(111, ["cba_main"], ["x.pbo: text config: macro class names"]));
        var mod = await ModTests.ModAsync(s.Owner, 111);
        Assert.Equal("Partial", mod.GetProperty("scanStatus").GetString());
        Assert.Contains("x.pbo", mod.GetProperty("scanNote").GetString());

        await s.Agent.SendAddonsScannedAsync(ModScan(111, [], error: "Workshop item 111 isn't installed on this server."));
        mod = await ModTests.ModAsync(s.Owner, 111);
        Assert.Equal("Failed", mod.GetProperty("scanStatus").GetString());
        Assert.Contains("isn't installed", mod.GetProperty("scanNote").GetString());
    }

    [Fact]
    public async Task A_successful_mod_job_rescans_the_mod()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        await s.Agent.SendAddonsScannedAsync(ModScan(111, ["cba_main"]));

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { s.ModId }, mode = "reinstall" })).StatusCode);
        var job = s.Agent.LastJob();
        await s.Agent.SendJobFinishedAsync(new JobResult(job.JobId, true, null, [new JobItemResult(111, true, null, 1234, [])]));

        await AgentKit.WaitUntilAsync(() => s.Agent.Scans().Any(r => r.Mods.Contains(new ModRef(111, null))), 10);
    }

    [Fact]
    public async Task A_server_install_job_rescans_the_install()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        await s.Agent.SendAddonsScannedAsync(InstallScan(new ScannedFolder("", ["A3_Core"], [], [])));

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/steamcmd", new { branch = "public" })).StatusCode);
        await s.Agent.SendJobFinishedAsync(new JobResult(s.Agent.LastJob().JobId, true, null, []));

        await AgentKit.WaitUntilAsync(() => s.Agent.Scans().Any(r => r.InstallRoots.Contains(AgentKit.InstallPath)), 10);
    }

    [Fact]
    public async Task Hello_rescans_what_was_never_scanned()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;

        await s.Agent.HelloAsync(installs: [new InstallInfo(AgentKit.InstallPath, AgentKit.InstallArea, true)]);

        await AgentKit.WaitUntilAsync(() => s.Agent.Scans().Any(r => r.InstallRoots.Contains(AgentKit.InstallPath) && r.Mods.Contains(new ModRef(111, null))), 10);
    }

    [Fact]
    public async Task Rescan_needs_mods_manage_or_server_update()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var member = await TestUsers.LoginAsync(f, "Member");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/mods/{s.ModId}/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/installs/{s.InstallId}/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync($"/api/mods/{s.ModId}/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync($"/api/installs/{s.InstallId}/scan", null)).StatusCode);
        Assert.Contains(s.Agent.Scans(), r => r.Mods.Contains(new ModRef(111, null)));
        Assert.Contains(s.Agent.Scans(), r => r.InstallRoots.Contains(AgentKit.InstallPath));
    }

    [Fact]
    public async Task A_report_for_an_unknown_mod_or_another_agents_install_is_ignored()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var (_, _, otherKey) = await AgentKit.PairAsync(f, s.Owner);
        await using var other = await FakeAgent.ConnectAsync(f, otherKey);

        await other.SendAddonsScannedAsync(InstallScan(new ScannedFolder("", ["evil_addon"], [], [])));
        await s.Agent.SendAddonsScannedAsync(ModScan(999, ["ghost"]));

        Assert.False(Read(f, db => db.InstallAddons.Any()));
        Assert.False(Read(f, db => db.ModAddons.Any()));
        Assert.Equal("NotScanned", (await InstallDtoAsync(s.Owner, s.InstallId)).GetProperty("scanStatus").GetString());
    }
}
