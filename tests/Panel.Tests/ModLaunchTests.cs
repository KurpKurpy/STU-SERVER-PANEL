using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class ModLaunchTests
{
    internal sealed record Setup(HttpClient Owner, FakeAgent Agent, Guid InstallId, Guid ProfileId, Dictionary<long, Guid> Mods);

    // Five library mods (111…555), marked installed unless listed in notInstalled, plus a profile on the install.
    internal static async Task<Setup> SetUpAsync(PanelFactory f, params long[] notInstalled)
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        await ModTests.PasteAsync(owner, "111\n222\n333\n444\n555");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            foreach (var m in await db.Mods.ToListAsync())
                if (!notInstalled.Contains(m.WorkshopId!.Value)) (m.Status, m.InstalledAt) = (ModStatus.Installed, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        var mods = (await owner.GetFromJsonAsync<JsonElement[]>("/api/mods"))!.ToDictionary(m => m.GetProperty("workshopId").GetInt64(), m => m.GetProperty("id").GetGuid());
        var profile = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId), applied: false);
        return new Setup(owner, agent, installId, profile, mods);
    }

    // 111 client, 222 server only, 333 client + headless, 444 optional, 555 disabled.
    internal static async Task<Guid> ListAsync(Setup s, string name = "Main")
    {
        var r = await s.Owner.PostAsJsonAsync("/api/collections", new
        {
            name,
            items = new object[]
            {
                new { modId = s.Mods[111], client = true, enabled = true },
                new { modId = s.Mods[222], serverOnly = true, enabled = true },
                new { modId = s.Mods[333], client = true, headless = true, enabled = true },
                new { modId = s.Mods[444], optional = true, enabled = true },
                new { modId = s.Mods[555], client = true, enabled = false },
            },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task UseListAsync(Setup s, Guid listId)
    {
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = listId.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(s.Owner, s.ProfileId)).StatusCode);
    }

    [Fact]
    public async Task A_profile_saves_its_mod_list_in_the_revision()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var list = await ListAsync(s);

        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = list.ToString(), note: "Mods")).StatusCode);

        var detail = await s.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{s.ProfileId}");
        Assert.Equal(list, detail.GetProperty("snapshot").GetProperty("modCollectionId").GetGuid());
        var bad = await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = Guid.NewGuid().ToString());
        Assert.Equal("modCollectionId", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Start_sends_client_server_and_headless_mods_and_deploys_keys_first()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        await UseListAsync(s, await ListAsync(s));

        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync($"/api/servers/{s.ProfileId}/start", null)).StatusCode);

        var start = (LaunchSpec)s.Agent.Calls.Last(c => c.Method == nameof(IAgentClient.Start)).Args[0]!;
        Assert.Equal([111L, 333L], start.Mods!.Select(m => m.WorkshopId!.Value));
        Assert.Equal([222L], start.ServerMods!.Select(m => m.WorkshopId!.Value));
        Assert.Equal([333L], start.HeadlessMods!.Select(m => m.WorkshopId!.Value));
        var methods = s.Agent.Calls.Select(c => c.Method).ToList();
        Assert.True(methods.LastIndexOf(nameof(IAgentClient.DeployKeys)) < methods.LastIndexOf(nameof(IAgentClient.Start)));
        var deploy = s.Agent.Calls.Last(c => c.Method == nameof(IAgentClient.DeployKeys));
        Assert.Equal(AgentKit.InstallPath, deploy.Args[0]);
        // Optional mods aren't loaded, but players who use them need their keys.
        Assert.Equal([111L, 222L, 333L, 444L], ((ModRef[])deploy.Args[1]!).Select(m => m.WorkshopId!.Value));
    }

    [Fact]
    public async Task Start_refuses_when_a_listed_mod_is_not_installed()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f, notInstalled: 333);
        await using var lease = s.Agent;
        await UseListAsync(s, await ListAsync(s));

        var r = await s.Owner.PostAsync($"/api/servers/{s.ProfileId}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mods_missing", body.GetProperty("code").GetString());
        Assert.Contains("Mod 333", body.GetProperty("message").GetString());
        Assert.DoesNotContain(s.Agent.Calls, c => c.Method == nameof(IAgentClient.Start));
    }

    [Fact]
    public async Task Failed_key_deploy_stops_the_start()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        await UseListAsync(s, await ListAsync(s));
        s.Agent.OnDeployKeys = (_, _) => new KeysResult(false, "Couldn't copy the keys: access denied", null, [], []);

        var r = await s.Owner.PostAsync($"/api/servers/{s.ProfileId}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.DoesNotContain(s.Agent.Calls, c => c.Method == nameof(IAgentClient.Start));
    }

    [Fact]
    public async Task Preview_shows_the_mod_arguments_with_the_agents_paths()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        const string workshop = @"D:\Server Data\workshop\steamapps\workshop\content\107410";
        await s.Agent.HelloAsync(installs: [new InstallInfo(AgentKit.InstallPath, AgentKit.InstallArea, true)], areas:
        [
            new AreaInfo("configs", AgentKit.DataRoot + @"\configs", true, true, false),
            new AreaInfo("profiles", AgentKit.DataRoot + @"\profiles", true, true, false),
            new AreaInfo("workshop", workshop, true, false, true),
        ]);
        var list = await ListAsync(s);
        var snapshot = System.Text.Json.Nodes.JsonNode.Parse((await s.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{s.ProfileId}")).GetProperty("snapshot").GetRawText())!;
        snapshot["modCollectionId"] = list.ToString();

        var preview = await (await s.Owner.PostAsJsonAsync($"/api/profiles/{s.ProfileId}/preview", new { snapshot })).Content.ReadFromJsonAsync<JsonElement>();

        var server = preview.GetProperty("command").GetProperty("server").GetString();
        Assert.Contains($"\"-mod={workshop}\\111;{workshop}\\333\"", server);
        Assert.Contains($"\"-serverMod={workshop}\\222\"", server);
    }

    [Fact]
    public async Task Start_waits_while_steamcmd_is_installing_the_server()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(s.Owner, s.ProfileId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/steamcmd", new { branch = "public" })).StatusCode);

        var r = await s.Owner.PostAsync($"/api/servers/{s.ProfileId}/start", null);

        // Arma would start on half-written files, and SteamCMD can't replace files a running server holds open.
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("installing", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.DoesNotContain(s.Agent.Calls, c => c.Method == nameof(IAgentClient.Start));

        var jobId = s.Agent.LastJob().JobId;
        await s.Agent.SendJobFinishedAsync(new JobResult(jobId, true, null, []));
        await AgentKit.WaitUntilAsync(() => s.Owner.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}").GetAwaiter().GetResult().GetProperty("status").GetString() == "Succeeded", 10);
        Assert.Equal(HttpStatusCode.Accepted, (await s.Owner.PostAsync($"/api/servers/{s.ProfileId}/start", null)).StatusCode);
    }

    [Fact]
    public async Task Mods_a_running_server_loads_wait_until_it_stops()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        await UseListAsync(s, await ListAsync(s));
        await s.Agent.SendStatusAsync(new ProfileStatus(s.ProfileId, ServerState.Online, 4242, DateTime.UtcNow, null, null, null));

        var loaded = await s.Owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { s.Mods[111] }, mode = "update" });

        // SteamCMD would replace files under the live server, or fail on the PBOs it holds open.
        Assert.Equal(HttpStatusCode.Conflict, loaded.StatusCode);
        Assert.Equal("running", (await loaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.DoesNotContain(s.Agent.Calls, c => c.Method == nameof(IAgentClient.RunJob));
        // Mods the running server doesn't load (disabled 555, optional 444) still download.
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { s.Mods[555], s.Mods[444] }, mode = "reinstall" })).StatusCode);
    }

    [Fact]
    public async Task Mod_downloads_and_file_removals_are_audited()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { s.Mods[111] }, mode = "reinstall" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Owner.DeleteAsync($"/api/mods/{s.Mods[222]}?files=true")).StatusCode);

        using var scope = f.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToListAsync();
        Assert.Contains("mods.install", actions);
        Assert.Contains("mods.filesRemoved", actions);
    }

    [Fact]
    public async Task Server_install_runs_a_steamcmd_job_for_server_update_holders()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var modManager = await TestUsers.LoginAsync(f, "S3"); // lacks server.update

        Assert.Equal(HttpStatusCode.Forbidden, (await modManager.PostAsJsonAsync($"/api/installs/{s.InstallId}/steamcmd", new { branch = "public" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/steamcmd", new { branch = "weird" })).StatusCode);
        var r = await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/steamcmd", new { branch = "creatordlc" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var spec = s.Agent.LastJob();
        Assert.Equal((JobKind.ServerInstall, AgentKit.InstallPath, "creatordlc", "crow_1"), (spec.Kind, spec.InstallRoot, spec.Branch, spec.SteamUser));
    }
}
