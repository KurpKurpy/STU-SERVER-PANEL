using System.Text.Json;
using Contracts;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Operations;
using static Panel.Features.Notifications.DiscordText;

namespace Panel.Features.Notifications;

// Phase 6b Ruling 2's texts in one place. In-app text names no time; Discord text carries only names, map and <t:…> times, with what
// members wrote escaped (Ruling 12). Error text goes in-app only.
public static class Notices
{
    private static string Op(Operation op) => $"/operations/{op.Id}";
    private static string Server(Profile p) => $"/server?p={p.Id}";
    private static string Clip(string s, int n) => s.Length > n ? s[..n] : s;

    public static async Task<NotificationEvent> OperationCreatedAsync(AppDb db, Operation op)
    {
        var mission = op.MissionId is { } m ? await db.Missions.FindAsync(m) : null;
        return new(NotificationEvents.OperationCreated,
            $"New operation: {op.Name}{(mission is null ? "" : $" ({mission.DisplayName} on {mission.Map})")}.",
            $"**New operation:** {Escape(op.Name)}, {Time(op.StartsAt, 'F')} ({Time(op.StartsAt, 'R')}).{(mission is null ? "" : $" {Escape(mission.DisplayName)} on {Escape(mission.Map)}.")}",
            Op(op), Permissions.OperationsView);
    }

    public static NotificationEvent OperationCancelled(Operation op) =>
        new(NotificationEvents.OperationCancelled, $"{op.Name} is cancelled.", $"**{Escape(op.Name)}** ({Time(op.StartsAt, 'F')}) is cancelled.", Op(op),
            Permissions.OperationsView);

    public static NotificationEvent OperationReady(Operation op) =>
        new(NotificationEvents.OperationReady, $"{op.Name} is ready to apply.", $"**{Escape(op.Name)}** is ready.", Op(op), Permissions.OperationsApply);

    public static NotificationEvent OperationReminder(Operation op, int minutes) =>
        new(NotificationEvents.OperationReminder, $"{op.Name} starts in {minutes} minute{(minutes == 1 ? "" : "s")}.",
            $"**{Escape(op.Name)}** starts {Time(op.StartsAt, 'R')} ({Time(op.StartsAt, 't')}).", Op(op), Permissions.OperationsView);

    public static NotificationEvent ServerStarted(Profile p) =>
        new(NotificationEvents.ServerStarted, $"{p.Name} started.", $"**{Escape(p.Name)}** started.", Server(p), Permissions.ServerView);

    // An Error status is the agent's "exited unexpectedly"; an asked-for stop arrives as Offline.
    public static NotificationEvent ServerOffline(Profile p, ProfileStatus s) => s.State == ServerState.Error
        ? new(NotificationEvents.ServerOffline, $"{p.Name} went offline unexpectedly{(s.Error is { Length: > 0 } e ? ": " + Clip(e.TrimEnd('.'), 200) : "")}.",
            $"**{Escape(p.Name)}** went offline unexpectedly.", Server(p), Permissions.ServerView)
        : new(NotificationEvents.ServerOffline, $"{p.Name} stopped.", $"**{Escape(p.Name)}** stopped.", Server(p), Permissions.ServerView);

    public static NotificationEvent MissionDeployed(Mission mission, int number, string? installName) =>
        new(NotificationEvents.MissionDeployed, $"{mission.DisplayName} v{number} is on {installName ?? "the server"}.",
            $"**{Escape(mission.DisplayName)}** v{number} is on {Escape(installName ?? "the server")}.", $"/missions/{mission.Id}", Permissions.MissionsView);

    // Ruling 7: once Apply's gate first passes for an op not yet applied; an edit re-arms it. Called after the result is stored.
    public static async Task NotifyIfReadyAsync(AppDb db, Notifier notifier, Operation op)
    {
        if (op.Status != OperationStatus.Planned || op.AppliedAt is not null || op.ReadyNotifiedAt is not null || op.LastCheckJson is not { } json) return;
        var results = JsonSerializer.Deserialize<CheckResult[]>(json, OperationEndpoints.Json) ?? [];
        if (results.Length == 0 || !Readiness.PassesGate(results)) return;
        op.ReadyNotifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        notifier.Enqueue(OperationReady(op));
    }
}
