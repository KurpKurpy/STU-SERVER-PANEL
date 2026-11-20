using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArmaFormats;
using static Panel.Tests.MissionKit;

namespace Panel.Tests;

public class VersionTests
{
    private static string V(Guid id) => $"/api/missions/{id}/versions";

    private static async Task<string> ShaOf(HttpClient c, Guid id, string path) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/missions/{id}/files/content?path={path}")).GetProperty("sha").GetString()!;

    private static async Task EditAsync(HttpClient c, Guid id, string path, string content) =>
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/missions/{id}/files/content", new { path, content, baseSha = await ShaOf(c, id, path) })).StatusCode);

    private static async Task<int> SaveAsync(HttpClient c, Guid id, string note)
    {
        var r = await c.PostAsJsonAsync(V(id), new { note });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32();
    }

    [Fact]
    public async Task Save_version_packs_a_pbo_that_reads_back_identical()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        await EditAsync(maker, id, "scripts/init.sqf", "hint \"v2\";");

        var n = await SaveAsync(maker, id, "Edited the intro");

        Assert.Equal(2, n);
        var pbo = ArmaFormats.Pbo.Read(new MemoryStream(await maker.GetByteArrayAsync($"{V(id)}/2/pbo")));
        Assert.Equal("hint \"v2\";", Utf8Text(pbo.Entries.Single(e => e.Path == "scripts/init.sqf").Data));
        Assert.Equal(["description.ext", "mission.sqm", "scripts/init.sqf"], pbo.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        var versions = (await maker.GetFromJsonAsync<JsonElement[]>(V(id)))!;
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal("Edited the intro", versions[0].GetProperty("note").GetString());
        Assert.True(versions[0].GetProperty("current").GetBoolean());
    }

    private static string Utf8Text(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

    [Fact]
    public async Task A_version_is_one_snapshot()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);

        await EditAsync(maker, id, "scripts/init.sqf", "hint \"after\";"); // the working copy moves on; version 1 doesn't

        var v1 = ArmaFormats.Pbo.Read(new MemoryStream(await maker.GetByteArrayAsync($"{V(id)}/1/pbo")));
        Assert.Equal("hint \"hi\";", Utf8Text(v1.Entries.Single(e => e.Path == "scripts/init.sqf").Data));
        var file = await maker.GetFromJsonAsync<JsonElement>($"{V(id)}/1/file?path=scripts/init.sqf");
        Assert.Equal("hint \"hi\";", file.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Versions_store_required_addons_with_mod_names()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);

        var v = (await maker.GetFromJsonAsync<JsonElement[]>(V(id)))![0];

        var addons = v.GetProperty("addons").EnumerateArray().ToArray();
        Assert.Equal(["A3_Characters_F", "cba_main"], addons.Select(a => a.GetProperty("addon").GetString()));
        Assert.Equal("Community Base Addons v3.19.0", addons[1].GetProperty("modName").GetString());
        Assert.False(v.GetProperty("binarized").GetBoolean());
    }

    [Fact]
    public async Task Compare_lists_changed_paths()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        await EditAsync(maker, id, "description.ext", "onLoadName = \"v2\";");
        Assert.Equal(HttpStatusCode.OK, (await maker.DeleteAsync($"/api/missions/{id}/files?path=scripts/init.sqf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maker.PutAsJsonAsync($"/api/missions/{id}/files/content", new { path = "briefing.sqf", content = "x", baseSha = (string?)null })).StatusCode);
        await SaveAsync(maker, id, null!);

        var diff = await maker.GetFromJsonAsync<JsonElement>($"{V(id)}/compare?a=1&b=2");

        Assert.Equal(["briefing.sqf"], diff.GetProperty("added").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["scripts/init.sqf"], diff.GetProperty("removed").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["description.ext"], diff.GetProperty("changed").EnumerateArray().Select(x => x.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Restore_makes_the_older_version_current()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        await EditAsync(maker, id, "description.ext", "onLoadName = \"v2\";");
        await SaveAsync(maker, id, "v2");

        var r = await maker.PostAsync($"{V(id)}/1/restore", null);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, (await maker.GetFromJsonAsync<JsonElement>($"/api/missions/{id}")).GetProperty("currentVersion").GetInt32());
        Assert.Equal(2, (await maker.GetFromJsonAsync<JsonElement[]>(V(id)))!.Length); // no new version
    }

    [Fact]
    public async Task Open_in_working_copy_replaces_files_as_revisions()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        await EditAsync(maker, id, "description.ext", "onLoadName = \"v2\";");
        await SaveAsync(maker, id, "v2");

        Assert.Equal(HttpStatusCode.OK, (await maker.PostAsync($"{V(id)}/1/open", null)).StatusCode);

        var content = await maker.GetFromJsonAsync<JsonElement>($"/api/missions/{id}/files/content?path=description.ext");
        Assert.Equal(DescriptionExt, content.GetProperty("text").GetString());
        Assert.Equal(3, (await maker.GetFromJsonAsync<JsonElement[]>($"/api/missions/{id}/files/history?path=description.ext"))!.Length);
    }

    [Fact]
    public async Task Downloads_need_missions_download()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3", "Hawk");
        var id = await CreateAsync(maker);
        var member = await TestUsers.LoginAsync(f, "Member", "Wren");

        var pbo = await member.GetAsync($"{V(id)}/1/pbo");
        var original = await member.GetAsync($"{V(id)}/1/original");
        await TestUsers.AddClaimAsync(f, "Wren", "perm.deny", "missions.download");

        Assert.Equal(HttpStatusCode.OK, pbo.StatusCode);
        Assert.Equal("co10_Escape.Altis.pbo", pbo.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"{V(id)}/1/pbo")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(V(id), new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task Header_properties_from_the_upload_are_kept()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        using var ms = new MemoryStream();
        ArmaFormats.Pbo.Write(new PboFile(new Dictionary<string, string> { ["prefix"] = "x\\y" }, Files().Select(kv => new PboEntry(kv.Key, kv.Value, 0)).ToList()), ms);
        var r = await UploadAsync(maker, "props_Op.Altis.pbo", ms.ToArray());
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mission").GetProperty("id").GetGuid();

        var pbo = ArmaFormats.Pbo.Read(new MemoryStream(await maker.GetByteArrayAsync($"{V(id)}/1/pbo")));

        Assert.Equal("x\\y", pbo.Properties["prefix"]);
    }
}
