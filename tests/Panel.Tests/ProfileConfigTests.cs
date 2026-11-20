using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

public class ProfileConfigTests
{
    private static async Task<string?> Code(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<(HttpClient Owner, FakeAgent Agent, Guid Id)> ProfileAsync(PanelFactory f)
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        return (owner, agent, await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId)));
    }

    private static Task<JsonElement> Detail(HttpClient c, Guid id) => c.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");

    [Fact]
    public async Task Config_save_creates_revision_and_head_moves()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var before = await AgentKit.RevisionAsync(owner, id);

        var r = await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["maxPlayers"] = 40, note: "More slots");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(before + 1, await AgentKit.RevisionAsync(owner, id));
        var files = await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/files");
        Assert.Contains("maxPlayers = 40;", files.GetProperty("serverCfg").GetString());
    }

    [Fact]
    public async Task Outdated_base_revision_is_stale()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var snapshot = (await Detail(owner, id)).GetProperty("snapshot");

        var r = await owner.PutAsJsonAsync($"/api/profiles/{id}/config", new { baseRevision = 1, snapshot });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("stale", await Code(r));
    }

    [Fact]
    public async Task Raw_server_cfg_save_keeps_unknown_entries()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await AgentKit.PutFileAsync(owner, id, "server.cfg", "hostname = \"A\";\r\nfake_x = 1;\r\nvonCodecQuality = 40;\r\n");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var warnings = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("warnings");
        Assert.Equal(3, warnings.EnumerateArray().Single().GetProperty("line").GetInt32());
        var snapshot = (await Detail(owner, id)).GetProperty("snapshot");
        Assert.Equal("A", snapshot.GetProperty("settings").GetProperty("server").GetProperty("hostname").GetString());
        Assert.Contains("fake_x = 1;", snapshot.GetProperty("extraServerCfg").GetString());
        Assert.Contains("fake_x = 1;", (await owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}/files")).GetProperty("serverCfg").GetString());

        var bad = await AgentKit.PutFileAsync(owner, id, "server.cfg", "hostname = \"x");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("config_syntax", await Code(bad));
    }

    [Fact]
    public async Task Password_typed_into_raw_text_becomes_a_secret()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        Assert.Equal(HttpStatusCode.OK, (await AgentKit.PutFileAsync(owner, id, "server.cfg", "hostname = \"A\";\r\npassword = \"hunter2-raw\";\r\n")).StatusCode);

        var detail = await owner.GetStringAsync($"/api/profiles/{id}");
        Assert.DoesNotContain("hunter2", detail);
        Assert.True(JsonDocument.Parse(detail).RootElement.GetProperty("secrets").GetProperty("password").GetProperty("set").GetBoolean());
        Assert.DoesNotContain("hunter2", await owner.GetStringAsync($"/api/profiles/{id}/files"));
    }

    [Fact]
    public async Task No_profile_surface_returns_a_password()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var set = await AgentKit.EditConfigAsync(owner, id, _ => { },
            secrets: new { password = "hunter2-join", passwordAdmin = "hunter2-admin", serverCommandPassword = "hunter2-cmd" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.DoesNotContain("hunter2", await set.Content.ReadAsStringAsync());
        var extra = await AgentKit.EditConfigAsync(owner, id, s => s["extraServerCfg"] = "passwordAdmin = \"hunter2-extra\";\r\nfake_y = 2;\r\n");
        Assert.Equal(HttpStatusCode.OK, extra.StatusCode);
        Assert.DoesNotContain("hunter2", await extra.Content.ReadAsStringAsync());

        var admin = await TestUsers.LoginAsync(f, "S4", "Admin");
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        foreach (var c in new[] { owner, admin, zeus })
        {
            foreach (var url in new[] { "/api/profiles", $"/api/profiles/{id}", $"/api/profiles/{id}/files", "/api/profiles/catalog" })
                Assert.DoesNotContain("hunter2", await c.GetStringAsync(url));
            var snapshot = (await Detail(c, id)).GetProperty("snapshot");
            var preview = await c.PostAsJsonAsync($"/api/profiles/{id}/preview", new { snapshot });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.DoesNotContain("hunter2", await preview.Content.ReadAsStringAsync());
        }
        var dup = await owner.PostAsJsonAsync($"/api/profiles/{id}/duplicate", new { name = "Copy", slug = "copy" });
        Assert.Equal(HttpStatusCode.Created, dup.StatusCode);
        Assert.DoesNotContain("hunter2", await dup.Content.ReadAsStringAsync());

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.DoesNotContain(await db.Audit.ToListAsync(), a => a.Summary.Contains("hunter2") || (a.DetailsJson ?? "").Contains("hunter2"));
        Assert.All(await db.ProfileRevisions.ToListAsync(), r => Assert.DoesNotContain("hunter2", r.SnapshotJson));
        Assert.All(await db.Profiles.ToListAsync(), p => Assert.DoesNotContain("hunter2", (p.SettingsJson ?? "") + p.ExtraServerCfg));
        // The password typed into the extras box became the admin password.
        Assert.Contains(await db.Audit.ToListAsync(), a => a.Action == "profile.saved" && (a.DetailsJson ?? "").Contains("server.passwordAdmin"));
    }

    [Fact]
    public async Task Unquoted_password_in_raw_text_or_extras_never_comes_back()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.PutFileAsync(owner, id, "server.cfg", "hostname = \"A\";\r\npasswordAdmin = 987654321;\r\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(owner, id, s => s["extraServerCfg"] = "password = hunter2word;\r\n")).StatusCode);

        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        foreach (var url in new[] { $"/api/profiles/{id}", $"/api/profiles/{id}/files", $"/api/profiles/{id}/revisions/{await AgentKit.RevisionAsync(owner, id)}/files" })
        {
            var body = await zeus.GetStringAsync(url);
            Assert.DoesNotContain("987654321", body);
            Assert.DoesNotContain("hunter2word", body);
        }
        var detail = await Detail(owner, id);
        Assert.True(detail.GetProperty("secrets").GetProperty("passwordAdmin").GetProperty("set").GetBoolean());
        Assert.True(detail.GetProperty("secrets").GetProperty("password").GetProperty("set").GetBoolean());
    }

    [Fact]
    public async Task Deeply_nested_text_is_refused_not_a_crash()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var snapshot = JsonNode.Parse((await Detail(zeus, id)).GetProperty("snapshot").GetRawText())!;
        snapshot["extraServerCfg"] = "x[] = " + new string('{', 60_000);
        snapshot["difficultyOverride"] = "class DifficultyPresets {" + new string(' ', 200_000) + "};";

        var r = await zeus.PostAsJsonAsync($"/api/profiles/{id}/preview", new { snapshot });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync($"/api/profiles/{id}")).StatusCode); // the panel is still up
    }

    [Fact]
    public async Task Managed_key_in_extras_that_the_form_also_sets_is_refused()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var both = await AgentKit.EditConfigAsync(owner, id, s =>
        {
            s["settings"]!["server"]!["vonCodecQuality"] = 20;
            s["extraServerCfg"] = "vonCodecQuality = 40;\r\n";
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal("extraServerCfg", (await both.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        // A value kept raw (out of range) is fine while the form leaves that key alone, and its warning comes back.
        var kept = await AgentKit.EditConfigAsync(owner, id, s => s["extraServerCfg"] = "vonCodecQuality = 40;\r\n");
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(1, (await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("warnings").EnumerateArray().Single().GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Managed_class_in_extras_that_the_form_also_sets_is_refused()
    {
        // Kept as written (unknown member) while the form writes its own class AdvancedOptions: the file would hold two.
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await AgentKit.EditConfigAsync(owner, id, s =>
        {
            s["settings"]!["server"]!["AdvancedOptions.logObjectNotFound"] = false;
            s["extraServerCfg"] = "class AdvancedOptions\r\n{\r\n\tfake_member = 1;\r\n};\r\n";
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("extraServerCfg", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Headless_arrays_in_extras_while_headless_is_on_are_refused()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await AgentKit.EditConfigAsync(owner, id, s =>
        {
            s["settings"]!["headless"]!["enabled"] = true;
            s["extraServerCfg"] = "headlessClients[] = {\"127.0.0.1\"};\r\nlocalClient[] = {\"10.0.0.9\"};\r\n";
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("extraServerCfg", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Non_finite_json_numbers_are_refused_not_a_crash()
    {
        // 1e999 is valid JSON that reads as Infinity, which passes a min-only range and would be written into server.cfg.
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        foreach (var (key, value) in new[] { ("motdInterval", "1e999"), ("votingTimeOut", "[1e999, 90]"), ("missionsToServerRestart", "1e999") })
        {
            var snapshot = JsonNode.Parse((await Detail(owner, id)).GetProperty("snapshot").GetRawText())!;
            snapshot["settings"]!["server"]![key] = JsonNode.Parse(value);
            snapshot["settings"]!["server"]!["missionsToShutdown"] = 1;

            var r = await owner.PostAsJsonAsync($"/api/profiles/{id}/preview", new { snapshot });

            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal($"server.{key}", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        }
    }

    [Fact]
    public async Task Saving_with_unreadable_passwords_is_refused_until_they_are_set_again()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Profiles.FindAsync(id))!.SecretsProtected = "CfDJ8-not-a-protected-payload";
            await db.SaveChangesAsync();
        }

        var r = await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["maxPlayers"] = 5);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("secrets_unreadable", await Code(r));
        Assert.Equal("secrets_unreadable", await Code(await AgentKit.PutFileAsync(owner, id, "basic.cfg", "MaxMsgSend = 128;")));
        Assert.Equal("secrets_unreadable", await Code(await owner.PostAsJsonAsync($"/api/profiles/{id}/duplicate", new { name = "Copy", slug = "copy" })));

        var reset = await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["server"]!["maxPlayers"] = 5,
            secrets: new { password = "", passwordAdmin = "new-admin", serverCommandPassword = "" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    }

    [Fact]
    public async Task Passwords_must_be_one_line_and_the_join_password_has_no_quotes()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var quote = await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { password = "a\"b" });
        Assert.Equal("secrets.password", (await quote.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        var newline = await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { passwordAdmin = "a\nb" });
        Assert.Equal("secrets.passwordAdmin", (await newline.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Managed_key_in_the_extras_box_is_refused()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await AgentKit.EditConfigAsync(owner, id, s => s["extraServerCfg"] = "maxPlayers = 10;\r\n");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("extraServerCfg", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Secret_write_semantics()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        async Task<(bool Join, bool Admin)> Set() =>
            ((await Detail(owner, id)).GetProperty("secrets") is var s ? (s.GetProperty("password").GetProperty("set").GetBoolean(), s.GetProperty("passwordAdmin").GetProperty("set").GetBoolean()) : default);

        Assert.Equal((false, true), await Set());
        await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { password = "join" });
        Assert.Equal((true, true), await Set());                          // absent keeps
        await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { passwordAdmin = "" });
        Assert.Equal((true, false), await Set());                         // "" clears
        await AgentKit.EditConfigAsync(owner, id, _ => { }, secrets: new { password = (string?)null, passwordAdmin = "again" });
        Assert.Equal((true, true), await Set());                          // null keeps, text replaces
    }

    [Fact]
    public async Task Identity_change_is_refused_while_running()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        await agent.SendStatusAsync(new ProfileStatus(id, ServerState.Online, 4242, DateTime.UtcNow, null, null, null));

        var port = await owner.PatchAsJsonAsync($"/api/profiles/{id}", new { port = 2402 });
        Assert.Equal(HttpStatusCode.Conflict, port.StatusCode);
        Assert.Equal("running", await Code(port));
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync($"/api/profiles/{id}", new { name = "Renamed" })).StatusCode);
    }

    [Fact]
    public async Task Slug_cannot_change()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await owner.PatchAsJsonAsync($"/api/profiles/{id}", new { slug = "other" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("slug", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Duplicate_copies_config_and_picks_free_ports()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await owner.PostAsJsonAsync($"/api/profiles/{id}/duplicate", new { name = "Training", slug = "training" });

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var copyId = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var copy = await Detail(owner, copyId);
        Assert.Equal(2307, copy.GetProperty("port").GetInt32());
        Assert.Equal(1, copy.GetProperty("revision").GetInt32());
        Assert.Equal("13th", copy.GetProperty("snapshot").GetProperty("settings").GetProperty("server").GetProperty("hostname").GetString());
        Assert.True(copy.GetProperty("secrets").GetProperty("passwordAdmin").GetProperty("set").GetBoolean());
        using var scope = f.Services.CreateScope();
        var note = (await scope.ServiceProvider.GetRequiredService<AppDb>().ProfileRevisions.SingleAsync(x => x.ProfileId == copyId)).Note;
        Assert.StartsWith("Duplicated from Main Operations revision ", note);
        Assert.Equal("conflict_slug", await Code(await owner.PostAsJsonAsync($"/api/profiles/{id}/duplicate", new { name = "X", slug = "training" })));
    }

    [Fact]
    public async Task Import_parses_on_the_server()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var text = "hostname = \"Imported\";\r\npasswordAdmin = \"hunter2-imported\";\r\n"u8.ToArray();
        agent.OnReadFile = (_, _, _, _) => new FileChunk(text, text.Length);

        var r = await owner.PostAsJsonAsync($"/api/profiles/{id}/import",
            new { baseRevision = await AgentKit.RevisionAsync(owner, id), file = "server.cfg", area = AgentKit.InstallArea, name = "server.cfg" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain("hunter2", await r.Content.ReadAsStringAsync());
        var detail = await Detail(owner, id);
        Assert.Equal("Imported", detail.GetProperty("snapshot").GetProperty("settings").GetProperty("server").GetProperty("hostname").GetString());
        Assert.True(detail.GetProperty("secrets").GetProperty("passwordAdmin").GetProperty("set").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/profiles/{id}/import",
            new { baseRevision = await AgentKit.RevisionAsync(owner, id), file = "server.cfg", area = "profiles", name = "server.cfg" })).StatusCode);
    }

    [Fact]
    public async Task Extra_args_are_validated()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;

        var r = await AgentKit.EditConfigAsync(owner, id, s => s["settings"]!["extraArgs"] = @"-profiles=C:\x");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("launch.extraArgs", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Zeus_can_view_but_not_save()
    {
        using var f = new PanelFactory();
        var (owner, agent, id) = await ProfileAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync($"/api/profiles/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AgentKit.EditConfigAsync(zeus, id, s => s["settings"]!["server"]!["maxPlayers"] = 5)).StatusCode);
    }
}
