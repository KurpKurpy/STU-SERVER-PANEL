using System.Security.Cryptography;
using Panel.Auth;
using Panel.Storage;

namespace Panel.Tests;

public class BlobStoreTests
{
    private static (BlobStore Store, string Root) New()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blobs-" + Guid.NewGuid().ToString("N"));
        return (new BlobStore(new PanelPaths(dir)), Path.Combine(dir, "blobs"));
    }

    [Fact]
    public async Task Blobs_are_content_addressed_and_never_overwritten()
    {
        var (store, root) = New();
        var data = "version=54;"u8.ToArray();

        var sha = await store.PutAsync(new MemoryStream(data));
        var path = Path.Combine(root, sha[..2], sha[2..4], sha);
        var written = File.GetLastWriteTimeUtc(path);
        await Task.Delay(50);
        var again = await store.PutAsync(new MemoryStream(data));

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), sha);
        Assert.Equal(sha, again);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path)); // the second put found it and wrote nothing
        Assert.True(store.Exists(sha));
        Assert.Equal(data.Length, store.Size(sha));
        using var read = store.Open(sha);
        Assert.Equal(data, new BinaryReader(read).ReadBytes(100));
        Assert.Throws<ArgumentException>(() => store.Open("../../etc"));
    }

    private sealed class Breaks(int after) : Stream
    {
        private int _given;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_given >= after) throw new IOException("the upload broke");
            var n = Math.Min(count, after - _given);
            Array.Fill(buffer, (byte)7, offset, n);
            _given += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Blob_writes_are_atomic()
    {
        var (store, root) = New();

        await Assert.ThrowsAsync<IOException>(() => store.PutAsync(new Breaks(100_000)));

        // Nothing half-written stays behind: no blob and no temp file.
        Assert.Empty(Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : []);
    }
}
