using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Panel.Data;
using Panel.Features.Profiles.Config;

namespace Panel.Features.Profiles;

// One revision's configuration. Passwords are never in it; they're in the revision's SecretsProtected.
public sealed record ProfileSnapshot(ProfileSettings Settings, string ExtraServerCfg, string ExtraBasicCfg, string DifficultyOverride,
    RotationItem[] Rotation, Guid? DifficultyPresetId, Guid? ModCollectionId = null)
{
    public static ProfileSnapshot Empty => new(ProfileSettings.Empty, "", "", "", [], null);

    public ProfileConfig ToConfig(Secrets secrets) => new(Settings, ExtraServerCfg, ExtraBasicCfg, DifficultyOverride, Rotation, secrets);
}

// Saves revisions and mirrors the head onto the profile row. Passwords are encrypted with purpose "profile.secrets".
public sealed class ProfileStore(AppDb db, IDataProtectionProvider dp)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IDataProtector Protector => dp.CreateProtector("profile.secrets");

    public string Protect(Secrets secrets) => Protector.Protect(JsonSerializer.Serialize(secrets, Json));

    // Null when the text can't be decrypted (the panel's keys changed).
    public Secrets? Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return Secrets.None;
        try { return JsonSerializer.Deserialize<Secrets>(Protector.Unprotect(protectedText), Json) ?? Secrets.None; }
        catch (CryptographicException) { return null; }
    }

    public Secrets? LoadSecrets(Profile p) => Unprotect(p.SecretsProtected);

    public (ProfileSnapshot Snapshot, Secrets? Secrets) Load(ProfileRevision revision) =>
        (JsonSerializer.Deserialize<ProfileSnapshot>(revision.SnapshotJson, Json) ?? ProfileSnapshot.Empty, Unprotect(revision.SecretsProtected));

    // The head configuration from the profile row and its rotation rows.
    public async Task<ProfileSnapshot> HeadAsync(Profile p)
    {
        var settings = p.SettingsJson is null ? ProfileSettings.Empty : JsonSerializer.Deserialize<ProfileSettings>(p.SettingsJson, Json) ?? ProfileSettings.Empty;
        var rotation = await db.RotationEntries.Where(e => e.ProfileId == p.Id).OrderBy(e => e.Order)
            .Select(e => new RotationItem(e.FileName, e.Difficulty, e.ParamsText)).ToArrayAsync();
        return new ProfileSnapshot(settings, p.ExtraServerCfg, p.ExtraBasicCfg, p.DifficultyOverride, rotation, p.DifficultyPresetId, p.ModCollectionId);
    }

    public Task<int> HeadNumberAsync(Guid profileId) =>
        db.ProfileRevisions.Where(r => r.ProfileId == profileId).MaxAsync(r => (int?)r.Number).ContinueWith(t => t.Result ?? 0);

    public async Task<ProfileRevision> SaveRevisionAsync(Profile p, ProfileSnapshot s, Secrets secrets, string? note, Guid? byId)
    {
        var revision = new ProfileRevision
        {
            Id = Guid.NewGuid(),
            ProfileId = p.Id,
            Number = await HeadNumberAsync(p.Id) + 1,
            SnapshotJson = JsonSerializer.Serialize(s, Json),
            SecretsProtected = Protect(secrets),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedById = byId,
        };
        db.ProfileRevisions.Add(revision);
        p.SettingsJson = JsonSerializer.Serialize(s.Settings, Json);
        (p.ExtraServerCfg, p.ExtraBasicCfg, p.DifficultyOverride, p.DifficultyPresetId, p.ModCollectionId) =
            (s.ExtraServerCfg, s.ExtraBasicCfg, s.DifficultyOverride, s.DifficultyPresetId, s.ModCollectionId);
        p.SecretsProtected = revision.SecretsProtected;
        p.UpdatedAt = revision.CreatedAt;
        db.RotationEntries.RemoveRange(await db.RotationEntries.Where(e => e.ProfileId == p.Id).ToListAsync());
        db.RotationEntries.AddRange(s.Rotation.Select((r, i) => new RotationEntry
        {
            ProfileId = p.Id, Order = i, FileName = r.Template, Difficulty = r.Difficulty, ParamsText = r.ParamsText,
        }));
        await db.SaveChangesAsync();
        return revision;
    }
}
