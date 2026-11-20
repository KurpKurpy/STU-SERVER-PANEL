using System.Collections.Concurrent;
using Panel.Features.Notifications;

namespace Panel.Tests;

// What the tests' Discord answers. Nothing leaves the machine (plan Global Constraints).
public sealed class FakeDiscord : IDiscordWebhook
{
    public ConcurrentQueue<(string Url, string Content)> Sent { get; } = new();
    public DiscordResult Answer { get; set; } = new(DiscordOutcome.Ok, null);
    public bool Throw { get; set; }

    // While set, a send waits here: a Discord that's down or slow (final review I1).
    public TaskCompletionSource? Gate { get; set; }

    public async Task<DiscordResult> SendAsync(string url, string content, CancellationToken ct)
    {
        if (Throw) throw new InvalidOperationException("boom " + url); // the worker must not log this message: it holds the URL
        Sent.Enqueue((url, content));
        if (Gate is { } gate) await gate.Task.WaitAsync(ct);
        return Answer;
    }
}
