using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Tests;

public class ServerPresetTests
{
    // Built around the unit's host: AMD EPYC 4344P (8 cores / 16 threads), 64 GB, Windows Server 2025, 1 Gbit/s uplink.
    private static readonly string[] BuiltIns = ["Default", "Performance", "Performance Enhanced", "Maximum"];

    private static async Task<string?> Field(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("field", out var f) ? f.GetString() : null;

    private static ProfileConfig With(ServerPresetDef p) =>
        new(ProfileSettings.Empty with { Basic = p.Basic.ToDictionary(), Launch = p.Launch.ToDictionary() }, "", "", "", [], Secrets.None);

    [Fact]
    public void Built_in_presets_set_only_valid_performance_values()
    {
        Assert.Equal(BuiltIns, ConfigCatalog.BuiltInServerPresets.Select(p => p.Name));
        foreach (var p in ConfigCatalog.BuiltInServerPresets)
        {
            Assert.All(p.Basic.Keys, k => Assert.Contains(k, ConfigCatalog.PresetBasicKeys));
            Assert.All(p.Launch.Keys, k => Assert.Contains(k, ConfigCatalog.PresetLaunchKeys));
            Assert.Empty(ConfigFiles.Validate(With(p)));
        }
        Assert.Empty(ConfigCatalog.BuiltInServerPresets[0].Basic); // Default: Arma's own values, nothing written
        Assert.Empty(ConfigCatalog.BuiltInServerPresets[0].Launch);

        // Research report "Arma 3 server performance tuning": above a home link the server is CPU-bound, so message limits
        // stay at Bohemia's 256 and MinBandwidth follows the player count (80 × 0.5 Mbit/s), not the upload.
        var perf = With(ConfigCatalog.BuiltInServerPresets.Single(p => p.Name == "Performance"));
        var basic = ConfigFiles.Generate(perf, SecretMode.Omit).BasicCfg;
        Assert.Contains("MaxMsgSend = 256;", basic);
        Assert.Contains("MinBandwidth = 40000000;", basic);
        Assert.Contains("MaxBandwidth = 1000000000;", basic);
        Assert.Equal(["-loadMissionToMemory"], ConfigFiles.LaunchArguments(perf.Settings));

        // A ladder: Enhanced is Performance plus the FPS limit; Maximum adds the 64 / 1024 pair on top.
        var enhanced = With(ConfigCatalog.BuiltInServerPresets.Single(p => p.Name == "Performance Enhanced"));
        Assert.Equal(basic, ConfigFiles.Generate(enhanced, SecretMode.Omit).BasicCfg);
        Assert.Equal(["-limitFPS=100", "-loadMissionToMemory"], ConfigFiles.LaunchArguments(enhanced.Settings).Order());

        // Arma detects the 8 cores and sizes memory itself; -enableHT tested slower; -hugePages needs a privilege we can't check.
        Assert.All(ConfigCatalog.BuiltInServerPresets, p =>
            Assert.Empty(p.Launch.Keys.Intersect(["-cpuCount", "-exThreads", "-enableHT", "-maxMem", "-hugePages", "-malloc", "-bandwidthAlg"])));

        var max = With(ConfigCatalog.BuiltInServerPresets.Single(p => p.Name == "Maximum"));
        var maxBasic = ConfigFiles.Generate(max, SecretMode.Omit).BasicCfg;
        Assert.Contains("MaxMsgSend = 64;", maxBasic);
        Assert.Contains("MaxSizeNonguaranteed = 1024;", maxBasic);
        Assert.Contains("MinBandwidth = 40000000;", maxBasic);
        Assert.Contains("MaxBandwidth = 1000000000;", maxBasic);
        Assert.Equal(["-limitFPS=100", "-loadMissionToMemory"], ConfigFiles.LaunchArguments(max.Settings).Order());
    }

    [Fact]
    public void Packets_larger_than_maxPacketSize_are_refused()
    {
        // BSCF: "Desync might happen if used MaxSizeGuaranteed/MaxSizeNonguaranteed values over the maxPacketSize."
        static IReadOnlyList<ConfigError> Check(object basic) => ConfigFiles.Validate(new ProfileConfig(
            ProfileSettings.Empty with { Basic = JsonSerializer.SerializeToElement(basic).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) },
            "", "", "", [], Secrets.None));

        Assert.Equal("basic.MaxSizeNonguaranteed", Assert.Single(Check(new { MaxSizeNonguaranteed = 1401 })).Field);
        Assert.Equal("basic.MaxSizeGuaranteed", Assert.Single(Check(new Dictionary<string, int> { ["MaxSizeGuaranteed"] = 1300, ["sockets.maxPacketSize"] = 1200 })).Field);
        Assert.Empty(Check(new Dictionary<string, int> { ["MaxSizeNonguaranteed"] = 1430, ["sockets.maxPacketSize"] = 1430 }));
    }

    [Fact]
    public async Task Profile_viewers_see_the_built_ins_but_cant_add_presets()
    {
        using var f = new PanelFactory();
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        var list = await zeus.GetFromJsonAsync<JsonElement>("/api/server-presets");

        Assert.Equal(BuiltIns, list.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.All(list.EnumerateArray(), p => Assert.True(p.GetProperty("isBuiltIn").GetBoolean()));
        Assert.Equal(64, list[3].GetProperty("basic").GetProperty("MaxMsgSend").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync("/api/server-presets", new { name = "Mine" })).StatusCode);
    }

    [Fact]
    public async Task Own_presets_can_be_saved_changed_and_deleted()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");

        var created = await owner.PostAsJsonAsync("/api/server-presets", new
        {
            name = "Op night", description = "Our 1 Gbit box",
            basic = new { MaxMsgSend = 768, MinBandwidth = 500000000 },
            launch = new Dictionary<string, object> { ["-hugePages"] = true },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var mine = (await owner.GetFromJsonAsync<JsonElement>("/api/server-presets")).EnumerateArray().Last();
        Assert.Equal(("Op night", "Our 1 Gbit box", false), (mine.GetProperty("name").GetString(), mine.GetProperty("description").GetString(), mine.GetProperty("isBuiltIn").GetBoolean()));
        Assert.Equal(768, mine.GetProperty("basic").GetProperty("MaxMsgSend").GetInt32());
        Assert.True(mine.GetProperty("launch").GetProperty("-hugePages").GetBoolean());

        var put = await owner.PutAsJsonAsync($"/api/server-presets/{id}", new { name = "Op night 2", basic = new { MaxMsgSend = 1024 }, launch = new { } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var changed = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Op night 2", changed.GetProperty("name").GetString());
        Assert.Equal(1024, changed.GetProperty("basic").GetProperty("MaxMsgSend").GetInt32());
        Assert.Empty(changed.GetProperty("launch").EnumerateObject());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/server-presets/{id}")).StatusCode);
        Assert.Equal(BuiltIns, (await owner.GetFromJsonAsync<JsonElement>("/api/server-presets")).EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        using var scope = f.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToListAsync();
        Assert.Contains("serverPreset.created", actions);
        Assert.Contains("serverPreset.updated", actions);
        Assert.Contains("serverPreset.deleted", actions);
    }

    [Fact]
    public async Task Presets_hold_only_valid_performance_values()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        async Task<HttpResponseMessage> Post(object body) => await owner.PostAsJsonAsync("/api/server-presets", body);

        // Kick rules and mission options aren't performance settings.
        Assert.Equal("basic.MaxCustomFileSize", await Field(await Post(new { name = "A", basic = new { MaxCustomFileSize = 0 } })));
        Assert.Equal("launch.-autoInit", await Field(await Post(new { name = "A", launch = new Dictionary<string, object> { ["-autoInit"] = true } })));
        Assert.Equal("basic.MaxMsgSend", await Field(await Post(new { name = "A", basic = new { MaxMsgSend = 0 } })));
        Assert.Equal("name", await Field(await Post(new { name = " " })));
        Assert.Equal("description", await Field(await Post(new { name = "A", description = "two\nlines" })));
        var taken = await Post(new { name = "performance" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
    }

    [Fact]
    public async Task Built_in_presets_cant_change()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var id = ConfigCatalog.BuiltInServerPresets[2].Id;

        var put = await owner.PutAsJsonAsync($"/api/server-presets/{id}", new { name = "Faster" });
        var delete = await owner.DeleteAsync($"/api/server-presets/{id}");

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }
}
