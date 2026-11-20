using System.Text.Json;
using ArmaFormats;
using Microsoft.EntityFrameworkCore;
using Panel.Data;
using Panel.Storage;

namespace Panel.Features.Missions;

public sealed class MissionException(string message) : Exception(message);

// The working copy and versions (design §5.4 steps 2–5, Phase 4a Ruling 8).
public sealed class MissionVersions(AppDb db, BlobStore blobs, MissionDeployments deployments)
{
    // Makes the working copy exactly these files: new and changed files and deletions each become a file revision.
    public async Task ReplaceWorkingCopyAsync(Mission m, IReadOnlyDictionary<string, byte[]> files, Guid? userId, string note)
    {
        var current = (await db.MissionWorkingFiles.Where(w => w.MissionId == m.Id).ToListAsync()).ToDictionary(w => w.Path, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var (path, data) in files)
        {
            var sha = await blobs.PutAsync(data);
            if (current.Remove(path, out var existing))
            {
                if (existing.Sha256 == sha) continue;
                (existing.Sha256, existing.Size, existing.UpdatedById, existing.UpdatedAt) = (sha, data.LongLength, userId, now); // keeps the stored spelling
            }
            else db.MissionWorkingFiles.Add(new MissionWorkingFile { MissionId = m.Id, Path = path, Sha256 = sha, Size = data.LongLength, UpdatedById = userId, UpdatedAt = now });
            db.MissionFileRevisions.Add(new MissionFileRevision { Id = Guid.NewGuid(), MissionId = m.Id, Path = existing?.Path ?? path, Sha256 = sha, ById = userId, At = now, Note = note });
        }
        foreach (var gone in current.Values)
        {
            db.MissionWorkingFiles.Remove(gone);
            db.MissionFileRevisions.Add(new MissionFileRevision { Id = Guid.NewGuid(), MissionId = m.Id, Path = gone.Path, Sha256 = null, ById = userId, At = now, Note = note });
        }
        m.UpdatedAt = now;
        await db.SaveChangesAsync();
    }

    // Snapshots the working copy in one transaction, packs it, reads the PBO back to check it, scans the addons and makes it current.
    public async Task<MissionVersion> SaveAsync(Mission m, string? note, Guid? userId, string? originalUploadSha = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var files = await db.MissionWorkingFiles.Where(w => w.MissionId == m.Id).OrderBy(w => w.Path).ToListAsync();
        var entries = new List<PboEntry>(files.Count);
        foreach (var f in files) entries.Add(new PboEntry(f.Path, await blobs.ReadAsync(f.Sha256), (uint)new DateTimeOffset(f.UpdatedAt).ToUnixTimeSeconds()));
        var sqm = entries.FirstOrDefault(e => e.Path.Equals("mission.sqm", StringComparison.OrdinalIgnoreCase))
            ?? throw new MissionException("The mission has no mission.sqm at its top.");
        MissionAddons addons;
        try { addons = MissionScan.Addons(sqm.Data); }
        catch (Exception e) when (e is ConfigSyntaxException or RapException) { throw new MissionException($"mission.sqm can't be read: {e.Message}"); }

        var properties = JsonSerializer.Deserialize<Dictionary<string, string>>(m.PboPropertiesJson) ?? [];
        byte[] pbo;
        using (var ms = new MemoryStream())
        {
            Pbo.Write(new PboFile(properties, entries), ms);
            pbo = ms.ToArray();
        }
        var back = Pbo.Read(new MemoryStream(pbo));
        if (back.Entries.Count != entries.Count || back.Entries.Zip(entries).Any(x => x.First.Path != x.Second.Path || !x.First.Data.AsSpan().SequenceEqual(x.Second.Data)))
            throw new MissionException("The packed PBO didn't read back the same, so no version was saved.");
        var pboSha = await blobs.PutAsync(pbo);

        var number = (await db.MissionVersions.Where(v => v.MissionId == m.Id).MaxAsync(v => (int?)v.Number) ?? 0) + 1;
        var version = new MissionVersion
        {
            Id = Guid.NewGuid(), MissionId = m.Id, Number = number, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedById = userId,
            PboSha256 = pboSha, PboSize = pbo.LongLength, OriginalUploadSha256 = originalUploadSha, Binarized = addons.Binarized,
        };
        db.MissionVersions.Add(version);
        db.MissionVersionFiles.AddRange(files.Select(f => new MissionVersionFile { VersionId = version.Id, Path = f.Path, Sha256 = f.Sha256, Size = f.Size }));
        var meta = addons.Meta.GroupBy(x => x.ClassName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        db.MissionVersionAddons.AddRange(addons.Addons.Distinct(StringComparer.OrdinalIgnoreCase).Select(a => new MissionVersionAddon
        {
            VersionId = version.Id, Addon = a, ModName = meta.GetValueOrDefault(a)?.Name, ModUrl = meta.GetValueOrDefault(a)?.Url,
        }));
        (m.CurrentVersionId, m.UpdatedAt) = (version.Id, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await deployments.DeployAsync(m, version, userId); // every saved version goes onto the servers (Amendment A)
        return version;
    }
}
