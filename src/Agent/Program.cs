using ServerAgent;
using ServerAgent.Files;
using ServerAgent.Games;
using ServerAgent.Games.Arma3;
using ServerAgent.Resources;
using ServerAgent.Steam;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "13th Console Agent");

var options = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new Areas(options, options.InstallRoots.Length > 0 ? options.InstallRoots : InstallDetector.Detect()));
builder.Services.AddSingleton<IGameAdapter, Arma3Adapter>();
builder.Services.AddSingleton<Supervisor>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<ConfigWriter>();
builder.Services.AddSingleton<ResourceSampler>();
builder.Services.AddSingleton(new SteamCmdInstaller(new HttpClient()));
builder.Services.AddSingleton(sp => new ServerAgent.Backups.ServerBackups(sp.GetRequiredService<Areas>(), options,
    () => sp.GetRequiredService<Supervisor>().Running.Count > 0)); // Phase 7b
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<ServerAgent.Mods.ModStore>();
builder.Services.AddSingleton<ServerAgent.Mods.KeyService>();
builder.Services.AddSingleton<ServerAgent.Mods.AddonScanner>();
// Pulls mission PBOs (Phase 4a Ruling 11) and file-manager uploads (Phase 4b Ruling 12) from the panel, with the same agent key as the hub.
HttpClient PanelHttp() => new() { BaseAddress = new Uri(options.PanelUrl.TrimEnd('/') + "/"), DefaultRequestHeaders = { { "Authorization", "AgentKey " + options.Key } } };
builder.Services.AddSingleton(sp => new ServerAgent.Missions.MissionWriter(sp.GetRequiredService<Areas>(), PanelHttp(),
    sp.GetRequiredService<ILogger<ServerAgent.Missions.MissionWriter>>()));
builder.Services.AddSingleton(sp => new FileSpaces(sp.GetRequiredService<Areas>(), sp.GetRequiredService<Supervisor>().IsRunningSlug));
builder.Services.AddSingleton(sp => new FileManager(sp.GetRequiredService<FileSpaces>(), sp.GetRequiredService<Areas>(), PanelHttp(),
    sp.GetRequiredService<ILogger<FileManager>>()));
builder.Services.AddHostedService<PanelConnection>();

builder.Build().Run();
