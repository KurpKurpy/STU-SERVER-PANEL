using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;
using static Panel.Tests.MissionKit;

namespace Panel.Tests;

// Phase 5b: which of a server's missions a mod list can run (Rulings 7–9), through GET /api/servers/{id}/missions.
public class ServerMissionsTests
{
    internal sealed record World(HttpClient Owner, FakeAgent Agent, Guid InstallId, Guid ProfileId, Dictionary<long, Guid> Mods,
        Guid FullList, Guid CbaOnly, Dictionary<string, Guid> Missions);

    internal static string SqmWith(params string[] addons) =>
        $"version=54;\naddons[]={{{string.Join(",", addons.Select(a => $"\"{a}\""))}}};\nclass Mission\n{{\n\tclass Entities\n\t{{\n\t\titems=0;\n\t}};\n}};\n";

    // An install with base addons (and a Creator DLC folder), five scanned mods, two mod lists, an applied profile using the full list,
    // and missions deployed (Done) to the install.
    internal static async Task<World> WorldAsync(PanelFactory f, Func<string, bool>? deployDone = null)
    {
        var (owner, _, agent, installId) = await AgentKit.InstallAsync(f);
        await ModTests.PasteAsync(owner, "111\n222\n333\n444\n555");
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            foreach (var m in await db.Mods.ToListAsync()) (m.Status, m.InstalledAt) = (ModStatus.Installed, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        var mods = (await owner.GetFromJsonAsync<JsonElement[]>("/api/mods"))!.ToDictionary(m => m.GetProperty("workshopId").GetInt64(), m => m.GetProperty("id").GetGuid());
        await agent.SendAddonsScannedAsync(new AddonScan(null, AgentKit.InstallPath,
            [new ScannedFolder("", ["A3_Characters_F", "A3_Core"], ["Altis", "VR"], []), new ScannedFolder("ws", ["ws_core"], ["Sefrou_Ramal"], [])], null));
        foreach (var (id, addon) in new[] { (111L, "cba_main"), (222L, "ace_realisticnames"), (333L, "hc_only_addon"), (444L, "optional_addon"), (555L, "disabled_addon") })
            await agent.SendAddonsScannedAsync(new AddonScan(new ModRef(id, null), null, [new ScannedFolder("", [addon], [], [])], null));

        async Task<Guid> List(string name, object[] items)
        {
            var r = await owner.PostAsJsonAsync("/api/collections", new { name, items });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var full = await List("Full", [
            new { modId = mods[111], client = true, enabled = true },
            new { modId = mods[222], serverOnly = true, enabled = true },
            new { modId = mods[333], headless = true, enabled = true },
            new { modId = mods[444], optional = true, enabled = true },
            new { modId = mods[555], client = true, enabled = false },
        ]);
        var cbaOnly = await List("CBA only", [new { modId = mods[111], client = true, enabled = true }]);
        var profile = await AgentKit.CreateProfileAsync(owner, AgentKit.Profile(installId), applied: false);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.EditConfigAsync(owner, profile, x => x["modCollectionId"] = full.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AgentKit.ApplyAsync(owner, profile)).StatusCode);

        agent.OnDeployMission = spec => new DeployResult(spec.DeploymentId, deployDone?.Invoke(spec.FileName) == false ? DeployResult.Accepted : DeployResult.Done, null, null);
        var missions = new Dictionary<string, Guid>();
        foreach (var (name, addons) in new (string, string[])[]
                 {
                     ("ace_op.Altis", ["A3_Characters_F", "cba_main", "ace_realisticnames"]),
                     ("cba_op.VR", ["A3_Characters_F", "cba_main"]),
                     ("hc_op.Altis", ["hc_only_addon"]),
                     ("opt_op.Altis", ["optional_addon"]),
                     ("dis_op.Altis", ["disabled_addon"]),
                     ("ws_op.Altis", ["ws_core"]),
                     ("tanoa_op.Tanoa", ["A3_Core"]),
                 })
            missions[name] = await CreateAsync(owner, name, Files(SqmWith(addons)));
        return new World(owner, agent, installId, profile, mods, full, cbaOnly, missions);
    }

    internal static async Task<Dictionary<string, JsonElement>> ListedAsync(HttpClient c, Guid profileId, Guid? listId = null)
    {
        var body = await c.GetFromJsonAsync<JsonElement>($"/api/servers/{profileId}/missions" + (listId is { } l ? $"?modListId={l}" : ""));
        return body.GetProperty("missions").EnumerateArray().ToDictionary(m => m.GetProperty("fileName").GetString()!, m => m);
    }

    private static string Status(Dictionary<string, JsonElement> listed, string file) => listed[file].GetProperty("status").GetString()!;

    [Fact]
    public async Task A_mission_using_a_mod_is_listed_only_for_lists_with_it()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;

        var full = await ListedAsync(w.Owner, w.ProfileId, w.FullList);
        var cba = await ListedAsync(w.Owner, w.ProfileId, w.CbaOnly);

        Assert.Equal("Ready", Status(full, "ace_op.Altis.pbo"));
        Assert.Equal("Missing", Status(cba, "ace_op.Altis.pbo"));
        var missing = Assert.Single(cba["ace_op.Altis.pbo"].GetProperty("missing").EnumerateArray());
        Assert.Equal(("ace_realisticnames", "Mod 222"), (missing.GetProperty("addon").GetString(), missing.GetProperty("libraryMod").GetString()));
        Assert.Equal("Ready", Status(full, "cba_op.VR.pbo"));
        Assert.Equal("Ready", Status(cba, "cba_op.VR.pbo"));
    }

    [Fact]
    public async Task Optional_and_headless_only_and_disabled_items_dont_provide()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;

        var full = await ListedAsync(w.Owner, w.ProfileId, w.FullList);

        Assert.Equal("Missing", Status(full, "hc_op.Altis.pbo"));
        Assert.Equal("Missing", Status(full, "opt_op.Altis.pbo"));
        Assert.Equal("Missing", Status(full, "dis_op.Altis.pbo"));
    }

    [Fact]
    public async Task Creator_dlc_folders_dont_provide_and_a_missing_map_is_named()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;

        var full = await ListedAsync(w.Owner, w.ProfileId, w.FullList);

        Assert.Equal("Missing", Status(full, "ws_op.Altis.pbo"));
        Assert.Equal(("Missing", "Tanoa"), (Status(full, "tanoa_op.Tanoa.pbo"), full["tanoa_op.Tanoa.pbo"].GetProperty("missingMap").GetString()));
    }

    [Fact]
    public async Task Server_missions_lists_only_done_missions_on_this_install_with_the_deployed_version()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f, deployDone: file => file != "hc_op.Altis.pbo");
        await using var lease = w.Agent;
        // v2 of cba_op needs ACE, but its deployment stays Pending: the server still has v1.
        w.Agent.OnDeployMission = spec => new DeployResult(spec.DeploymentId, DeployResult.Accepted, null, null);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(w.Owner, "cba_op.VR.pbo", Pbo(Files(SqmWith("cba_main", "ace_realisticnames"))), w.Missions["cba_op.VR"])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsync($"/api/missions/{w.Missions["dis_op.Altis"]}/archive", null)).StatusCode);

        var cba = await ListedAsync(w.Owner, w.ProfileId, w.CbaOnly);

        Assert.DoesNotContain("hc_op.Altis.pbo", cba.Keys);   // never Done on this install
        Assert.DoesNotContain("dis_op.Altis.pbo", cba.Keys);  // archived
        Assert.Equal(("Ready", 1), (Status(cba, "cba_op.VR.pbo"), cba["cba_op.VR.pbo"].GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task The_applied_list_is_the_default_and_unreadable_mods_make_unattributed_addons_unknown()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;
        await w.Agent.SendAddonsScannedAsync(new AddonScan(new ModRef(222, null), null, [new ScannedFolder("", ["ace_realisticnames"], [], ["x.pbo: text config: macro class names"])], null));
        var mystery = await CreateAsync(w.Owner, "mystery_op.Altis", Files(SqmWith("ace_mystery")));

        var body = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/missions");

        Assert.Equal(w.FullList, body.GetProperty("modListId").GetGuid());
        Assert.Equal(w.FullList, body.GetProperty("appliedModListId").GetGuid());
        Assert.Equal(["CBA only", "Full"], body.GetProperty("modLists").EnumerateArray().Select(l => l.GetProperty("name").GetString()).Order());
        var row = body.GetProperty("missions").EnumerateArray().Single(m => m.GetProperty("missionId").GetGuid() == mystery);
        Assert.Equal("Unknown", row.GetProperty("status").GetString());
        Assert.Equal(["Mod 222"], row.GetProperty("unreadableMods").EnumerateArray().Select(x => x.GetString()));
    }

    private static async Task SetModAsync(PanelFactory f, World w, long workshopId, Action<Mod> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        change(await db.Mods.SingleAsync(m => m.WorkshopId == workshopId));
        await db.SaveChangesAsync();
    }

    // Final review I-2: a list mod that isn't installed doesn't provide (it read as "not scanned", so gaps were Unknown) and is named.
    [Fact]
    public async Task A_list_mod_that_isnt_installed_doesnt_provide_and_is_named()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;
        await SetModAsync(f, w, 222, m => m.Status = ModStatus.Missing); // its files are gone; its old rows remain

        var body = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/missions?modListId={w.FullList}");

        Assert.Equal(["Mod 222"], body.GetProperty("notInstalled").EnumerateArray().Select(x => x.GetString()));
        var ace = body.GetProperty("missions").EnumerateArray().Single(m => m.GetProperty("fileName").GetString() == "ace_op.Altis.pbo");
        Assert.Equal("Missing", ace.GetProperty("status").GetString());
    }

    // Final review I-2: after a job marks a mod for a rescan, its old rows must not make a mission Ready.
    [Fact]
    public async Task Rows_of_a_mod_or_install_waiting_for_a_rescan_dont_count()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;
        await SetModAsync(f, w, 222, m => m.ScanStatus = ScanStatus.NotScanned);

        var full = await ListedAsync(w.Owner, w.ProfileId, w.FullList);
        Assert.Equal("Unknown", Status(full, "ace_op.Altis.pbo"));
        Assert.Equal(["Mod 222"], full["ace_op.Altis.pbo"].GetProperty("unreadableMods").EnumerateArray().Select(x => x.GetString()));

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Installs.SingleAsync()).ScanStatus = ScanStatus.NotScanned;
            await db.SaveChangesAsync();
        }
        var cba = await ListedAsync(w.Owner, w.ProfileId, w.CbaOnly);
        Assert.Equal("Unknown", Status(cba, "cba_op.VR.pbo")); // A3_Characters_F and VR wait for the install's scan
    }

    [Fact]
    public async Task Mission_compatibility_lists_every_mod_list_per_install()
    {
        using var f = new PanelFactory();
        var w = await WorldAsync(f);
        await using var lease = w.Agent;

        var rows = await w.Owner.GetFromJsonAsync<JsonElement[]>($"/api/missions/{w.Missions["ace_op.Altis"]}/compatibility");

        var install = Assert.Single(rows!);
        Assert.Equal("Arma 3", install.GetProperty("installName").GetString());
        var lists = install.GetProperty("lists").EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("status").GetString());
        Assert.Equal("Ready", lists["Full"]);
        Assert.Equal("Missing", lists["CBA only"]);
    }
}
