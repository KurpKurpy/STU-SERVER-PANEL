using System.Net;
using System.Text;
using System.Text.Json;

namespace Panel.Features.Notifications;

public enum DiscordOutcome { Ok, Gone, Failed }

// Error is our own wording, never exception text (it could hold the URL).
public sealed record DiscordResult(DiscordOutcome Outcome, string? Error);

public interface IDiscordWebhook
{
    Task<DiscordResult> SendAsync(string url, string content, CancellationToken ct);
}

// Phase 6b Ruling 9, from Discord's docs (20 Nov): content up to 2,000 characters; {"parse": []} suppresses every mention;
// a 429 carries retry_after in seconds; "if a webhook returns a 404 status you should not attempt to use it again"; 401/403/429 count
// toward Cloudflare's invalid-request ban; a DiscordBot User-Agent is required.
public sealed class DiscordWebhook(HttpClient http) : IDiscordWebhook
{
    private const int MaxContent = 2000, Attempts = 3;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    // How the sender waits between attempts; tests replace it.
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    // The factory's default handlers log the full request URL, token included, so they're removed; redirects are never followed.
    public static IHttpClientBuilder Register(IServiceCollection services) =>
        services.AddHttpClient<IDiscordWebhook, DiscordWebhook>(c =>
            {
                c.Timeout = TimeSpan.FromSeconds(15);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (13th Console, 6b)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();

    public async Task<DiscordResult> SendAsync(string url, string content, CancellationToken ct)
    {
        if (!DiscordUrl.TryCanonical(url, out var target)) return new(DiscordOutcome.Failed, "The Discord webhook address isn't valid.");
        var body = JsonSerializer.Serialize(new { content = content.Length > MaxContent ? content[..MaxContent] : content, allowed_mentions = new { parse = Array.Empty<string>() } });
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException)
            {
                if (attempt < Attempts) { await Delay(Backoff(attempt), ct); continue; }
                return new(DiscordOutcome.Failed, "Couldn't reach Discord.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < Attempts) { await Delay(Backoff(attempt), ct); continue; }
                return new(DiscordOutcome.Failed, "Discord didn't answer in time.");
            }

            using (response)
            {
                var code = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) return new(DiscordOutcome.Ok, null);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new(DiscordOutcome.Gone, $"Discord says this webhook no longer exists (HTTP {code}). Paste a new one.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < Attempts) { await Delay(await RetryAfterAsync(response, ct), ct); continue; }
                if (code >= 500 && attempt < Attempts) { await Delay(Backoff(attempt), ct); continue; }
                return new(DiscordOutcome.Failed, response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Discord is rate limiting this webhook; try again later." : $"Discord refused the message (HTTP {code}).");
            }
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(attempt == 1 ? 2 : 5);

    // retry_after (seconds, may be fractional) from the body, else the Retry-After header, else a second; never more than 30 s.
    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(1);
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("retry_after", out var r) && r.TryGetDouble(out var seconds)) wait = TimeSpan.FromSeconds(Math.Max(0, seconds));
            else if (response.Headers.RetryAfter?.Delta is { } delta) wait = delta;
        }
        catch (JsonException) { if (response.Headers.RetryAfter?.Delta is { } delta) wait = delta; }
        return wait > MaxWait ? MaxWait : wait;
    }
}
