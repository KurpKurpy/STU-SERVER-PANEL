using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Backups;

namespace Panel.Tests;

// Phase 7b Task 2: console backups (database, blobs, keys) made by the panel, server backups made by the agent as jobs, pre-update
// backups, retention, restore and the offline console restore command (plan Global Constraints, Rulings 2–3, Review Focus 3).
public class BackupTests
{
    private static async Task<JsonElement> WaitForAsync(HttpClient c, Guid id, Func<JsonElement, bool> until)
    {
        JsonElement row = default;
        await AgentKit.WaitUntilAsync(() =>
        {
            row = c.GetFromJsonAsync<JsonElement[]>("/api/backups").Result!.FirstOrDefault(b => b.GetProperty("id").GetGuid() == id);
            return row.ValueKind == JsonValueKind.Object && until(row);
        }, 30);
        return row;
    }

    private static JobSpec[] Jobs(FakeAgent agent) => agent.Calls.Where(c => c.Method == nameof(IAgentClient.RunJob)).Select(c => (JobSpec)c.Args[0]!).ToArray();

    private static async Task<Guid> SeedAsync(PanelFactory f, Action<Backup> set)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var b = new Backup { Id = Guid.NewGuid(), Status = BackupStatus.Done };
        set(b);
        db.Backups.Add(b);
        await db.SaveChangesAsync();
        return b.Id;
    }

    [Fact]
    public async Task Console_backup_zips_a_consistent_database_blobs_and_keys()
    {
        using var f = new PanelFactory();
        var admin = await TestUsers.LoginAsync(f, "S4", "Quartermaster");
        var data = f.Services.GetRequiredService<PanelPaths>().DataDir;
        Directory.CreateDirectory(Path.Combine(data, "blobs", "ab"));
        File.WriteAllText(Path.Combine(data, "blobs", "ab", "abcdef"), "mission bytes");

        var r = await admin.PostAsJsonAsync("/api/backups", new { type = "console" });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var done = await WaitForAsync(admin, id, b => b.GetProperty("status").GetString() != "Running");
        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Matches(@"^console-\d{8}-\d{6}-manual\.zip$", done.GetProperty("fileName").GetString());

        var bytes = await admin.GetByteArrayAsync($"/api/backups/{id}/download");
        Assert.Equal(done.GetProperty("sizeBytes").GetInt64(), bytes.Length);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "blobs/ab/abcdef");
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("keys/") && e.FullName.EndsWith(".xml"));
        var db = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        zip.GetEntry("console.db")!.ExtractToFile(db);
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var check = c.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", check.ExecuteScalar());
            check.CommandText = "SELECT COUNT(*) FROM AspNetUsers WHERE UserName = 'Quartermaster'";
            Assert.Equal(1L, check.ExecuteScalar());
        }
        File.Delete(db);
    }

    [Fact]
    public async Task Server_backup_runs_as_an_agent_job_and_the_result_fills_in_the_row()
    {
        using var f = new PanelFactory();
        var (_, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var admin = await TestUsers.LoginAsync(f, "S4");

        var r = await admin.PostAsJsonAsync("/api/backups", new { type = "server", agentId });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var spec = Assert.Single(Jobs(agent));
        Assert.Equal((JobKind.ServerBackup, "manual"), (spec.Kind, spec.Label));

        await agent.SendJobFinishedAsync(new JobResult(spec.JobId, true, null, [], Made: new BackupMade("server-20261121-030000-manual.zip", 4, null)));
        var done = await WaitForAsync(admin, id, b => b.GetProperty("status").GetString() == "Done");
        Assert.Equal(("server-20261121-030000-manual.zip", 4L), (done.GetProperty("fileName").GetString(), done.GetProperty("sizeBytes").GetInt64()));

        agent.OnFileRead = (space, path, offset, length) =>
            space.Name == "backups" && path == "server/server-20261121-030000-manual.zip" ? new FileChunk("ZIP!"u8.ToArray()[(int)offset..], 4) : new FileChunk([], 0);
        Assert.Equal("ZIP!"u8.ToArray(), await admin.GetByteArrayAsync($"/api/backups/{id}/download"));
    }

    [Fact]
    public async Task Server_and_mod_updates_take_a_pre_update_backup_first()
    {
        using var f = new PanelFactory();
        var s = await ModLaunchTests.SetUpAsync(f);
        await using var lease = s.Agent;

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync($"/api/installs/{s.InstallId}/steamcmd", new { branch = "public" })).StatusCode);
        Assert.Equal([JobKind.ServerBackup, JobKind.ServerInstall], Jobs(s.Agent).Select(j => j.Kind));
        Assert.Equal("pre-update", Jobs(s.Agent)[0].Label);

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { s.Mods[111] }, mode = "update" })).StatusCode);
        Assert.Equal([JobKind.ServerBackup, JobKind.ServerInstall, JobKind.ServerBackup, JobKind.WorkshopDownload], Jobs(s.Agent).Select(j => j.Kind));

        Assert.Equal(HttpStatusCode.OK, (await s.Owner.PostAsJsonAsync("/api/mods/install", new { modIds = new[] { s.Mods[222] }, mode = "install" })).StatusCode);
        Assert.Equal(JobKind.WorkshopDownload, Jobs(s.Agent)[^1].Kind);
        Assert.Equal(JobKind.WorkshopDownload, Jobs(s.Agent)[^2].Kind); // a first-time install takes no backup (plan Ruling 3)
    }

    [Fact]
    public async Task Retention_keeps_the_newest_and_never_manual_pre_restore_or_the_newest()
    {
        using var f = new PanelFactory();
        var (_, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var start = DateTime.UtcNow.AddDays(-40);
        var n = 0;
        async Task Seed(BackupKind kind, int count)
        {
            for (var i = 0; i < count; i++, n++)
            {
                var at = start.AddHours(n);
                await SeedAsync(f, b => (b.Type, b.Kind, b.AgentId, b.CreatedAt, b.FileName) = (BackupType.Server, kind, agentId, at, $"server-{at:yyyyMMdd-HHmmss}.zip"));
            }
        }
        await Seed(BackupKind.Scheduled, 16);
        await Seed(BackupKind.PreUpdate, 12);
        await Seed(BackupKind.Manual, 3);
        await Seed(BackupKind.PreRestore, 1);

        using (var scope = f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BackupService>().ApplyRetentionAsync();

        using var check = f.Services.CreateScope();
        var rows = await check.ServiceProvider.GetRequiredService<AppDb>().Backups.ToListAsync();
        Assert.Equal((14, 10, 3, 1), (rows.Count(b => b.Kind == BackupKind.Scheduled), rows.Count(b => b.Kind == BackupKind.PreUpdate),
            rows.Count(b => b.Kind == BackupKind.Manual), rows.Count(b => b.Kind == BackupKind.PreRestore)));
        var deleted = agent.Calls.Where(c => c.Method == nameof(IAgentClient.DeleteBackup)).Select(c => (string)c.Args[0]!).Order().ToArray();
        Assert.Equal(4, deleted.Length);
        Assert.Equal($"server-{start:yyyyMMdd-HHmmss}.zip", deleted[0]); // the oldest scheduled went first
    }

    [Fact]
    public async Task Restore_refuses_console_backups_and_records_the_before_restore_backup()
    {
        using var f = new PanelFactory();
        var (_, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        var console = await SeedAsync(f, b => (b.Type, b.Kind, b.FileName) = (BackupType.Console, BackupKind.Manual, "console-20261121-030000-manual.zip"));
        var server = await SeedAsync(f, b => (b.Type, b.Kind, b.AgentId, b.FileName) = (BackupType.Server, BackupKind.Manual, agentId, "server-20261121-030000-manual.zip"));

        var refused = await admin.PostAsJsonAsync($"/api/backups/{console}/restore", new { parts = new[] { "profiles" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Panel.exe restore", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/backups/{server}/restore", new { parts = new[] { "everything" } })).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync($"/api/backups/{server}/restore", new { parts = new[] { "profiles", "configs" } })).StatusCode);
        var spec = Assert.Single(Jobs(agent));
        Assert.Equal((JobKind.ServerRestore, "server-20261121-030000-manual.zip"), (spec.Kind, spec.Backup));
        Assert.Equal(["profiles", "configs"], spec.Parts);

        await agent.SendJobFinishedAsync(new JobResult(spec.JobId, true, null, [], Before: new BackupMade("server-20261121-031000-before-restore.zip", 9, null)));
        await AgentKit.WaitUntilAsync(() =>
        {
            using var scope = f.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<AppDb>().Backups.Any(b => b.Kind == BackupKind.PreRestore && b.FileName == "server-20261121-031000-before-restore.zip");
        });
    }

    private static async Task<Guid> AgentOfAsync(PanelFactory f, Guid installId)
    {
        using var scope = f.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDb>().Installs.FindAsync(installId))!.AgentId;
    }

    // Final review I4: a restore while a server on that agent runs is refused where the person asked, with the exact message.
    [Fact]
    public async Task Restore_is_refused_while_a_server_on_that_agent_runs()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var agentId = await AgentOfAsync(f, w.InstallId);
        await w.Agent.SendStatusAsync(new ProfileStatus(w.ProfileId, ServerState.Online, 4242, DateTime.UtcNow, null, null, null));
        var id = await SeedAsync(f, b => (b.Type, b.Kind, b.AgentId, b.FileName) = (BackupType.Server, BackupKind.Manual, agentId, "server-20261121-030000-manual.zip"));

        var r = await w.Owner.PostAsJsonAsync($"/api/backups/{id}/restore", new { parts = new[] { "profiles" } });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("Stop every server on this agent first", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.DoesNotContain(w.Agent.Calls, c => c.Method == nameof(IAgentClient.RunJob));
    }

    // Final review I4: a restore the agent refuses at once, or that fails later, says so on the Backups page.
    [Fact]
    public async Task A_refused_or_failed_restore_shows_where_the_person_looks()
    {
        using var f = new PanelFactory();
        var (_, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        var id = await SeedAsync(f, b => (b.Type, b.Kind, b.AgentId, b.FileName) = (BackupType.Server, BackupKind.Manual, agentId, "server-20261121-030000-manual.zip"));

        agent.OnRunJob = _ => new CommandResult(false, "The agent is busy shutting down.");
        var refused = await admin.PostAsJsonAsync($"/api/backups/{id}/restore", new { parts = new[] { "configs" } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("busy shutting down", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        agent.OnRunJob = _ => new CommandResult(true, null);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync($"/api/backups/{id}/restore", new { parts = new[] { "configs" } })).StatusCode);
        var spec = Jobs(agent).Last();
        await agent.SendJobFinishedAsync(new JobResult(spec.JobId, false, "Restore stopped after writing profiles: disk full. The before-restore backup server-20261121-031000-before-restore.zip puts it back.", [],
            Before: new BackupMade("server-20261121-031000-before-restore.zip", 9, null)));
        var row = await WaitForAsync(admin, id, b => b.GetProperty("error").GetString()?.StartsWith("Last restore failed") == true);
        Assert.Contains("disk full", row.GetProperty("error").GetString());
        Assert.Contains((await admin.GetFromJsonAsync<JsonElement[]>("/api/backups"))!, b => b.GetProperty("kind").GetString() == "PreRestore");
    }

    // Final review I2: nothing starts a server on an agent while a restore is writing files there.
    [Fact]
    public async Task Start_is_refused_while_a_restore_runs_on_its_agent()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var agentId = await AgentOfAsync(f, w.InstallId);
        var id = await SeedAsync(f, b => (b.Type, b.Kind, b.AgentId, b.FileName) = (BackupType.Server, BackupKind.Manual, agentId, "server-20261121-030000-manual.zip"));
        Assert.Equal(HttpStatusCode.Accepted, (await w.Owner.PostAsJsonAsync($"/api/backups/{id}/restore", new { parts = new[] { "profiles" } })).StatusCode);

        var start = await w.Owner.PostAsync($"/api/servers/{w.ProfileId}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        Assert.Equal("restoring", (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    // Final review I5: a console backup cut off by a restart is closed (so it can be deleted) and its temp files go.
    [Fact]
    public async Task A_console_backup_cut_off_by_a_restart_is_closed_and_its_temp_files_go()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        var id = await SeedAsync(f, b => (b.Type, b.Kind, b.Status) = (BackupType.Console, BackupKind.Scheduled, BackupStatus.Running));
        var console = f.Services.GetRequiredService<ConsoleBackups>();
        Directory.CreateDirectory(console.Folder);
        File.WriteAllText(Path.Combine(console.Folder, "console-20261121-030000-scheduled.zip.tmp"), "half");
        File.WriteAllText(Path.Combine(console.Folder, "console-20261121-030000-scheduled.zip.db.tmp"), "half");

        using (var scope = f.Services.CreateScope()) await console.SweepAsync(scope.ServiceProvider.GetRequiredService<AppDb>());

        using var check = f.Services.CreateScope();
        var row = (await check.ServiceProvider.GetRequiredService<AppDb>().Backups.FindAsync(id))!;
        Assert.Equal((BackupStatus.Failed, "The console restarted while this backup was being made."), (row.Status, row.Error));
        Assert.Empty(Directory.GetFiles(console.Folder, "*.tmp"));
    }

    // Final review I7: the file manager's Backups space doesn't hand out server backup zips without backups.manage.
    [Fact]
    public async Task Server_backup_zips_in_Files_need_backups_manage()
    {
        using var f = new PanelFactory();
        var (_, _, agent, installId) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        await TestUsers.CreateAsync(f, "Member", "Archivist");
        await TestUsers.AddClaimAsync(f, "Archivist", "perm.grant", "files.backups.read");
        var archivist = f.CreateClient();
        Assert.Equal("ok", await AuthTests.Login(archivist, "Archivist"));

        Assert.Equal(HttpStatusCode.Forbidden, (await archivist.GetAsync($"/api/files/download?install={installId}&space=backups&path=server/server-20261121-030000-manual.zip")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await archivist.GetAsync($"/api/files/list?install={installId}&space=backups&path=server")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await archivist.GetAsync($"/api/files/list?install={installId}&space=backups&path=missions")).StatusCode);
    }

    [Fact]
    public async Task Backups_need_backups_manage()
    {
        using var f = new PanelFactory();
        var id = await SeedAsync(f, b => (b.Type, b.Kind, b.FileName) = (BackupType.Console, BackupKind.Manual, "console-20261121-030000-manual.zip"));
        var zeus = await TestUsers.LoginAsync(f, "S3");
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.GetAsync("/api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.GetAsync($"/api/backups/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PostAsJsonAsync("/api/backups", new { type = "console" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await TestUsers.LoginAsync(f, "S4")).GetAsync("/api/backups")).StatusCode);
    }

    [Fact]
    public async Task Settings_show_the_defaults_and_the_agents_and_refuse_bad_counts()
    {
        using var f = new PanelFactory();
        var (_, _, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        var s = await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings");
        Assert.Equal((14, 10, 30), (s.GetProperty("keepScheduled").GetInt32(), s.GetProperty("keepPreUpdate").GetInt32(), s.GetProperty("safetyCopyDays").GetInt32()));
        Assert.Contains(s.GetProperty("agents").EnumerateArray(), a => a.GetProperty("name").GetString() == "Dev PC");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/backups/settings", new { keepScheduled = 0, keepPreUpdate = 10, safetyCopyDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/backups/settings", new { keepScheduled = 7, keepPreUpdate = 5, safetyCopyDays = 0 })).StatusCode);
        Assert.Equal(7, (await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings")).GetProperty("keepScheduled").GetInt32());
    }

    [Fact]
    public async Task Backups_are_audited()
    {
        using var f = new PanelFactory();
        var (_, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var admin = await TestUsers.LoginAsync(f, "S4");
        var server = await SeedAsync(f, b => (b.Type, b.Kind, b.AgentId, b.FileName) = (BackupType.Server, BackupKind.Manual, agentId, "server-20261121-030000-manual.zip"));

        await admin.PostAsJsonAsync("/api/backups", new { type = "server", agentId });
        await admin.PostAsJsonAsync($"/api/backups/{server}/restore", new { parts = new[] { "configs" } });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/backups/{server}")).StatusCode);

        using var scope = f.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDb>().Audit.Select(a => a.Action).ToListAsync();
        Assert.Contains("backup.created", actions);
        Assert.Contains("backup.restored", actions);
        Assert.Contains("backup.deleted", actions);
        Assert.Contains(agent.Calls, c => c.Method == nameof(IAgentClient.DeleteBackup) && (string)c.Args[0]! == "server-20261121-030000-manual.zip");
    }

    [Fact]
    public void Console_restore_command_swaps_in_the_backup_and_keeps_the_old_data_aside()
    {
        var data = Path.Combine(Path.GetTempPath(), "13c-restore-" + Guid.NewGuid().ToString("N"));
        void Put(string root, string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        Put(data, "console.db", "old db");
        Put(data, Path.Combine("blobs", "ab", "old"), "old blob");
        Put(data, Path.Combine("keys", "key-old.xml"), "old key");
        Put(data, "dev-accounts.txt", "untouched");
        var zipPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("console.db", "new db"), ("blobs/cd/new", "new blob"), ("keys/key-new.xml", "new key") })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        }

        Assert.Equal(0, ConsoleRestore.Run(zipPath, data));

        Assert.Equal("new db", File.ReadAllText(Path.Combine(data, "console.db")));
        Assert.True(File.Exists(Path.Combine(data, "blobs", "cd", "new")));
        Assert.False(File.Exists(Path.Combine(data, "blobs", "ab", "old")));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(data, "dev-accounts.txt")));
        var aside = Assert.Single(Directory.GetDirectories(data, "before-restore-*"));
        Assert.Equal("old db", File.ReadAllText(Path.Combine(aside, "console.db")));
        Assert.True(File.Exists(Path.Combine(aside, "keys", "key-old.xml")));

        var empty = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        Assert.Equal(2, ConsoleRestore.Run(empty, data));
    }
}
