using System.Security.Cryptography;
using System.Text;
using ArmaFormats;
using Contracts;
using ServerAgent.Games;

namespace ServerAgent;

// Apply (plan Ruling 1): the only writer of a profile's config files. The agent picks every path; the panel sends contents.
// It backs up what's there, writes temp files, moves them into place, and restores the backup if any step fails.
public sealed class ConfigWriter(Areas areas, IGameAdapter game, Supervisor supervisor, ILogger<ConfigWriter> log)
{
    public const int ServerCfgMax = 65536, BasicCfgMax = 16384, DifficultyMax = 16384, ReadMax = 262144;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public ApplyResult Apply(ConfigBundle bundle)
    {
        ApplyResult Refuse(string error, string? backup = null) => new(false, error, backup, []);
        if (!Supervisor.SlugPattern().IsMatch(bundle.Slug ?? "")) return Refuse("The profile slug is invalid.");
        if (supervisor.Running.ContainsKey(bundle.ProfileId)) return Refuse("Stop the server before applying; it rewrites its profile file when it exits.");
        if ((bundle.ServerCfg ?? "").Length > ServerCfgMax) return Refuse($"server.cfg is over {ServerCfgMax:N0} characters.");
        if (bundle.BasicCfg is { Length: > BasicCfgMax }) return Refuse($"basic.cfg is over {BasicCfgMax:N0} characters.");
        if ((bundle.DifficultyPresets ?? "").Length > DifficultyMax) return Refuse($"The difficulty class is over {DifficultyMax:N0} characters.");
        try
        {
            var items = ConfigDocument.Parse(bundle.DifficultyPresets ?? "").Items.Where(i => i is not CfgTrivia).ToList();
            if (items is not [CfgClass { Name: var n, Body: not null }] || !n.Equals("DifficultyPresets", StringComparison.OrdinalIgnoreCase))
                return Refuse("The difficulty text must be one class DifficultyPresets.");
        }
        catch (ConfigSyntaxException e) { return Refuse($"The difficulty text doesn't parse: {e.Message}"); }

        var targets = game.ConfigTargets(bundle.Slug!).Select(t => (t.Name, Path: areas.Resolve(t.Area, t.RelativePath))).ToList();
        string? backupRel = null;
        string? backupDir = null;
        var written = new List<(string Name, string Path)>();
        var temps = new List<string>();
        try
        {
            // 1. Back up whatever exists now.
            var existing = targets.Where(t => File.Exists(t.Path)).ToList();
            if (existing.Count > 0)
            {
                backupRel = Path.Combine("profiles", bundle.Slug!, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                backupDir = areas.Resolve("backups", backupRel);
                Directory.CreateDirectory(backupDir);
                foreach (var t in existing) File.Copy(t.Path, Path.Combine(backupDir, t.Name), overwrite: true);
            }

            // 2. The new contents; the profile file keeps everything the server wrote outside DifficultyPresets (Ruling 7).
            var profile = targets[2];
            var current = File.Exists(profile.Path) ? File.ReadAllText(profile.Path) : "";
            string merged;
            try { merged = ConfigDocument.Parse(current).ReplaceClass("DifficultyPresets", bundle.DifficultyPresets!); }
            catch (ConfigSyntaxException e) { return Refuse($"The server's profile file doesn't parse, so it was left alone: {e.Message}", backupRel); }
            var contents = new (string Name, string Path, string? Text)[]
            {
                (targets[0].Name, targets[0].Path, bundle.ServerCfg ?? ""),
                (targets[1].Name, targets[1].Path, bundle.BasicCfg),
                (profile.Name, profile.Path, merged),
            };

            // 3. Temp files next to each target, then move them into place.
            foreach (var c in contents.Where(c => c.Text is not null))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(c.Path)!);
                var tmp = c.Path + ".13c-tmp";
                File.WriteAllText(tmp, c.Text, Utf8);
                temps.Add(tmp);
            }
            foreach (var c in contents)
            {
                try
                {
                    if (c.Text is null) { if (File.Exists(c.Path)) File.Delete(c.Path); }
                    else File.Move(c.Path + ".13c-tmp", c.Path, overwrite: true);
                    written.Add((c.Name, c.Path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Couldn't write {c.Name}: {e.Message}", e);
                }
            }

            // 4. Re-read what's on disk now, so the panel records "applied" only for what was really written.
            var files = contents.Where(c => c.Text is not null).Select(c =>
            {
                var bytes = File.ReadAllBytes(c.Path);
                return new WrittenFile(c.Name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
            }).ToArray();
            log.LogInformation("Applied profile {Slug} (backup {Backup})", bundle.Slug, backupRel ?? "none");
            return new ApplyResult(true, null, backupRel, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AreaException)
        {
            foreach (var (name, path) in written)
            {
                try
                {
                    var saved = backupDir is null ? null : Path.Combine(backupDir, name);
                    if (saved is not null && File.Exists(saved)) File.Copy(saved, path, overwrite: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    log.LogError(restore, "Couldn't restore {Name} after a failed apply", name);
                }
            }
            log.LogWarning("Applying profile {Slug} failed: {Error}", bundle.Slug, e.Message);
            return Refuse(e.Message, backupRel);
        }
        finally
        {
            foreach (var tmp in temps)
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    public ConfigSnapshot Read(Guid profileId, string slug)
    {
        if (!Supervisor.SlugPattern().IsMatch(slug ?? "")) throw new AreaException("The profile slug is invalid.");
        var texts = game.ConfigTargets(slug!).Select(t =>
        {
            var path = areas.Resolve(t.Area, t.RelativePath);
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > ReadMax) throw new AreaException($"{t.Name} is larger than {ReadMax / 1024} KB.");
            using var reader = new StreamReader(fs, Utf8);
            return reader.ReadToEnd();
        }).ToArray();
        return new ConfigSnapshot(texts[0], texts[1], texts[2]);
    }
}
