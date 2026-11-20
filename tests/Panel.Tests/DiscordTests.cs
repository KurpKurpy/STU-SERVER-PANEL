using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Notifications;

namespace Panel.Tests;

// Phase 6b Task 2: the Discord half (plan Rulings 9–12, Review Focus 1, 2 and 5).
public class DiscordTests
{
    private const string Token = "SeCrEtToKeN_a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8s9T0u1V2w3X4y5Z6";
    private const string Url = "https://discord.com/api/webhooks/1234567890123456789/" + Token;

    private sealed class Stub(Func<int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add((r.RequestUri!.ToString(), r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return answer(Requests.Count);
        }
    }

    internal sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string category) => new Logger(this, category);
        public void Dispose() { }

        private sealed class Logger(CaptureLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format) =>
                owner.Lines.Enqueue($"{category}: {format(state, ex)} {ex}");
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? json = null) =>
        new(code) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };

    private static (DiscordWebhook Hook, Stub Stub, List<TimeSpan> Waits) Sender(Func<int, HttpResponseMessage> answer)
    {
        var stub = new Stub(answer);
        var waits = new List<TimeSpan>();
        return (new DiscordWebhook(new HttpClient(stub)) { Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } }, stub, waits);
    }

    [Theory]
    [InlineData(Url, true)]
    [InlineData("https://canary.discord.com/api/webhooks/1234567890123456789/" + Token, true)]
    [InlineData("https://ptb.discord.com/api/webhooks/1234567890123456789/" + Token, true)]
    [InlineData("https://discordapp.com/api/webhooks/1234567890123456789/" + Token, true)]
    [InlineData("https://discord.com/api/v10/webhooks/1234567890123456789/" + Token, true)]
    [InlineData("  " + Url + "  ", true)]
    [InlineData("http://discord.com/api/webhooks/1234567890123456789/" + Token, false)]
    [InlineData("https://example.com/api/webhooks/1234567890123456789/" + Token, false)]
    [InlineData("https://discord.com.evil.net/api/webhooks/1234567890123456789/" + Token, false)]
    [InlineData("https://discord.com:444/api/webhooks/1234567890123456789/" + Token, false)]
    [InlineData("https://user:pw@discord.com/api/webhooks/1234567890123456789/" + Token, false)]
    [InlineData(Url + "?wait=true", false)]
    [InlineData(Url + "#x", false)]
    [InlineData("https://discord.com/api/webhooks/1234567890123456789/", false)]
    [InlineData("https://discord.com/api/channels/1234567890123456789/" + Token, false)]
    [InlineData("https://discord.com/api/webhooks/12345/" + Token, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_Discord_webhook_urls_are_accepted_and_made_canonical(string? input, bool ok)
    {
        Assert.Equal(ok, DiscordUrl.TryCanonical(input, out var canonical));
        if (ok) Assert.Equal(Url, canonical);
    }

    [Fact]
    public async Task The_sender_posts_suppressed_mentions_and_clips_to_2000()
    {
        var (hook, stub, _) = Sender(_ => Status(HttpStatusCode.NoContent));

        var r = await hook.SendAsync(Url, new string('x', 2500), CancellationToken.None);

        Assert.Equal(DiscordOutcome.Ok, r.Outcome);
        var (url, body) = Assert.Single(stub.Requests);
        Assert.Equal(Url, url);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(2000, json.RootElement.GetProperty("content").GetString()!.Length);
        Assert.Empty(json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task A_429_waits_retry_after_then_retries_and_gives_up_after_3()
    {
        var (hook, stub, waits) = Sender(_ => Status(HttpStatusCode.TooManyRequests, """{"message":"slow","retry_after":1.5,"global":false}"""));
        var r = await hook.SendAsync(Url, "hi", CancellationToken.None);
        Assert.Equal((DiscordOutcome.Failed, 3), (r.Outcome, stub.Requests.Count));
        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5)], waits);

        (hook, stub, waits) = Sender(n => n == 1 ? Status(HttpStatusCode.TooManyRequests, """{"retry_after":999}""") : Status(HttpStatusCode.NoContent));
        r = await hook.SendAsync(Url, "hi", CancellationToken.None);
        Assert.Equal((DiscordOutcome.Ok, 2), (r.Outcome, stub.Requests.Count));
        Assert.Equal([TimeSpan.FromSeconds(30)], waits); // capped
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        var (hook, stub, _) = Sender(_ =>
        {
            var m = Status(HttpStatusCode.MovedPermanently);
            m.Headers.Location = new Uri("https://evil.example/collect");
            return m;
        });
        var r = await hook.SendAsync(Url, "hi", CancellationToken.None);
        Assert.Equal((DiscordOutcome.Failed, 1), (r.Outcome, stub.Requests.Count));
        Assert.DoesNotContain(Token, r.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_missing_or_refused_webhook_is_Gone_after_one_try(HttpStatusCode code)
    {
        var (hook, stub, _) = Sender(_ => Status(code));
        var r = await hook.SendAsync(Url, "hi", CancellationToken.None);
        Assert.Equal((DiscordOutcome.Gone, 1), (r.Outcome, stub.Requests.Count));
    }

    [Fact]
    public async Task A_server_error_retries_and_network_errors_never_carry_exception_text()
    {
        var (hook, stub, waits) = Sender(n => n < 3 ? Status(HttpStatusCode.BadGateway) : Status(HttpStatusCode.NoContent));
        Assert.Equal(DiscordOutcome.Ok, (await hook.SendAsync(Url, "hi", CancellationToken.None)).Outcome);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(2, waits.Count);

        (hook, _, _) = Sender(_ => throw new HttpRequestException("socket error talking to " + Url));
        var r = await hook.SendAsync(Url, "hi", CancellationToken.None);
        Assert.Equal((DiscordOutcome.Failed, "Couldn't reach Discord."), (r.Outcome, r.Error));
    }

    [Fact]
    public async Task The_registered_client_never_logs_the_token_and_never_follows_redirects()
    {
        var logs = new CaptureLogs();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        DiscordWebhook.Register(services);
        var stub = new Stub(_ => Status(HttpStatusCode.NoContent));
        services.AddHttpClient<IDiscordWebhook, DiscordWebhook>().ConfigurePrimaryHttpMessageHandler(() => stub);
        services.AddHttpClient("control").ConfigurePrimaryHttpMessageHandler(() => stub); // the factory's default handlers log the URL
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<IDiscordWebhook>().SendAsync(Url, "hi", CancellationToken.None);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(Token));
        await sp.GetRequiredService<IHttpClientFactory>().CreateClient("control").PostAsync(Url, null);
        Assert.Contains(logs.Lines, l => l.Contains(Token)); // proves the capture would have seen it

        var plain = new ServiceCollection();
        plain.AddLogging();
        DiscordWebhook.Register(plain);
        using var sp2 = plain.BuildServiceProvider();
        HttpMessageHandler? h = sp2.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IDiscordWebhook));
        var chain = new List<HttpMessageHandler>();
        while (h is not null) { chain.Add(h); h = (h as DelegatingHandler)?.InnerHandler; }
        Assert.DoesNotContain(chain, x => x.GetType().Name.Contains("Logging"));
        Assert.False(Assert.Single(chain.OfType<SocketsHttpHandler>()).AllowAutoRedirect);
    }

    [Fact]
    public void Discord_text_escapes_names_and_writes_times_as_timestamps()
    {
        var s = DiscordText.Escape("[x](https://e) *b* @everyone <t:1:F>\nnext");
        Assert.DoesNotContain("\n", s);
        foreach (var c in new[] { '[', ']', '(', ')', '*', '@', '<', ':' })
            Assert.All(Enumerable.Range(0, s.Length).Where(i => s[i] == c), i => Assert.Equal('\\', s[i - 1]));
        Assert.Equal("<t:1795204800:F>", DiscordText.Time(new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc), 'F'));
    }

    // --- through the panel, with the fake Discord ---

    private static async Task<HttpClient> OwnerAsync(PanelFactory f) => await TestUsers.LoginAsync(f, "Owner", "Crow");

    private static Task<HttpResponseMessage> SetUrlAsync(HttpClient c, string? url) => c.PutAsJsonAsync("/api/notifications/discord", new { url });

    private static NotificationEvent Created(string name = "Op Kestrel") =>
        new(NotificationEvents.OperationCreated, $"New operation: {name}.", $"**New operation:** {name}", "/operations/x", Permissions.OperationsView);

    [Fact]
    public async Task The_webhook_is_validated_and_settings_never_return_it_nor_audit_record_it()
    {
        var logs = new CaptureLogs();
        using var f = new PanelFactory(services: s => s.AddSingleton<ILoggerProvider>(logs));
        var owner = await OwnerAsync(f);

        var bad = await SetUrlAsync(owner, "https://example.com/api/webhooks/1/x");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetUrlAsync(owner, "https://canary.discord.com/api/webhooks/1234567890123456789/" + Token)).StatusCode);

        var settings = await owner.GetStringAsync("/api/notifications/settings");
        Assert.DoesNotContain(Token, settings);
        Assert.True(JsonDocument.Parse(settings).RootElement.GetProperty("discord").GetProperty("set").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/notifications/discord/test", null)).StatusCode);
        Assert.Equal(Url, Assert.Single(f.Discord.Sent).Url); // stored canonically

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var audit = await db.Audit.Where(a => a.Action.StartsWith("notifications")).ToListAsync();
            Assert.NotEmpty(audit);
            Assert.All(audit, a => Assert.DoesNotContain(Token, a.Summary + a.DetailsJson));
            Assert.DoesNotContain(Token, (await db.Settings.FindAsync("discordWebhook"))!.Value); // encrypted at rest
        }
        Assert.DoesNotContain(logs.Lines, l => l.Contains(Token));

        var member = await TestUsers.LoginAsync(f, "Member", "Pvt");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/notifications/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetUrlAsync(member, Url)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetUrlAsync(owner, "")).StatusCode);
        Assert.False(JsonDocument.Parse(await owner.GetStringAsync("/api/notifications/settings")).RootElement.GetProperty("discord").GetProperty("set").GetBoolean());
    }

    [Fact]
    public async Task An_unreadable_stored_webhook_reads_as_not_set()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);
        await SetUrlAsync(owner, Url);
        await ReadinessTests.DbAsync(f, async db => (await db.Settings.FindAsync("discordWebhook"))!.Value = "not-a-protected-value");

        var discord = JsonDocument.Parse(await owner.GetStringAsync("/api/notifications/settings")).RootElement.GetProperty("discord");
        Assert.False(discord.GetProperty("set").GetBoolean());
        var test = await owner.PostAsync("/api/notifications/discord/test", null);
        Assert.Equal((HttpStatusCode.Conflict, "not_set"), (test.StatusCode, await MissionKit.CodeAsync(test)));
    }

    [Fact]
    public async Task Rules_and_reminder_minutes_are_saved_and_unknown_types_refused()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);

        var bad = await owner.PutAsJsonAsync("/api/notifications/settings", new { reminderMinutes = 60, rules = new[] { new { type = "mission.approved", inApp = true, discord = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/notifications/settings", new { reminderMinutes = 5000, rules = Array.Empty<object>() })).StatusCode);

        var ok = await owner.PutAsJsonAsync("/api/notifications/settings", new { reminderMinutes = 30, rules = new[] { new { type = "server.started", inApp = false, discord = true } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var s = JsonDocument.Parse(await owner.GetStringAsync("/api/notifications/settings")).RootElement;
        Assert.Equal(30, s.GetProperty("reminderMinutes").GetInt32());
        var rule = s.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "server.started");
        Assert.Equal((false, true, "Server started"), (rule.GetProperty("inApp").GetBoolean(), rule.GetProperty("discord").GetBoolean(), rule.GetProperty("label").GetString()));
        Assert.Equal(9, s.GetProperty("rules").GetArrayLength()); // 9 since Phase 7b's schedule.notrun
    }

    [Fact]
    public async Task Events_reach_Discord_only_when_the_rule_says_so_and_a_webhook_is_set()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);
        var notifier = NotificationTests.Notifier(f);

        notifier.Enqueue(Created("Op One"));
        await NotificationTests.WaitRowsAsync(f, NotificationEvents.OperationCreated, 1);
        Assert.Empty(f.Discord.Sent); // no webhook set: Discord is optional

        await SetUrlAsync(owner, Url);
        notifier.Enqueue(new NotificationEvent(NotificationEvents.ServerStarted, "Main started.", "**Main** started.", "/server", Permissions.ServerView)); // rule: Discord off
        notifier.Enqueue(Created("Op Two"));
        await NotificationTests.WaitRowsAsync(f, NotificationEvents.OperationCreated, 2);
        await AgentKit.WaitUntilAsync(() => !f.Discord.Sent.IsEmpty);
        Assert.Equal("**New operation:** Op Two", Assert.Single(f.Discord.Sent).Content);

        notifier.Enqueue(new NotificationEvent(NotificationEvents.UpdateFailed, "A download failed: disk full.", null, "/mods", Permissions.ModsManage));
        await ReadinessTests.DbAsync(f, async db => (await db.NotificationRules.FindAsync(NotificationEvents.UpdateFailed))!.Discord = true);
        notifier.Enqueue(new NotificationEvent(NotificationEvents.UpdateFailed, "A download failed: disk full.", null, "/mods", Permissions.ModsManage));
        await NotificationTests.WaitRowsAsync(f, NotificationEvents.UpdateFailed, 2);
        Assert.Single(f.Discord.Sent); // no Discord text: never sent, whatever the rule says
    }

    [Fact]
    public async Task A_gone_webhook_is_marked_broken_and_not_used_again_until_replaced()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);
        await SetUrlAsync(owner, Url);
        f.Discord.Answer = new DiscordResult(DiscordOutcome.Gone, "Discord says this webhook no longer exists (HTTP 404). Paste a new one.");
        var notifier = NotificationTests.Notifier(f);

        notifier.Enqueue(Created("Op One"));
        await AgentKit.WaitUntilAsync(() => JsonDocument.Parse(owner.GetStringAsync("/api/notifications/settings").GetAwaiter().GetResult())
            .RootElement.GetProperty("discord").GetProperty("broken").GetBoolean());
        notifier.Enqueue(Created("Op Two"));
        await NotificationTests.WaitRowsAsync(f, NotificationEvents.OperationCreated, 2);
        Assert.Single(f.Discord.Sent);
        var discord = JsonDocument.Parse(await owner.GetStringAsync("/api/notifications/settings")).RootElement.GetProperty("discord");
        Assert.Contains("no longer exists", discord.GetProperty("lastError").GetString());

        f.Discord.Answer = new DiscordResult(DiscordOutcome.Ok, null);
        await SetUrlAsync(owner, Url.Replace("1234567890123456789", "9876543210987654321"));
        notifier.Enqueue(Created("Op Three"));
        await AgentKit.WaitUntilAsync(() => f.Discord.Sent.Count == 2);
    }

    [Fact]
    public async Task A_throwing_sender_still_leaves_the_in_app_rows_and_the_worker_running_and_logs_no_url()
    {
        var logs = new CaptureLogs();
        using var f = new PanelFactory(services: s => s.AddSingleton<ILoggerProvider>(logs));
        var owner = await OwnerAsync(f);
        await SetUrlAsync(owner, Url);
        var notifier = NotificationTests.Notifier(f);

        f.Discord.Throw = true;
        notifier.Enqueue(Created("Op One"));
        await NotificationTests.WaitRowsAsync(f, NotificationEvents.OperationCreated, 1);
        f.Discord.Throw = false;
        notifier.Enqueue(Created("Op Two"));
        await AgentKit.WaitUntilAsync(() => !f.Discord.Sent.IsEmpty);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(Token));
    }

    [Fact]
    public async Task A_stuck_Discord_send_doesnt_hold_up_the_bell()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);
        await SetUrlAsync(owner, Url);
        f.Discord.Gate = new TaskCompletionSource();
        var notifier = NotificationTests.Notifier(f);

        notifier.Enqueue(Created("Op One"));
        await AgentKit.WaitUntilAsync(() => !f.Discord.Sent.IsEmpty); // its Discord send is now stuck
        notifier.Enqueue(Created("Op Two"));
        await AgentKit.WaitUntilAsync(() => NotificationTests.RowsAsync(f).GetAwaiter().GetResult().Any(r => r.Text == "New operation: Op Two."), 5);
        Assert.Single(f.Discord.Sent); // still stuck on the first send
        f.Discord.Gate.SetResult();
        await AgentKit.WaitUntilAsync(() => f.Discord.Sent.Count == 2);
    }

    // Final review I2: a good send clears an old error; a webhook Discord said is gone isn't tried again, not even by Test.
    [Fact]
    public async Task A_good_send_clears_the_last_error_and_Test_never_reuses_a_gone_webhook()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);
        await SetUrlAsync(owner, Url);
        JsonElement Discord() => JsonDocument.Parse(owner.GetStringAsync("/api/notifications/settings").GetAwaiter().GetResult()).RootElement.GetProperty("discord");

        f.Discord.Answer = new DiscordResult(DiscordOutcome.Failed, "Discord didn't answer in time.");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync("/api/notifications/discord/test", null)).StatusCode);
        Assert.Equal("Discord didn't answer in time.", Discord().GetProperty("lastError").GetString());
        f.Discord.Answer = new DiscordResult(DiscordOutcome.Ok, null);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/notifications/discord/test", null)).StatusCode);
        Assert.Equal(JsonValueKind.Null, Discord().GetProperty("lastError").ValueKind);

        f.Discord.Answer = new DiscordResult(DiscordOutcome.Gone, "Discord says this webhook no longer exists (HTTP 404). Paste a new one.");
        await owner.PostAsync("/api/notifications/discord/test", null);
        Assert.True(Discord().GetProperty("broken").GetBoolean());
        var sent = f.Discord.Sent.Count;
        var again = await owner.PostAsync("/api/notifications/discord/test", null);
        Assert.Equal((HttpStatusCode.Conflict, "discord_gone"), (again.StatusCode, await MissionKit.CodeAsync(again)));
        Assert.Equal(sent, f.Discord.Sent.Count);
    }

    [Fact]
    public async Task The_test_message_reports_the_outcome()
    {
        using var f = new PanelFactory();
        var owner = await OwnerAsync(f);
        var none = await owner.PostAsync("/api/notifications/discord/test", null);
        Assert.Equal((HttpStatusCode.Conflict, "not_set"), (none.StatusCode, await MissionKit.CodeAsync(none)));

        await SetUrlAsync(owner, Url);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/notifications/discord/test", null)).StatusCode);
        Assert.Contains("work", Assert.Single(f.Discord.Sent).Content);

        f.Discord.Answer = new DiscordResult(DiscordOutcome.Gone, "Discord says this webhook no longer exists (HTTP 404). Paste a new one.");
        var gone = await owner.PostAsync("/api/notifications/discord/test", null);
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
        Assert.Contains("no longer exists", await gone.Content.ReadAsStringAsync());
    }
}
