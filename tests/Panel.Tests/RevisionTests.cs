using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Tests;

public class RevisionTests
{
    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<(HttpClient Owner, FakeAgent Agent, Guid InstallId, Guid Id)> ProfileAsync(PanelFactory f)
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        return (owner, agent, installId, await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId)));
    }

    private static string Lines(JsonElement files, string name) =>
        string.Join("\n", files.GetProperty("files").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name)
            .GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("kind").GetString() + l.GetProperty("text").GetString()));

    [Fact]
    public void LineDiff_marks_added_and_removed_lines_with_context()
    {
        var old = string.Join("\r\n", Enumerable.Range(1, 20).Select(i => $"line{i};")) + "\r\n";
        var changed = old.Replace("line10;", "line10 changed;").Replace("line20;\r\n", "line20;\r\nline21;\r\n");

        var diff = LineDiff.Compute(old, changed);

        Assert.Contains(diff, d => d is { Kind: '-', Text: "line10;", OldLine: 10 });
        Assert.Contains(diff, d => d is { Kind: '+', Text: "line10 changed;", NewLine: 10 });
        Assert.Contains(diff, d => d is { Kind: '+', Text: "line21;", NewLine: 21 });
        Assert.Contains(diff, d => d is { Kind: ' ', Text: "line7;" });          // 3 lines of context before
        Assert.DoesNotContain(diff, d => d.Text == "line6;");
        Assert.Equal('…', diff[0].Kind);                                       // lines 1–6 collapsed
        Assert.Empty(LineDiff.Compute(old, old));
    }

    [Fact]
    public async Task Revisions_list_marks_the_applied_one()
    {
        using var f = new PanelFactory();
        var (owner, agent, _, id) = await ProfileAsync(f);
        await using var lease = agent;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Profiles.FindAsync(id))!.AppliedRevisionId = (await db.ProfileRevisions.SingleAsync(r => r.ProfileId == id && r.Number == 2)).Id;
            await db.SaveChangesAsync();
        }

        var list = (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/revisions")).EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], list.Select(r => r.GetProperty("number").GetInt32()));
        Assert.Equal([false, true, false], list.Select(r => r.GetProperty("applied").GetBoolean()));
        Assert.Equal("Created", list[2].GetProperty("note").GetString());
    }

    [Fact]
    public async Task Reset_creates_a_new_revision_with_old_values_and_passwords()
    {
        using var f = new PanelFactory();
        var (owner, agent, _, id) = await ProfileAsync(f);
        await using var lease = agent;
        var keep = await AgentKit.RevisionAsync(owner, id);
        await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["hostname"] = "Changed", secrets: new { passwordAdmin = "" });

        var r = await owner.PostAsJsonAsync($"/api/profiles/{id}/revisions/{keep}/reset", new { baseRevision = keep + 1 });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        Assert.Equal(keep + 2, detail.GetProperty("revision").GetInt32());
        Assert.Equal("13th", detail.GetProperty("snapshot").GetProperty("settings").GetProperty("server").GetProperty("hostname").GetString());
        Assert.True(detail.GetProperty("secrets").GetProperty("passwordAdmin").GetProperty("set").GetBoolean());
        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/revisions");
        Assert.Equal($"Reset to revision {keep}", list[0].GetProperty("note").GetString());
        Assert.Equal("stale", await Code(await owner.PostAsJsonAsync($"/api/profiles/{id}/revisions/{keep}/reset", new { baseRevision = keep })));
    }

    [Fact]
    public async Task Compare_shows_a_password_change_without_its_value()
    {
        using var f = new PanelFactory();
        var (owner, agent, _, id) = await ProfileAsync(f);
        await using var lease = agent;
        await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { passwordAdmin = "hunter2-a" });
        var from = await AgentKit.RevisionAsync(owner, id);
        await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["maxPlayers"] = 12, secrets: new { passwordAdmin = "hunter2-b" });

        var body = await owner.GetStringAsync($"/api/profiles/{id}/compare?from={from}");

        Assert.DoesNotContain("hunter2", body);
        var server = Lines(JsonDocument.Parse(body).RootElement, "server.cfg");
        Assert.Contains("+passwordAdmin = \"******** (changed)\";", server);
        Assert.Contains("+maxPlayers = 12;", server);
    }

    [Fact]
    public async Task Compare_profiles_lists_identity_and_file_differences()
    {
        using var f = new PanelFactory();
        var (owner, agent, installId, a) = await ProfileAsync(f);
        await using var lease = agent;
        var b = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId, name: "Training", slug: "training", port: 2402,
            serverCfg: "hostname = \"Training\"; passwordAdmin = \"hunter2-other\";"));

        var body = await owner.GetStringAsync($"/api/profiles/compare?a={a}&b={b}");

        Assert.DoesNotContain("hunter2", body);
        var root = JsonDocument.Parse(body).RootElement;
        var port = root.GetProperty("identity").EnumerateArray().Single(i => i.GetProperty("field").GetString() == "port");
        Assert.Equal(("2302", "2402"), (port.GetProperty("a").GetString(), port.GetProperty("b").GetString()));
        Assert.Contains("+hostname = \"Training\";", Lines(root, "server.cfg"));
        Assert.Contains("+passwordAdmin = \"******** (changed)\";", Lines(root, "server.cfg"));
    }

    [Fact]
    public async Task Builtin_preset_cannot_change()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner");
        var regular = (await owner.GetFromJsonAsync<JsonElement>("/api/difficulty-presets")).EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Regular");
        var presetId = regular.GetProperty("id").GetGuid();

        Assert.Equal("built_in", await Code(await owner.PutAsJsonAsync($"/api/difficulty-presets/{presetId}", new { name = "Regular", values = new { reducedDamage = 1 } })));
        Assert.Equal("built_in", await Code(await owner.DeleteAsync($"/api/difficulty-presets/{presetId}")));

        var created = await owner.PostAsJsonAsync("/api/difficulty-presets", new { name = "Unit milsim", values = new { thirdPersonView = 0, crosshair = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/difficulty-presets", new { name = "Unit milsim", values = new { thirdPersonView = 0 } })).StatusCode);
        Assert.Equal("conflict", await Code(await owner.PostAsJsonAsync("/api/difficulty-presets", new { name = "Unit milsim", values = new { } })));
    }

    [Fact]
    public async Task Editing_a_preset_never_changes_a_profile()
    {
        using var f = new PanelFactory();
        var (owner, agent, _, id) = await ProfileAsync(f);
        await using var lease = agent;
        var created = await owner.PostAsJsonAsync("/api/difficulty-presets", new { name = "Milsim", values = new { thirdPersonView = 0 } });
        var presetId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await AgentKit.EditConfigAsync(owner, id, s =>
        {
            s["settings"]!["difficulty"]!["thirdPersonView"] = 0;
            s["difficultyPresetId"] = presetId.ToString();
        });

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/difficulty-presets/{presetId}", new { name = "Milsim", values = new { thirdPersonView = 1 } })).StatusCode);

        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        Assert.Equal(0, detail.GetProperty("snapshot").GetProperty("settings").GetProperty("difficulty").GetProperty("thirdPersonView").GetInt32());
    }
}
