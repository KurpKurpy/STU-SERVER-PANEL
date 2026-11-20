using Contracts;
using ServerAgent;
using ServerAgent.Mods;

namespace Agent.Tests;

public sealed class ModStoreTests : IDisposable
{
    private readonly SupervisorHarness _h = new();
    public void Dispose() => _h.Dispose();

    private Areas Areas() => new(new AgentOptions { DataRoot = _h.DataRoot, InstallRoots = [_h.InstallRoot] }, [_h.InstallRoot]);

    private string Workshop(long id, string? name = null, params string[] keys)
    {
        var folder = Path.Combine(_h.DataRoot, "workshop", "steamapps", "workshop", "content", "107410", id.ToString());
        Directory.CreateDirectory(Path.Combine(folder, "addons"));
        File.WriteAllBytes(Path.Combine(folder, "addons", "x.pbo"), new byte[100]);
        if (name is not null) File.WriteAllText(Path.Combine(folder, "meta.cpp"), $"protocol = 1;\npublishedid = {id};\nname = \"{name}\";\n");
        foreach (var key in keys)
        {
            Directory.CreateDirectory(Path.Combine(folder, "keys"));
            File.WriteAllText(Path.Combine(folder, "keys", key), key);
        }
        return folder;
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(5L, "@x")]
    [InlineData(0L, null)]
    [InlineData(null, "..")]
    [InlineData(null, "a..b")]
    [InlineData(null, @"a\b")]
    [InlineData(null, "C:x")]
    [InlineData(null, "a;b")]
    [InlineData(null, "a\"b")]
    [InlineData(null, "")]
    public void Mod_refs_cannot_escape_the_roots(long? id, string? local) =>
        Assert.NotNull(ModRef.Problem(new ModRef(id, local)));

    [Theory]
    [InlineData(450814997L, null)]
    [InlineData(null, "@ace")]
    [InlineData(null, "@Unit Mod_1.2-final")]
    public void Valid_mod_refs_pass(long? id, string? local) =>
        Assert.Null(ModRef.Problem(new ModRef(id, local)));

    [Fact]
    public void ListMods_reports_workshop_and_local_mods_with_keys()
    {
        var areas = Areas();
        Workshop(450814997, "CBA_A3", "cba_a3.bikey");
        Directory.CreateDirectory(Path.Combine(areas.WorkshopContent, "not-a-number"));
        var local = Path.Combine(areas.LocalMods, "@unit");
        Directory.CreateDirectory(Path.Combine(local, "Keys"));
        File.WriteAllText(Path.Combine(local, "mod.cpp"), "name = \"Unit Mod\";");
        File.WriteAllText(Path.Combine(local, "Keys", "unit.bikey"), "k");

        var mods = new ModStore(areas).List();

        Assert.Equal(2, mods.Length);
        var cba = mods.Single(m => m.Ref.WorkshopId == 450814997);
        Assert.Equal("CBA_A3", cba.Name);
        Assert.Equal(["cba_a3.bikey"], cba.KeyFiles);
        Assert.True(cba.SizeBytes >= 100);
        var unit = mods.Single(m => m.Ref.LocalFolder == "@unit");
        Assert.Equal("Unit Mod", unit.Name);
        Assert.Equal(["unit.bikey"], unit.KeyFiles);
    }

    [Fact]
    public void Mod_arguments_are_generated()
    {
        var spec = _h.Spec() with
        {
            Mods = [new ModRef(1, null), new ModRef(null, "@a b")],
            ServerMods = [new ModRef(2, null)],
            HeadlessMods = [new ModRef(3, null)],
            Headless = new HeadlessSpec(1, "127.0.0.1", null, false),
        };
        static string Path(ModRef r) => r.WorkshopId is { } id ? $@"W:\{id}" : $@"L:\{r.LocalFolder}";

        var server = Arma3Command.ServerArguments(spec, "C", "P", Path);
        var hc = Arma3Command.HeadlessArguments(spec, 1, "P", Path);

        Assert.Contains(@"-mod=W:\1;L:\@a b", server);
        Assert.Contains(@"-serverMod=W:\2", server);
        Assert.Contains(@"-mod=W:\3", hc);
        Assert.DoesNotContain(Arma3Command.ServerArguments(_h.Spec(), "C", "P", Path), a => a.StartsWith("-mod") || a.StartsWith("-serverMod"));
        // Shown as one quoted argument; the agent passes it as one ArgumentList entry.
        Assert.Contains("\"-mod=W:\\1;L:\\@a b\"", Arma3Command.Display("x.exe", server));
    }

    [Fact]
    public async Task Start_refuses_a_missing_mod()
    {
        var sup = _h.Create();
        var r = await sup.StartAsync(_h.Spec() with { Mods = [new ModRef(999, null)] });

        Assert.False(r.Ok);
        Assert.Contains("999", r.Error);
    }

    [Fact]
    public async Task Start_passes_the_mod_folders_to_the_server()
    {
        var folder = Workshop(5, "Five");
        var sup = _h.Create();
        var spec = _h.Spec() with { Mods = [new ModRef(5, null)] };

        Assert.True((await sup.StartAsync(spec)).Ok);
        await SupervisorHarness.WaitAsync(sup, spec.ProfileId, s => s.State == ServerState.Online);

        var rpt = Directory.GetFiles(Path.Combine(_h.DataRoot, "profiles", "main"), "*.rpt").Single();
        using var reader = new StreamReader(new FileStream(rpt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.Contains($"-mod={folder}", reader.ReadToEnd().Split("\r\n").First(l => l.StartsWith("ARGS:")));
    }
}
