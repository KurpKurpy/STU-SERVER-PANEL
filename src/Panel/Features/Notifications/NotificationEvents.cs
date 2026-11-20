namespace Panel.Features.Notifications;

// What happened, ready to send: the in-app text, the Discord text (null = never to Discord), the page it links to, and the permission
// a member needs to be told (the one guarding that page). Texts never hold a local time (plan Global Constraints).
public sealed record NotificationEvent(string Type, string Text, string? DiscordText, string? Link, string Permission);

// Phase 6b Ruling 2: the events this panel can produce, with their default channels. There's no mission approval step, so no rule for it.
public static class NotificationEvents
{
    public const string OperationCreated = "operation.created", OperationReminder = "operation.reminder", OperationCancelled = "operation.cancelled",
        OperationReady = "operation.ready", ServerStarted = "server.started", ServerOffline = "server.offline", MissionDeployed = "mission.deployed",
        UpdateFailed = "update.failed", ScheduleNotRun = "schedule.notrun"; // Phase 7b

    public static readonly (string Type, string Label, bool InApp, bool Discord)[] All =
    [
        (OperationCreated, "New operation", true, true),
        (OperationReminder, "Operation starting soon", true, true),
        (OperationCancelled, "Operation cancelled", true, true),
        (OperationReady, "Operation ready to apply", true, false),
        (ServerStarted, "Server started", true, false),
        (ServerOffline, "Server stopped or went offline", true, false),
        (MissionDeployed, "Mission written to a server", true, false),
        (UpdateFailed, "Update or download failed", true, false),
        (ScheduleNotRun, "Scheduled task didn't run", true, true),
    ];

    public static bool IsKnown(string type) => All.Any(e => e.Type == type);
}
