using System.Text.Json;
using ArmaFormats;
using Panel.Features.Profiles.Config;

namespace Panel.Tests;

public class ConfigFilesTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static ProfileConfig From(ParsedFile server, ParsedFile? basic = null, ParsedFile? difficulty = null) =>
        new(new ProfileSettings(server.Values, basic?.Values ?? [], difficulty?.Values ?? [], [], "", server.Headless ?? new HeadlessSettings(), server.MissionsOverride),
            server.Extra, basic?.Extra ?? "", difficulty?.Extra ?? "", server.Rotation ?? [], server.Secrets ?? Secrets.None);

    private static ProfileConfig Config(Dictionary<string, JsonElement>? server = null, Dictionary<string, JsonElement>? basic = null,
        Dictionary<string, JsonElement>? difficulty = null, Dictionary<string, JsonElement>? launch = null, string extraArgs = "",
        HeadlessSettings? headless = null, IReadOnlyList<RotationItem>? rotation = null, Secrets? secrets = null) =>
        new(new ProfileSettings(server ?? [], basic ?? [], difficulty ?? [], launch ?? [], extraArgs, headless ?? new HeadlessSettings(), null),
            "", "", "", rotation ?? [], secrets ?? Secrets.None);

    private const string RealStyle =
        "// Unit server\r\nhostname = \"13th Test\";\r\nmaxPlayers = 40;\r\nBattlEye = 0;\r\nverifySignatures = 2;\r\npersistent = 1;\r\n"
        + "voteThreshold = 0.33;\r\npasswordAdmin = \"admin-secret\";\r\nkickTimeout[] = { {0, -1}, {1, 180} };\r\n"
        + "class AdvancedOptions\r\n{\r\n\tlogObjectNotFound = false;\r\n\tcustomThing = 1;\r\n};\r\n"
        + "class Missions\r\n{\r\n\tclass A\r\n\t{\r\n\t\ttemplate = \"co40_Kestrel.Altis\";\r\n\t\tdifficulty = \"Custom\";\r\n\t};\r\n"
        + "\tclass B\r\n\t{\r\n\t\ttemplate = \"MP_CombatPatrol_01.Altis\";\r\n\t\tdifficulty = \"Regular\";\r\n\t\tclass Params\r\n\t\t{\r\n\t\t\tBIS_CP_tickets = 5;\r\n\t\t};\r\n\t};\r\n};\r\n";

    [Fact]
    public void Real_style_server_cfg_round_trips_without_loss()
    {
        var p = ConfigFiles.ParseServerCfg(RealStyle);

        Assert.Equal("13th Test", p.Values["hostname"].GetString());
        Assert.Equal(40, p.Values["maxPlayers"].GetInt64());
        Assert.False(p.Values["BattlEye"].GetBoolean());
        Assert.Equal(2, p.Values["verifySignatures"].GetInt64());
        Assert.True(p.Values["persistent"].GetBoolean());
        Assert.Equal(0.33, p.Values["voteThreshold"].GetDouble());
        Assert.Equal("admin-secret", p.Secrets!.PasswordAdmin);
        Assert.Contains("// Unit server\r\n", p.Extra);
        Assert.Contains("kickTimeout[] = { {0, -1}, {1, 180} };\r\n", p.Extra);
        Assert.Contains("class AdvancedOptions\r\n{\r\n\tlogObjectNotFound = false;\r\n\tcustomThing = 1;\r\n};\r\n", p.Extra);
        var rotation = Assert.IsAssignableFrom<IReadOnlyList<RotationItem>>(p.Rotation);
        Assert.Equal(["co40_Kestrel.Altis", "MP_CombatPatrol_01.Altis"], rotation.Select(r => r.Template));
        Assert.Equal("BIS_CP_tickets = 5;", rotation[1].ParamsText.Trim());

        var text = ConfigFiles.Generate(From(p), SecretMode.Real).ServerCfg;
        Assert.Contains("passwordAdmin = \"admin-secret\";", text);
        Assert.Equal(text, ConfigFiles.Generate(From(ConfigFiles.ParseServerCfg(text)), SecretMode.Real).ServerCfg);
    }

    [Fact]
    public void Missions_with_extra_entries_become_an_override()
    {
        const string missions = "class Missions\r\n{\r\n\tclass Apex {};\r\n};\r\n";
        var p = ConfigFiles.ParseServerCfg("hostname = \"x\";\r\n" + missions);
        Assert.Null(p.Rotation);
        Assert.Equal(missions, p.MissionsOverride);
        Assert.Contains(missions, ConfigFiles.Generate(From(p), SecretMode.Real).ServerCfg);
    }

    [Fact]
    public void Duplicate_managed_key_is_refused_with_both_lines()
    {
        var e = Assert.Throws<ConfigSyntaxException>(() => ConfigFiles.ParseServerCfg("hostname = \"a\";\nmaxPlayers = 2;\nHostName = \"b\";\n"));
        Assert.Equal(3, e.Line);
        Assert.Contains("lines 1 and 3", e.Message);
    }

    [Fact]
    public void Form_values_out_of_range_are_field_errors()
    {
        var errors = ConfigFiles.Validate(Config(server: new() { ["maxPlayers"] = J(0) }, launch: new() { ["-maxMem"] = J(512) }));
        Assert.Contains(errors, e => e.Field == "server.maxPlayers");
        Assert.Contains(errors, e => e.Field == "launch.-maxMem");
        Assert.Contains(ConfigFiles.Validate(Config(server: new() { ["nonsense"] = J(1) })), e => e.Field == "server.nonsense");
        Assert.Empty(ConfigFiles.Validate(Config(server: new() { ["maxPlayers"] = J(40), ["forcedDifficulty"] = J("regular") })));
    }

    [Fact]
    public void File_values_out_of_range_stay_in_extras_with_a_warning()
    {
        var p = ConfigFiles.ParseServerCfg("vonCodecQuality = 40;\r\nmaxPlayers = \"x\";\r\nforcedDifficulty = \"regular\";\r\nverifySignatures = 1;\r\n");
        Assert.Contains("vonCodecQuality = 40;\r\n", p.Extra);
        Assert.Contains("maxPlayers = \"x\";\r\n", p.Extra);
        Assert.Equal([1, 2], p.Warnings.Select(w => w.Line));
        Assert.Equal("regular", p.Values["forcedDifficulty"].GetString());
        Assert.Equal(1, p.Values["verifySignatures"].GetInt64());
        Assert.False(p.Values.ContainsKey("vonCodecQuality"));
    }

    [Fact]
    public void Motd_keeps_empty_lines()
    {
        var p = ConfigFiles.ParseServerCfg("motd[] = {\"\", \"\", \"Welcome\"};\r\n");
        Assert.Equal(["", "", "Welcome"], p.Values["motd"].EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("motd[] = {\"\", \"\", \"Welcome\"};", ConfigFiles.Generate(From(p), SecretMode.Real).ServerCfg);
    }

    [Fact]
    public void Omit_mode_never_contains_a_password()
    {
        var config = Config(server: new() { ["hostname"] = J("x") }, secrets: new Secrets("hunter2-a", "hunter2-b", "hunter2-c"));
        var text = ConfigFiles.Generate(config, SecretMode.Omit).ServerCfg;
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains(ConfigFiles.PasswordsComment, text);
        // Saving the shown text back must not turn the comment into an extra.
        Assert.DoesNotContain("Passwords are set", ConfigFiles.ParseServerCfg(text).Extra);
    }

    [Fact]
    public void Real_mode_writes_them_quoted()
    {
        var text = ConfigFiles.Generate(Config(secrets: new Secrets("a\"b", null, "")), SecretMode.Real).ServerCfg;
        Assert.Contains("password = \"a\"\"b\";", text);
        Assert.DoesNotContain("passwordAdmin", text);
        Assert.DoesNotContain("serverCommandPassword", text); // empty means not set
    }

    [Fact]
    public void Basic_cfg_is_null_when_nothing_is_set()
    {
        Assert.Null(ConfigFiles.Generate(Config(), SecretMode.Real).BasicCfg);
        Assert.Contains("MaxMsgSend = 256;", ConfigFiles.Generate(Config(basic: new() { ["MaxMsgSend"] = J(256) }), SecretMode.Real).BasicCfg);
        var withSockets = ConfigFiles.Generate(Config(basic: new() { ["sockets.maxPacketSize"] = J(1300) }), SecretMode.Real).BasicCfg;
        Assert.Contains("class sockets\r\n{\r\n\tmaxPacketSize = 1300;\r\n};", withSockets);
    }

    private const string BohemiaDifficultyExample =
        "class DifficultyPresets\r\n{\r\n\tclass CustomDifficulty\r\n\t{\r\n\t\tclass Options\r\n\t\t{\r\n\t\t\t/* Simulation */\r\n\r\n\t\t\treducedDamage = 0;\t\t// Reduced damage\r\n"
        + "\t\t\tgroupIndicators = 0;\r\n\t\t\tthirdPersonView = 0;\t// 3rd person view\r\n\t\t\tmapContent = 0;\t\t\t// Extended map content\r\n\t\t\tmultipleSaves = 0;\r\n\t\t};\r\n"
        + "\t\t// aiLevelPreset defines AI skill level\r\n\t\taiLevelPreset = 3;\r\n\t};\r\n\tclass CustomAILevel\r\n\t{\r\n\t\tskillAI = 0.5;\r\n\t\tprecisionAI = 0.5;\r\n\t};\r\n};\r\n";

    [Fact]
    public void Bohemia_difficulty_example_parses_into_values()
    {
        var p = ConfigFiles.ParseDifficulty(BohemiaDifficultyExample);
        Assert.Equal("", p.Extra);
        Assert.Equal(0, p.Values["reducedDamage"].GetInt64());
        Assert.Equal(0, p.Values["mapContent"].GetInt64());
        Assert.Equal(3, p.Values["CustomDifficulty.aiLevelPreset"].GetInt64());
        Assert.Equal(0.5, p.Values["CustomAILevel.skillAI"].GetDouble());
        Assert.Contains(p.Warnings, w => w.Message.Contains("Comments"));
    }

    [Fact]
    public void Difficulty_class_with_unknown_members_becomes_an_override()
    {
        var text = BohemiaDifficultyExample.Replace("multipleSaves = 0;", "multipleSaves = 0;\r\n\t\t\tfutureOption = 1;");
        var p = ConfigFiles.ParseDifficulty(text);
        Assert.Empty(p.Values);
        Assert.Equal(text, p.Extra);
        Assert.Equal(text, ConfigFiles.Generate(From(ConfigFiles.ParseServerCfg(""), difficulty: p), SecretMode.Real).DifficultyPresets);
    }

    [Fact]
    public void Difficulty_class_uses_the_servers_own_format()
    {
        // Spike F20: what the server wrote back for the class we gave it.
        var server = "class DifficultyPresets\r\n{\r\n\tclass CustomDifficulty\r\n\t{\r\n\t\tclass Options\r\n\t\t{\r\n\t\t\treducedDamage=0;\r\n\t\t\tthirdPersonView=0;\r\n"
            + "\t\t\tmapContentEnemy=0;\r\n\t\t};\r\n\t\taiLevelPreset=1;\r\n\t};\r\n\tclass CustomAILevel\r\n\t{\r\n\t\tskillAI=0.5;\r\n\t\tprecisionAI=0.5;\r\n\t};\r\n};\r\n";
        var p = ConfigFiles.ParseDifficulty(server);
        Assert.Equal(server, ConfigFiles.Generate(From(ConfigFiles.ParseServerCfg(""), difficulty: p), SecretMode.Real).DifficultyPresets);
    }

    [Fact]
    public void Class_with_unknown_members_stays_in_extras()
    {
        const string unknown = "class AntiFlood { cycleTime = 1; fake = 2; };\r\n";
        var raw = ConfigFiles.ParseServerCfg(unknown);
        Assert.DoesNotContain(raw.Values.Keys, k => k.StartsWith("AntiFlood."));
        Assert.Equal(unknown, raw.Extra);

        var known = ConfigFiles.ParseServerCfg("class AntiFlood { cycleTime = 1; enableKick = 1; };\r\n");
        Assert.Equal(1, known.Values["AntiFlood.cycleTime"].GetDouble());
        Assert.True(known.Values["AntiFlood.enableKick"].GetBoolean());
        Assert.Equal("", known.Extra);
        Assert.Contains("class AntiFlood\r\n{\r\n\tcycleTime = 1;\r\n\tenableKick = 1;\r\n};", ConfigFiles.Generate(From(known), SecretMode.Real).ServerCfg);
    }

    [Fact]
    public void Empty_list_is_kept_distinct_from_unset()
    {
        var p = ConfigFiles.ParseServerCfg("allowedLoadFileExtensions[] = {};\r\n");
        Assert.Equal(0, p.Values["allowedLoadFileExtensions"].GetArrayLength());
        Assert.Contains("allowedLoadFileExtensions[] = {};", ConfigFiles.Generate(From(p), SecretMode.Real).ServerCfg);
        Assert.DoesNotContain("allowedLoadFileExtensions", ConfigFiles.Generate(Config(), SecretMode.Real).ServerCfg);
    }

    [Fact]
    public void Restart_and_shutdown_counts_cannot_combine()
    {
        var errors = ConfigFiles.Validate(Config(server: new() { ["missionsToServerRestart"] = J(8), ["missionsToShutdown"] = J(8) }));
        Assert.Contains(errors, e => e.Field == "server.missionsToShutdown");
        Assert.Empty(ConfigFiles.Validate(Config(server: new() { ["missionsToServerRestart"] = J(8), ["missionsToShutdown"] = J(0) })));
    }

    [Fact]
    public void Numbers_are_written_without_exponents()
    {
        var basic = ConfigFiles.Generate(Config(basic: new() { ["MaxBandwidth"] = J(10000000000L), ["MinErrorToSend"] = J(0.001) }), SecretMode.Real).BasicCfg!;
        Assert.Contains("MaxBandwidth = 10000000000;", basic);
        Assert.Contains("MinErrorToSend = 0.001;", basic);
    }

    [Fact]
    public void Missions_class_is_written_lowercase_with_template_and_difficulty()
    {
        var text = ConfigFiles.Generate(Config(server: new() { ["randomMissionOrder"] = J(true) },
            rotation: [new RotationItem("co40_Kestrel.Altis", "Custom", "RespawnDelay = 15;\r\n")]), SecretMode.Real).ServerCfg;
        Assert.Contains("class Missions\r\n{\r\n\tclass Mission_01\r\n\t{\r\n\t\ttemplate = \"co40_Kestrel.Altis\";\r\n\t\tdifficulty = \"Custom\";\r\n"
            + "\t\tclass Params\r\n\t\t{\r\n\t\t\tRespawnDelay = 15;\r\n\t\t};\r\n\t};\r\n};", text);
        Assert.True(text.IndexOf("randomMissionOrder = true;", StringComparison.Ordinal) < text.IndexOf("class Missions", StringComparison.Ordinal));
        var errors = ConfigFiles.Validate(Config(rotation: [new RotationItem("x.Altis", "Bad Name", "")]));
        Assert.Contains(errors, e => e.Field == "rotation.0.difficulty");
    }

    [Fact]
    public void Launch_arguments_follow_catalog_order_then_extra_args()
    {
        var args = ConfigFiles.LaunchArguments(Config(launch: new() { ["-maxMem"] = J(8192), ["-autoInit"] = J(true) }, extraArgs: "-malloc=system").Settings);
        Assert.Equal(["-autoInit", "-maxMem=8192", "-malloc=system"], args);
    }

    [Fact]
    public void Extra_args_must_pass_the_allowlist()
    {
        Assert.Contains(ConfigFiles.Validate(Config(extraArgs: "-profiles=C:\\x")), e => e.Field == "launch.extraArgs");
        Assert.Contains(ConfigFiles.Validate(Config(launch: new() { ["-maxMem"] = J(8192) }, extraArgs: "-maxMem=4096")), e => e.Field == "launch.extraArgs");
    }

    [Fact]
    public void Headless_addresses_generate_both_arrays()
    {
        var text = ConfigFiles.Generate(Config(headless: new HeadlessSettings(true, 2, false, ["127.0.0.1", "10.0.0.5"])), SecretMode.Real).ServerCfg;
        Assert.Contains("headlessClients[] = {\"127.0.0.1\", \"10.0.0.5\"};", text);
        Assert.Contains("localClient[] = {\"127.0.0.1\", \"10.0.0.5\"};", text);
        var parsed = ConfigFiles.ParseServerCfg(text);
        Assert.True(parsed.Headless!.Enabled);
        Assert.Equal(["127.0.0.1", "10.0.0.5"], parsed.Headless.Addresses!);
    }

    [Fact]
    public void Unquoted_passwords_are_lifted_as_secrets()
    {
        // Review C1: an unquoted value used to stay in the extras, in clear.
        var p = ConfigFiles.ParseServerCfg("passwordAdmin = 987654321;\r\npassword = hunter2word;\r\n");
        Assert.Equal(("987654321", "hunter2word"), (p.Secrets!.PasswordAdmin, p.Secrets.Password));
        Assert.DoesNotContain("987654321", p.Extra);
        Assert.DoesNotContain("hunter2word", p.Extra);
        Assert.Contains("passwordAdmin = \"987654321\";", ConfigFiles.Generate(From(p), SecretMode.Real).ServerCfg);
    }

    [Fact]
    public void MaskSecrets_masks_secrets_anywhere_on_a_line()
    {
        // Review I8: the agent-read file can hold secrets mid-line, in comments, or with the value on the next line.
        const string text = "hostname=\"x\"; password=\"mid-line\";\r\n// passwordAdmin = \"in-comment\";\r\nserverCommandPassword =\r\n\"next-line\";\r\nfake_password = \"keep\";\r\n";
        var masked = ConfigFiles.MaskSecrets(text);
        foreach (var leak in new[] { "mid-line", "in-comment", "next-line" }) Assert.DoesNotContain(leak, masked);
        Assert.Contains("fake_password = \"keep\";", masked);
    }

    [Fact]
    public void Non_finite_numbers_stay_raw()
    {
        // Review (re-graded): 1e999 / NaN used to throw while turning the value into JSON.
        var p = ConfigFiles.ParseServerCfg("MaxPing = 1e999;\r\nvoteThreshold = NaN;\r\nmaxPlayers = 8;\r\n");
        Assert.Contains("MaxPing = 1e999;", p.Extra);
        Assert.Contains("voteThreshold = NaN;", p.Extra);
        Assert.Equal(8, p.Values["maxPlayers"].GetInt64());
        Assert.Equal(2, p.Warnings.Count);
    }

    [Fact]
    public void MaskSecrets_marks_changed_values()
    {
        const string text = "hostname = \"x\";\r\npassword = \"old\";\r\n\tPasswordAdmin=\"a\"\"b\"; // admin\r\n";
        var masked = ConfigFiles.MaskSecrets(text);
        Assert.Contains("password = \"********\";", masked);
        Assert.Contains("\tPasswordAdmin=\"********\"; // admin", masked);
        Assert.DoesNotContain("old", masked);
        var compared = ConfigFiles.MaskSecrets(text, new Secrets("new", "a\"b", null));
        Assert.Contains("password = \"******** (changed)\";", compared);
        Assert.Contains("PasswordAdmin=\"********\";", compared);
    }
}
