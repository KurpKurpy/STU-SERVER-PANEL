using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Panel.Features.Mods;

namespace Panel.Tests;

public sealed class PanelFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "13c-tests", Guid.NewGuid().ToString("N"));
    private readonly string _webRoot;
    private readonly string _twoFactorRoles;
    private readonly int _stampSeconds;
    private readonly int _authPerMinute;
    private readonly Action<IServiceCollection>? _services;
    private readonly int _stopWaitSeconds, _fileWaitSeconds;

    public PanelFactory(string? twoFactorRequiredRoles = "", int stampSeconds = 0, int authPerMinute = 1000, Action<IServiceCollection>? services = null,
        int stopWaitSeconds = 150, int fileWaitSeconds = 600)
    {
        _stopWaitSeconds = stopWaitSeconds;
        _fileWaitSeconds = fileWaitSeconds;
        _services = services;
        _twoFactorRoles = twoFactorRequiredRoles ?? "";
        _stampSeconds = stampSeconds;
        _authPerMinute = authPerMinute;
        _webRoot = Path.Combine(DataDir, "wwwroot");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>13th Console</title>");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("DataDir", DataDir);
        builder.UseSetting("Auth:TwoFactorRequiredRoles", _twoFactorRoles);
        builder.UseSetting("Auth:StampValidationSeconds", _stampSeconds.ToString());
        builder.UseSetting("RateLimit:AuthPerMinute", _authPerMinute.ToString());
        builder.UseSetting("StartMission:StopWaitSeconds", _stopWaitSeconds.ToString());
        builder.UseSetting("Files:ChangeWaitSeconds", _fileWaitSeconds.ToString());
        builder.UseSetting("Notifications:ReminderScanSeconds", "0"); // tests call OperationReminders.RunOnceAsync themselves
        builder.UseSetting("Schedules:TickSeconds", "0");          // Phase 7b: tests call ScheduleRunner.RunDueAsync themselves
        builder.UseSetting("Schedules:MinuteSeconds", "0.01");     // a schedule "minute" (warnings, polling) is 10 ms
        builder.UseSetting("Schedules:WaitLimitMinutes", "3000");  // 30 s for jobs and stops
        builder.UseWebRoot(_webRoot);
        builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, TestRemoteIp>());
        if (_services is not null) builder.ConfigureServices(_services);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<IWorkshopClient>(Workshop); // no Steam Web API calls from tests
            s.AddSingleton<Panel.Features.Notifications.IDiscordWebhook>(Discord); // and no Discord
        });
    }

    // What the fake Steam Web API answers; unknown IDs are ordinary Arma 3 items named "Mod <id>".
    public FakeWorkshop Workshop { get; } = new();

    // What the fake Discord received (Phase 6b).
    public FakeDiscord Discord { get; } = new();

    // TestServer has no client address; act as a loopback caller unless the test names one (X-Test-Remote-Ip).
    private sealed class TestRemoteIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ctx.Request.Headers["X-Test-Remote-Ip"].FirstOrDefault() ?? "127.0.0.1");
                return nxt(ctx);
            });
            next(app);
        };
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add("X-Console", "1");
        return client;
    }

    public HttpClient CreateClientWithoutHeader() => base.CreateClient();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // The SQLite pool keeps console.db open after the host stops, and the delete then failed quietly: 15,000 test folders
        // (23 GB) had piled up in %TEMP% by Phase 6b. Clearing the pool first lets it go.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
