using System.Text.Json;

// A stand-in steamcmd.exe for the agent's tests. It speaks the way real SteamCMD does inside a console (Phase 5a spike S1–S6):
// prompts without a newline, VT colour codes, result lines run together, exit code 0 even when an item fails.
// What it does comes from fake-steamcmd.json next to the exe, so tests running in parallel never share settings.
var dir = AppContext.BaseDirectory;
var scenario = File.Exists(Path.Combine(dir, "fake-steamcmd.json"))
    ? JsonSerializer.Deserialize<Scenario>(File.ReadAllText(Path.Combine(dir, "fake-steamcmd.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!
    : new Scenario();
File.AppendAllText(Path.Combine(dir, "fake-steamcmd-args.txt"), string.Join(' ', args) + "\n");

void Out(string s) { Console.Write(s); Console.Out.Flush(); }
string? ReadAnswer() => Console.ReadLine()?.TrimEnd('\r');

var selfUpdated = Path.Combine(dir, "fake-selfupdated");
if (scenario.SelfUpdate && !File.Exists(selfUpdated))
{
    Out("[  0%] Checking for available updates...\r\n[----] Downloading update (0 of 43,472 KB)...\r\n[----] Update complete, launching...\r\n");
    File.WriteAllText(selfUpdated, "");
    return 7;
}

Out("\x1B]0;steamcmd\x07Steam Console Client (c) Valve Corporation - version 1794537082\r\n-- type 'quit' to exit --\r\nLoading Steam API...OK\r\n");
if (scenario.Hang) { Thread.Sleep(Timeout.Infinite); }

string? installDir = null;
var exit = 0;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "+force_install_dir":
            installDir = args[++i];
            break;
        case "+login":
            var user = args[++i];
            if (user == "anonymous") { Out("\r\nConnecting anonymously to Steam Public...OK\r\nWaiting for client config...OK\r\nWaiting for user info...OK\r\n"); break; }
            var cache = Path.Combine(dir, "fake-cached-" + user);
            if (!File.Exists(cache))
            {
                Out("\x1B[97mCached credentials not found.\r\n\x1B[m\r\npassword: ");
                var password = ReadAnswer();
                if (scenario.EchoSecrets) Out($"\r\ndebug: got {password}\r\n");
                if (password != scenario.Password) { Out("\r\nLogging in user '" + user + "' to Steam Public...ERROR (Invalid Password)\r\n"); return 5; }
                if (scenario.Guard is { } guard)
                {
                    if (guard == "mobile")
                    {
                        Out("\r\nPlease confirm the login in the Steam Mobile app on your phone.\r\n");
                        Thread.Sleep(scenario.MobileDelayMs);
                    }
                    else
                    {
                        Out(guard == "email" ? "\r\nSteam Guard code:" : "\r\nTwo-factor code:");
                        var code = ReadAnswer();
                        if (scenario.EchoSecrets) Out($"\r\ndebug: got {code}\r\n");
                        if (code != scenario.GuardCode) { Out("\r\nFAILED (Invalid Login Auth Code)\r\n"); return 5; }
                    }
                }
                File.WriteAllText(cache, "");
            }
            Out("\r\nLogging in user '" + user + "' to Steam Public...OK\r\nWaiting for client config...OK\r\nWaiting for user info...OK\r\n");
            break;
        case "+app_update":
            var app = args[++i];
            while (i + 1 < args.Length && !args[i + 1].StartsWith('+')) i++; // -beta x -betapassword y validate
            foreach (var p in new[] { 0.0, 12.5, 45.23, 100.0 })
            {
                Out($" Update state (0x61) downloading, progress: {p:F2} ({(long)(p * 100)} / 10000)\r");
                Thread.Sleep(30);
            }
            if (scenario.AppError is { } appError) { Out($"\r\nERROR! Failed to install app '{app}' ({appError})\r\n"); exit = 8; break; }
            Directory.CreateDirectory(Path.Combine(installDir!, "keys"));
            File.WriteAllText(Path.Combine(installDir!, "keys", "a3.bikey"), "a3");
            File.WriteAllText(Path.Combine(installDir!, "arma3server_x64.exe"), "fake");
            Out($"\r\nSuccess! App '{app}' fully installed.\r\n");
            break;
        case "+workshop_download_item":
            var gameApp = args[++i];
            var id = args[++i];
            if (i + 1 < args.Length && args[i + 1] == "validate") i++;
            Out($"Downloading item {id} ...\r\n");
            var item = scenario.Items.FirstOrDefault(x => x.Id.ToString() == id);
            var attempts = Path.Combine(dir, $"fake-attempts-{id}");
            var attempt = (File.Exists(attempts) ? int.Parse(File.ReadAllText(attempts)) : 0) + 1;
            File.WriteAllText(attempts, attempt.ToString());
            if (item is null || attempt <= item.FailRuns)
            {
                Out($"ERROR! Download item {id} failed ({(item is null ? "File Not Found" : item.Error)}).");
                break;
            }
            var folder = Path.Combine(installDir!, "steamapps", "workshop", "content", gameApp, id);
            if (item.SilentMs > 0)
            {
                // Real SteamCMD prints nothing while an item downloads: bytes only appear in the staging folder.
                var staging = Path.Combine(installDir!, "steamapps", "workshop", "downloads", gameApp, id);
                Directory.CreateDirectory(staging);
                using (var pbo = File.Create(Path.Combine(staging, "mod.pbo")))
                    for (var chunk = 0; chunk < 10; chunk++)
                    {
                        pbo.Write(new byte[item.Bytes / 10]);
                        pbo.Flush();
                        Thread.Sleep(item.SilentMs / 10);
                    }
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(Path.Combine(folder, "addons"));
            File.WriteAllBytes(Path.Combine(folder, "addons", "mod.pbo"), new byte[item.Bytes]);
            foreach (var key in item.Keys)
            {
                Directory.CreateDirectory(Path.Combine(folder, "keys"));
                File.WriteAllText(Path.Combine(folder, "keys", key), key);
            }
            if (item.Name is { } name) File.WriteAllText(Path.Combine(folder, "meta.cpp"), $"protocol = 1;\r\npublishedid = {id};\r\nname = \"{name}\";\r\n");
            Out($"Success. Downloaded item {id} to \"{folder}\" ({item.Bytes} bytes) ");
            break;
        case "+quit":
            Out("Unloading Steam API...OK\r\n");
            return exit;
    }
}
return exit;

sealed class Scenario
{
    public bool SelfUpdate { get; set; }
    public bool Hang { get; set; }
    public string? Password { get; set; }
    public string? Guard { get; set; } // email, twofactor, mobile
    public string? GuardCode { get; set; }
    public int MobileDelayMs { get; set; } = 200;
    public bool EchoSecrets { get; set; }
    public string? AppError { get; set; }
    public List<Item> Items { get; set; } = [];
}

sealed class Item
{
    public long Id { get; set; }
    public long Bytes { get; set; } = 1024;
    public int FailRuns { get; set; }
    public string Error { get; set; } = "Timeout";
    public string? Name { get; set; }
    public List<string> Keys { get; set; } = [];
    public int SilentMs { get; set; }
}
