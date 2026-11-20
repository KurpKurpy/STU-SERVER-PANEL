using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Data;

namespace Panel.Tests;

// Phase 4b Task 3: the file manager's panel side — per-space permissions, transfers, text, downloads, owners and audit.
public class FileEndpointsTests
{
    private static string Q(Guid install, string space, string path = "") =>
        $"install={install}&space={space}&path={Uri.EscapeDataString(path)}";

    private static Task<HttpResponseMessage> UploadAsync(HttpClient c, Guid install, string space, string folder, string name, byte[] data, bool overwrite = false)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(data), "file", name } };
        return c.PostAsync($"/api/files/upload?{Q(install, space, folder)}&overwrite={overwrite}", form);
    }

    private static Task<HttpResponseMessage> ActionAsync(HttpClient c, string action, Guid install, string space, string path, string? target = null) =>
        c.PostAsJsonAsync($"/api/files/{action}", new { install, space, path, target });

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static async Task<T> WithDbAsync<T>(PanelFactory f, Func<AppDb, Task<T>> read)
    {
        using var scope = f.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    [Fact]
    public async Task Each_space_needs_its_read_permission()
    {
        using var f = new PanelFactory();
        var (_, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.OK, (await zeus.GetAsync($"/api/files/list?{Q(install, "mpmissions")}")).StatusCode);
        foreach (var space in new[] { "keys", "configs", "profiles", "backups", "localmods", "workshop" })
            Assert.Equal(HttpStatusCode.Forbidden, (await zeus.GetAsync($"/api/files/list?{Q(install, space)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.GetAsync($"/api/files/download?{Q(install, "configs", @"main\server.cfg")}")).StatusCode);
    }

    [Fact]
    public async Task Writes_need_the_write_permission()
    {
        using var f = new PanelFactory();
        var (_, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        await TestUsers.AddClaimAsync(f, "Hawk", "perm.deny", "files.mpmissions.write"); // reads the folder, can't write it
        var maker = await TestUsers.LoginAsync(f, "S3", "Quill");

        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(zeus, install, "mpmissions", "", "x.pbo", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActionAsync(zeus, "folder", install, "mpmissions", "new")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(maker, install, "mpmissions", "", "x.pbo", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActionAsync(maker, "folder", install, "keys", "new")).StatusCode);
        Assert.DoesNotContain(agent.Calls, c => c.Method == "FileChange" && ((FileChangeSpec)c.Args[0]!).Space.Name == "keys");
    }

    [Fact]
    public async Task An_unknown_space_is_404()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/files/list?{Q(install, "install")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/files/list?{Q(Guid.NewGuid(), "mpmissions")}")).StatusCode);
    }

    [Fact]
    public async Task Spaces_lists_only_what_the_person_can_read()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");
        await TestUsers.AddClaimAsync(f, "Hawk", "perm.deny", "files.mpmissions.write"); // reads the folder, can't write it

        var mine = await zeus.GetFromJsonAsync<JsonElement>("/api/files/spaces");
        var space = Assert.Single(mine.GetProperty("spaces").EnumerateArray());
        Assert.Equal(("mpmissions", false), (space.GetProperty("name").GetString(), space.GetProperty("canWrite").GetBoolean()));
        Assert.Equal(install, Assert.Single(mine.GetProperty("installs").EnumerateArray()).GetProperty("id").GetGuid());

        var all = (await owner.GetFromJsonAsync<JsonElement>("/api/files/spaces")).GetProperty("spaces").EnumerateArray().ToArray();
        Assert.Equal(7, all.Length);
        Assert.True(all.Single(s => s.GetProperty("name").GetString() == "configs").GetProperty("hasPasswords").GetBoolean());
    }

    [Fact]
    public async Task Upload_stores_a_transfer_the_agent_can_pull_once_and_it_is_deleted_after()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        agent.ReportFileChanges = false;
        var data = Encoding.UTF8.GetBytes("BEServer settings");

        var upload = UploadAsync(owner, install, "profiles", @"main\BattlEye", "bans.txt", data);
        await AgentKit.WaitUntilAsync(() => agent.Calls.Any(c => c.Method == "FileChange"));
        var spec = (FileChangeSpec)agent.Calls.Single(c => c.Method == "FileChange").Args[0]!;
        Assert.Equal((FileOps.Write, @"main\BattlEye\bans.txt", (long)data.Length, Sha(data), false), (spec.Op, spec.Path, spec.Size, spec.Sha256, spec.Overwrite));
        var pulled = await agent.PullTransferAsync(f, spec.Id);
        Assert.Equal(data, await pulled.Content.ReadAsByteArrayAsync());

        await agent.SendFileChangedAsync(new FileChangeResult(spec.Id, true, null, null));
        Assert.Equal(HttpStatusCode.OK, (await upload).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PullTransferAsync(f, spec.Id)).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.DataDir, "transfers")));
    }

    [Fact]
    public async Task Another_agent_cant_pull_a_transfer()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var (_, _, otherKey) = await AgentKit.PairAsync(f, owner);
        await using var other = await FakeAgent.ConnectAsync(f, otherKey);
        agent.ReportFileChanges = false;

        _ = UploadAsync(owner, install, "profiles", "", "a.txt", [1, 2, 3]);
        await AgentKit.WaitUntilAsync(() => agent.Calls.Any(c => c.Method == "FileChange"));
        var spec = (FileChangeSpec)agent.Calls.Single(c => c.Method == "FileChange").Args[0]!;

        Assert.Equal(HttpStatusCode.NotFound, (await other.PullTransferAsync(f, spec.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync($"/api/agent/transfers/{spec.Id}")).StatusCode);
        await agent.SendFileChangedAsync(new FileChangeResult(spec.Id, false, "test over", null));
    }

    [Fact]
    public async Task Download_is_an_attachment_with_octet_stream()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var data = Encoding.UTF8.GetBytes("<script>alert(1)</script>");
        agent.OnFileRead = (_, _, o, l) => new FileChunk(data.Skip((int)o).Take(l).ToArray(), data.Length);

        var r = await owner.GetAsync($"/api/files/download?{Q(install, "profiles", @"main\page.html")}");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/octet-stream", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("page.html", r.Content.Headers.ContentDisposition.FileNameStar ?? r.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal("nosniff", string.Join(",", r.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(data, await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Text_refuses_a_binary_or_large_file()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;

        agent.OnFileRead = (_, _, _, _) => new FileChunk([0x4D, 0x5A, 0x00, 0x01], 4);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/files/text?{Q(install, "profiles", "a.bin")}")).StatusCode);
        agent.OnFileRead = (_, _, _, _) => new FileChunk(Encoding.UTF8.GetBytes("x"), 2_000_000);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/files/text?{Q(install, "profiles", "big.txt")}")).StatusCode);
        agent.OnFileRead = (_, _, _, _) => new FileChunk([0xC3, 0x28], 2); // invalid UTF-8
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/files/text?{Q(install, "profiles", "latin1.txt")}")).StatusCode);
    }

    [Fact]
    public async Task A_text_save_round_trips_bom_and_line_endings()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a\r\nb\r\n")).ToArray();
        agent.OnFileRead = (_, _, o, l) => new FileChunk(original.Skip((int)o).Take(l).ToArray(), original.Length);

        var text = await owner.GetFromJsonAsync<JsonElement>($"/api/files/text?{Q(install, "profiles", @"main\BattlEye\beserver_x64.cfg")}");
        Assert.Equal(("a\r\nb\r\n", true, "\r\n", Sha(original)),
            (text.GetProperty("text").GetString(), text.GetProperty("bom").GetBoolean(), text.GetProperty("eol").GetString(), text.GetProperty("sha256").GetString()));

        byte[]? written = null;
        agent.ReportFileChanges = false;
        agent.OnFileChange = spec =>
        {
            _ = Task.Run(async () =>
            {
                written = await (await agent.PullTransferAsync(f, spec.Id)).Content.ReadAsByteArrayAsync();
                await agent.SendFileChangedAsync(new FileChangeResult(spec.Id, true, null, @"files\profiles\x"));
            });
            return new CommandResult(true, null);
        };
        var save = await owner.PutAsJsonAsync("/api/files/text",
            new { install, space = "profiles", path = @"main\BattlEye\beserver_x64.cfg", text = "a\r\nc\r\n", bom = true, sha256 = Sha(original) });

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a\r\nc\r\n")), written);
        var spec = (FileChangeSpec)agent.Calls.Last(c => c.Method == "FileChange").Args[0]!;
        Assert.Equal((true, Sha(original)), (spec.Overwrite, spec.ExpectSha256));
        Assert.Equal(@"files\profiles\x", (await save.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("backup").GetString());
    }

    [Fact]
    public async Task A_library_pbo_cant_be_overwritten_or_renamed()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;
        var before = w.Agent.Calls.Count(c => c.Method == "FileChange");

        var upload = await UploadAsync(w.Owner, w.InstallId, "mpmissions", "", "CBA_OP.VR.pbo", [1], overwrite: true);
        var rename = await ActionAsync(w.Owner, "move", w.InstallId, "mpmissions", "cba_op.VR.pbo", "renamed.VR.pbo");
        var onto = await ActionAsync(w.Owner, "move", w.InstallId, "mpmissions", "other.pbo", "cba_op.VR.pbo");

        foreach (var r in new[] { upload, rename, onto })
            Assert.Equal((HttpStatusCode.Conflict, "library_managed"), (r.StatusCode, await MissionKit.CodeAsync(r)));
        Assert.Equal(before, w.Agent.Calls.Count(c => c.Method == "FileChange"));
        Assert.Equal(HttpStatusCode.OK, (await ActionAsync(w.Owner, "copy", w.InstallId, "mpmissions", "cba_op.VR.pbo", "copy.VR.pbo")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_library_pbo_marks_its_deployments_removed()
    {
        using var f = new PanelFactory();
        var w = await ServerMissionsTests.WorldAsync(f);
        await using var lease = w.Agent;

        var r = await w.Owner.DeleteAsync($"/api/files?{Q(w.InstallId, "mpmissions", "cba_op.VR.pbo")}");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var rows = await WithDbAsync(f, db => db.Deployments.Where(d => d.ServerInstallId == w.InstallId && d.FileName == "cba_op.VR.pbo").ToListAsync());
        Assert.NotEmpty(rows);
        Assert.All(rows, d => Assert.Equal((DeploymentStatus.Failed, "Removed in the file manager"), (d.Status, d.Error)));
        var missions = await w.Owner.GetFromJsonAsync<JsonElement>($"/api/servers/{w.ProfileId}/missions?modListId={w.FullList}");
        Assert.DoesNotContain(missions.GetProperty("missions").EnumerateArray(), m => m.GetProperty("fileName").GetString() == "cba_op.VR.pbo");
    }

    [Fact]
    public async Task A_change_under_localmods_marks_the_mod_for_rescan()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var modId = await WithDbAsync(f, async db =>
        {
            var mod = new Mod { Id = Guid.NewGuid(), LocalFolder = "@local", Name = "Local", Status = ModStatus.Installed, ScanStatus = ScanStatus.Scanned };
            db.Mods.Add(mod);
            await db.SaveChangesAsync();
            return mod.Id;
        });

        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(owner, install, "localmods", @"@local\addons", "x.pbo", [1])).StatusCode);

        Assert.Equal(ScanStatus.NotScanned, await WithDbAsync(f, async db => (await db.Mods.FindAsync(modId))!.ScanStatus));
        await AgentKit.WaitUntilAsync(() => agent.Scans().Any(s => (s.Mods ?? []).Any(m => m.LocalFolder == "@local")));
    }

    [Fact]
    public async Task A_change_that_outlasts_the_wait_answers_202_still_running()
    {
        using var f = new PanelFactory(fileWaitSeconds: 1);
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        agent.ReportFileChanges = false;

        var r = await ActionAsync(owner, "folder", install, "profiles", "slow");

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Contains("still", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_refused_or_failed_change_is_409_with_the_agents_words()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;

        agent.OnFileChange = _ => new CommandResult(false, "Only .bikey files go in the keys folder.");
        var refused = await ActionAsync(owner, "folder", install, "keys", "x");
        Assert.Equal((HttpStatusCode.Conflict, "file_failed"), (refused.StatusCode, await MissionKit.CodeAsync(refused)));
        Assert.Contains(".bikey", await refused.Content.ReadAsStringAsync());

        agent.OnFileChange = _ => new CommandResult(true, null);
        agent.ReportFileChanges = false;
        var failing = ActionAsync(owner, "folder", install, "profiles", "y");
        await AgentKit.WaitUntilAsync(() => agent.Calls.Count(c => c.Method == "FileChange") == 2);
        var spec = (FileChangeSpec)agent.Calls.Last(c => c.Method == "FileChange").Args[0]!;
        await agent.SendFileChangedAsync(new FileChangeResult(spec.Id, false, "Something with that name is already there.", null));
        var failed = await failing;
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Contains("already there", await failed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Paths_with_bad_names_never_reach_the_agent()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;

        foreach (var path in new[] { @"..\x", "CON", "a:b", @"C:\Windows" })
            Assert.Equal(HttpStatusCode.BadRequest, (await ActionAsync(owner, "folder", install, "profiles", path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, install, "profiles", "", "nul.txt", [1])).StatusCode);
        Assert.DoesNotContain(agent.Calls, c => c.Method == "FileChange");
    }

    // Final review I-2: a cut-off upload left its partial file in transfers until the panel restarted.
    private sealed class BreaksAfter(int bytes) : Stream
    {
        private int _left = bytes;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) throw new IOException("The connection was reset.");
            var n = Math.Min(count, _left);
            _left -= n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_cut_off_upload_leaves_no_partial_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "13c-tests", Guid.NewGuid().ToString("N"));
        var transfers = new Panel.Features.Files.FileTransfers(new Panel.Auth.PanelPaths(dir));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => transfers.SaveAsync(Guid.NewGuid(), new BreaksAfter(300_000)));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "transfers")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_malformed_upload_is_400_and_leaves_nothing()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var body = new StringContent("--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\n\r\npartial data with no closing boundary");
        body.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=b");

        var r = await owner.PostAsync($"/api/files/upload?{Q(install, "profiles")}", body);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.DoesNotContain(agent.Calls, c => c.Method == "FileChange");
        var transfers = Path.Combine(f.DataDir, "transfers");
        Assert.True(!Directory.Exists(transfers) || !Directory.EnumerateFiles(transfers).Any());
    }

    // Final review M-17, graded up: the folder-set list went to every signed-in member, with the installs' paths.
    [Fact]
    public async Task Spaces_needs_a_file_permission_and_hides_install_paths()
    {
        using var f = new PanelFactory();
        var (_, _, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var member = await TestUsers.LoginAsync(f, "Member");
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/files/spaces")).StatusCode);
        var install = Assert.Single((await zeus.GetFromJsonAsync<JsonElement>("/api/files/spaces")).GetProperty("installs").EnumerateArray());
        Assert.False(install.TryGetProperty("installPath", out _));
    }

    [Fact]
    public async Task Every_change_is_audited()
    {
        using var f = new PanelFactory();
        var (owner, _, agent, install) = await AgentKit.InstallAsync(f);
        await using var lease = agent;

        Assert.Equal(HttpStatusCode.OK, (await ActionAsync(owner, "folder", install, "profiles", @"main\notes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/files?{Q(install, "profiles", @"main\notes")}")).StatusCode);

        var actions = await WithDbAsync(f, db => db.Audit.Where(a => a.Action.StartsWith("file.")).Select(a => a.Action).ToListAsync());
        Assert.Equal(["file.folderCreated", "file.deleted"], actions.Order().Reverse());
    }
}
