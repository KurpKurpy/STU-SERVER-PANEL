using System.Text;
using ArmaFormats;
using ArmaFormats.Tests;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Mods;
using static ArmaFormats.Tests.RapBuilder;

namespace Agent.Tests;

public class AddonScannerTests
{
    private readonly string _dataRoot = TempDir.Create();
    private readonly string _install = TempDir.Create();
    private readonly string _readOnly = TempDir.Create();

    private AddonScanner New(out Areas areas)
    {
        areas = new Areas(new AgentOptions { DataRoot = _dataRoot, ReadOnlyInstallRoots = [_readOnly] }, [_install, _readOnly]);
        return new AddonScanner(areas, new ModStore(areas), NullLogger<AddonScanner>.Instance);
    }

    private static void Pbo(string addonsDir, string name, params (string Path, byte[] Data)[] entries)
    {
        Directory.CreateDirectory(addonsDir);
        using var fs = File.Create(Path.Combine(addonsDir, name));
        ArmaFormats.Pbo.Write(new PboFile(new Dictionary<string, string>(), entries.Select(e => new PboEntry(e.Path, e.Data, 0)).ToList()), fs);
    }

    private static byte[] Patches(params string[] names) => Build([Class("CfgPatches", names.Select(n => (ConfigItem)Class(n)).ToArray())]);

    private static async Task<List<AddonScan>> ScanAsync(AddonScanner scanner, AddonScanRequest request, int expected)
    {
        var seen = new List<AddonScan>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.Scanned += s => { lock (seen) { seen.Add(s); if (seen.Count == expected) done.TrySetResult(); } };
        Assert.True(scanner.Enqueue(request).Ok);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return seen;
    }

    [Fact]
    public async Task Scans_a_mod_and_reports_its_addons()
    {
        var scanner = New(out var areas);
        var mod = Path.Combine(areas.WorkshopContent, "123", "addons");
        Pbo(mod, "a.pbo", ("config.bin", Patches("mod_a")));
        Pbo(mod, "b.pbo", ("sub/config.cpp", Encoding.UTF8.GetBytes("class CfgPatches { class mod_b {}; }; class CfgWorlds { class MyIsland {}; };")));

        var scan = Assert.Single(await ScanAsync(scanner, new AddonScanRequest([new ModRef(123, null)], []), 1));

        Assert.Equal(new ModRef(123, null), scan.Mod);
        Assert.Null(scan.Error);
        var folder = Assert.Single(scan.Folders);
        Assert.Equal("", folder.Folder);
        Assert.Equal(["mod_a", "mod_b"], folder.Addons);
        Assert.Equal(["MyIsland"], folder.Worlds);
        Assert.Empty(folder.Unreadable);
    }

    [Fact]
    public async Task Scans_an_install_by_folder()
    {
        var scanner = New(out _);
        Pbo(Path.Combine(_install, "addons"), "core.pbo", ("config.bin", Patches("A3_Core")));
        Pbo(Path.Combine(_install, "expansion", "addons"), "exp.pbo", ("config.bin", Patches("A3_Exp")));
        Pbo(Path.Combine(_install, "@localmod", "addons"), "x.pbo", ("config.bin", Patches("not_base")));   // a mod, loaded only by -mod
        Pbo(Path.Combine(_install, "!Workshop", "@m", "addons"), "y.pbo", ("config.bin", Patches("not_base_either")));

        var scan = Assert.Single(await ScanAsync(scanner, new AddonScanRequest([], [_install]), 1));

        Assert.Equal(_install, scan.InstallRoot, ignoreCase: true);
        Assert.Equal(["", "expansion"], scan.Folders.Select(f => f.Folder));
        Assert.Equal(["A3_Core"], scan.Folders[0].Addons);
        Assert.Equal(["A3_Exp"], scan.Folders[1].Addons);
    }

    [Fact]
    public async Task A_bad_pbo_is_marked_unreadable_and_the_scan_goes_on()
    {
        var scanner = New(out var areas);
        var mod = Path.Combine(areas.WorkshopContent, "5", "addons");
        Directory.CreateDirectory(mod);
        File.WriteAllBytes(Path.Combine(mod, "bad.pbo"), [1, 2, 3, 4, 5]);
        File.WriteAllBytes(Path.Combine(mod, "dlc.ebo"), [1, 2, 3]);
        Pbo(mod, "good.pbo", ("config.bin", Patches("good_one")));

        var folder = Assert.Single(Assert.Single(await ScanAsync(scanner, new AddonScanRequest([new ModRef(5, null)], []), 1)).Folders);

        Assert.Equal(["good_one"], folder.Addons);
        Assert.Contains(folder.Unreadable, u => u.StartsWith("bad.pbo: "));
        Assert.Contains("dlc.ebo: encrypted", folder.Unreadable);
    }

    [Fact]
    public void Invalid_refs_are_refused()
    {
        var scanner = New(out _);
        Assert.False(scanner.Enqueue(new AddonScanRequest([new ModRef(-1, null)], [])).Ok);
        Assert.False(scanner.Enqueue(new AddonScanRequest([new ModRef(null, "..")], [])).Ok);
    }

    // Final review M-4 (graded up): one root the agent no longer lists refused the whole batch, so no automatic scan ever ran.
    [Fact]
    public async Task An_unknown_root_is_reported_failed_and_the_rest_is_scanned()
    {
        var scanner = New(out var areas);
        Pbo(Path.Combine(areas.WorkshopContent, "8", "addons"), "a.pbo", ("config.bin", Patches("eight")));
        var gone = TempDir.Create();

        var scans = await ScanAsync(scanner, new AddonScanRequest([new ModRef(8, null)], [gone]), 2);

        Assert.Contains(scans, s => s.InstallRoot == gone && s.Error is not null && s.Folders.Length == 0);
        Assert.Contains(scans, s => s.Mod == new ModRef(8, null) && s.Folders[0].Addons.SequenceEqual(["eight"]));
    }

    // Final review I-6: a report over the hub's 1 MiB limit closed the link in a loop; names are capped per folder, and long ones dropped.
    [Fact]
    public async Task Names_are_capped_per_folder_and_overlong_names_dropped()
    {
        var scanner = New(out var areas);
        var classes = string.Concat(Enumerable.Range(0, AddonScanner.MaxNames + 50).Select(i => $" class n{i} {{}};"));
        Pbo(Path.Combine(areas.WorkshopContent, "9", "addons"), "big.pbo",
            ("config.cpp", Encoding.UTF8.GetBytes($"class CfgPatches {{ class {new string('x', 300)} {{}};{classes} }};")));

        var folder = Assert.Single(Assert.Single(await ScanAsync(scanner, new AddonScanRequest([new ModRef(9, null)], []), 1)).Folders);

        Assert.Equal(AddonScanner.MaxNames, folder.Addons.Length);
        Assert.DoesNotContain(folder.Addons, a => a.Length > 200);
        Assert.Contains(folder.Unreadable, u => u.Contains("too many"));
    }

    [Fact]
    public async Task Read_only_installs_can_be_scanned()
    {
        var scanner = New(out _);
        Pbo(Path.Combine(_readOnly, "addons"), "core.pbo", ("config.bin", Patches("A3_ReadOnly")));

        var scan = Assert.Single(await ScanAsync(scanner, new AddonScanRequest([], [_readOnly]), 1));

        Assert.Equal(["A3_ReadOnly"], Assert.Single(scan.Folders).Addons);
    }

    [Fact]
    public async Task A_mod_that_isnt_installed_reports_an_error_and_each_target_is_reported()
    {
        var scanner = New(out var areas);
        Pbo(Path.Combine(areas.LocalMods, "@here", "addons"), "a.pbo", ("config.bin", Patches("here_a")));

        var scans = await ScanAsync(scanner, new AddonScanRequest([new ModRef(999, null), new ModRef(null, "@here")], [_install]), 3);

        Assert.Contains(scans, s => s.Mod == new ModRef(999, null) && s.Error is not null && s.Folders.Length == 0);
        Assert.Contains(scans, s => s.Mod == new ModRef(null, "@here") && s.Folders[0].Addons.SequenceEqual(["here_a"]));
        Assert.Contains(scans, s => s.InstallRoot is not null && s.Error is null);
    }
}
