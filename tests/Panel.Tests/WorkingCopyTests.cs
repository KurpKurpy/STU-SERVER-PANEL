using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArmaFormats;
using ArmaFormats.Tests;
using static Panel.Tests.MissionKit;

namespace Panel.Tests;

public class WorkingCopyTests
{
    private static string Files(Guid id) => $"/api/missions/{id}/files";

    private static async Task<JsonElement> Content(HttpClient c, Guid id, string path) =>
        await c.GetFromJsonAsync<JsonElement>($"{Files(id)}/content?path={Uri.EscapeDataString(path)}");

    private static Task<HttpResponseMessage> Save(HttpClient c, Guid id, string path, string content, string? baseSha) =>
        c.PutAsJsonAsync($"{Files(id)}/content", new { path, content, baseSha });

    private static async Task<string[]> Tree(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement[]>(Files(id)))!.Select(f => f.GetProperty("path").GetString()!).Order(StringComparer.Ordinal).ToArray();

    private static byte[] BinarizedSqm() => RapBuilder.Build(ConfigDocument.Parse(Sqm).Items);

    [Fact]
    public async Task Save_creates_a_revision_and_a_stale_save_is_refused()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        var opened = await Content(maker, id, "description.ext");

        var saved = await Save(maker, id, "description.ext", "onLoadName = \"Edited\";", opened.GetProperty("sha").GetString());
        var stale = await Save(maker, id, "description.ext", "onLoadName = \"Lost\";", opened.GetProperty("sha").GetString());

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); // A_stale_save_is_refused
        Assert.Equal("stale", await CodeAsync(stale));
        Assert.Equal("onLoadName = \"Edited\";", (await Content(maker, id, "description.ext")).GetProperty("text").GetString());
        var history = await maker.GetFromJsonAsync<JsonElement[]>($"{Files(id)}/history?path=description.ext");
        Assert.Equal(2, history!.Length);
    }

    [Fact]
    public async Task Create_rename_delete_and_folders()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);

        Assert.Equal(HttpStatusCode.OK, (await Save(maker, id, "scripts/new/a.sqf", "hint 1;", null)).StatusCode);   // a file in a new folder
        Assert.Equal(HttpStatusCode.Conflict, (await Save(maker, id, "scripts/new/a.sqf", "hint 2;", null)).StatusCode); // exists: needs its sha
        Assert.Equal(HttpStatusCode.OK, (await maker.PostAsJsonAsync($"{Files(id)}/rename", new { from = "scripts", to = "code" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maker.PostAsJsonAsync($"{Files(id)}/rename", new { from = "description.ext", to = "Description.ext" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maker.DeleteAsync($"{Files(id)}?path=code/new")).StatusCode);
        var upload = await maker.PostAsync($"{Files(id)}/upload?folder=images", Form(("logo.paa", new byte[] { 0, 1, 2, 0 })));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        Assert.Equal(["Description.ext", "code/init.sqf", "images/logo.paa", "mission.sqm"], await Tree(maker, id));
        Assert.True((await Content(maker, id, "images/logo.paa")).GetProperty("binary").GetBoolean());
    }

    [Fact]
    public async Task Binarized_mission_sqm_is_read_only_text()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var files = MissionKit.Files();
        files["mission.sqm"] = BinarizedSqm();
        var id = await CreateAsync(maker, files: files);

        var sqm = await Content(maker, id, "mission.sqm");

        Assert.True(sqm.GetProperty("readOnly").GetBoolean());
        Assert.True(sqm.GetProperty("binarized").GetBoolean());
        Assert.Contains("addons[]={\"A3_Characters_F\", \"cba_main\"};", sqm.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Save(maker, id, "mission.sqm", "version=54;", sqm.GetProperty("sha").GetString())).StatusCode);
    }

    [Fact]
    public async Task Convert_to_text_keeps_the_original_in_history()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var files = MissionKit.Files();
        files["mission.sqm"] = BinarizedSqm();
        var id = await CreateAsync(maker, files: files);

        var r = await maker.PostAsync($"{Files(id)}/convert?path=mission.sqm", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var sqm = await Content(maker, id, "mission.sqm");
        Assert.False(sqm.GetProperty("readOnly").GetBoolean());
        Assert.Equal(["A3_Characters_F", "cba_main"], MissionScan.Addons(MissionKit.Utf8(sqm.GetProperty("text").GetString()!)).Addons);
        var history = await maker.GetFromJsonAsync<JsonElement[]>($"{Files(id)}/history?path=mission.sqm");
        Assert.Equal(2, history!.Length); // the binarized original is still there
        Assert.Equal(HttpStatusCode.Conflict, (await maker.PostAsync($"{Files(id)}/convert?path=mission.sqm", null)).StatusCode); // already text
    }

    [Fact]
    public async Task History_and_revision_content()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        var first = await Content(maker, id, "scripts/init.sqf");
        await Save(maker, id, "scripts/init.sqf", "hint \"two\";", first.GetProperty("sha").GetString());

        var history = (await maker.GetFromJsonAsync<JsonElement[]>($"{Files(id)}/history?path=scripts/init.sqf"))!;
        var oldest = history.Last();
        var old = await maker.GetFromJsonAsync<JsonElement>($"{Files(id)}/revision/{oldest.GetProperty("id").GetGuid()}");

        Assert.Equal("hint \"hi\";", old.GetProperty("text").GetString());
        Assert.Equal("hint \"two\";", (await Content(maker, id, "scripts/init.sqf")).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Search_finds_text_and_skips_binary()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var files = MissionKit.Files();
        files["data/blob.bin"] = [0, (byte)'h', (byte)'i', (byte)'n', (byte)'t', 0];
        var id = await CreateAsync(maker, files: files);

        var hits = (await maker.GetFromJsonAsync<JsonElement[]>($"{Files(id)}/search?q=HINT"))!;
        var regex = (await maker.GetFromJsonAsync<JsonElement[]>($"{Files(id)}/search?q=onLoad%5Cw%2B&regex=true"))!;

        Assert.Equal("scripts/init.sqf", Assert.Single(hits).GetProperty("path").GetString());
        Assert.Equal(1, hits[0].GetProperty("line").GetInt32());
        Assert.Equal("description.ext", Assert.Single(regex).GetProperty("path").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.GetAsync($"{Files(id)}/search?q=(&regex=true")).StatusCode);
    }

    [Fact]
    public async Task Paths_cannot_escape_or_drop_mission_sqm()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);

        foreach (var bad in new[] { "../x.sqf", "a/../../x.sqf", "C:/x.sqf", "a:b.sqf", "/x.sqf", "CON.sqf" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(maker, id, bad, "x", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.DeleteAsync($"{Files(id)}?path=mission.sqm")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync($"{Files(id)}/rename", new { from = "mission.sqm", to = "old/mission.sqm" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await maker.PostAsJsonAsync($"{Files(id)}/rename", new { from = "description.ext", to = "mission.sqm" })).StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_change_files()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var zeus = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        var sha = (await Content(zeus, id, "description.ext")).GetProperty("sha").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Save(zeus, id, "description.ext", "x", sha)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.DeleteAsync($"{Files(id)}?path=description.ext")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync($"{Files(id)}/download?path=description.ext")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync($"{Files(id)}/zip")).StatusCode);
    }
}
