using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ArmaFormats;
using Contracts;

namespace ServerAgent.Files;

// Phase 4b: the file manager's operations. Reads answer at once and are bounded (Ruling 5). Changes are checked at once, then run one at
// a time in the background and reported through Changed, so a long upload or copy never holds up the agent's other commands.
// Writes and extractions are built in <space root>\.13c-tmp\<id> and moved into place (Ruling 7); deletes and overwrites are
// backed up to backups\files\<space>\<time> first (Ruling 6).
public sealed class FileManager : IDisposable
{
    public const int MaxList = 5000, MaxHits = 500, MaxChunk = 262144, MaxQuery = 100, MaxBytes = 900_000;
    public const long MaxWrite = 2L << 30, MaxCrossVolumeBackup = 2L << 30;
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(5);

    private readonly FileSpaces _spaces;
    private readonly Areas _areas;
    private readonly HttpClient _http;
    private readonly ILogger<FileManager> _log;
    private readonly long _maxExtractBytes;
    private readonly int _maxExtractEntries;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    public event Action<FileChangeResult>? Changed;

    public FileManager(FileSpaces spaces, Areas areas, HttpClient http, ILogger<FileManager> log, long maxExtractBytes = 4L << 30, int maxExtractEntries = 20_000)
    {
        (_spaces, _areas, _http, _log, _maxExtractBytes, _maxExtractEntries) = (spaces, areas, http, log, maxExtractBytes, maxExtractEntries);
        foreach (var root in spaces.WritableRoots()) RemoveTemp(root, null);
    }

    public FileListing List(FileSpace space, string path)
    {
        var r = _spaces.Resolve(space, path);
        var dir = new DirectoryInfo(r.Full);
        if (!dir.Exists)
            return r.Relative.Length == 0 ? new FileListing([], false) : throw new AreaException(File.Exists(r.Full) ? "That's a file, not a folder." : "No such folder.");
        var items = new List<FileSystemInfo>();
        long bytes = 0;
        var truncated = false;
        foreach (var e in dir.EnumerateFileSystemInfos().Where(Visible))
        {
            if (items.Count == MaxList || (bytes += Cost(e.Name)) > MaxBytes) { truncated = true; break; }
            items.Add(e);
        }
        return new FileListing(items.OrderBy(i => i is FileInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(Item).ToArray(), truncated);
    }

    // What an item costs in the hub's JSON (non-ASCII is escaped), so a reply stays under the panel's 1 MiB limit (final review I-1).
    private static int Cost(string name) => JsonEncodedText.Encode(name).EncodedUtf8Bytes.Length + 100;

    public FileSearchResult Search(FileSpace space, string path, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQuery) throw new AreaException($"Search for 1 to {MaxQuery} characters.");
        var r = _spaces.Resolve(space, path);
        if (!Directory.Exists(r.Full)) throw new AreaException("No such folder.");
        var hits = new List<FileHit>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(r.Full)]);
        var truncated = false;
        long bytes = 0;
        while (pending.Count > 0 && !truncated)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = pending.Pop().EnumerateFileSystemInfos().Where(Visible).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (hits.Count >= MaxHits || clock.Elapsed > SearchBudget) { truncated = true; break; }
                if (e is DirectoryInfo d) pending.Push(d);
                if (!e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var hit = Path.GetRelativePath(r.Root, e.FullName);
                if ((bytes += Cost(hit)) > MaxBytes) { truncated = true; break; }
                hits.Add(new FileHit(hit, e is DirectoryInfo, e is FileInfo f ? f.Length : 0, e.LastWriteTimeUtc));
            }
        }
        return new FileSearchResult(hits.ToArray(), truncated);
    }

    public FileChunk Read(FileSpace space, string path, long offset, int length)
    {
        if (length is < 1 or > MaxChunk) throw new AreaException($"Read between 1 and {MaxChunk} bytes at a time.");
        var r = _spaces.Resolve(space, path);
        if (!File.Exists(r.Full)) throw new AreaException("No such file.");
        using var fs = new FileStream(r.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset < 0 || offset > fs.Length) throw new AreaException("The offset is outside the file.");
        var data = new byte[(int)Math.Min(length, fs.Length - offset)];
        fs.Position = offset;
        fs.ReadExactly(data);
        return new FileChunk(data, fs.Length);
    }

    // Checked now so a refusal is answered at once; checked again when it runs, since the folder may have changed meanwhile.
    public CommandResult Change(FileChangeSpec spec)
    {
        try { Plan(spec); }
        catch (AreaException e) { return new CommandResult(false, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new CommandResult(false, e.Message); }
        _ = Task.Run(() => RunAsync(spec));
        return new CommandResult(true, null);
    }

    private sealed record Planned(ResolvedPath Source, ResolvedPath? Target, bool IsFile);

    private Planned Plan(FileChangeSpec spec)
    {
        var space = spec.Space;
        switch (spec.Op)
        {
            case FileOps.Mkdir:
            {
                var t = _spaces.ResolveForWrite(space, spec.Path, isFile: false);
                if (Exists(t.Full)) throw new AreaException("Something with that name is already there.");
                return new(t, null, false);
            }
            case FileOps.Write:
            {
                var t = _spaces.ResolveForWrite(space, spec.Path, isFile: true);
                if (spec.Size is < 0 or > MaxWrite || spec.Sha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigitLower))
                    throw new AreaException("The upload's size or hash isn't valid.");
                if (Directory.Exists(t.Full)) throw new AreaException("A folder has that name.");
                if (File.Exists(t.Full) && !spec.Overwrite) throw new AreaException("A file with that name is already there.");
                return new(t, null, true);
            }
            case FileOps.Delete:
            {
                var isFile = IsFile(_spaces.Resolve(space, spec.Path).Full);
                return new(_spaces.ResolveForWrite(space, spec.Path, isFile), null, isFile);
            }
            case FileOps.Move or FileOps.Copy:
            {
                var isFile = IsFile(_spaces.Resolve(space, spec.Path).Full);
                var source = spec.Op == FileOps.Move ? _spaces.ResolveForWrite(space, spec.Path, isFile) : _spaces.Resolve(space, spec.Path);
                if (source.Relative.Length == 0) throw new AreaException("The folder itself can't be moved or copied.");
                var target = _spaces.ResolveForWrite(space, spec.Target ?? "", isFile);
                if (Exists(target.Full)) throw new AreaException("Something with that name is already there.");
                if (!Directory.Exists(Path.GetDirectoryName(target.Full))) throw new AreaException("The folder to put it in doesn't exist.");
                if (!isFile && (target.Full + "\\").StartsWith(source.Full + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new AreaException("A folder can't go inside itself.");
                return new(source, target, isFile);
            }
            case FileOps.Extract:
            {
                var source = _spaces.Resolve(space, spec.Path);
                if (!source.Relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(source.Full))
                    throw new AreaException("Only .zip files can be extracted.");
                var target = _spaces.ResolveForWrite(space, spec.Target ?? source.Relative[..^4], isFile: false);
                if (Exists(target.Full)) throw new AreaException("Something with that name is already there; extract into a new folder.");
                return new(source, target, false);
            }
            default: throw new AreaException($"Unknown change \"{spec.Op}\".");
        }
    }

    private async Task RunAsync(FileChangeSpec spec)
    {
        try { await _one.WaitAsync(_stop.Token); }
        catch (OperationCanceledException) { return; }
        FileChangeResult result;
        string? root = null;
        try
        {
            var plan = Plan(spec);
            root = plan.Source.Root;
            var backup = spec.Op switch
            {
                FileOps.Mkdir => Mkdir(plan),
                FileOps.Write => await WriteAsync(spec, plan),
                FileOps.Delete => Delete(plan),
                FileOps.Move => Move(plan),
                FileOps.Copy => Copy(spec, plan),
                _ => Extract(spec, plan),
            };
            result = new FileChangeResult(spec.Id, true, null, backup);
        }
        catch (Exception e) when (e is AreaException or IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            result = new FileChangeResult(spec.Id, false, e.Message, null);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "A file change failed");
            result = new FileChangeResult(spec.Id, false, "The change failed on the agent; see its log.", null);
        }
        finally
        {
            if (root is not null) RemoveTemp(root, spec.Id);
            _one.Release();
        }
        Changed?.Invoke(result);
    }

    private static string? Mkdir(Planned plan)
    {
        Directory.CreateDirectory(plan.Source.Full);
        return null;
    }

    private async Task<string?> WriteAsync(FileChangeSpec spec, Planned plan)
    {
        var temp = Path.Combine(TempOf(plan.Source.Root, spec.Id), "upload");
        using (var response = await _http.GetAsync($"api/agent/transfers/{spec.Id}", HttpCompletionOption.ResponseHeadersRead, _stop.Token))
        {
            if (!response.IsSuccessStatusCode) throw new AreaException($"The panel didn't send the file (HTTP {(int)response.StatusCode}).");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var body = await response.Content.ReadAsStreamAsync(_stop.Token))
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    if ((total += n) > spec.Size) break;
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
                }
            }
            if (total != spec.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != spec.Sha256)
                throw new AreaException("The file arrived damaged (its size or hash didn't match), so nothing was written.");
        }
        string? backup = null;
        if (File.Exists(plan.Source.Full))
        {
            if (spec.ExpectSha256 is { } expected && HashOf(plan.Source.Full) != expected)
                throw new AreaException("The file changed since it was opened; reload it before saving.");
            backup = BackupCopy(plan.Source);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(plan.Source.Full)!);
        File.Move(temp, plan.Source.Full, overwrite: true);
        return backup;
    }

    private string Delete(Planned plan)
    {
        var item = plan.Source;
        if (!plan.IsFile) RefuseLinks(item.Full);
        var folder = BackupFolder(item.BackupKey);
        var to = Path.Combine(_areas.Resolve("backups", folder), item.Relative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (SameVolume(item.Full, to))
        {
            if (plan.IsFile) File.Move(item.Full, to);
            else Directory.Move(item.Full, to);
        }
        else
        {
            if (SizeOf(item.Full, plan.IsFile) > MaxCrossVolumeBackup) throw new AreaException("That's too large to back up to another drive, so it wasn't deleted.");
            CopyItem(item.Full, to, plan.IsFile, null);
            if (plan.IsFile) File.Delete(item.Full);
            else Directory.Delete(item.Full, recursive: true);
        }
        return folder;
    }

    private static string? Move(Planned plan)
    {
        if (plan.IsFile) File.Move(plan.Source.Full, plan.Target!.Full);
        else Directory.Move(plan.Source.Full, plan.Target!.Full);
        return null;
    }

    // Built in the temp folder and moved into place, so a failure leaves no half copy. Each copied file obeys the target's rules.
    private string? Copy(FileChangeSpec spec, Planned plan)
    {
        if (!plan.IsFile) RefuseLinks(plan.Source.Full);
        var temp = Path.Combine(TempOf(plan.Target!.Root, spec.Id), "copy");
        CopyItem(plan.Source.Full, temp, plan.IsFile, rel => _spaces.ResolveForWrite(spec.Space, Path.Combine(plan.Target.Relative, rel), isFile: true));
        if (plan.IsFile) File.Move(temp, plan.Target.Full);
        else Directory.Move(temp, plan.Target.Full);
        return null;
    }

    private string? Extract(FileChangeSpec spec, Planned plan)
    {
        var target = plan.Target!;
        var temp = Path.Combine(TempOf(target.Root, spec.Id), "extract");
        Directory.CreateDirectory(temp);
        // The declared count is checked before .NET builds an entry for every directory record; it checks its own walk against it (final review I-3).
        if (DeclaredEntries(plan.Source.Full) > _maxExtractEntries) throw new AreaException($"The zip has more than {_maxExtractEntries:N0} entries.");
        using (var zip = ZipFile.OpenRead(plan.Source.Full))
        {
            long written = 0;
            foreach (var entry in zip.Entries)
            {
                var rel = entry.FullName.Replace('/', '\\').TrimEnd('\\');
                if (rel.Length == 0) continue;
                var isFolder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                // The entry's place in the real folder obeys every rule (names, containment, file types); it is written in the temp copy.
                _spaces.ResolveForWrite(spec.Space, Path.Combine(target.Relative, rel), isFile: !isFolder);
                var to = Areas.ResolveUnder(temp, rel);
                if (isFolder) { Directory.CreateDirectory(to); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                using var input = entry.Open();
                using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    if ((written += n) > _maxExtractBytes) throw new AreaException($"The zip unpacks to more than {_maxExtractBytes / (1024 * 1024):N0} MB.");
                    output.Write(buffer, 0, n);
                }
            }
        }
        Directory.Move(temp, target.Full);
        return null;
    }

    // The entry count the zip's end-of-central-directory record declares, or its Zip64 record's when that says 0xFFFF.
    private static long DeclaredEntries(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var tail = (int)Math.Min(fs.Length, 22 + 65_535);
        var buffer = new byte[tail];
        fs.Position = fs.Length - tail;
        fs.ReadExactly(buffer);
        for (var i = tail - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(buffer, i) != 0x06054b50) continue;
            long count = BitConverter.ToUInt16(buffer, i + 10);
            if (count != 0xFFFF) return count;
            var locator = fs.Length - tail + i - 20;
            var record = locator >= 0 ? ReadAt(fs, locator, 20) : null;
            if (record is null || BitConverter.ToUInt32(record, 0) != 0x07064b50) break;
            var zip64 = BitConverter.ToInt64(record, 8);
            var head = zip64 >= 0 && zip64 <= fs.Length - 56 ? ReadAt(fs, zip64, 56) : null;
            if (head is null || BitConverter.ToUInt32(head, 0) != 0x06064b50) break;
            return BitConverter.ToInt64(head, 32);
        }
        throw new AreaException("That isn't a readable .zip file.");
    }

    private static byte[] ReadAt(FileStream fs, long position, int length)
    {
        var data = new byte[length];
        fs.Position = position;
        fs.ReadExactly(data);
        return data;
    }

    private string BackupCopy(ResolvedPath item)
    {
        var folder = BackupFolder(item.BackupKey);
        var to = Path.Combine(_areas.Resolve("backups", folder), item.Relative);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(item.Full, to);
        return folder;
    }

    private string BackupFolder(string key)
    {
        var baseName = Path.Combine("files", key, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var folder = baseName;
        for (var n = 2; Directory.Exists(_areas.Resolve("backups", folder)); n++) folder = $"{baseName}-{n}";
        return folder;
    }

    private static void CopyItem(string from, string to, bool isFile, Func<string, ResolvedPath>? check)
    {
        if (isFile)
        {
            check?.Invoke("");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            return;
        }
        Directory.CreateDirectory(to);
        foreach (var e in new DirectoryInfo(from).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, e.FullName);
            if (e is DirectoryInfo) { Directory.CreateDirectory(Path.Combine(to, rel)); continue; }
            check?.Invoke(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, rel))!);
            File.Copy(e.FullName, Path.Combine(to, rel));
        }
    }

    private static void RefuseLinks(string folder)
    {
        if (new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Any(e => (e.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new AreaException("That folder contains a link to somewhere else, so the file manager leaves it alone.");
    }

    private static long SizeOf(string path, bool isFile) => isFile ? new FileInfo(path).Length
        : new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static bool SameVolume(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsFile(string path) =>
        File.Exists(path) ? true : Directory.Exists(path) ? false : throw new AreaException("No such file or folder.");

    private static bool Visible(FileSystemInfo e) =>
        (e.Attributes & FileAttributes.ReparsePoint) == 0 && !e.Name.Equals(FileNames.TempFolder, StringComparison.OrdinalIgnoreCase);

    private static FileItem Item(FileSystemInfo e) => new(e.Name, e is DirectoryInfo, e is FileInfo f ? f.Length : 0, e.LastWriteTimeUtc);

    private static string HashOf(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string TempOf(string root, Guid id)
    {
        var dir = Path.Combine(root, FileNames.TempFolder, id.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // One change's temp folder, or every leftover one at start; .13c-tmp itself goes once it's empty.
    private void RemoveTemp(string root, Guid? id)
    {
        var tmp = Path.Combine(root, FileNames.TempFolder);
        try
        {
            if (!Directory.Exists(tmp)) return;
            if (id is { } one) { var dir = Path.Combine(tmp, one.ToString("N")); if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            else Directory.Delete(tmp, recursive: true);
            if (Directory.Exists(tmp) && !Directory.EnumerateFileSystemEntries(tmp).Any()) Directory.Delete(tmp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.LogWarning(e, "Couldn't remove a file-manager temp folder in {Root}", root); }
    }

    public void Dispose() => _stop.Cancel();
}
