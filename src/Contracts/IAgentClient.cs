namespace Contracts;

// Panel → agent, as SignalR client results. No CancellationToken: the agent registers these same signatures.
public interface IAgentClient
{
    Task<StartResult> Start(LaunchSpec spec);
    Task<CommandResult> Stop(Guid profileId);
    Task<CommandResult> ForceStop(Guid profileId);
    Task<CommandResult> Restart(LaunchSpec spec);
    Task<FileEntry[]> ListFiles(string area, string relativeDir, string pattern);
    Task<FileChunk> ReadFile(string area, string relativePath, long offset, int length);
    Task<ApplyResult> ApplyConfig(ConfigBundle bundle);
    Task<ConfigSnapshot> ReadConfig(Guid profileId, string slug);
    Task<CommandResult> StartHeadless(LaunchSpec spec);
    Task<CommandResult> StopHeadless(Guid profileId);
    Task<CommandResult> RunJob(JobSpec spec);
    Task<CommandResult> AnswerJob(Guid jobId, string answer);
    Task<CommandResult> CancelJob(Guid jobId);
    Task<DiskMod[]> ListMods();
    Task<KeyFile[]> ListKeys(string installRoot);
    Task<KeysResult> DeployKeys(string installRoot, ModRef[] mods);
    Task<KeysResult> CleanupKeys(string installRoot, string[] keyNames);
    Task<DeployResult> DeployMission(MissionDeploySpec spec);
    Task<CommandResult> ScanAddons(AddonScanRequest request);
    Task<FileListing> FileList(FileSpace space, string path);
    Task<FileSearchResult> FileSearch(FileSpace space, string path, string query);
    Task<FileChunk> FileRead(FileSpace space, string path, long offset, int length);
    Task<CommandResult> FileChange(FileChangeSpec spec);
    Task<RconReply> Rcon(Guid profileId, string command);
    Task<CommandResult> DeleteBackup(string file);              // Phase 7b: a server-*.zip, and its copy elsewhere
    Task<int> PruneSafetyCopies(int olderThanDays);              // Phase 7b: 0 prunes nothing
}

// Agent → panel.
public interface IAgentHub
{
    Task Hello(AgentHello hello);
    Task StatusChanged(ProfileStatus status);
    Task LogLines(Guid profileId, LogLine[] lines);
    Task Resources(ResourceSample sample);
    Task JobUpdated(JobUpdate update);
    Task JobFinished(JobResult result);
    Task DeploymentChanged(DeployResult result);
    Task AddonsScanned(AddonScan scan);
    Task FileChanged(FileChangeResult result);
    Task RconMessage(Guid profileId, DateTime at, string text);
}
