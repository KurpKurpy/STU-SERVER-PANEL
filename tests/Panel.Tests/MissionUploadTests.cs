using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using Panel.Storage;
using static Panel.Tests.MissionKit;

namespace Panel.Tests;

public class MissionUploadTests
{
    private static async Task<JsonElement> Mission(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/missions/{id}");

    [Fact]
    public async Task Pbo_upload_creates_the_mission_and_version_1()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3", "Hawk");

        var r = await UploadAsync(maker, "co10_Escape.Altis.pbo", Pbo(Files()));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        var m = await Mission(maker, body.GetProperty("mission").GetProperty("id").GetGuid());
        Assert.Equal(("co10_Escape", "Altis", "Operation Escape", "Coop"),
            (m.GetProperty("className").GetString(), m.GetProperty("map").GetString(), m.GetProperty("displayName").GetString(), m.GetProperty("gameType").GetString()));
        Assert.Equal(1, m.GetProperty("currentVersion").GetInt32());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var version = await db.MissionVersions.SingleAsync();
        Assert.NotNull(version.OriginalUploadSha256); // the uploaded PBO is kept as-is
        Assert.True(scope.ServiceProvider.GetRequiredService<BlobStore>().Exists(version.PboSha256));
        Assert.Equal(new[] { "description.ext", "mission.sqm", "scripts/init.sqf" }, (await db.MissionWorkingFiles.Select(w => w.Path).ToListAsync()).Order());
        Assert.Equal(["A3_Characters_F", "cba_main"], (await db.MissionVersionAddons.OrderBy(a => a.Addon).Select(a => a.Addon).ToListAsync()));
        Assert.Equal("Community Base Addons v3.19.0", (await db.MissionVersionAddons.SingleAsync(a => a.Addon == "cba_main")).ModName);
    }

    [Fact]
    public async Task Zip_and_folder_uploads_find_the_mission_root()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");

        var zipped = await UploadAsync(maker, "whatever.zip", Zip(Files(), "zip_Root.Stratis"));
        var flatZip = await UploadAsync(maker, "flat_Op.Tanoa.zip", Zip(Files(), null));
        var folder = await maker.PostAsync("/api/missions/upload", Form(Files().Select(kv => ("drop_Op.Malden/" + kv.Key, kv.Value)).ToArray()));

        foreach (var (r, name) in new[] { (zipped, "zip_Root.Stratis"), (flatZip, "flat_Op.Tanoa"), (folder, "drop_Op.Malden") })
        {
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            var m = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mission");
            Assert.Equal(name, m.GetProperty("className").GetString() + "." + m.GetProperty("map").GetString());
        }
    }

    [Fact]
    public async Task Upload_reads_names_with_spaces_and_escapes()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");

        var id = await CreateAsync(maker, "MOSTOVIAN%20INTERVENTION.Tanoa");

        var m = await Mission(maker, id);
        Assert.Equal(("MOSTOVIAN%20INTERVENTION", "Tanoa"), (m.GetProperty("className").GetString(), m.GetProperty("map").GetString()));
    }

    [Fact]
    public async Task Duplicate_mission_names_are_refused()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        await CreateAsync(maker, "co10_Escape.Altis");

        var again = await UploadAsync(maker, "CO10_ESCAPE.altis.pbo", Pbo(Files()));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("exists", await CodeAsync(again));
    }

    [Fact]
    public async Task Hostile_uploads_are_refused()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var slip = Files(); slip["../../evil.sqf"] = Utf8("x");
        var bombEntries = new Dictionary<string, byte[]>(Files());
        for (var i = 0; i < 5001; i++) bombEntries[$"f/{i}.txt"] = [];
        var twoSqm = Files(); twoSqm["sub/mission.sqm"] = Utf8("version=54;");
        var noSqm = Files(); noSqm.Remove("mission.sqm");
        var badSqm = Files("addons[]={\"a\"");
        using var bigZip = new MemoryStream();
        using (var zip = new ZipArchive(bigZip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (p, d) in Files()) { using var e = zip.CreateEntry("big_Op.Altis/" + p).Open(); e.Write(d); }
            using var huge = zip.CreateEntry("big_Op.Altis/huge.bin", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 520; i++) huge.Write(zeros); // 520 MB of zeros, a few hundred KB zipped
        }

        var cases = new (string Name, HttpResponseMessage Response)[]
        {
            ("zip slip", await UploadAsync(maker, "slip_Op.Altis.zip", Zip(slip, null))),
            ("entry count", await UploadAsync(maker, "bomb_Op.Altis.zip", Zip(bombEntries, null))),
            ("unpacked size", await UploadAsync(maker, "big.zip", bigZip.ToArray())),
            ("two mission.sqm", await UploadAsync(maker, "two_Op.Altis.zip", Zip(twoSqm, null))),
            ("no mission.sqm", await UploadAsync(maker, "none_Op.Altis.pbo", Pbo(noSqm))),
            ("unreadable mission.sqm", await UploadAsync(maker, "bad_Op.Altis.pbo", Pbo(badSqm))),
            ("not Name.Map", await UploadAsync(maker, "nomap.pbo", Pbo(Files()))),
            ("wrong extension", await UploadAsync(maker, "co10_Op.Altis.rar", [1, 2, 3])),
            ("not a pbo", await UploadAsync(maker, "co10_Op.Altis.pbo", Utf8("hello"))),
            ("reserved name", await UploadAsync(maker, "CON.Altis.pbo", Pbo(Files()))),
        };

        foreach (var (name, r) in cases)
        {
            Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{name}: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            Assert.Equal("file", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        }
        using var scope = f.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<AppDb>().Missions);
    }

    [Fact]
    public async Task Upload_to_an_existing_mission_saves_the_next_version_and_keeps_old_files_as_revisions()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var id = await CreateAsync(maker);
        var next = Files();
        next.Remove("scripts/init.sqf");
        next["description.ext"] = Utf8("onLoadName = \"Escape v2\";");

        var r = await UploadAsync(maker, "co10_Escape.Altis.pbo", Pbo(next), id);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.DoesNotContain(await db.MissionWorkingFiles.Select(w => w.Path).ToListAsync(), p => p == "scripts/init.sqf");
        Assert.Contains(await db.MissionFileRevisions.Where(x => x.Path == "scripts/init.sqf").ToListAsync(), x => x.Sha256 == null); // the deletion is recorded
        Assert.Equal(2, await db.MissionFileRevisions.CountAsync(x => x.Path == "description.ext"));
        Assert.Equal(2, await db.MissionVersions.CountAsync()); // version 1 is untouched
    }

    [Fact]
    public async Task Uploads_need_missions_create_and_edit_rights()
    {
        using var f = new PanelFactory();
        var maker = await TestUsers.LoginAsync(f, "S3");
        var other = await TestUsers.LoginAsync(f, "S3");
        var zeus = await TestUsers.LoginAsync(f, "S7"); // no missions.create
        var id = await CreateAsync(maker);

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(zeus, "z_Op.Altis.pbo", Pbo(Files()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(other, "co10_Escape.Altis.pbo", Pbo(Files()), id)).StatusCode);
    }
}
