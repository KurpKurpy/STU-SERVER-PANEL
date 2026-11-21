using System.IO.Compression;
using System.Net;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Steam;

namespace Agent.Tests;

public sealed class JobRunnerTests : IDisposable
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _installRoot = TempDir.Create();
    private readonly List<JobRunner> _runners = [];

    public void Dispose()
    {
        foreach (var r in _runners) r.Dispose();
    }

    private JobRunner Runner(string steamCmd, TimeSpan? answerTimeout = null, HttpMessageHandler? download = null, TimeSpan? idle = null)
    {
        var options = new AgentOptions { DataRoot = _dataRoot, InstallRoots = [_installRoot], SteamCmdPath = steamCmd };
        var areas = new Areas(options, options.InstallRoots);
        var installer = new SteamCmdInstaller(new HttpClient(download ?? new NoNetwork()));
        var runner = new JobRunner(areas, installer, NullLogger<JobRunner>.Instance,
            new JobTimeouts(answerTimeout ?? TimeSpan.FromSeconds(30), idle ?? TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100)));
        _runners.Add(runner);
        return runner;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No network in tests: " + request.RequestUri);
    }

    // What HttpClient throws when its timeout elapses: an OperationCanceledException that isn't a shutdown.
    private sealed class TimesOut : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
    }

    [Fact]
    public async Task A_timed_out_steamcmd_download_fails_the_job_and_the_runner_keeps_going()
    {
        var missing = Path.Combine(TempDir.Create(), "steamcmd", "steamcmd.exe");
        var runner = Runner(missing, download: new TimesOut());
        var first = Download("crow", new WorkshopItem(111, 1000));
        var second = Download("crow", new WorkshopItem(222, 1000));
        var firstDone = FinishedAsync(runner, first.JobId);
        var secondDone = FinishedAsync(runner, second.JobId);

        Assert.True(runner.Enqueue(first).Ok);
        Assert.True(runner.Enqueue(second).Ok);

        Assert.Contains("Timeout", (await firstDone).Error);
        Assert.False((await secondDone).Ok); // the runner is still taking jobs
    }

    private static Task<JobResult> FinishedAsync(JobRunner runner, Guid jobId)
    {
        var done = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Finished += r => { if (r.JobId == jobId) done.TrySetResult(r); };
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static string Cached(string exe, string user)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "fake-cached-" + user), "");
        return exe;
    }

    private static JobSpec Download(string user, params WorkshopItem[] items) => new(Guid.NewGuid(), JobKind.WorkshopDownload, user, null, null, items);

    [Fact]
    public async Task A_silent_download_that_keeps_writing_is_not_killed()
    {
        // SteamCMD prints nothing while a big item downloads; bytes arriving on disk count as activity for the idle limit.
        var exe = Cached(FakeSteam.Create(new { items = new[] { new { id = 111, bytes = 500_000, silentMs = 9000 } } }), "crow");
        var runner = Runner(exe, idle: TimeSpan.FromSeconds(4));
        var spec = Download("crow", new WorkshopItem(111, 500_000));
        var finished = FinishedAsync(runner, spec.JobId);

        Assert.True(runner.Enqueue(spec).Ok);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        Assert.Single(FakeSteam.ArgsSeen(exe)); // one run: never killed and retried
    }

    [Fact]
    public async Task A_reinstall_measures_new_bytes_not_the_files_already_there()
    {
        var installed = Path.Combine(_dataRoot, "workshop", "steamapps", "workshop", "content", "107410", "111", "addons");
        Directory.CreateDirectory(installed);
        File.WriteAllBytes(Path.Combine(installed, "mod.pbo"), new byte[500_000]); // the old version, already complete
        var exe = Cached(FakeSteam.Create(new { items = new[] { new { id = 111, bytes = 500_000, silentMs = 5000 } } }), "crow");
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(111, 500_000));
        var seen = new List<double>();
        runner.Updated += u => { if (u.JobId == spec.JobId && u.Progress is { } p) lock (seen) seen.Add(p); };
        var finished = FinishedAsync(runner, spec.JobId);

        Assert.True(runner.Enqueue(spec).Ok);
        Assert.True((await finished).Ok);

        // Before: the old folder counted as downloaded, so the bar sat near 100% for the whole update.
        lock (seen) Assert.True(seen.Count > 0 && seen[0] < 0.5, $"first progress {string.Join(", ", seen)}");
    }

    [Fact]
    public async Task Server_install_runs_app_update_into_the_install_root()
    {
        var exe = Cached(FakeSteam.Create(), "crow");
        var runner = Runner(exe);
        var spec = new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, "crow", _installRoot, "creatordlc", []);
        var finished = FinishedAsync(runner, spec.JobId);

        Assert.True(runner.Enqueue(spec).Ok);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        var args = FakeSteam.ArgsSeen(exe).Last();
        Assert.Contains($"+force_install_dir {_installRoot}", args);
        Assert.Contains("+login crow +app_update 233780 -beta creatordlc validate +quit", args);
        Assert.True(File.Exists(Path.Combine(_installRoot, "arma3server_x64.exe")));
    }

    // Seen on the box (2026-11-21): SteamCMD says "ERROR (Invalid Password)", and the job only said "exit code 5".
    [Fact]
    public async Task Server_install_with_a_wrong_password_says_so()
    {
        var exe = FakeSteam.Create(new { password = "pw-1" });
        var runner = Runner(exe);
        var spec = new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, "crow", _installRoot, null, []);
        runner.Updated += u => { if (u.Prompt == SteamPrompt.Password) runner.Answer(spec.JobId, "wrong"); };
        var finished = FinishedAsync(runner, spec.JobId);

        Assert.True(runner.Enqueue(spec).Ok);
        var result = await finished;

        Assert.False(result.Ok);
        Assert.Equal("Steam sign-in failed: Invalid Password.", result.Error);
    }

    [Fact]
    public void Install_root_branch_and_user_are_checked()
    {
        var runner = Runner(FakeSteam.Create());
        Assert.False(runner.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, "crow", TempDir.Create(), null, [])).Ok);
        Assert.False(runner.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, "crow", _installRoot, "beta-of-mine", [])).Ok);
        Assert.False(runner.Enqueue(new JobSpec(Guid.NewGuid(), JobKind.ServerInstall, "crow +quit", _installRoot, null, [])).Ok);
        Assert.False(runner.Enqueue(Download("crow")).Ok); // no items
        Assert.False(runner.Enqueue(Download("crow", new WorkshopItem(0, null))).Ok);
    }

    [Fact]
    public async Task Workshop_download_logs_in_as_the_user_and_reports_items()
    {
        var exe = Cached(FakeSteam.Create(new { items = new[] { new { id = 450814997, bytes = 2048, name = "CBA_A3", keys = new[] { "cba_a3.bikey" } } } }), "crow");
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(450814997, 2048));
        var finished = FinishedAsync(runner, spec.JobId);

        Assert.True(runner.Enqueue(spec).Ok);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        var item = Assert.Single(result.Items);
        Assert.True(item.Ok);
        Assert.True(item.SizeBytes >= 2048);
        Assert.Equal(["cba_a3.bikey"], item.KeyFiles);
        Assert.Equal("CBA_A3", item.Name); // from its meta.cpp: an unlisted mod's only source of a name
        Assert.Contains("+login crow +workshop_download_item 107410 450814997 validate +quit", FakeSteam.ArgsSeen(exe).Last());
    }

    [Fact]
    public async Task Failed_items_are_retried_up_to_three_runs()
    {
        var exe = Cached(FakeSteam.Create(new { items = new object[] { new { id = 11, failRuns = 2 }, new { id = 22, failRuns = 5, error = "Timeout" } } }), "crow");
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(11, null), new WorkshopItem(22, null));
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.False(result.Ok);
        Assert.True(result.Items.Single(i => i.WorkshopId == 11).Ok);
        Assert.Equal("Timeout", result.Items.Single(i => i.WorkshopId == 22).Error);
        Assert.Equal(3, FakeSteam.ArgsSeen(exe).Length);
    }

    [Fact]
    public async Task Failures_a_retry_cannot_fix_are_not_retried()
    {
        // Ruling 12: only Timeout and Failure get another run; "File Not Found" (an unknown item) won't change.
        var exe = Cached(FakeSteam.Create(new { items = new object[] { new { id = 11, failRuns = 9, error = "Access Denied" } } }), "crow");
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(11, null), new WorkshopItem(999, null));
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.Equal(("Access Denied", "File Not Found"), (result.Items.Single(i => i.WorkshopId == 11).Error, result.Items.Single(i => i.WorkshopId == 999).Error));
        Assert.Single(FakeSteam.ArgsSeen(exe));
    }

    [Fact]
    public async Task Second_job_waits_for_the_first()
    {
        var exe = Cached(FakeSteam.Create(new { items = new[] { new { id = 1 }, new { id = 2 } } }), "crow");
        var runner = Runner(exe);
        var order = new List<Guid>();
        runner.Finished += r => { lock (order) order.Add(r.JobId); };
        var first = Download("crow", new WorkshopItem(1, null));
        var second = Download("crow", new WorkshopItem(2, null));
        var done = FinishedAsync(runner, second.JobId);

        runner.Enqueue(first);
        runner.Enqueue(second);
        Assert.Equal([first.JobId, second.JobId], runner.Jobs);
        await done;

        Assert.Equal([first.JobId, second.JobId], order);
        Assert.Empty(runner.Jobs);
    }

    [Fact]
    public async Task Password_prompt_is_relayed_and_the_answer_completes_the_job()
    {
        var exe = FakeSteam.Create(new { password = "pw-1", guard = "email", guardCode = "C0DE", items = new[] { new { id = 5 } } });
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(5, null));
        var prompts = new List<SteamPrompt>();
        runner.Updated += u =>
        {
            if (u.JobId != spec.JobId || u.Prompt is not { } p) return;
            lock (prompts) prompts.Add(p);
            runner.Answer(spec.JobId, p == SteamPrompt.Password ? "pw-1" : "C0DE");
        };
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        Assert.Equal([SteamPrompt.Password, SteamPrompt.EmailCode], prompts);
    }

    [Fact]
    public async Task Unanswered_prompt_fails_the_job_after_the_timeout()
    {
        var exe = FakeSteam.Create(new { password = "pw-1", items = new[] { new { id = 5 } } });
        var runner = Runner(exe, answerTimeout: TimeSpan.FromSeconds(1));
        var spec = Download("crow", new WorkshopItem(5, null));
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.False(result.Ok);
        Assert.Contains("sign-in", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_stops_without_retries()
    {
        var exe = FakeSteam.Create(new { password = "pw-1", items = new[] { new { id = 5 } } });
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(5, null));
        runner.Updated += u => { if (u.Prompt == SteamPrompt.Password) runner.Answer(spec.JobId, "wrong"); };
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.False(result.Ok);
        Assert.Contains("Invalid Password", result.Error);
        Assert.Single(FakeSteam.ArgsSeen(exe));
    }

    [Fact]
    public async Task Input_is_masked_in_output_and_never_in_arguments()
    {
        var exe = FakeSteam.Create(new { password = "s3cret-pw", echoSecrets = true, items = new[] { new { id = 5 } } });
        var runner = Runner(exe);
        var spec = Download("crow", new WorkshopItem(5, null));
        var lines = new List<string>();
        runner.Updated += u =>
        {
            lock (lines) lines.AddRange(u.Lines);
            if (u.Prompt == SteamPrompt.Password) runner.Answer(spec.JobId, "s3cret-pw");
        };
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        Assert.Contains(lines, l => l.Contains("debug: got ****"));
        Assert.DoesNotContain(lines, l => l.Contains("s3cret-pw"));
        Assert.DoesNotContain(FakeSteam.ArgsSeen(exe), a => a.Contains("s3cret-pw"));
    }

    [Fact]
    public async Task Remove_deletes_only_that_items_folder()
    {
        var runner = Runner(FakeSteam.Create());
        var content = Path.Combine(_dataRoot, "workshop", "steamapps", "workshop", "content", "107410");
        Directory.CreateDirectory(Path.Combine(content, "11", "addons"));
        Directory.CreateDirectory(Path.Combine(content, "22", "addons"));
        var spec = new JobSpec(Guid.NewGuid(), JobKind.WorkshopRemove, null, null, null, [new WorkshopItem(11, null)]);
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(Path.Combine(content, "11")));
        Assert.True(Directory.Exists(Path.Combine(content, "22")));
    }

    [Fact]
    public async Task SteamCmd_is_bootstrapped_from_the_zip_when_missing()
    {
        var fake = Cached(FakeSteam.Create(new { selfUpdate = true, items = new[] { new { id = 5 } } }), "crow");
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fake)!))
                archive.CreateEntryFromFile(file, Path.GetFileName(file));
        var target = Path.Combine(TempDir.Create(), "steamcmd", "steamcmd.exe");
        var runner = Runner(target, download: new ZipHandler(zip.ToArray()));
        var spec = Download("crow", new WorkshopItem(5, null));
        var finished = FinishedAsync(runner, spec.JobId);

        runner.Enqueue(spec);
        var result = await finished;

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(target));
        var runs = FakeSteam.ArgsSeen(target);
        Assert.Equal("+quit", runs[0]); // the self-update warm-up (spike S1)
        Assert.Contains("+workshop_download_item 107410 5", runs[^1]);
    }

    private sealed class ZipHandler(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(SteamCmdInstaller.DefaultUrl, request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        }
    }
}
