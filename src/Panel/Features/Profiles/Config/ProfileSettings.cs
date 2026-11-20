using System.Text.Json;

namespace Panel.Features.Profiles.Config;

public sealed record HeadlessSettings(bool Enabled = false, int Count = 1, bool WithServer = false, string[]? Addresses = null)
{
    public string[] AddressList => Addresses is { Length: > 0 } ? Addresses : ["127.0.0.1"];
}

// A profile's configuration. A key missing from a dictionary isn't written, so Arma's default applies.
public sealed record ProfileSettings(Dictionary<string, JsonElement> Server, Dictionary<string, JsonElement> Basic,
    Dictionary<string, JsonElement> Difficulty, Dictionary<string, JsonElement> Launch, string ExtraArgs,
    HeadlessSettings Headless, string? MissionsOverride)
{
    public static ProfileSettings Empty => new([], [], [], [], "", new HeadlessSettings(), null);
}

public sealed record RotationItem(string Template, string Difficulty, string ParamsText);

// The three passwords; null or "" means not set.
public sealed record Secrets(string? Password, string? PasswordAdmin, string? ServerCommandPassword)
{
    public static Secrets None { get; } = new(null, null, null);
    public static readonly string[] Names = ["password", "passwordAdmin", "serverCommandPassword"];

    public string? Get(string name) => name.ToLowerInvariant() switch
    {
        "password" => Password,
        "passwordadmin" => PasswordAdmin,
        "servercommandpassword" => ServerCommandPassword,
        _ => null,
    };

    public Secrets With(string name, string? value) => name.ToLowerInvariant() switch
    {
        "password" => this with { Password = value },
        "passwordadmin" => this with { PasswordAdmin = value },
        "servercommandpassword" => this with { ServerCommandPassword = value },
        _ => this,
    };
}

public sealed record ProfileConfig(ProfileSettings Settings, string ExtraServerCfg, string ExtraBasicCfg, string DifficultyOverride,
    IReadOnlyList<RotationItem> Rotation, Secrets Secrets);

public enum SecretMode { Real, Omit }

public sealed record GeneratedFiles(string ServerCfg, string? BasicCfg, string DifficultyPresets);

public sealed record ConfigError(string Field, string Message);

public sealed record ConfigWarning(int Line, string Message);

// For difficulty, Extra is the whole class kept verbatim (DifficultyOverride) when it couldn't become values.
public sealed record ParsedFile(Dictionary<string, JsonElement> Values, string Extra, Secrets? Secrets,
    IReadOnlyList<RotationItem>? Rotation, string? MissionsOverride, HeadlessSettings? Headless,
    IReadOnlyList<ConfigWarning> Warnings);
