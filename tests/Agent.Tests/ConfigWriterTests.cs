using System.Security.Cryptography;
using System.Text;
using Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using ServerAgent;
using ServerAgent.Games.Arma3;

namespace Agent.Tests;

public sealed class ConfigWriterTests : IDisposable
{
    private readonly SupervisorHarness _h = new();
    public void Dispose() => _h.Dispose();

    private const string Difficulty = "class DifficultyPresets\r\n{\r\n\tclass CustomDifficulty\r\n\t{\r\n\t\tclass Options\r\n\t\t{\r\n\t\t\tthirdPersonView=0;\r\n\t\t};\r\n\t};\r\n};\r\n";

    private (ConfigWriter Writer, Supervisor Sup) Create()
    {
        var sup = _h.Create();
        var options = new AgentOptions { DataRoot = _h.DataRoot };
        return (new ConfigWriter(new Areas(options, [_h.InstallRoot]), new Arma3Adapter(), sup, NullLogger<ConfigWriter>.Instance), sup);
    }

    private static ConfigBundle Bundle(string serverCfg = "hostname = \"A\";\r\n", string? basicCfg = "MaxMsgSend = 128;\r\n", string slug = "main") =>
        new(Guid.NewGuid(), slug, serverCfg, basicCfg, Difficulty);

    private string Path_(params string[] parts) => Path.Combine([_h.DataRoot, .. parts]);
    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Apply_writes_files_and_returns_their_hashes()
    {
        var (writer, _) = Create();
        var bundle = Bundle();

        var r = writer.Apply(bundle);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(bundle.ServerCfg, File.ReadAllText(Path_("configs", "main", "server.cfg")));
        Assert.Equal(bundle.BasicCfg, File.ReadAllText(Path_("configs", "main", "basic.cfg")));
        Assert.Equal(Difficulty, File.ReadAllText(Path_("profiles", "main", "Users", "main", "main.Arma3Profile")));
        Assert.Equal(Sha(bundle.ServerCfg), r.Files.Single(f => f.Name == "server.cfg").Sha256);
        Assert.Equal(Sha(bundle.BasicCfg!), r.Files.Single(f => f.Name == "basic.cfg").Sha256);
        Assert.Null(r.BackupFolder); // nothing existed to back up
        Assert.False(File.Exists(Path_("configs", "main", "server.cfg") + ".13c-tmp"));
    }

    [Fact]
    public void Apply_backs_up_existing_files_first()
    {
        var (writer, _) = Create();
        Assert.True(writer.Apply(Bundle(serverCfg: "hostname = \"Old\";\r\n")).Ok);

        var r = writer.Apply(Bundle(serverCfg: "hostname = \"New\";\r\n"));

        Assert.True(r.Ok, r.Error);
        Assert.NotNull(r.BackupFolder);
        Assert.Equal("hostname = \"Old\";\r\n", File.ReadAllText(Path.Combine(_h.DataRoot, "backups", r.BackupFolder!, "server.cfg")));
        Assert.True(File.Exists(Path.Combine(_h.DataRoot, "backups", r.BackupFolder!, "main.Arma3Profile")));
    }

    [Fact]
    public void Apply_merges_only_the_difficulty_class()
    {
        var (writer, _) = Create();
        var profile = Path_("profiles", "main", "Users", "main", "main.Arma3Profile");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "version=1;\r\nclass DifficultyPresets\r\n{\r\n\tclass Old {};\r\n};\r\nblood=1;\r\nviewDistance=1000;\r\n");

        Assert.True(writer.Apply(Bundle()).Ok);

        Assert.Equal("version=1;\r\n" + Difficulty + "blood=1;\r\nviewDistance=1000;\r\n", File.ReadAllText(profile));
    }

    [Fact]
    public async Task Apply_is_refused_while_running()
    {
        var (writer, sup) = Create();
        var spec = _h.Spec();
        Assert.True((await sup.StartAsync(spec)).Ok);

        var r = writer.Apply(Bundle() with { ProfileId = spec.ProfileId });

        Assert.False(r.Ok);
        Assert.Contains("Stop the server", r.Error);
    }

    [Fact]
    public void Apply_is_all_or_nothing()
    {
        var (writer, _) = Create();
        Assert.True(writer.Apply(Bundle(serverCfg: "hostname = \"Old\";\r\n")).Ok);
        var basic = Path_("configs", "main", "basic.cfg");

        ApplyResult r;
        using (new FileStream(basic, FileMode.Open, FileAccess.Read, FileShare.Read)) // readable for the backup, but can't be replaced
            r = writer.Apply(Bundle(serverCfg: "hostname = \"New\";\r\n"));

        Assert.False(r.Ok);
        Assert.Contains("basic.cfg", r.Error);
        Assert.Equal("hostname = \"Old\";\r\n", File.ReadAllText(Path_("configs", "main", "server.cfg")));
        Assert.NotNull(r.BackupFolder);
        Assert.True(File.Exists(Path.Combine(_h.DataRoot, "backups", r.BackupFolder!, "server.cfg")));
        Assert.Empty(Directory.GetFiles(Path_("configs", "main"), "*.13c-tmp"));
    }

    [Fact]
    public void Null_basic_cfg_removes_the_file()
    {
        var (writer, _) = Create();
        Assert.True(writer.Apply(Bundle()).Ok);

        var r = writer.Apply(Bundle(basicCfg: null));

        Assert.True(r.Ok, r.Error);
        Assert.False(File.Exists(Path_("configs", "main", "basic.cfg")));
        Assert.True(File.Exists(Path.Combine(_h.DataRoot, "backups", r.BackupFolder!, "basic.cfg")));
        Assert.DoesNotContain(r.Files, f => f.Name == "basic.cfg");
    }

    [Fact]
    public void Read_returns_null_for_missing_files()
    {
        var (writer, _) = Create();
        var empty = writer.Read(Guid.NewGuid(), "main");
        Assert.Equal((null, null, null), (empty.ServerCfg, empty.BasicCfg, empty.Arma3Profile));

        Assert.True(writer.Apply(Bundle(basicCfg: null)).Ok);
        var read = writer.Read(Guid.NewGuid(), "main");
        Assert.Equal("hostname = \"A\";\r\n", read.ServerCfg);
        Assert.Null(read.BasicCfg);
        Assert.Equal(Difficulty, read.Arma3Profile);
    }

    [Fact]
    public void Apply_refuses_a_bad_slug_or_difficulty_text()
    {
        var (writer, _) = Create();
        Assert.False(writer.Apply(Bundle(slug: @"..\x")).Ok);
        Assert.False(writer.Apply(Bundle() with { DifficultyPresets = "class Other {};" }).Ok);
    }

    [Fact]
    public void Display_quotes_paths_and_masks_passwords()
    {
        var line = Arma3Command.Display(@"K:\Steam\Arma 3\arma3server_x64.exe", ["-port=2302", @"-config=D:\a b\server.cfg", "-password=secret"]);
        Assert.Equal("\"K:\\Steam\\Arma 3\\arma3server_x64.exe\" -port=2302 \"-config=D:\\a b\\server.cfg\" -password=********", line);
    }
}
