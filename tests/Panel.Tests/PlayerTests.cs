using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 7a Task 3: RCon settings at launch, players, bans, kick/ban/unban/say, allowlisted commands and the RCon log (plan Rulings 4, 7–11).
public class PlayerTests
{
    private const string GuidA = "0123456789abcdef0123456789abcdef", GuidB = "fedcba9876543210fedcba9876543210";

    private const string Players = "Players on server:\n[#] [IP Address]:[Port] [Ping] [GUID] [Name]\n--------------------------------------------------\n"
        + "0   203.0.113.7:2304      45   " + GuidA + "(OK) Crow\n"
        + "1   198.51.100.2:2304     120  " + GuidB + "(?) Hawk (Lobby)\n"
        + "(2 players in total)";

    private const string Bans = "GUID Bans:\n[#] [GUID] [Minutes left] [Reason]\n----------------------------------------\n"
        + "0  " + GuidA + " perm Teamkilling\n"
        + "1  " + GuidB + " 42 Spam\n\n"
        + "IP Bans:\n[#] [IP Address] [Minutes left] [Reason]\n----------------------------------------------\n"
        + "2  203.0.113.9     -  Old";

    private static async Task<ServerMissionsTests.World> RunningAsync(PanelFactory f, Func<string, RconReply>? rcon = null)
    {
        var w = await ServerMissionsTests.WorldAsync(f);
        w.Agent.OnRcon = (_, c) => rcon?.Invoke(c) ?? new RconReply(true, c == "players" ? Players : c == "bans" ? Bans : "", null);
        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Online, 4242, DateTime.UtcNow, null, null, null, 0, RconState.Connected));
        return w;
    }

    private static string[] RconCommands(ServerMissionsTests.World w) =>
        w.Agent.Calls.Where(c => c.Method == nameof(IAgentClient.Rcon)).Select(c => (string)c.Args[1]!).ToArray();

    private static LaunchSpec LastStart(ServerMissionsTests.World w) =>
        (LaunchSpec)w.Agent.Calls.Last(c => c.Method == nameof(IAgentClient.Start)).Args[0]!;

    private static async Task ApplyBattlEyeAsync(ServerMissionsTests.World w, bool on)
    {
        var p = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}");
        var snapshot = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(p.GetProperty("snapshot").GetRawText())!;
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(snapshot["settings"].GetRawText())!;
        var server = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings["server"].GetRawText())!;
        server["BattlEye"] = JsonSerializer.SerializeToElement(on);
        settings["server"] = JsonSerializer.SerializeToElement(server);
        snapshot["settings"] = JsonSerializer.SerializeToElement(settings);
        var put = await w.Owner.PutAsJsonAsync($"/api/profiles/{w.ProfileId}/config", new { baseRevision = p.GetProperty("revision").GetInt32(), snapshot });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var head = (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}")).GetProperty("revision").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/profiles/{w.ProfileId}/apply", new { revision = head })).StatusCode);
    }

    [Fact]
    public async Task Start_sends_an_rcon_spec_only_with_BattlEye_on_and_generates_the_password_once()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        Assert.Equal(HttpStatusCode.Accepted, (await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null)).StatusCode);
        var first = LastStart(w).Rcon;
        Assert.NotNull(first);
        Assert.Equal(LastStart(w).Port + 10, first.Port);
        Assert.Matches("^[A-Za-z0-9]{24}$", first.Password);

        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Offline, null, null, null, null, 0));
        Assert.Equal(HttpStatusCode.Accepted, (await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null)).StatusCode);
        Assert.Equal(first.Password, LastStart(w).Rcon!.Password);

        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Offline, null, null, null, null, 0));
        await ApplyBattlEyeAsync(w, on: false);
        Assert.Equal(HttpStatusCode.Accepted, (await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null)).StatusCode);
        Assert.Null(LastStart(w).Rcon);
    }

    [Fact]
    public async Task The_password_never_appears_in_responses_or_audit_and_a_new_one_replaces_it()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null);
        var password = LastStart(w).Rcon!.Password;

        var renew = await w.Owner.PostAsync($"/api/profiles/{w.ProfileId}/rcon-password", null);
        Assert.Equal(HttpStatusCode.OK, renew.StatusCode);
        Assert.DoesNotContain(password, await renew.Content.ReadAsStringAsync());
        foreach (var url in new[] { $"/api/profiles/{w.ProfileId}", "/api/servers", $"/api/servers/{w.ProfileId}/players" })
            Assert.DoesNotContain(password, await w.Owner.GetStringAsync(url));

        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Offline, null, null, null, null, 0));
        await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null);
        var next = LastStart(w).Rcon!.Password;
        Assert.NotEqual(password, next);

        using var scope = f.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.ToListAsync();
        Assert.Contains(audit, a => a.Action == "profile.rcon_password");
        Assert.All(audit, a => Assert.DoesNotContain(password, a.Summary + a.DetailsJson));
        Assert.All(audit, a => Assert.DoesNotContain(next, a.Summary + a.DetailsJson));

        var member = await TestUsers.LoginAsync(f, "Member", "Pvt");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/api/profiles/{w.ProfileId}/rcon-password", null)).StatusCode);
    }

    [Fact]
    public async Task A_profile_whose_ports_hit_another_profiles_rcon_port_is_refused()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var existing = (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}")).GetProperty("port").GetInt32();
        var install = (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}")).GetProperty("serverInstallId").GetGuid();
        Task<HttpResponseMessage> Create(int port, string slug) => w.Owner.PostAsJsonAsync("/api/profiles",
            new { serverInstallId = install, name = slug, slug, executable = "arma3server_x64.exe", port, publicAddress = "127.0.0.1" });

        Assert.Equal(HttpStatusCode.Conflict, (await Create(existing + 8, "over-rcon")).StatusCode);   // its game ports cover existing + 10
        Assert.Equal(HttpStatusCode.Conflict, (await Create(existing - 10, "rcon-on-game")).StatusCode); // its RCon port is existing's game port
        Assert.Equal(HttpStatusCode.Created, (await Create(existing + 20, "clear")).StatusCode);
    }

    [Fact]
    public async Task Players_hides_GUID_and_IP_without_players_manage_and_says_when_RCon_is_unavailable()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;

        var owner = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/players");
        Assert.Equal("Connected", owner.GetProperty("rcon").GetString());
        var rows = owner.GetProperty("players").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal((0, "Crow", 45, GuidA, true, "203.0.113.7", false), (rows[0].GetProperty("number").GetInt32(), rows[0].GetProperty("name").GetString(),
            rows[0].GetProperty("ping").GetInt32(), rows[0].GetProperty("guid").GetString(), rows[0].GetProperty("verified").GetBoolean(),
            rows[0].GetProperty("ip").GetString(), rows[0].GetProperty("lobby").GetBoolean()));
        Assert.True(rows[1].GetProperty("lobby").GetBoolean());

        var member = await TestUsers.LoginAsync(f, "Member", "Pvt");
        var seen = await member.GetStringAsync($"/api/servers/{w.ProfileId}/players");
        Assert.Contains("Crow", seen);
        Assert.DoesNotContain(GuidA, seen);
        Assert.DoesNotContain("203.0.113.7", seen);

        w.Agent.OnRcon = (_, _) => new RconReply(false, null, "RCon isn't connected yet.");
        var down = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/players");
        Assert.Empty(down.GetProperty("players").EnumerateArray());
        Assert.Contains("isn't connected", down.GetProperty("unavailable").GetString());
    }

    [Fact]
    public async Task Kick_and_ban_recheck_the_GUID_clean_the_reason_and_refuse_a_changed_player()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;

        var changed = await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/players/kick", new { number = 0, guid = GuidB, reason = "x" });
        Assert.Equal((HttpStatusCode.Conflict, "changed"), (changed.StatusCode, await MissionKit.CodeAsync(changed)));
        Assert.DoesNotContain(RconCommands(w), c => c.StartsWith("kick"));

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/players/kick",
            new { number = 0, guid = GuidA, reason = "spam\r\nkick 1\u0007" + new string('x', 300) })).StatusCode);
        var kick = RconCommands(w).Last(c => c.StartsWith("kick"));
        Assert.StartsWith("kick 0 spam kick 1", kick);
        Assert.DoesNotContain('\n', kick);
        Assert.True(kick.Length <= "kick 0 ".Length + 120);

        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/players/ban",
            new { number = 1, guid = GuidB, minutes = 60, reason = "Teamkilling" })).StatusCode);
        Assert.Equal("ban 1 60 Teamkilling", RconCommands(w).Last(c => c.StartsWith("ban")));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/players/ban",
            new { number = 1, guid = GuidB, minutes = -5, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/players/kick",
            new { number = 0, guid = "not-a-guid", reason = "x" })).StatusCode);

        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        Assert.Equal(HttpStatusCode.OK, (await zeus.PostAsJsonAsync($"/api/servers/{w.ProfileId}/say", new { message = "Formation on me" })).StatusCode);
        Assert.Equal("say -1 Formation on me", RconCommands(w).Last(c => c.StartsWith("say")));
        var member = await TestUsers.LoginAsync(f, "Member", "Pvt");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/servers/{w.ProfileId}/players/kick", new { number = 0, guid = GuidA, reason = "x" })).StatusCode);

        using var scope = f.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToListAsync();
        Assert.Contains("players.kick", audit);
        Assert.Contains("players.ban", audit);
        Assert.Contains("players.say", audit);
    }

    // Final review I2: two unbans at once both re-checked first, then the second removed whoever had shifted into its number.
    [Fact]
    public async Task Two_unbans_at_once_never_remove_a_ban_nobody_asked_for()
    {
        const string GuidC = "00112233445566778899aabbccddeeff";
        using var f = new PanelFactory();
        var list = new List<string> { GuidA, GuidB, GuidC };
        int listings = 0, removals = 0;
        var w = await RunningAsync(f, c =>
        {
            if (c == "bans")
            {
                string text;
                lock (list) text = "GUID Bans:\n[#] [GUID] [Minutes left] [Reason]\n----------------------------------------\n"
                    + string.Join("\n", list.Select((g, i) => $"{i}  {g} perm Reason"));
                Interlocked.Increment(ref listings);
                Thread.Sleep(150); // the second request reads the list before the first one's removal lands
                return new RconReply(true, text, null);
            }
            if (c.StartsWith("removeBan "))
            {
                var n = int.Parse(c["removeBan ".Length..]);
                if (n > 0) SpinWait.SpinUntil(() => Volatile.Read(ref removals) > 0, 2000); // the removals land in request order
                lock (list) list.RemoveAt(n);
                Interlocked.Increment(ref removals);
            }
            return new RconReply(true, "", null);
        });
        await using var lease = w.Agent;

        var first = w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/bans/remove", new { number = 0, target = GuidA });
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref listings) > 0, 5000));
        var second = w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/bans/remove", new { number = 1, target = GuidB });
        var codes = await Task.WhenAll(first, second);

        lock (list) Assert.Equal([GuidB, GuidC], list); // A removed as asked; B's request found B had moved, so C is untouched
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], codes.Select(r => r.StatusCode).Order());
    }

    // Final review I3: players, bans and admins print every IP and GUID, which only players.manage may see.
    [Fact]
    public async Task The_command_box_also_needs_players_manage()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;
        await TestUsers.CreateAsync(f, "Member", "Ops");
        await TestUsers.AddClaimAsync(f, "Ops", "perm.grant", "server.console");
        var ops = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(ops, "Ops"));

        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync($"/api/servers/{w.ProfileId}/rcon", new { command = "players" })).StatusCode);
        Assert.DoesNotContain("players", RconCommands(w));
    }

    // Final review I4: "on for" counts from when this run of the server first listed them, and starts again after they leave.
    [Fact]
    public async Task Time_on_starts_again_when_a_player_rejoins_or_the_server_restarts()
    {
        using var f = new PanelFactory();
        var crowOn = true;
        var withoutCrow = string.Join("\n", Players.Split('\n').Where(l => !l.Contains(GuidA)));
        var w = await RunningAsync(f, c => new RconReply(true, c == "players" ? (crowOn ? Players + "\n2   192.0.2.5:2304   80   - Newbie" : withoutCrow) : "", null));
        await using var lease = w.Agent;
        async Task<Dictionary<string, JsonElement>> SeenAsync() =>
            (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/players")).GetProperty("players").EnumerateArray()
                .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("firstSeen"));

        var first = await SeenAsync();
        Assert.Equal(JsonValueKind.Null, first["Newbie"].ValueKind); // no GUID yet: unknown, not shared with every other GUID-less player
        await Task.Delay(30);
        Assert.Equal(first["Hawk"].GetDateTime(), (await SeenAsync())["Hawk"].GetDateTime());

        crowOn = false;
        await SeenAsync();
        crowOn = true;
        await Task.Delay(30);
        var back = await SeenAsync();
        Assert.True(back["Crow"].GetDateTime() > first["Crow"].GetDateTime());
        Assert.Equal(first["Hawk"].GetDateTime(), back["Hawk"].GetDateTime());

        await Task.Delay(30);
        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Online, 4343, DateTime.UtcNow, null, null, null, 0, RconState.Connected));
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((await SeenAsync())["Hawk"].GetDateTime() == first["Hawk"].GetDateTime())
        {
            Assert.True(DateTime.UtcNow < until, "Hawk's time on didn't start again after the server restarted");
            await Task.Delay(50);
        }
    }

    // Final review I5: profiles saved before 7a can already sit on each other's RCon port (game port + 10); Start says so.
    [Fact]
    public async Task Start_refuses_a_profile_saved_before_7a_whose_ports_hit_another_profiles_rcon_port()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;
        var p = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/profiles/{w.ProfileId}");
        var port = p.GetProperty("port").GetInt32();
        var made = await w.Owner.PostAsJsonAsync("/api/profiles", new { serverInstallId = p.GetProperty("serverInstallId").GetGuid(), name = "Second",
            slug = "second", executable = "arma3server_x64.exe", port = port + 100, publicAddress = "127.0.0.1" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await db.Profiles.SingleAsync(x => x.Slug == "second")).Port = port + 10; // what a 10-spaced pair saved before 7a looks like
            await db.SaveChangesAsync();
        }
        var starts = w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.Start));

        var r = await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("port_conflict", body.GetProperty("code").GetString());
        Assert.Contains("Second", body.GetProperty("message").GetString());
        Assert.Equal(starts, w.Agent.Calls.Count(c => c.Method == nameof(IAgentClient.Start)));
    }

    [Fact]
    public async Task Bans_list_and_unban_recheck_the_ban_number_by_GUID()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;

        var bans = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/bans");
        var rows = bans.GetProperty("bans").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal((0, GuidA, "perm", "Teamkilling"), (rows[0].GetProperty("number").GetInt32(), rows[0].GetProperty("target").GetString(),
            rows[0].GetProperty("minutesLeft").GetString(), rows[0].GetProperty("reason").GetString()));

        var wrong = await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/bans/remove", new { number = 0, target = GuidB });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/bans/remove", new { number = 1, target = GuidB })).StatusCode);
        Assert.Equal("removeBan 1", RconCommands(w).Last(c => c.StartsWith("removeBan")));

        var member = await TestUsers.LoginAsync(f, "Member", "Pvt");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/servers/{w.ProfileId}/bans")).StatusCode);
    }

    [Fact]
    public async Task Raw_commands_are_allowlisted_and_need_server_console()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;

        var ok = await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/rcon", new { command = "players" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("Players on server", (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
        foreach (var bad in new[] { "RConPassword x", "loadScripts", "MaxPing 1", "#shutdown", "players\nRConPassword x", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/rcon", new { command = bad })).StatusCode);
        Assert.DoesNotContain(RconCommands(w), c => c.Contains("RConPassword") || c.Contains("loadScripts"));

        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync($"/api/servers/{w.ProfileId}/rcon", new { command = "players" })).StatusCode);
    }

    [Fact]
    public async Task The_rcon_log_keeps_500_lines_records_actions_and_needs_players_manage()
    {
        using var f = new PanelFactory();
        var w = await RunningAsync(f);
        await using var lease = w.Agent;

        for (var i = 0; i < 510; i++) await w.Agent.SendRconMessageAsync(w.ProfileId, $"line {i}");
        await w.Owner.PostAsJsonAsync($"/api/servers/{w.ProfileId}/say", new { message = "hello" });

        var log = (await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/rcon/log")).EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(500, log.Length);
        Assert.DoesNotContain("line 0", log);
        Assert.Contains(log, l => l.Contains("said") && l.Contains("hello"));

        var member = await TestUsers.LoginAsync(f, "Member", "Pvt");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync($"/api/servers/{w.ProfileId}/rcon/log")).StatusCode);
    }
}
