using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Files;

namespace Agent.Tests;

// Phase 4b Rulings 5–7 and 14: the agent's file operations, against real folders. Transfers come from a stub panel.
public sealed class FileManagerTests : IDisposable
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _install = TempDir.Create();
    private readonly Areas _areas;
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Panel _panel = new();
    private readonly ConcurrentQueue<FileChangeResult> _reports = new();

    public FileManagerTests() => _areas = new Areas(new AgentOptions { DataRoot = _dataRoot }, [_install]);

    public void Dispose()
    {
        TempDir.Delete(_dataRoot);
        TempDir.Delete(_install);
    }

    // Serves transfers by change id; counts how many are being pulled at once.
    private sealed class Panel : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<Guid, byte[]> Transfers = new();
        public TimeSpan Delay = TimeSpan.Zero;
        private int _now;
        public int MostAtOnce;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var at = Interlocked.Increment(ref _now);
            lock (this) MostAtOnce = Math.Max(MostAtOnce, at);
            try
            {
                await Task.Delay(Delay, ct);
                var id = Guid.Parse(request.RequestUri!.Segments[^1]);
                Assert.Equal($"/api/agent/transfers/{id}", request.RequestUri.AbsolutePath);
                return Transfers.TryGetValue(id, out var data)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally { Interlocked.Decrement(ref _now); }
        }
    }

    private FileManager New(long maxExtractBytes = 4L << 30, int maxExtractEntries = 20_000)
    {
        var fm = new FileManager(new FileSpaces(_areas, _running.Contains), _areas,
            new HttpClient(_panel) { BaseAddress = new Uri("http://panel/") }, NullLogger<FileManager>.Instance, maxExtractBytes, maxExtractEntries);
        fm.Changed += _reports.Enqueue;
        return fm;
    }

    private static FileSpace Profiles => new("profiles", null);
    private static FileSpace LocalMods => new("localmods", null);
    private FileSpace Missions => new("mpmissions", _install);
    private string P(params string[] parts) => Path.Combine([_dataRoot, "profiles", .. parts]);
    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static FileChangeSpec Spec(FileSpace space, string op, string path, string? target = null, byte[]? data = null, bool overwrite = false,
        string? expect = null) =>
        new(Guid.NewGuid(), space, op, path, target, data?.Length ?? 0, data is null ? null : Sha(data), overwrite, expect);

    // Accepts the change and waits for its report.
    private async Task<FileChangeResult> RunAsync(FileManager fm, FileChangeSpec spec, byte[]? transfer = null)
    {
        if (transfer is not null) _panel.Transfers[spec.Id] = transfer;
        var accepted = fm.Change(spec);
        Assert.True(accepted.Ok, accepted.Error);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (_reports.FirstOrDefault(r => r.Id == spec.Id) is { } done) return done;
            await Task.Delay(20);
        }
        throw new TimeoutException("No report");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void List_hides_the_temp_folder_and_links_and_truncates_at_5000()
    {
        Write(P("main", "a.rpt"), "x");
        Directory.CreateDirectory(P("main", "BattlEye"));
        Directory.CreateDirectory(P(".13c-tmp"));
        Junction(P("link"), _install);
        var fm = New();

        var root = fm.List(Profiles, "");
        Assert.Equal(["main"], root.Items.Select(i => i.Name));
        var main = fm.List(Profiles, "main");
        Assert.Equal([("BattlEye", true), ("a.rpt", false)], main.Items.Select(i => (i.Name, i.IsFolder)));
        Assert.False(main.Truncated);

        for (var i = 0; i < FileManager.MaxList + 1; i++) File.WriteAllText(P("main", $"f{i}.txt"), "");
        var many = fm.List(Profiles, "main");
        Assert.Equal(FileManager.MaxList, many.Items.Length);
        Assert.True(many.Truncated);
    }

    [Fact]
    public void Search_finds_names_under_a_folder_skips_links_and_stops_at_500()
    {
        Write(P("main", "BattlEye", "bans.txt"), "");
        Write(P("other", "bans.txt"), "");
        Write(P("main", "x.rpt"), "");
        Junction(P("main", "link"), P("other"));
        var fm = New();

        Assert.Equal([@"main\BattlEye\bans.txt"], fm.Search(Profiles, "main", "BANS").Hits.Select(h => h.Path));
        Assert.Equal(2, fm.Search(Profiles, "", "bans").Hits.Length);

        for (var i = 0; i < FileManager.MaxHits + 5; i++) File.WriteAllText(P("main", $"hit{i}.txt"), "");
        var r = fm.Search(Profiles, "", "hit");
        Assert.Equal(FileManager.MaxHits, r.Hits.Length);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Read_returns_a_chunk_and_the_size()
    {
        Write(P("main", "a.rpt"), "hello world");
        var chunk = New().Read(Profiles, @"main\a.rpt", 6, 100);
        Assert.Equal("world", Encoding.UTF8.GetString(chunk.Data));
        Assert.Equal(11, chunk.TotalSize);
        Assert.Throws<AreaException>(() => New().Read(Profiles, "main", 0, 10)); // a folder isn't a file
    }

    [Fact]
    public async Task Write_pulls_the_transfer_checks_its_hash_and_moves_it_into_place()
    {
        var data = Encoding.UTF8.GetBytes("123 // ban");
        var r = await RunAsync(New(), Spec(Profiles, FileOps.Write, @"main\BattlEye\bans.txt", data: data), data);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("123 // ban", File.ReadAllText(P("main", "BattlEye", "bans.txt")));
        Assert.False(Directory.Exists(P(".13c-tmp")));
    }

    [Fact]
    public async Task Write_with_a_wrong_hash_or_size_writes_nothing()
    {
        var fm = New();
        var spec = Spec(Profiles, FileOps.Write, @"main\a.txt", data: Encoding.UTF8.GetBytes("expected"));
        var r = await RunAsync(fm, spec, Encoding.UTF8.GetBytes("tampered"));
        Assert.False(r.Ok);
        Assert.False(File.Exists(P("main", "a.txt")));
    }

    [Fact]
    public async Task Overwrite_needs_the_flag_and_backs_the_old_file_up()
    {
        Write(P("main", "a.txt"), "old");
        var fm = New();
        var data = Encoding.UTF8.GetBytes("new");
        Assert.False(fm.Change(Spec(Profiles, FileOps.Write, @"main\a.txt", data: data)).Ok); // exists, no flag: refused at once

        var r = await RunAsync(fm, Spec(Profiles, FileOps.Write, @"main\a.txt", data: data, overwrite: true), data);

        Assert.True(r.Ok, r.Error);
        Assert.Equal("new", File.ReadAllText(P("main", "a.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_dataRoot, "backups", r.Backup!, "main", "a.txt")));
        Assert.StartsWith(@"files\profiles\", r.Backup);
    }

    [Fact]
    public async Task A_text_save_with_a_stale_hash_is_a_conflict()
    {
        Write(P("main", "a.txt"), "theirs");
        var data = Encoding.UTF8.GetBytes("ours");
        var r = await RunAsync(New(), Spec(Profiles, FileOps.Write, @"main\a.txt", data: data, overwrite: true, expect: Sha(Encoding.UTF8.GetBytes("what we loaded"))), data);
        Assert.False(r.Ok);
        Assert.Contains("changed", r.Error);
        Assert.Equal("theirs", File.ReadAllText(P("main", "a.txt")));
    }

    [Fact]
    public async Task Delete_moves_the_item_into_backups()
    {
        Write(P("main", "BattlEye", "bans.txt"), "x");
        var r = await RunAsync(New(), Spec(Profiles, FileOps.Delete, @"main\BattlEye"));
        Assert.True(r.Ok, r.Error);
        Assert.False(Directory.Exists(P("main", "BattlEye")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(_dataRoot, "backups", r.Backup!, "main", "BattlEye", "bans.txt")));
    }

    [Fact]
    public async Task Delete_and_copy_refuse_a_folder_containing_a_link()
    {
        Directory.CreateDirectory(P("main"));
        Write(P("outside", "keep.txt"), "keep");
        Junction(P("main", "link"), P("outside"));
        var fm = New();

        Assert.False((await RunAsync(fm, Spec(Profiles, FileOps.Delete, "main"))).Ok);
        Assert.False((await RunAsync(fm, Spec(Profiles, FileOps.Copy, "main", "copy"))).Ok);
        Assert.True(File.Exists(P("outside", "keep.txt")));
        Assert.False(Directory.Exists(P("copy")));
    }

    [Fact]
    public void Move_and_copy_refuse_an_existing_target()
    {
        Write(P("a.txt"), "a");
        Write(P("b.txt"), "b");
        var fm = New();
        Assert.False(fm.Change(Spec(Profiles, FileOps.Move, "a.txt", "b.txt")).Ok);
        Assert.False(fm.Change(Spec(Profiles, FileOps.Copy, "a.txt", "b.txt")).Ok);
        Assert.Equal("b", File.ReadAllText(P("b.txt")));
    }

    [Fact]
    public async Task Move_renames_and_copy_copies_a_folder_tree()
    {
        Write(P("main", "BattlEye", "bans.txt"), "x");
        Write(P("main", "a.rpt"), "y");
        var fm = New();

        Assert.True((await RunAsync(fm, Spec(Profiles, FileOps.Copy, "main", "main-copy"))).Ok);
        Assert.True((await RunAsync(fm, Spec(Profiles, FileOps.Move, @"main\a.rpt", @"main\b.rpt"))).Ok);

        Assert.Equal("x", File.ReadAllText(P("main-copy", "BattlEye", "bans.txt")));
        Assert.Equal("y", File.ReadAllText(P("main-copy", "a.rpt")));
        Assert.True(File.Exists(P("main", "b.rpt")));
        Assert.False(File.Exists(P("main", "a.rpt")));
    }

    [Fact]
    public void The_space_root_and_missing_items_are_refused_at_once()
    {
        var fm = New();
        Assert.False(fm.Change(Spec(Profiles, FileOps.Delete, "")).Ok);
        Assert.False(fm.Change(Spec(Profiles, FileOps.Delete, "nothing-here")).Ok);
        Assert.False(fm.Change(Spec(Profiles, FileOps.Mkdir, @"..\escape")).Ok);
        Assert.Empty(_reports);
    }

    [Fact]
    public async Task Mkdir_makes_a_folder_and_refuses_one_that_exists()
    {
        var fm = New();
        Assert.True((await RunAsync(fm, Spec(Profiles, FileOps.Mkdir, @"main\BattlEye"))).Ok);
        Assert.True(Directory.Exists(P("main", "BattlEye")));
        Assert.False(fm.Change(Spec(Profiles, FileOps.Mkdir, @"main\BattlEye")).Ok);
    }

    private string Zip(string space, string name, params (string Entry, string Text)[] entries)
    {
        var path = space == "localmods" ? Path.Combine(_dataRoot, "localmods", name) : P(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(text);
        }
        return path;
    }

    [Fact]
    public async Task Extract_unzips_into_a_new_folder()
    {
        Zip("localmods", "mod.zip", (@"addons/x.pbo", "pbo"), ("mod.cpp", "name"), ("x_x64.dll", "dll"));
        var r = await RunAsync(New(), Spec(LocalMods, FileOps.Extract, "mod.zip", "@mod"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("pbo", File.ReadAllText(Path.Combine(_dataRoot, "localmods", "@mod", "addons", "x.pbo")));
        Assert.True(File.Exists(Path.Combine(_dataRoot, "localmods", "@mod", "x_x64.dll"))); // DLLs are fine in localmods
        Assert.False(New().Change(Spec(LocalMods, FileOps.Extract, "mod.zip", "@mod")).Ok); // the folder now exists
    }

    [Fact]
    public async Task Extract_refuses_an_entry_that_escapes()
    {
        Zip("profiles", "bad.zip", ("ok.txt", "ok"), (@"../../escaped.txt", "x"));
        var r = await RunAsync(New(), Spec(Profiles, FileOps.Extract, "bad.zip", "out"));
        Assert.False(r.Ok);
        Assert.False(Directory.Exists(P("out")));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "escaped.txt")));
    }

    [Fact]
    public async Task Extract_refuses_an_executable_outside_localmods()
    {
        Zip("profiles", "tools.zip", ("readme.txt", "x"), ("tool.exe", "MZ"));
        var r = await RunAsync(New(), Spec(Profiles, FileOps.Extract, "tools.zip", "tools"));
        Assert.False(r.Ok);
        Assert.Contains(".exe", r.Error);
        Assert.False(Directory.Exists(P("tools")));
    }

    [Fact]
    public async Task Extract_stops_at_the_size_cap_and_removes_the_partial_folder()
    {
        Zip("profiles", "big.zip", ("a.txt", new string('a', 600)), ("b.txt", new string('b', 600)));
        var r = await RunAsync(New(maxExtractBytes: 1000), Spec(Profiles, FileOps.Extract, "big.zip", "big"));
        Assert.False(r.Ok);
        Assert.False(Directory.Exists(P("big")));
        Assert.False(Directory.Exists(P(".13c-tmp")));

        Zip("profiles", "many.zip", ("1.txt", ""), ("2.txt", ""), ("3.txt", ""));
        Assert.False((await RunAsync(New(maxExtractEntries: 2), Spec(Profiles, FileOps.Extract, "many.zip", "many"))).Ok);
    }

    [Fact]
    public async Task Changes_run_one_at_a_time_and_each_is_reported()
    {
        _panel.Delay = TimeSpan.FromMilliseconds(150);
        var fm = New();
        var specs = Enumerable.Range(0, 3).Select(i => (Spec: Spec(Profiles, FileOps.Write, $"f{i}.txt", data: [(byte)i]), Data: new[] { (byte)i })).ToArray();
        foreach (var (spec, data) in specs) _panel.Transfers[spec.Id] = data;
        foreach (var (spec, _) in specs) Assert.True(fm.Change(spec).Ok);

        await Task.WhenAll(specs.Select(s => RunUntilReported(s.Spec.Id)));
        Assert.Equal(1, _panel.MostAtOnce);
        Assert.All(_reports, r => Assert.True(r.Ok, r.Error));
    }

    private async Task RunUntilReported(Guid id)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (_reports.All(r => r.Id != id))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    // Final review I-1: the 5,000-item cap counted items, not bytes; long names overflowed the hub's 1 MiB message and dropped the agent.
    [Fact]
    public void A_long_listing_stops_before_the_hub_limit()
    {
        Directory.CreateDirectory(P("big"));
        for (var i = 0; i < FileManager.MaxList; i++) File.WriteAllText(P("big", $"{i:D5}{new string('n', 145)}.txt"), "");

        var listing = New().List(Profiles, "big");

        Assert.True(listing.Truncated);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(listing, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.InRange(bytes.Length, 1, 1_000_000);
    }

    // Final review I-3: the entry cap was checked only after .NET had built every entry of the central directory.
    [Fact]
    public async Task Extract_refuses_a_zip_declaring_too_many_entries_before_opening_it()
    {
        var path = Zip("profiles", "crafted.zip", ("1.txt", "a"), ("2.txt", "b"), ("3.txt", "c"));
        var bytes = File.ReadAllBytes(path);
        var eocd = bytes.Length - 22;
        Assert.Equal(0x06054b50, BitConverter.ToInt32(bytes, eocd));
        var centralDirectory = BitConverter.ToInt32(bytes, eocd + 16);
        Array.Clear(bytes, centralDirectory, 4); // the directory is unreadable: only the declared count can refuse it
        File.WriteAllBytes(path, bytes);

        var r = await RunAsync(New(maxExtractEntries: 2), Spec(Profiles, FileOps.Extract, "crafted.zip", "crafted"));

        Assert.False(r.Ok);
        Assert.Contains("more than 2 entries", r.Error);
    }

    [Fact]
    public void Leftover_temp_folders_are_removed_at_start()
    {
        Write(P(".13c-tmp", "x", "partial.txt"), "x");
        Write(Path.Combine(_install, "mpmissions", ".13c-tmp", "y"), "y");
        New();
        Assert.False(Directory.Exists(P(".13c-tmp")));
        Assert.False(Directory.Exists(Path.Combine(_install, "mpmissions", ".13c-tmp")));
    }

    [Fact]
    public void Writes_follow_the_space_rules()
    {
        var fm = New();
        Assert.False(fm.Change(Spec(Missions, FileOps.Write, "x.exe", data: [1])).Ok);
        Assert.False(fm.Change(Spec(new FileSpace("configs", null), FileOps.Mkdir, "x")).Ok);
        Assert.True(fm.Change(Spec(Missions, FileOps.Write, "x.pbo", data: [1])).Ok);
    }
}
