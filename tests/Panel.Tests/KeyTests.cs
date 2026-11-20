using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Panel.Tests;

public class KeyTests
{
    [Fact]
    public async Task Keys_are_listed_with_the_mods_that_provide_them()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;
        await s.Agent.SendJobFinishedAsync(new JobResult(Guid.NewGuid(), true, null, [])); // harmless: an unknown job is ignored
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Panel.Data.AppDb>();
            db.Mods.Single(m => m.WorkshopId == 111).KeyFilesJson = "[\"cba.bikey\"]";
            await db.SaveChangesAsync();
        }
        s.Agent.OnListKeys = _ => [new KeyFile("a3.bikey", 10), new KeyFile("cba.bikey", 10), new KeyFile("stray.bikey", 10)];

        var keys = (await s.Owner.GetFromJsonAsync<JsonElement>($"/api/installs/{s.InstallId}/keys")).GetProperty("keys").EnumerateArray().ToArray();

        Assert.Equal("a3", keys.Single(k => k.GetProperty("name").GetString() == "a3.bikey").GetProperty("kind").GetString());
        var cba = keys.Single(k => k.GetProperty("name").GetString() == "cba.bikey");
        Assert.Equal(("mod", "Mod 111"), (cba.GetProperty("kind").GetString(), cba.GetProperty("mods")[0].GetString()));
        Assert.Equal("unknown", keys.Single(k => k.GetProperty("name").GetString() == "stray.bikey").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Deploy_and_cleanup_send_the_lists_mods_to_the_agent()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;
        var list = await ModLaunchTests.ListAsync(s);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Panel.Data.AppDb>();
            db.Mods.Single(m => m.WorkshopId == 111).KeyFilesJson = "[\"cba.bikey\"]";      // in the list
            db.Mods.Single(m => m.WorkshopId == 444).KeyFilesJson = "[\"optional.bikey\"]"; // optional in the list: kept
            db.Mods.Single(m => m.WorkshopId == 555).KeyFilesJson = "[\"old.bikey\"]";      // disabled in the list: removable
            await db.SaveChangesAsync();
        }
        s.Agent.OnCleanupKeys = (_, _) => new KeysResult(true, null, @"D:\Server Data\backups\keys\x\1", [], ["old.bikey"]);

        var deploy = await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/keys/deploy", new { collectionId = list });
        var cleanup = await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/keys/cleanup", new { collectionIds = new[] { list } });

        Assert.Equal(HttpStatusCode.OK, deploy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleanup.StatusCode);
        Assert.Equal(["old.bikey"], (await cleanup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
        // Only keys a library mod provides and no kept list needs; Creator DLC and hand-added keys are never named.
        Assert.Equal(["old.bikey"], (string[])s.Agent.Calls.Last(c => c.Method == nameof(IAgentClient.CleanupKeys)).Args[1]!);
    }

    [Fact]
    public async Task Mod_viewers_can_list_the_installs_for_keys()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        var installs = await zeus.GetFromJsonAsync<JsonElement[]>("/api/mods/installs");

        var install = Assert.Single(installs!);
        Assert.Equal((s.InstallId, "Arma 3"), (install.GetProperty("id").GetGuid(), install.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.GetAsync("/api/installs")).StatusCode); // the full install list stays with profiles.manage
    }

    [Fact]
    public async Task Key_changes_need_keys_manage()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;
        var list = await ModLaunchTests.ListAsync(s);
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync($"/api/installs/{s.InstallId}/keys/deploy", new { collectionId = list })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync($"/api/installs/{s.InstallId}/keys/cleanup", new { collectionIds = new[] { list } })).StatusCode);
        Assert.DoesNotContain(s.Agent.Calls, c => c.Method is nameof(IAgentClient.DeployKeys) or nameof(IAgentClient.CleanupKeys));
    }
}
