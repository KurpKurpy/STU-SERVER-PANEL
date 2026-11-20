using Contracts;
using ServerAgent.Steam;

namespace Agent.Tests;

public sealed class SteamCmdTests
{
    private static async Task<T> Within<T>(Task<T> task, int seconds = 20) =>
        await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private sealed class Seen
    {
        public readonly List<string> Lines = [];
        public readonly List<SteamPrompt> Prompts = [];
        public readonly List<double> Progress = [];
        public Seen(SteamCmdRun run)
        {
            run.Line += l => { lock (Lines) Lines.Add(l); };
            run.Prompt += p => { lock (Prompts) Prompts.Add(p); };
            run.Progress += p => { lock (Progress) Progress.Add(p); };
        }
    }

    [Fact]
    public async Task Lines_and_progress_are_reported_without_terminal_codes()
    {
        var exe = FakeSteam.Create();
        using var run = SteamCmdRun.Start(exe, ["+force_install_dir", TempDir.Create(), "+login", "anonymous", "+app_update", "233780", "+quit"]);
        var seen = new Seen(run);

        var exit = await Within(run.Completion);

        Assert.Equal(0, exit.ExitCode);
        Assert.Contains(0.4523, seen.Progress);
        Assert.Contains(seen.Lines, l => l.Contains("Success! App '233780' fully installed."));
        Assert.Contains(seen.Lines, l => l.StartsWith("Steam Console Client"));
        Assert.DoesNotContain(seen.Lines, l => l.Contains('\x1B'));
    }

    [Theory]
    [InlineData(null, SteamPrompt.Password)]
    [InlineData("email", SteamPrompt.EmailCode)]
    [InlineData("twofactor", SteamPrompt.TwoFactorCode)]
    public async Task Prompt_without_newline_is_detected_and_answered(string? guard, SteamPrompt last)
    {
        var exe = FakeSteam.Create(new { password = "pw-1", guard, guardCode = "CODE7" });
        using var run = SteamCmdRun.Start(exe, ["+login", "crow", "+quit"]);
        var seen = new Seen(run);
        run.Prompt += p => _ = run.WriteAsync(p == SteamPrompt.Password ? "pw-1" : "CODE7");

        var exit = await Within(run.Completion);

        Assert.Equal(0, exit.ExitCode);
        Assert.Equal(SteamPrompt.Password, seen.Prompts[0]);
        Assert.Equal(last, seen.Prompts[^1]);
        Assert.Contains(seen.Lines, l => l.Contains("Logging in user 'crow' to Steam Public...OK"));
    }

    [Fact]
    public async Task Mobile_confirmation_is_reported_without_waiting_for_input()
    {
        var exe = FakeSteam.Create(new { password = "pw-1", guard = "mobile" });
        using var run = SteamCmdRun.Start(exe, ["+login", "crow", "+quit"]);
        var seen = new Seen(run);
        run.Prompt += p => { if (p == SteamPrompt.Password) _ = run.WriteAsync("pw-1"); };

        var exit = await Within(run.Completion);

        Assert.Equal(0, exit.ExitCode);
        Assert.Contains(SteamPrompt.MobileConfirm, seen.Prompts);
    }

    [Fact]
    public async Task Answers_are_masked_in_every_line()
    {
        var exe = FakeSteam.Create(new { password = "s3cret-pw", guard = "email", guardCode = "K9X2Q", echoSecrets = true });
        using var run = SteamCmdRun.Start(exe, ["+login", "crow", "+quit"]);
        var seen = new Seen(run);
        run.Prompt += p => _ = run.WriteAsync(p == SteamPrompt.Password ? "s3cret-pw" : "K9X2Q");

        await Within(run.Completion);

        Assert.Contains(seen.Lines, l => l.Contains("debug: got ****"));
        Assert.DoesNotContain(seen.Lines, l => l.Contains("s3cret-pw") || l.Contains("K9X2Q"));
    }

    [Fact]
    public void Result_lines_are_parsed_even_when_run_together()
    {
        const string text = "Downloading item 1 ...\nERROR! Download item 1 failed (File Not Found).Unloading Steam API...OK\n"
            + "Success. Downloaded item 450814997 to \"D:\\w\\steamapps\\workshop\\content\\107410\\450814997\" (123 bytes) "
            + "ERROR! Download item 7 failed (Timeout).\nERROR! Failed to install app '233780' (No subscription)\nSuccess! App '233780' fully installed.";

        var items = SteamCmd.ItemResults(text);
        var apps = SteamCmd.AppResults(text);

        Assert.Equal((false, "File Not Found"), (items[1].Ok, items[1].Error));
        Assert.Equal((true, (string?)null), (items[450814997].Ok, items[450814997].Error));
        Assert.Equal("Timeout", items[7].Error);
        Assert.True(apps[233780].Ok); // the later success wins
        Assert.Equal(0.4523, SteamCmd.ParseProgress(" Update state (0x61) downloading, progress: 45.23 (4523 / 10000)"));
        Assert.Null(SteamCmd.ParseProgress("Loading Steam API...OK"));
    }

    [Fact]
    public async Task Silent_steamcmd_times_out_and_is_killed()
    {
        var exe = FakeSteam.Create(new { hang = true });
        using var run = SteamCmdRun.Start(exe, ["+quit"], idleLimit: TimeSpan.FromSeconds(2));

        var exit = await Within(run.Completion);

        Assert.True(exit.TimedOut);
        Assert.DoesNotContain(System.Diagnostics.Process.GetProcessesByName("steamcmd"), p => p.MainModule?.FileName == exe);
    }

    [Fact]
    public async Task Self_update_exits_with_seven()
    {
        var exe = FakeSteam.Create(new { selfUpdate = true });
        using var first = SteamCmdRun.Start(exe, ["+quit"]);
        Assert.Equal(7, (await Within(first.Completion)).ExitCode);
        using var second = SteamCmdRun.Start(exe, ["+quit"]);
        Assert.Equal(0, (await Within(second.Completion)).ExitCode);
    }
}
