using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Panel.Tests.MissionKit;

namespace Panel.Tests;

public class MissionLibraryTests
{
    private static async Task<string[]> Names(HttpClient c, string query) =>
        (await c.GetFromJsonAsync<JsonElement[]>("/api/missions" + query))!.Select(m => m.GetProperty("className").GetString()!).Order().ToArray();

    [Fact]
    public async Task Library_search_and_filters()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var a = await CreateAsync(maker, "alpha.Altis");
        var b = await CreateAsync(maker, "bravo.Stratis");
        await CreateAsync(maker, "charlie.Altis");
        Assert.Equal(HttpStatusCode.OK, (await maker.PutAsJsonAsync($"/api/missions/{a}",
            new { displayName = "Night Raid", description = "Hostage rescue at night", gameType = "Coop", category = "Training", tags = new[] { "night", "Hostage" }, collaborators = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maker.PostAsync($"/api/missions/{b}/archive", null)).StatusCode);
        var member = await TestUsers.LoginAsync(f, "Member");

        Assert.Equal(["alpha", "charlie"], await Names(member, ""));             // archived missions are hidden by default
        Assert.Equal(["alpha"], await Names(member, "?q=rescue"));                // description search
        Assert.Equal(["alpha"], await Names(member, "?q=night%20raid"));          // display name search
        Assert.Equal(["alpha", "charlie"], await Names(member, "?map=altis"));
        Assert.Equal(["alpha"], await Names(member, "?category=Training"));
        Assert.Equal(["alpha"], await Names(member, "?tag=hostage"));             // tags ignore case
        Assert.Equal(["bravo"], await Names(member, "?archived=true"));
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PutAsJsonAsync($"/api/missions/{a}",
            new { displayName = "x", description = "", category = "Not a category", tags = Array.Empty<string>(), collaborators = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Edit_own_means_author_or_collaborator()
    {
        using var f = new PanelFactory();
        var author = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var helper = await TestUsers.LoginAsync(f, "S3", "Owl");
        var admin = await TestUsers.LoginAsync(f, "S1", "Crane"); // missions.editAny
        var id = await CreateAsync(author);
        object Edit(params Guid[] collaborators) => new { displayName = "Escape", description = "", tags = Array.Empty<string>(), collaborators };

        Assert.Equal(HttpStatusCode.Forbidden, (await helper.PutAsJsonAsync($"/api/missions/{id}", Edit())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.PutAsJsonAsync($"/api/missions/{id}", Edit(await TestUsers.IdAsync(f, "Owl")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await helper.PutAsJsonAsync($"/api/missions/{id}", Edit(await TestUsers.IdAsync(f, "Owl")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/missions/{id}/archive", null)).StatusCode); // editAny
        var detail = await author.GetFromJsonAsync<JsonElement>($"/api/missions/{id}");
        Assert.True(detail.GetProperty("canEdit").GetBoolean());
        Assert.False((await (await TestUsers.LoginAsync(f, "S3")).GetFromJsonAsync<JsonElement>($"/api/missions/{id}")).GetProperty("canEdit").GetBoolean());
    }

    // Final review I-3 (plan Ruling 15): collaborators edit the mission; only the author or a mission editor changes who they are.
    [Fact]
    public async Task Only_the_author_or_an_editor_changes_the_collaborators()
    {
        using var f = new PanelFactory();
        var author = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var helper = await TestUsers.LoginAsync(f, "S3", "Owl");
        await TestUsers.LoginAsync(f, "S3", "Wren");
        var admin = await TestUsers.LoginAsync(f, "S1", "Crane"); // missions.editAny
        var (owl, wren) = (await TestUsers.IdAsync(f, "Owl"), await TestUsers.IdAsync(f, "Wren"));
        var id = await CreateAsync(author);
        object Edit(params Guid[] collaborators) => new { displayName = "Escape", description = "", tags = Array.Empty<string>(), collaborators };
        Assert.Equal(HttpStatusCode.OK, (await author.PutAsJsonAsync($"/api/missions/{id}", Edit(owl))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await helper.PutAsJsonAsync($"/api/missions/{id}", Edit(owl, wren))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helper.PutAsJsonAsync($"/api/missions/{id}", Edit())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await helper.PutAsJsonAsync($"/api/missions/{id}", Edit(owl))).StatusCode); // the same list: details still save
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/missions/{id}", Edit(owl, wren))).StatusCode);
        Assert.False((await helper.GetFromJsonAsync<JsonElement>($"/api/missions/{id}")).GetProperty("canShare").GetBoolean());
        Assert.True((await author.GetFromJsonAsync<JsonElement>($"/api/missions/{id}")).GetProperty("canShare").GetBoolean());
        Assert.True((await admin.GetFromJsonAsync<JsonElement>($"/api/missions/{id}")).GetProperty("canShare").GetBoolean());
    }

    [Fact]
    public async Task Missions_need_missions_view()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3", "Hawk");
        await TestUsers.AddClaimAsync(f, "Hawk", "perm.deny", "missions.view");

        Assert.Equal(HttpStatusCode.Forbidden, (await maker.GetAsync("/api/missions")).StatusCode);
    }
}
