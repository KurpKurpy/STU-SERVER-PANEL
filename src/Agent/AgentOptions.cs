namespace ServerAgent;

// Section "Agent" of appsettings.json. Paths live here, on the box, never in the panel (design §5.3).
public sealed class AgentOptions
{
    public string PanelUrl { get; set; } = "";
    public string Key { get; set; } = "";
    public string[] InstallRoots { get; set; } = [];
    // Install roots the agent may launch from but never writes into (Phase 4a Ruling 9); empty on the real box.
    public string[] ReadOnlyInstallRoots { get; set; } = [];
    public string DataRoot { get; set; } = "";
    public int StopTimeoutSeconds { get; set; } = 60;
    public string? BackupCopyTo { get; set; }           // Phase 7b: every server backup zip is also copied to <BackupCopyTo>\server (another drive, a share)
    public int RconRetrySeconds { get; set; } = 10;     // Phase 7a: between RCon login attempts while BattlEye isn't answering or refuses
    public int QueryIntervalSeconds { get; set; } = 5;
    // Empty = <DataRoot>\steamcmd\steamcmd.exe, downloaded from Valve on first use (Phase 5a Ruling 4).
    public string SteamCmdPath { get; set; } = "";
}
