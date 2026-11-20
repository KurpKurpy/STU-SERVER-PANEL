using System.Text.Json;

namespace Panel.Features.Profiles.Config;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CfgType>))]
public enum CfgType { Text, Integer, Number, Bool01, BoolWord, TextList, NumberList, Flag }

// One config key the forms understand. A dotted Name (AntiFlood.cycleTime) is a member of that top-level class.
// Default is Arma's own default, shown as a placeholder and never written.
// A built-in server preset: basic.cfg and launch values, keyed like ProfileSettings.Basic and .Launch.
public sealed record ServerPresetDef(Guid Id, string Name, string Description,
    IReadOnlyDictionary<string, JsonElement> Basic, IReadOnlyDictionary<string, JsonElement> Launch);

public sealed record CfgKey(string File, string Name, CfgType Type, string Tab, string Group, string Label,
    string? Default = null, double? Min = null, double? Max = null, string[]? Choices = null, int? Length = null,
    bool Secret = false, string? Help = null, string? Pattern = null)
{
    public bool IsList => Type is CfgType.TextList or CfgType.NumberList;
    public string? Class => Name.IndexOf('.') is var i and > 0 ? Name[..i] : null;
    public string Member => Name.IndexOf('.') is var i and > 0 ? Name[(i + 1)..] : Name;
    public bool InServerPresets => File == ConfigCatalog.Basic && ConfigCatalog.PresetBasicKeys.Contains(Name)
        || File == ConfigCatalog.Launch && ConfigCatalog.PresetLaunchKeys.Contains(Name);
}

// Every key cites a Bohemia page (plan "Config catalog"): SCF = Arma 3: Server Config File, BSCF = Basic Server Config File,
// SAP = server.armaprofile, plus the local corpus pages Startup Parameters and Difficulty Settings.
public static class ConfigCatalog
{
    public const string Server = "server", Basic = "basic", Difficulty = "difficulty", Launch = "launch";

    private const string AllowedListHelp = "Not set allows everything; an empty list allows nothing.";
    private const string SafeValue = "^[A-Za-z0-9._-]{1,64}$";
    private const string Ipv4 = @"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$";
    public const string PresetPattern = "^[A-Za-z0-9_]{0,32}$";
    private static readonly string[] Never2 = ["0", "1", "2"];
    private static readonly string[] Format = ["none", "short", "full"];

    private static CfgKey S(string tab, string group, string name, CfgType type, string label, string? def = null, double? min = null, double? max = null,
        string[]? choices = null, int? length = null, bool secret = false, string? help = null, string? pattern = null) =>
        new(Server, name, type, tab, group, label, def, min, max, choices, length, secret, help, pattern);

    private static CfgKey B(string name, CfgType type, string label, string? def, double? min, string? help = null) =>
        new(Basic, name, type, "performance", "Network tuning", label, def, min, null, null, null, false, help);

    private static CfgKey D(string group, string name, string label, int max, string? help = null) =>
        new(Difficulty, name, CfgType.Integer, "difficulty", group, label, null, 0, max, null, null, false, help);

    private static CfgKey L(string name, CfgType type, string label, double? min = null, double? max = null, string[]? choices = null,
        string? help = null, string? pattern = null) =>
        new(Launch, name, type, "startup", type == CfgType.Flag ? "Options" : "Performance", label, null, min, max, choices, null, false, help, pattern);

    public static IReadOnlyList<CfgKey> All { get; } =
    [
        // server.cfg: General tab
        S("general", "Server", "hostname", CfgType.Text, "Server name", "local machine name"),
        S("general", "Server", "maxPlayers", CfgType.Integer, "Max players", "64", min: 1),
        S("general", "Server", "motd", CfgType.TextList, "Message of the day", help: "One line per message; empty lines are pauses."),
        S("general", "Server", "motdInterval", CfgType.Number, "Seconds between MOTD lines", "5", min: 0),
        S("general", "Server", "admins", CfgType.TextList, "Admin Steam IDs", help: "These players can #login without the admin password."),
        S("general", "Passwords", "password", CfgType.Text, "Join password", secret: true),
        S("general", "Passwords", "passwordAdmin", CfgType.Text, "Admin password", secret: true),
        S("general", "Passwords", "serverCommandPassword", CfgType.Text, "Server command password", secret: true, help: "Case-sensitive."),
        S("general", "Security", "BattlEye", CfgType.Bool01, "BattlEye", "1"),
        S("general", "Security", "verifySignatures", CfgType.Integer, "Verify signatures", "2", choices: ["0", "1", "2"], help: "1 behaves as 2."),
        S("general", "Security", "allowedFilePatching", CfgType.Integer, "Allowed file patching", "0", choices: Never2, help: "0 none, 1 headless clients only, 2 all clients."),
        S("general", "Security", "filePatchingExceptions", CfgType.TextList, "File patching exceptions (Steam IDs)"),
        S("general", "Security", "kickDuplicate", CfgType.Bool01, "Kick duplicate game IDs", "0"),
        S("general", "Security", "requiredBuild", CfgType.Integer, "Minimum client build", "0", min: 0),
        S("general", "Security", "loopback", CfgType.Bool01, "LAN only (loopback)", "0", help: "Blocks everyone not on this machine."),
        S("general", "Security", "upnp", CfgType.Bool01, "UPnP port mapping", "0", help: "Can delay start-up by 600 s behind a firewall."),

        // server.cfg: Missions tab
        S("missions", "Mission cycle", "autoSelectMission", CfgType.BoolWord, "Auto-select mission", "false"),
        S("missions", "Mission cycle", "randomMissionOrder", CfgType.BoolWord, "Random mission order", "false"),
        S("missions", "Mission cycle", "persistent", CfgType.Bool01, "Persistent battlefield", "0", help: "-autoInit needs this on."),
        S("missions", "Mission cycle", "missionsToServerRestart", CfgType.Integer, "Missions before a restart", "0", min: 0),
        S("missions", "Mission cycle", "missionsToShutdown", CfgType.Integer, "Missions before a shutdown", "0", min: 0, help: "Can't be combined with missions before a restart."),
        S("missions", "Mission cycle", "forcedDifficulty", CfgType.Text, "Forced difficulty", "", help: "Recruit, Regular, Veteran or Custom. A mission cycle's difficulty overrides it.", pattern: PresetPattern),
        S("missions", "Mission cycle", "missionWhitelist", CfgType.TextList, "Mission whitelist"),

        // server.cfg: Server config tab
        S("server", "Voting", "voteThreshold", CfgType.Number, "Vote threshold", "0.5", 0, 1),
        S("server", "Voting", "voteMissionPlayers", CfgType.Integer, "Players needed for mission voting", "1", min: 0),
        S("server", "Network limits", "MaxPing", CfgType.Integer, "Max ping", "-1", min: -1),
        S("server", "Network limits", "MaxPacketLoss", CfgType.Integer, "Max packet loss", "-1", min: -1),
        S("server", "Network limits", "MaxDesync", CfgType.Integer, "Max desync", "-1", min: -1),
        S("server", "Network limits", "DisconnectTimeout", CfgType.Integer, "Disconnect timeout (s)", "15", 1, 90),
        S("server", "Network limits", "kickClientsOnSlowNetwork", CfgType.NumberList, "Kick on slow network", "{1, 1, 1, 1}", 0, 1, length: 4, help: "Ping, packet loss, desync, timeout: 0 logs, 1 kicks."),
        S("server", "Network limits", "enablePlayerDiag", CfgType.Bool01, "Player diagnostics", "0"),
        S("server", "Network limits", "callExtReportLimit", CfgType.Integer, "callExtension report limit (ms)", "1000", min: 0),
        S("server", "Network limits", "steamProtocolMaxDataSize", CfgType.Integer, "Steam query packet limit", "1024", min: 1024, help: "Above the MTU, packets fragment."),
        S("server", "Network limits", "armaUnitsTimeout", CfgType.Integer, "Units data timeout (s)", "30", min: 0),
        S("server", "Network limits", "idleFPSLimit", CfgType.Integer, "FPS limit with no players", "30", 5, 60),
        S("server", "Timeouts", "votingTimeOut", CfgType.NumberList, "Voting timeout (ready, not ready)", "{60, 90}", min: 0, length: 2),
        S("server", "Timeouts", "roleTimeOut", CfgType.NumberList, "Role selection timeout (ready, not ready)", "{90, 120}", min: 0, length: 2),
        S("server", "Timeouts", "briefingTimeOut", CfgType.NumberList, "Briefing timeout (ready, not ready)", "{60, 90}", min: 0, length: 2),
        S("server", "Timeouts", "debriefingTimeOut", CfgType.NumberList, "Debriefing timeout (ready, not ready)", "{45, 60}", min: 0, length: 2),
        S("server", "Timeouts", "lobbyIdleTimeout", CfgType.Integer, "Lobby idle timeout (s)", "0", min: 0),
        S("server", "In game", "disableVoN", CfgType.Bool01, "Disable voice (VoN)", "0"),
        S("server", "In game", "vonCodec", CfgType.Integer, "VoN codec", "1", choices: ["0", "1"], help: "0 SPEEX, 1 OPUS."),
        S("server", "In game", "vonCodecQuality", CfgType.Integer, "VoN quality", "3", 0, 30, help: "0–10 is 8 kHz, 11–20 16 kHz, 21–30 32/48 kHz."),
        S("server", "In game", "drawingInMap", CfgType.BoolWord, "Drawing on the map", "true"),
        S("server", "In game", "skipLobby", CfgType.BoolWord, "Skip role selection", "false"),
        S("server", "In game", "allowProfileGlasses", CfgType.BoolWord, "Allow profile glasses", "true"),
        S("server", "In game", "zeusCompositionScriptLevel", CfgType.Integer, "Zeus composition scripts", "1", 0, 2, help: "0 none, 1 attributes only, 2 all."),
        S("server", "In game", "forceRotorLibSimulation", CfgType.Integer, "Flight model", "0", 0, 2, help: "0 player's choice, 1 advanced, 2 standard."),
        S("server", "In game", "overrideHazeQuality", CfgType.Integer, "Haze quality", "-1", -1, 2, help: "-1 not forced, 0 very low, 1 low, 2 standard."),
        S("server", "Scripts and files", "allowedLoadFileExtensions", CfgType.TextList, "loadFile extensions", help: AllowedListHelp),
        S("server", "Scripts and files", "allowedPreprocessFileExtensions", CfgType.TextList, "preprocessFile extensions", help: AllowedListHelp),
        S("server", "Scripts and files", "allowedHTMLLoadExtensions", CfgType.TextList, "htmlLoad extensions", help: AllowedListHelp),
        S("server", "Scripts and files", "allowedHTMLLoadURIs", CfgType.TextList, "htmlLoad URIs", help: AllowedListHelp),
        S("server", "Scripts and files", "missionHTTPDownloadBaseURL", CfgType.Text, "Mission HTTP download URL", ""),
        S("server", "Scripts and files", "statisticsEnabled", CfgType.Bool01, "Arma 3 analytics", "1"),
        S("server", "Server events", "doubleIdDetected", CfgType.Text, "On double ID", ""),
        S("server", "Server events", "onUserConnected", CfgType.Text, "On user connected", ""),
        S("server", "Server events", "onUserDisconnected", CfgType.Text, "On user disconnected", ""),
        S("server", "Server events", "onHackedData", CfgType.Text, "On hacked data", ""),
        S("server", "Server events", "onDifferentData", CfgType.Text, "On different data", ""),
        S("server", "Server events", "onUnsignedData", CfgType.Text, "On unsigned data", ""),
        S("server", "Server events", "onUserKicked", CfgType.Text, "On user kicked", ""),
        S("server", "Server events", "regularCheck", CfgType.Text, "Regular check", ""),
        S("server", "Logging", "logFile", CfgType.Text, "Console log file", ""),
        S("server", "Logging", "timeStampFormat", CfgType.Text, "RPT timestamps", "short", choices: Format),
        S("server", "Logging", "timeStampFormatConsole", CfgType.Text, "Console timestamps", "short", choices: Format),
        S("server", "Anti-flood", "AntiFlood.cycleTime", CfgType.Number, "Cycle time (s)", "0.5", min: 0),
        S("server", "Anti-flood", "AntiFlood.cycleLimit", CfgType.Integer, "Messages per cycle", "400", min: 0),
        S("server", "Anti-flood", "AntiFlood.cycleHardLimit", CfgType.Integer, "Hard limit per cycle", "4000", min: 0),
        S("server", "Anti-flood", "AntiFlood.enableKick", CfgType.Bool01, "Kick flooders", "0"),
        S("server", "Advanced", "AdvancedOptions.logObjectNotFound", CfgType.Bool01, "Log \"object not found\"", "1"),
        S("server", "Advanced", "AdvancedOptions.skipDescriptionParsing", CfgType.Bool01, "Skip description parsing", "0"),
        S("server", "Advanced", "AdvancedOptions.ignoreMissionLoadErrors", CfgType.Bool01, "Ignore mission load errors", "0"),
        S("server", "Advanced", "AdvancedOptions.queueSizeLogG", CfgType.Integer, "Queue size log threshold", "0", min: 0),

        // server.cfg: Headless tab (generated from HeadlessSettings, never from the Server values)
        S("headless", "Headless clients", "headlessClients", CfgType.TextList, "Headless client addresses"),
        S("headless", "Headless clients", "localClient", CfgType.TextList, "Local client addresses"),

        // basic.cfg (BSCF)
        B("MaxMsgSend", CfgType.Integer, "Max messages per cycle", "128", 1),
        B("MaxSizeGuaranteed", CfgType.Integer, "Max guaranteed packet (bytes)", "512", 1),
        B("MaxSizeNonguaranteed", CfgType.Integer, "Max non-guaranteed packet (bytes)", "256", 1),
        B("MinBandwidth", CfgType.Integer, "Min bandwidth (bps)", "131072", 0),
        B("MaxBandwidth", CfgType.Integer, "Max bandwidth (bps)", null, 0),
        B("MinErrorToSend", CfgType.Number, "Min error to send", "0.001", 0),
        B("MinErrorToSendNear", CfgType.Number, "Min error to send (near)", "0.01", 0),
        B("MaxCustomFileSize", CfgType.Integer, "Max custom face/sound (bytes)", null, 0),
        B("sockets.maxPacketSize", CfgType.Integer, "Max packet size", "1400", 1, "Only when your router or ISP needs smaller packets; larger guaranteed sizes can desync."),

        // Difficulty (Difficulty Settings page; layout from SAP)
        D("Simulation", "reducedDamage", "Reduced damage", 1),
        D("Situational awareness", "groupIndicators", "Group indicators", 2, "0 never, 1 limited distance, 2 always."),
        D("Situational awareness", "friendlyTags", "Friendly name tags", 2, "0 never, 1 limited distance, 2 always."),
        D("Situational awareness", "enemyTags", "Enemy name tags", 2, "0 never, 1 limited distance, 2 always."),
        D("Situational awareness", "detectedMines", "Detected mines", 2, "0 never, 1 limited distance, 2 always."),
        D("Situational awareness", "commands", "Commands", 2, "0 never, 1 fade out, 2 always."),
        D("Situational awareness", "waypoints", "Waypoints", 2, "0 never, 1 fade out, 2 always."),
        D("Situational awareness", "tacticalPing", "Tactical ping", 3, "0 off, 1 3D scene, 2 map, 3 both."),
        D("Personal awareness", "weaponInfo", "Weapon info", 2, "0 never, 1 fade out, 2 always."),
        D("Personal awareness", "stanceIndicator", "Stance indicator", 2, "0 never, 1 fade out, 2 always."),
        D("Personal awareness", "staminaBar", "Stamina bar", 1),
        D("Personal awareness", "weaponCrosshair", "Weapon crosshair", 1),
        D("Personal awareness", "visionAid", "Vision aid", 1),
        D("View", "thirdPersonView", "Third person view", 2, "0 off, 1 on, 2 vehicles only."),
        D("View", "cameraShake", "Camera shake", 1),
        D("Multiplayer", "scoreTable", "Score table", 1),
        D("Multiplayer", "deathMessages", "Killed by", 1),
        D("Multiplayer", "vonID", "VoN ID", 1),
        D("Map", "mapContent", "Extended map content (old)", 1, "Older key (before 1.68); when set it overrides the three map keys."),
        D("Map", "mapContentFriendly", "Map: friendlies", 1),
        D("Map", "mapContentEnemy", "Map: enemies", 1),
        D("Map", "mapContentMines", "Map: mines", 1),
        D("Misc", "autoReport", "Automatic reporting", 1),
        D("Misc", "multipleSaves", "Multiple saves", 1),
        new(Difficulty, "CustomDifficulty.aiLevelPreset", CfgType.Integer, "difficulty", "AI", "AI level", null, 0, 3, ["0", "1", "2", "3"], null, false, "0 low, 1 normal, 2 high, 3 custom."),
        new(Difficulty, "CustomAILevel.skillAI", CfgType.Number, "difficulty", "AI", "AI skill", null, 0, 1, null, null, false, "Used when the AI level is custom."),
        new(Difficulty, "CustomAILevel.precisionAI", CfgType.Number, "difficulty", "AI", "AI precision", null, 0, 1, null, null, false, "Used when the AI level is custom."),

        // Launch arguments (Startup Parameters page); the same allowlist as Arma3LaunchArgs.
        L("-autoInit", CfgType.Flag, "Auto-initialise the mission", help: "Needs persistent battlefield on; breaks mission parameters (only defaults apply)."),
        L("-loadMissionToMemory", CfgType.Flag, "Keep the mission in memory"),
        L("-enableHT", CfgType.Flag, "Use all logical cores", help: "Ignored when -cpuCount is set."),
        L("-hugePages", CfgType.Flag, "Huge pages"),
        L("-filePatching", CfgType.Flag, "File patching"),
        L("-netlog", CfgType.Flag, "Network log"),
        L("-noSound", CfgType.Flag, "No sound"),
        L("-noLogs", CfgType.Flag, "No logs", help: "The console and the Logs page stay empty."),
        L("-maxMem", CfgType.Integer, "Max memory (MB)", min: 1024),
        L("-cpuCount", CfgType.Integer, "CPU count", min: 2),
        L("-exThreads", CfgType.Integer, "Extra threads", choices: ["0", "1", "3", "5", "7"]),
        L("-limitFPS", CfgType.Integer, "FPS limit", 5, 1000),
        L("-bandwidthAlg", CfgType.Integer, "Bandwidth algorithm", choices: ["2"]),
        L("-malloc", CfgType.Text, "Memory allocator", pattern: SafeValue),
        L("-world", CfgType.Text, "Default world", help: "For example empty.", pattern: SafeValue),
        L("-ip", CfgType.Text, "Bind address", pattern: Ipv4),
    ];

    // Bohemia's CfgDifficultyPresets values (Difficulty Settings page); AI level from levelAI Low/Medium/High.
    private static readonly string[] PresetKeys =
    [
        "reducedDamage", "groupIndicators", "friendlyTags", "enemyTags", "detectedMines", "commands", "waypoints", "tacticalPing",
        "weaponInfo", "stanceIndicator", "staminaBar", "weaponCrosshair", "visionAid", "thirdPersonView", "cameraShake",
        "scoreTable", "deathMessages", "vonID", "mapContentFriendly", "mapContentEnemy", "mapContentMines", "autoReport", "multipleSaves",
        "CustomDifficulty.aiLevelPreset",
    ];

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> BuiltInPresets { get; } = new Dictionary<string, IReadOnlyDictionary<string, int>>
    {
        ["Recruit"] = Preset([1, 2, 2, 0, 2, 2, 2, 3, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0]),
        ["Regular"] = Preset([0, 1, 1, 0, 1, 1, 2, 3, 2, 2, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]),
        ["Veteran"] = Preset([0, 0, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 2]),
    };

    private static IReadOnlyDictionary<string, int> Preset(int[] values) => PresetKeys.Zip(values).ToDictionary(p => p.First, p => p.Second);

    // Server presets (Overview tab) are about performance only: loading one sets these keys to the preset's values and
    // clears the rest, so player limits, timeouts, kick rules, names, passwords and missions are never touched.
    public static readonly string[] PresetBasicKeys =
        ["MaxMsgSend", "MaxSizeGuaranteed", "MaxSizeNonguaranteed", "MinBandwidth", "MaxBandwidth", "MinErrorToSend", "MinErrorToSendNear", "sockets.maxPacketSize"];
    public static readonly string[] PresetLaunchKeys =
        ["-loadMissionToMemory", "-enableHT", "-hugePages", "-cpuCount", "-exThreads", "-maxMem", "-limitFPS", "-malloc", "-bandwidthAlg"];

    private static ServerPresetDef ServerPreset(int n, string name, string description, object basic, object launch) =>
        new(new Guid($"a3c0f5e0-0000-4000-8000-{n:000000000000}"), name, description,
            JsonSerializer.SerializeToElement(basic).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            JsonSerializer.SerializeToElement(launch).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));

    private static readonly Dictionary<string, bool> InMemory = new() { ["-loadMissionToMemory"] = true };

    // Values from the research report reports/Arma 3 server performance tuning.md (2026-11-19), which reads Bohemia's
    // basic.cfg page (edited June 2025), the 2.20 OPREP and the Startup Parameters page:
    // - BSCF's 1024 kbps example (MaxMsgSend 256, MinBandwidth 768000) tells Arma ~75% of the upload. That holds only where
    //   the server can fill the link (home tier). Above it the server is CPU-bound, so MaxMsgSend stays at Bohemia's 256 and
    //   MinBandwidth follows the player count (80 × 0.5 Mbit/s); a floor near a 1 Gbit line is BSCF's "too optimistic" case.
    // - Packet sizes and error thresholds stay at Arma's defaults; over maxPacketSize they desync (BSCF).
    // - Launch: -loadMissionToMemory saves CPU; -cpuCount/-exThreads are auto-detected since 2.20; -enableHT tested slower
    //   (2.20 OPREP); -hugePages needs a Windows privilege the panel can't check; -bandwidthAlg is still experimental.
    // - Maximum: MaxMsgSend 64 with MaxSizeNonguaranteed 1024 is the pair seven milsim communities on 1 Gbit hosts settled
    //   on (one community gist, low confidence, predates 2.20); -limitFPS 100 is supported and idle under heavy load.
    // Built around the unit's host (2026-11-19): AMD EPYC 4344P, 8 cores / 16 threads, 64 GB, Windows Server 2025, with a
    // 1 Gbit/s uplink (confirmed by the user; it sets MaxBandwidth). Arma detects the cores and sizes memory (~4/5 of RAM) itself, so no
    // preset sets -cpuCount, -exThreads, -enableHT or -maxMem. The tiers are a ladder, each adding the next, less proven step.
    private static readonly object HostNetwork = new { MaxMsgSend = 256, MinBandwidth = 40_000_000, MaxBandwidth = 1_000_000_000 };
    private static readonly Dictionary<string, object> FpsLimit = new() { ["-loadMissionToMemory"] = true, ["-limitFPS"] = 100 };

    public static IReadOnlyList<ServerPresetDef> BuiltInServerPresets { get; } =
    [
        ServerPreset(1, "Default", "Arma's own values. Nothing is written, so the game picks its defaults.", new { }, new { }),
        ServerPreset(3, "Performance", "Network tuning for this server's 1 Gbit/s link, sized for up to 80 players, and the mission kept in memory. Safe for any mission.",
            HostNetwork, InMemory),
        ServerPreset(4, "Performance Enhanced", "Performance, plus a server FPS limit of 100 instead of 50, so the server updates more often when the CPU has headroom. No effect once a mission maxes it out.",
            HostNetwork, FpsLimit),
        ServerPreset(5, "Maximum", "Performance Enhanced, plus the 64 messages / 1,024-byte packets pair milsim units run for heavy ACE/Zeus ops. "
            + "If players lag while connecting, put both back.",
            new { MaxMsgSend = 64, MaxSizeNonguaranteed = 1024, MinBandwidth = 40_000_000, MaxBandwidth = 1_000_000_000 }, FpsLimit),
    ];

    public static IEnumerable<CfgKey> Of(string file) => All.Where(k => k.File == file);

    public static CfgKey? Find(string file, string name) =>
        All.FirstOrDefault(k => k.File == file && k.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
