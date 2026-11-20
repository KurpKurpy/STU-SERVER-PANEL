using System.Security.Cryptography;
using Panel.Auth;

namespace Panel.Storage;

// Content-addressed files at <DataDir>/blobs/ab/cd/<sha256> (Phase 4 Ruling 7). A blob is written through a temp file and a rename,
// never overwritten and never deleted in 4a, so every version can always be restored.
public sealed class BlobStore(PanelPaths paths)
{
    public string Root { get; } = Path.Combine(paths.DataDir, "blobs");

    public async Task<string> PutAsync(Stream data, CancellationToken ct = default)
    {
        var temp = Path.Combine(Root, "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        string sha;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await data.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            var target = PathOf(sha);
            if (File.Exists(target)) return sha; // same content already stored: never rewritten
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target)) { } // another request stored the same content first
            return sha;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            TryRemoveEmptyTemp();
        }
    }

    public Task<string> PutAsync(byte[] data, CancellationToken ct = default) => PutAsync(new MemoryStream(data, writable: false), ct);

    public bool Exists(string sha) => File.Exists(PathOf(sha));
    public long Size(string sha) => new FileInfo(PathOf(sha)).Length;
    public Stream Open(string sha) => new FileStream(PathOf(sha), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
    public async Task<byte[]> ReadAsync(string sha, CancellationToken ct = default) => await File.ReadAllBytesAsync(PathOf(sha), ct);

    public string PathOf(string sha)
    {
        if (sha is not { Length: 64 } || !sha.All(char.IsAsciiHexDigitLower)) throw new ArgumentException("A blob is named by its lowercase SHA-256.", nameof(sha));
        return Path.Combine(Root, sha[..2], sha[2..4], sha);
    }

    private void TryRemoveEmptyTemp()
    {
        try
        {
            var tmp = Path.Combine(Root, "tmp");
            if (Directory.Exists(tmp) && !Directory.EnumerateFileSystemEntries(tmp).Any()) Directory.Delete(tmp);
            if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any()) Directory.Delete(Root);
        }
        catch (IOException) { } // a concurrent put is using it
    }
}
