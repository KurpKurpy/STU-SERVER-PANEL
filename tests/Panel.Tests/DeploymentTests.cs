using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using static Panel.Tests.MissionKit;

namespace Panel.Tests;

public class DeploymentTests
{
    private const string ReadOnlyPath = @"E:\Game\Arma 3";

    private sealed record Setup(HttpClient Owner, Guid AgentId, string Key, FakeAgent Agent, Guid InstallId);

    private static async Task<Setup> SetUpAsync(PanelFactory f, bool withReadOnly = false)
    {
        var (owner, agentId, key) = await AgentKit.PairAsync(f);
        var agent = await FakeAgent.ConnectAsync(f, key);
        await agent.HelloAsync(installs: Installs(withReadOnly));
        var r = await owner.PostAsJsonAsync("/api/installs", new { agentId, path = AgentKit.InstallPath, name = "Arma 3" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        if (withReadOnly)
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/installs", new { agentId, path = ReadOnlyPath, name = "Game folder" })).StatusCode);
        return new Setup(owner, agentId, key, agent, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static InstallInfo[] Installs(bool withReadOnly) => withReadOnly
        ? [new(AgentKit.InstallPath, AgentKit.InstallArea, true), new(ReadOnlyPath, "install-kkkkkkkk", true, ReadOnly: true)]
        : [new(AgentKit.InstallPath, AgentKit.InstallArea, true)];

    private static Task<JsonElement[]> Deployments(HttpClient c, Guid id) => c.GetFromJsonAsync<JsonElement[]>($"/api/missions/{id}/deployments")!;

    private static async Task<JsonElement> WaitForAsync(HttpClient c, Guid id, string install, string status)
    {
        JsonElement found = default;
        await AgentKit.WaitUntilAsync(() =>
        {
            var row = Deployments(c, id).GetAwaiter().GetResult().FirstOrDefault(d => d.GetProperty("installName").GetString() == install);
            if (row.ValueKind == JsonValueKind.Undefined || row.GetProperty("status").GetString() != status) return false;
            found = row;
            return true;
        }, 10);
        return found;
    }

    private static async Task<string> PboShaAsync(PanelFactory f, Guid missionId, int number)
    {
        using var scope = f.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDb>().MissionVersions.SingleAsync(v => v.MissionId == missionId && v.Number == number)).PboSha256;
    }

    [Fact]
    public async Task Saving_a_version_deploys_to_every_writable_install()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;

        var id = await CreateAsync(s.Owner);

        var row = await WaitForAsync(s.Owner, id, "Arma 3", "Done");
        var spec = Assert.Single(s.Agent.Deploys());
        Assert.Equal((AgentKit.InstallPath, "co10_Escape.Altis.pbo", await PboShaAsync(f, id, 1)), (spec.InstallRoot, spec.FileName, spec.Sha256));
        Assert.Equal(1, row.GetProperty("version").GetInt32());
        Assert.Contains("backups", row.GetProperty("backupPath").GetString());
    }

    [Fact]
    public async Task Read_only_installs_are_skipped_and_say_why()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f, withReadOnly: true);
        await using var lease = s.Agent;

        var id = await CreateAsync(s.Owner);

        await WaitForAsync(s.Owner, id, "Arma 3", "Done");
        var skipped = await WaitForAsync(s.Owner, id, "Game folder", "Failed");
        Assert.Contains("read-only", skipped.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(s.Agent.Deploys(), d => d.InstallRoot == ReadOnlyPath); // never asked to write there
    }

    [Fact]
    public async Task Pending_deployments_are_resent_on_hello()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await s.Agent.DisposeAsync(); // the agent is offline while the version is saved
        await Task.Delay(200);

        var id = await CreateAsync(s.Owner);
        await WaitForAsync(s.Owner, id, "Arma 3", "Pending");
        await using var back = await FakeAgent.ConnectAsync(f, s.Key);
        await back.HelloAsync(installs: Installs(false));

        await WaitForAsync(s.Owner, id, "Arma 3", "Done");
        Assert.Single(back.Deploys());
    }

    [Fact]
    public async Task Waiting_then_done_reports_are_recorded_and_audited()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        s.Agent.OnDeployMission = spec => new DeployResult(spec.DeploymentId, DeployResult.Accepted, null, null);

        var id = await CreateAsync(s.Owner);
        await WaitForAsync(s.Owner, id, "Arma 3", "Pending");
        var deploymentId = s.Agent.Deploys()[0].DeploymentId;
        await s.Agent.SendDeploymentChangedAsync(new DeployResult(deploymentId, DeployResult.Waiting, "The mission is running", null));
        await WaitForAsync(s.Owner, id, "Arma 3", "Waiting");
        await s.Agent.SendDeploymentChangedAsync(new DeployResult(deploymentId, DeployResult.Done, null, @"D:\b\1"));
        await WaitForAsync(s.Owner, id, "Arma 3", "Done");

        using var scope = f.Services.CreateScope();
        Assert.Contains(await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToListAsync(), a => a == "mission.deployed");
    }

    // Phase 4a final review M-2, met again as a flaky test in 5b: a resend (hello) answered Accepted turned Waiting back into Pending.
    [Fact]
    public async Task A_resend_doesnt_turn_waiting_back_into_sending()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        s.Agent.OnDeployMission = spec => new DeployResult(spec.DeploymentId, DeployResult.Accepted, null, null);
        var id = await CreateAsync(s.Owner);
        await WaitForAsync(s.Owner, id, "Arma 3", "Pending");
        await s.Agent.SendDeploymentChangedAsync(new DeployResult(s.Agent.Deploys()[0].DeploymentId, DeployResult.Waiting, "The mission is running", null));
        await WaitForAsync(s.Owner, id, "Arma 3", "Waiting");

        await s.Agent.HelloAsync(installs: Installs(false)); // the agent reconnects: the hub resends Pending and Waiting rows
        await AgentKit.WaitUntilAsync(() => s.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.DeployMission)) == 2, 10);
        await Task.Delay(200);

        Assert.Equal("Waiting", (await Deployments(s.Owner, id)).Single(d => d.GetProperty("installName").GetString() == "Arma 3").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Agents_can_pull_only_their_deployment_blobs()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var id = await CreateAsync(s.Owner);
        await WaitForAsync(s.Owner, id, "Arma 3", "Done");
        var pbo = await PboShaAsync(f, id, 1);
        string working;
        using (var scope = f.Services.CreateScope())
            working = (await scope.ServiceProvider.GetRequiredService<AppDb>().MissionWorkingFiles.FirstAsync()).Sha256;
        var (_, _, otherKey) = await AgentKit.PairAsync(f, s.Owner);
        HttpClient As(string? key)
        {
            var c = f.CreateClient();
            if (key is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("AgentKey", key);
            return c;
        }

        var ok = await As(s.Key).GetAsync($"/api/agent/blobs/{pbo}");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(await s.Owner.GetByteArrayAsync($"/api/missions/{id}/versions/1/pbo"), await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await As(s.Key).GetAsync($"/api/agent/blobs/{working}")).StatusCode);   // not a deployment PBO
        Assert.Equal(HttpStatusCode.NotFound, (await As(otherKey).GetAsync($"/api/agent/blobs/{pbo}")).StatusCode);  // not this agent's
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).GetAsync($"/api/agent/blobs/{pbo}")).StatusCode);
    }

    [Fact]
    public async Task Restore_writes_the_older_pbo_again()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var id = await CreateAsync(s.Owner);
        var next = Files();
        next["description.ext"] = Utf8("onLoadName = \"v2\";");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(s.Owner, "co10_Escape.Altis.pbo", Pbo(next), id)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsync($"/api/missions/{id}/versions/1/restore", null)).StatusCode);

        await AgentKit.WaitUntilAsync(() => s.Agent.Deploys().Length == 3, 10);
        Assert.Equal(await PboShaAsync(f, id, 1), s.Agent.Deploys()[^1].Sha256);
        Assert.Equal(1, (await WaitForAsync(s.Owner, id, "Arma 3", "Done")).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Archive_keeps_the_server_file()
    {
        using var f = new PanelFactory();
        var s = await SetUpAsync(f);
        await using var lease = s.Agent;
        var id = await CreateAsync(s.Owner);
        await WaitForAsync(s.Owner, id, "Arma 3", "Done");

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsync($"/api/missions/{id}/archive", null)).StatusCode);

        Assert.Single(s.Agent.Deploys());
        Assert.DoesNotContain(s.Agent.Calls, c => c.Method is not (nameof(IAgentClient.DeployMission)) && c.Method.Contains("Mission"));
        Assert.Equal("Done", (await Deployments(s.Owner, id))[0].GetProperty("status").GetString());
    }
}
