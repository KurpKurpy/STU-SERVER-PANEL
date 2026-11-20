using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contracts;

namespace Panel.Tests;

public class HeadlessApiTests
{
    private static async Task<(HttpClient Owner, FakeAgent Agent, Guid Id)> ProfileAsync(PanelFactory f)
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        var id = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId, serverCfg: "hostname = \"13th\"; password = \"hunter2-join\";"), applied: false);
        return (owner, agent, id);
    }

    private static Task<HttpResponseMessage> EnableAsync(HttpClient c, Guid id, int count = 2, bool withServer = true) =>
        AgentKit.EditConfigAsync(c, id, s => s["settings"]!["headless"] = new JsonObject
        {
            ["enabled"] = true, ["count"] = count, ["withServer"] = withServer, ["addresses"] = new JsonArray("127.0.0.1"),
        });

    [Fact]
    public async Task Headless_settings_generate_server_cfg_arrays()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        Assert.Equal(HttpStatusCode.OK, (await EnableAsync(owner, id)).StatusCode);

        var cfg = (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/files")).GetProperty("serverCfg").GetString();
        Assert.Contains("headlessClients[] = {\"127.0.0.1\"};", cfg);
        Assert.Contains("localClient[] = {\"127.0.0.1\"};", cfg);
    }

    [Fact]
    public async Task Headless_command_preview_masks_the_password()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await EnableAsync(owner, id);
        var snapshot = (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}")).GetProperty("snapshot");

        var body = await (await owner.PostAsJsonAsync($"/api/profiles/{id}/preview", new { snapshot })).Content.ReadAsStringAsync();

        Assert.DoesNotContain("hunter2", body);
        var headless = JsonDocument.Parse(body).RootElement.GetProperty("command").GetProperty("headless").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(2, headless.Length);
        Assert.Contains("-client -connect=127.0.0.1 -port=2302 -password=********", headless[0]);
        Assert.Contains(@"-profiles=D:\Server Data\profiles\main\hc2", headless[1]);
    }

    [Fact]
    public async Task Start_passes_the_applied_headless_spec()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await EnableAsync(owner, id, count: 3);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(owner, id)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/servers/{id}/start", null)).StatusCode);

        var spec = (LaunchSpec)agent.Calls.Single(c => c.Method == "Start").Args[0]!;
        Assert.Equal(new HeadlessSpec(3, "127.0.0.1", "hunter2-join", true), spec.Headless);
        var server = await owner.GetFromJsonAsync<JsonElement>($"/api/servers/{id}");
        Assert.Equal(3, server.GetProperty("headless").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Headless_start_needs_server_control()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await EnableAsync(owner, id, withServer: false);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(owner, id)).StatusCode);
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsync($"/api/servers/{id}/headless/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/servers/{id}/headless/start", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsync($"/api/servers/{id}/headless/stop", null)).StatusCode);
        Assert.Equal(["StartHeadless", "StopHeadless"], agent.Calls.Select(c => c.Method).Where(m => m.Contains("Headless")));
    }
}
