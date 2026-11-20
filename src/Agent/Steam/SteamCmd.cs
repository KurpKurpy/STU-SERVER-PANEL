using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Contracts;

namespace ServerAgent.Steam;

public sealed record SteamCmdExit(int ExitCode, bool TimedOut);
public sealed record ItemResult(bool Ok, string? Error);
public sealed record AppResult(bool Ok, string? Error);

// Parsers for SteamCMD's result lines (Phase 5a spike S4–S6). They search mid-line: SteamCMD runs messages together, and its exit
// code says nothing about a failed Workshop item.
public static partial class SteamCmd
{
    private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"progress:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProgressPattern();

    [GeneratedRegex(@"Success\. Downloaded item (\d+)|ERROR! Download item (\d+) failed \(([^)]*)\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"Success! App '(\d+)' fully installed|ERROR! Failed to install app '(\d+)' \(([^)]*)\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppPattern();

    public static double? ParseProgress(string line) =>
        ProgressPattern().Match(line) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100 : null;

    // Workshop item → its last result in the text.
    public static Dictionary<long, ItemResult> ItemResults(string text)
    {
        var results = new Dictionary<long, ItemResult>();
        foreach (Match m in ItemPattern().Matches(text))
            if (m.Groups[1].Success) results[long.Parse(m.Groups[1].Value)] = new ItemResult(true, null);
            else results[long.Parse(m.Groups[2].Value)] = new ItemResult(false, m.Groups[3].Value);
        return results;
    }

    public static Dictionary<long, AppResult> AppResults(string text)
    {
        var results = new Dictionary<long, AppResult>();
        foreach (Match m in AppPattern().Matches(text))
            if (m.Groups[1].Success) results[long.Parse(m.Groups[1].Value)] = new AppResult(true, null);
            else results[long.Parse(m.Groups[2].Value)] = new AppResult(false, m.Groups[3].Value);
        return results;
    }
}

// One SteamCMD run inside a pseudo console: cleaned lines, prompts (also without a newline), progress, answers written to its input
// and masked in everything it prints afterwards, and a kill after a long silence (Review Focus 2).
public sealed partial class SteamCmdRun : IDisposable
{
    public static readonly TimeSpan DefaultIdleLimit = TimeSpan.FromMinutes(10);

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[=>]|\x1B[()][0-9A-Za-z]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TerminalCodes();

    [GeneratedRegex(@"\x1B(?:\[[0-9;?]*[ -/]*|\][^\x07\x1B]*|[()])?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnfinishedCode();

    private static readonly (Regex Pattern, SteamPrompt Prompt)[] Prompts =
    [
        (new Regex(@"password:\s*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), SteamPrompt.Password),
        (new Regex(@"steam guard code:\s*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), SteamPrompt.EmailCode),
        (new Regex(@"two-factor code:\s*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), SteamPrompt.TwoFactorCode),
    ];

    private static readonly Regex MobileConfirm = new(@"confirm the login in the Steam Mobile app", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private readonly PseudoConsoleProcess _process;
    private readonly TimeSpan _idleLimit;
    private readonly List<string> _secrets = [];
    private readonly StringBuilder _transcript = new();
    private readonly object _gate = new();
    private string _pending = "";   // raw text with an unfinished terminal code
    private string _partial = "";   // cleaned text after the last line break
    private bool _promptRaised;     // for the current partial line
    private SteamPrompt? _awaiting; // a prompt is open: silence is expected, not a hang
    private readonly List<Action> _outbox = []; // events raised after the lock is released
    private DateTime _lastOutput = DateTime.UtcNow;
    private bool _timedOut;

    public event Action<string>? Line;
    public event Action<SteamPrompt>? Prompt;
    public event Action<double>? Progress;
    public Task<SteamCmdExit> Completion { get; }

    public string Transcript { get { lock (_gate) return _transcript.ToString(); } }

    private SteamCmdRun(PseudoConsoleProcess process, TimeSpan idleLimit)
    {
        (_process, _idleLimit) = (process, idleLimit);
        Completion = RunAsync();
    }

    public static SteamCmdRun Start(string exe, IReadOnlyList<string> args, string? workingDirectory = null, TimeSpan? idleLimit = null) =>
        new(PseudoConsoleProcess.Start(exe, args, workingDirectory ?? Path.GetDirectoryName(exe)), idleLimit ?? DefaultIdleLimit);

    // The answer is masked before it's written, so an echo never shows it.
    public async Task WriteAsync(string answer)
    {
        lock (_gate)
        {
            if (answer.Length > 0) _secrets.Add(answer);
            _awaiting = null;
            _lastOutput = DateTime.UtcNow;
        }
        var bytes = Encoding.UTF8.GetBytes(answer + "\r");
        await _process.Input.WriteAsync(bytes);
        await _process.Input.FlushAsync();
    }

    public void Kill() => _process.Kill();

    // Activity the console doesn't show (SteamCMD prints nothing while an item downloads) still means it's alive.
    public void Touch() { lock (_gate) _lastOutput = DateTime.UtcNow; }

    private async Task<SteamCmdExit> RunAsync()
    {
        var reader = Task.Run(ReadAsync);
        var watchdog = Task.Run(async () =>
        {
            while (!_process.Exited.IsCompleted)
            {
                await Task.WhenAny(_process.Exited, Task.Delay(250));
                bool silent;
                lock (_gate) silent = _awaiting is null && DateTime.UtcNow - _lastOutput > _idleLimit;
                if (silent) { lock (_gate) _timedOut = true; _process.Kill(); }
            }
        });
        var code = await _process.Exited;
        await watchdog;
        _process.CloseConsole();
        await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(3)));
        SteamCmdExit exit;
        lock (_gate)
        {
            if (_partial.Trim().Length > 0) EmitLine(_partial);
            _partial = "";
            exit = new SteamCmdExit(code, _timedOut);
        }
        Flush();
        return exit;
    }

    private void Flush()
    {
        Action[] events;
        lock (_gate)
        {
            events = [.. _outbox];
            _outbox.Clear();
        }
        foreach (var e in events) e();
    }

    private async Task ReadAsync()
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096];
        try
        {
            int n;
            while ((n = await _process.Output.ReadAsync(bytes)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, n, chars, 0);
                lock (_gate) Feed(new string(chars, 0, count));
                Flush();
            }
        }
        catch (IOException) { } // the console closed under us
        catch (ObjectDisposedException) { }
    }

    private void Feed(string raw)
    {
        _lastOutput = DateTime.UtcNow;
        if (_awaiting == SteamPrompt.MobileConfirm) _awaiting = null; // the app was answered: SteamCMD moved on
        var text = _pending + raw;
        var unfinished = UnfinishedCode().Match(text);
        _pending = unfinished.Success ? unfinished.Value : "";
        if (unfinished.Success) text = text[..unfinished.Index];
        text = TerminalCodes().Replace(text, "");

        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                if (_partial.Length > 0) EmitLine(_partial);
                _partial = "";
                _promptRaised = false;
            }
            else _partial += c;
        }
        if (!_promptRaised && Match(_partial) is { } prompt)
        {
            _promptRaised = true;
            Raise(prompt);
        }
    }

    private static SteamPrompt? Match(string text)
    {
        foreach (var (pattern, prompt) in Prompts)
            if (pattern.IsMatch(text)) return prompt;
        return null;
    }

    private void Raise(SteamPrompt prompt)
    {
        _awaiting = prompt;
        _outbox.Add(() => Prompt?.Invoke(prompt));
    }

    private void EmitLine(string line)
    {
        foreach (var secret in _secrets) line = line.Replace(secret, "****");
        _transcript.Append(line).Append('\n');
        if (!_promptRaised && Match(line) is { } prompt) Raise(prompt);
        else if (MobileConfirm.IsMatch(line)) Raise(SteamPrompt.MobileConfirm);
        _outbox.Add(() => Line?.Invoke(line));
        if (SteamCmd.ParseProgress(line) is { } progress) _outbox.Add(() => Progress?.Invoke(progress));
    }

    public void Dispose()
    {
        if (!_process.Exited.IsCompleted) _process.Kill();
        _process.Dispose();
    }
}
