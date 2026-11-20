using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using Panel.Features.Mods;

namespace Panel.Tests;

public class CollectionTests
{
    private static async Task<(HttpClient Owner, FakeAgent Agent, Guid[] Mods)> SetUpAsync(PanelFactory f, string ids = "111\n222\n333")
    {
        var (owner, _, agent, _) = await AgentKit.InstallAsync(f);
        await ModTests.PasteAsync(owner, ids);
        var mods = (await owner.GetFromJsonAsync<JsonElement[]>("/api/mods"))!
            .OrderBy(m => m.GetProperty("workshopId").GetInt64()).Select(m => m.GetProperty("id").GetGuid()).ToArray();
        return (owner, agent, mods);
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient c, string name, params object[] items) =>
        await c.PostAsJsonAsync("/api/collections", new { name, description = "", items });

    private static async Task<Guid> IdOf(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Flags_and_order_are_saved()
    {
        using var f = new PanelFactory();
        var (owner, agent, m) = await SetUpAsync(f);
        await using var lease = agent;

        var id = await IdOf(await CreateAsync(owner, "Main",
            new { modId = m[2], client = true, headless = true, enabled = true },
            new { modId = m[0], serverOnly = true, enabled = true },
            new { modId = m[1], optional = true, enabled = false }));

        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{id}");
        var items = list.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal([m[2], m[0], m[1]], items.Select(i => i.GetProperty("modId").GetGuid()));
        Assert.True(items[0].GetProperty("client").GetBoolean() && items[0].GetProperty("headless").GetBoolean());
        Assert.True(items[1].GetProperty("serverOnly").GetBoolean());
        Assert.False(items[2].GetProperty("enabled").GetBoolean());
        Assert.Equal("Mod 333", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Contradictory_or_unknown_items_are_refused()
    {
        using var f = new PanelFactory();
        var (owner, agent, m) = await SetUpAsync(f);
        await using var lease = agent;

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(owner, "A", new { modId = m[0], client = true, serverOnly = true, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(owner, "B", new { modId = m[0], client = true, optional = true, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(owner, "C", new { modId = Guid.NewGuid(), client = true, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(owner, "D", new { modId = m[0], client = true, enabled = true }, new { modId = m[0], headless = true, enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(owner, " ")).StatusCode);
        await IdOf(await CreateAsync(owner, "Main"));
        Assert.Equal(HttpStatusCode.Conflict, (await CreateAsync(owner, "main")).StatusCode);
    }

    [Fact]
    public async Task Import_adds_missing_mods_as_not_installed_and_builds_the_list()
    {
        using var f = new PanelFactory();
        f.Workshop.Set(450814997, "CBA_A3");
        f.Workshop.Set(463939057, "ace");
        var (owner, agent, _) = await SetUpAsync(f, "450814997");
        await using var lease = agent;

        var r = await owner.PostAsJsonAsync("/api/collections/import", new { text = LauncherPresetTests.Preset });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var list = body.GetProperty("collection");
        Assert.Equal("Main Ops & Training", list.GetProperty("name").GetString());
        Assert.Equal(["CBA_A3", "ace"], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        Assert.True(list.GetProperty("items").EnumerateArray().All(i => i.GetProperty("client").GetBoolean() && i.GetProperty("enabled").GetBoolean()));
        Assert.Equal([450814997L, 463939057L], body.GetProperty("missing").EnumerateArray().Select(x => x.GetProperty("workshopId").GetInt64()).Order());
        Assert.Contains(body.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("@unit_assets"));
        Assert.Equal("NotInstalled", (await ModTests.ModAsync(owner, 463939057)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Import_is_capped()
    {
        using var f = new PanelFactory();
        var (owner, agent, _) = await SetUpAsync(f);
        await using var lease = agent;

        var r = await owner.PostAsJsonAsync("/api/collections/import", new { text = new string('x', 600_000) });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Export_downloads_a_launcher_preset()
    {
        using var f = new PanelFactory();
        var (owner, agent, m) = await SetUpAsync(f);
        await using var lease = agent;
        var id = await IdOf(await CreateAsync(owner, "Main", new { modId = m[0], client = true, enabled = true }, new { modId = m[1], serverOnly = true, enabled = true }));

        var r = await owner.GetAsync($"/api/collections/{id}/export");

        Assert.Equal("text/html", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Main.html", r.Content.Headers.ContentDisposition!.FileNameStar ?? r.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var preset = LauncherPreset.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("Main", preset.Name);
        Assert.Equal([111L], preset.Mods.Select(x => x.WorkshopId!.Value)); // the Launcher loads client mods; server-only ones stay out
    }

    [Fact]
    public async Task Duplicate_copies_items()
    {
        using var f = new PanelFactory();
        var (owner, agent, m) = await SetUpAsync(f);
        await using var lease = agent;
        var id = await IdOf(await CreateAsync(owner, "Main", new { modId = m[0], client = true, enabled = true }));

        var copy = await IdOf(await owner.PostAsJsonAsync($"/api/collections/{id}/duplicate", new { name = "Main copy" }));

        var items = (await owner.GetFromJsonAsync<JsonElement>($"/api/collections/{copy}")).GetProperty("items");
        Assert.Equal(m[0], Assert.Single(items.EnumerateArray()).GetProperty("modId").GetGuid());
    }

    [Fact]
    public async Task Reset_to_a_revision_whose_list_was_deleted_is_refused_not_a_crash()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;
        var list = await ModLaunchTests.ListAsync(s);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = list.ToString())).StatusCode);
        var withList = (await s.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{s.ProfileId}")).GetProperty("revision").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Owner.DeleteAsync($"/api/collections/{list}")).StatusCode);

        var r = await s.Owner.PostAsJsonAsync($"/api/profiles/{s.ProfileId}/revisions/{withList}/reset", new { baseRevision = withList + 1 });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("modCollectionId", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task A_list_only_the_applied_revision_uses_cannot_be_deleted()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;
        var list = await ModLaunchTests.ListAsync(s);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = list.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(s.Owner, s.ProfileId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(s.Owner, s.ProfileId, x => x["modCollectionId"] = null)).StatusCode); // saved, not applied

        var r = await s.Owner.DeleteAsync($"/api/collections/{list}");

        // The next Start still loads the applied revision's list.
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task A_list_a_profile_uses_cannot_be_deleted()
    {
        using var f = new PanelFactory();
        var (owner, agent, m) = await SetUpAsync(f);
        await using var lease = agent;
        var used = await IdOf(await CreateAsync(owner, "Used", new { modId = m[0], client = true, enabled = true }));
        var free = await IdOf(await CreateAsync(owner, "Free"));
        var profile = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile((await owner.GetFromJsonAsync<JsonElement[]>("/api/installs"))![0].GetProperty("id").GetGuid()));
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Profiles.FindAsync(profile))!.ModCollectionId = used;
            await db.SaveChangesAsync();
        }

        var r = await owner.DeleteAsync($"/api/collections/{used}");

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Main Operations", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/collections/{free}")).StatusCode);
    }
}
