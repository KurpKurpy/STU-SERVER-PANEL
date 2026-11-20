namespace Contracts;

// A mission version to write into an install's mpmissions (Phase 4a Rulings 10–12). The agent pulls the PBO by its hash from the panel.
public sealed record MissionDeploySpec(Guid DeploymentId, string InstallRoot, string FileName, string Sha256, long Size);

// Status: Accepted (the agent is working on it), Waiting (the file is in use by a running mission), Done or Failed.
public sealed record DeployResult(Guid DeploymentId, string Status, string? Error, string? BackupFolder)
{
    public const string Accepted = "Accepted", Waiting = "Waiting", Done = "Done", Failed = "Failed";
}
