using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class ModTests
{
    internal static async Task<JsonElement> PasteAsync(HttpClient c, string text)
    {
        var r = await c.PostAsJsonAsync("/api/mods", new { text });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    internal static async Task<JsonElement> ModAsync(HttpClient c, long workshopId) =>
        (await c.GetFromJsonAsync<JsonElement[]>("/api/mods"))!.Single(m => m.GetProperty("workshopId").ValueKind == JsonValueKind.Number
            && m.GetProperty("workshopId").GetInt64() == workshopId);

    private static async Task<(HttpClient Owner, FakeAgent Agent)> SetUpAsync(PanelFactory f)
    {
        var (owner, _, agent, _) = await AgentKit.InstallAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        return (owner, agent);
    }

    [Fact]
    public async Task Paste_accepts_ids_and_urls_and_refuses_other_games()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        f.Workshop.Set(111, "CBA_A3", size: 5000);
        f.Workshop.Set(222, "Someone's GMod thing", app: 4000);
        f.Workshop.Set(333, "", ok: false);

        var r = await PasteAsync(owner, "111\nhttps://steamcommunity.com/sharedfiles/filedetails/?id=222\n333");

        var added = r.GetProperty("added").EnumerateArray().ToArray();
        Assert.Equal(("CBA_A3", "NotInstalled", 5000), (added[0].GetProperty("name").GetString(), added[0].GetProperty("status").GetString(), added[0].GetProperty("expectedBytes").GetInt64()));
        Assert.Equal("Workshop item 333", added[1].GetProperty("name").GetString()); // unlisted: Steam's public lookup can't see it, SteamCMD can
        Assert.Equal([222L], r.GetProperty("refused").EnumerateArray().Select(x => x.GetProperty("workshopId").GetInt64()));
        Assert.Single((await PasteAsync(owner, "111")).GetProperty("existing").EnumerateArray());
    }

    // The unit's unlisted mods (2026-11-21): added, named from their own files once downloaded, and fetched by Update all, since Steam's
    // public lookup can't say when they change.
    [Fact]
    public async Task An_unlisted_mod_is_named_from_its_files_and_Update_all_fetches_it()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        f.Workshop.Set(999, "", ok: false);
        await PasteAsync(owner, "999");
        var mod = await ModAsync(owner, 999);
        Assert.True(mod.GetProperty("notPublic").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { mod.GetProperty("id").GetGuid() }, mode = "install" })).StatusCode);
        await agent.SendJobFinishedAsync(new JobResult(agent.LastJob().JobId, true, null, [new JobItemResult(999, true, null, 10, [], "13th STU Aux")]));
        await AgentKit.WaitUntilAsync(() => ModAsync(owner, 999).GetAwaiter().GetResult().GetProperty("status").GetString() == "Installed", 10);
        Assert.Equal("13th STU Aux", (await ModAsync(owner, 999)).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "update" })).StatusCode);
        Assert.Equal(999, Assert.Single(agent.LastJob().Items).Id);
    }

    [Fact]
    public async Task Install_creates_a_download_job_and_marks_mods_installed_on_success()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        var mod = await ModAsync(owner, 111);

        var r = await owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { mod.GetProperty("id").GetGuid() }, mode = "install" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var spec = agent.LastJob();
        Assert.Equal((JobKind.WorkshopDownload, "crow_1"), (spec.Kind, spec.SteamUser));
        Assert.Equal(new WorkshopItem(111, 1111), Assert.Single(spec.Items));
        Assert.Equal("Queued", (await ModAsync(owner, 111)).GetProperty("status").GetString());

        await agent.SendJobFinishedAsync(new JobResult(spec.JobId, true, null, [new JobItemResult(111, true, null, 1234, ["cba_a3.bikey"])]));
        await AgentKit.WaitUntilAsync(() => ModAsync(owner, 111).GetAwaiter().GetResult().GetProperty("status").GetString() == "Installed", 10);
        var installed = await ModAsync(owner, 111);
        Assert.Equal(1234, installed.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(["cba_a3.bikey"], installed.GetProperty("keyFiles").EnumerateArray().Select(k => k.GetString()));
        Assert.False(installed.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task A_failed_item_marks_its_mod_failed_with_the_reason()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });

        await agent.SendJobFinishedAsync(new JobResult(agent.LastJob().JobId, false, "1 of 1 items failed.", [new JobItemResult(111, false, "Timeout", 0, [])]));

        await AgentKit.WaitUntilAsync(() => ModAsync(owner, 111).GetAwaiter().GetResult().GetProperty("status").GetString() == "Failed", 10);
        Assert.Equal("Timeout", (await ModAsync(owner, 111)).GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task An_agent_restart_mid_download_frees_its_mods()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });
        var first = agent.LastJob().JobId;

        await agent.HelloAsync(jobs: []); // the agent came back without the job

        await AgentKit.WaitUntilAsync(() => ModAsync(owner, 111).GetAwaiter().GetResult().GetProperty("status").GetString() == "Failed", 10);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" })).StatusCode);
        Assert.NotEqual(first, agent.LastJob().JobId);
    }

    [Fact]
    public async Task A_panel_restart_mid_download_frees_its_mods()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });

        using (var scope = f.Services.CreateScope())
            await Panel.Features.Jobs.JobService.InterruptOpenJobsAsync(scope.ServiceProvider.GetRequiredService<AppDb>(),
                scope.ServiceProvider.GetServices<Panel.Features.Jobs.IJobResultHandler>()); // what Program runs at startup

        Assert.Equal("Failed", (await ModAsync(owner, 111)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Install_needs_a_steam_account()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");

        var r = await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("steam_user", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Update_available_when_the_workshop_item_is_newer()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" });
        await agent.SendJobFinishedAsync(new JobResult(agent.LastJob().JobId, true, null, [new JobItemResult(111, true, null, 1, [])]));
        await AgentKit.WaitUntilAsync(() => ModAsync(owner, 111).GetAwaiter().GetResult().GetProperty("status").GetString() == "Installed", 10);
        agent.OnListMods = () => [new DiskMod(new ModRef(111, null), "Mod 111", 1, DateTime.UtcNow, [])];

        f.Workshop.Set(111, "Mod 111", FakeWorkshop.Updated.AddDays(3));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/mods/refresh", null)).StatusCode);

        Assert.True((await ModAsync(owner, 111)).GetProperty("updateAvailable").GetBoolean());
        await owner.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "update" });
        Assert.Equal([111L], agent.LastJob().Items.Select(i => i.Id));
    }

    // Phase 6a final review I-2: readiness needs to know when Steam was last asked, not just what it said.
    [Fact]
    public async Task Adding_a_mod_and_checking_for_updates_record_when_Steam_was_asked()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        DateTime? Checked()
        {
            using var scope = f.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDb>().Mods.Single(x => x.WorkshopId == 111).WorkshopCheckedAt;
        }
        Assert.True(Checked() > DateTime.UtcNow.AddMinutes(-1));

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Mods.Single(x => x.WorkshopId == 111).WorkshopCheckedAt = DateTime.UtcNow.AddDays(-5);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/mods/refresh", null)).StatusCode);
        Assert.True(Checked() > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Refresh_syncs_the_library_with_the_agents_disk()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111\n222");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Mods.Single(m => m.WorkshopId == 222).Status = ModStatus.Installed; // the panel thinks so, but the folder is gone
            await db.SaveChangesAsync();
        }
        agent.OnListMods = () =>
        [
            new DiskMod(new ModRef(111, null), "CBA_A3", 500, DateTime.UtcNow, ["cba.bikey"]),
            new DiskMod(new ModRef(null, "@unit"), "Unit Mod", 700, DateTime.UtcNow, ["unit.bikey"]),
        ];

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/mods/refresh", null)).StatusCode);

        var mods = (await owner.GetFromJsonAsync<JsonElement[]>("/api/mods"))!;
        Assert.Equal("Installed", (await ModAsync(owner, 111)).GetProperty("status").GetString());
        Assert.Equal("Missing", (await ModAsync(owner, 222)).GetProperty("status").GetString());
        var local = Assert.Single(mods, m => m.GetProperty("localFolder").GetString() == "@unit");
        Assert.Equal(("Unit Mod", "Installed"), (local.GetProperty("name").GetString(), local.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Removing_files_runs_a_remove_job_then_drops_the_mod()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        var id = (await ModAsync(owner, 111)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/mods/{id}?files=true")).StatusCode);
        var spec = agent.LastJob();
        Assert.Equal(JobKind.WorkshopRemove, spec.Kind);
        await agent.SendJobFinishedAsync(new JobResult(spec.JobId, true, null, [new JobItemResult(111, true, null, 0, [])]));

        await AgentKit.WaitUntilAsync(() => !owner.GetStringAsync("/api/mods").GetAwaiter().GetResult().Contains("\"workshopId\":111"), 10);
    }

    [Fact]
    public async Task A_mod_in_a_list_cannot_be_removed()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        await PasteAsync(owner, "111");
        var id = (await ModAsync(owner, 111)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/collections",
            new { name = "Main", items = new[] { new { modId = id, client = true, enabled = true } } })).StatusCode);

        var r = await owner.DeleteAsync($"/api/mods/{id}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Main", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Paste_is_capped()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;

        var r = await owner.PostAsJsonAsync("/api/mods", new { text = new string('1', 600_000) });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Viewers_cannot_change_anything()
    {
        using var f = new PanelFactory();
        var (owner, agent) = await SetUpAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync("/api/mods")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync("/api/mods", new { text = "111" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync("/api/mods/install", new { all = true, mode = "install" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync("/api/collections", new { name = "x" })).StatusCode);
    }
}
