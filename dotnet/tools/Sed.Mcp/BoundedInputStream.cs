namespace Sed.Mcp;

/// <summary>Bound newline-delimited transport frames before the SDK allocates an entire JSON line.</summary>
public sealed class BoundedInputStream(Stream inner, int maxLineBytes = 2 * 1024 * 1024) : Stream
{
    private int currentLineBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private void Check(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b == (byte)'\n') currentLineBytes = 0;
            else if (++currentLineBytes > maxLineBytes) throw new InvalidDataException("MCP frame exceeds 2 MiB; connection closed.");
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    { int read = inner.Read(buffer, offset, count); Check(buffer.AsSpan(offset, read)); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { int read = await inner.ReadAsync(buffer, token); Check(buffer.Span[..read]); return read; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
