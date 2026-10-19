namespace IronPaw.AgentCore.Mcp;

// Limits apply before SDK decoding, including unterminated lines and notification floods.
internal sealed class McpTrafficBudget(int maxBytes, int maxFrames)
{
    private long _bytes;
    private long _frames;
    public void Charge(int bytes, int frames)
    {
        if (Interlocked.Add(ref _bytes, bytes) > maxBytes || Interlocked.Add(ref _frames, frames) > maxFrames)
            throw new IOException("The connector exceeded its reviewed session traffic limits.");
    }
}

internal sealed class BoundedMcpStream(Stream inner, int maxFrameBytes, McpTrafficBudget budget) : Stream
{
    private int _lineBytes;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private void Check(ReadOnlySpan<byte> bytes)
    {
        var frames = 0;
        foreach (var value in bytes)
        {
            if (++_lineBytes > maxFrameBytes) throw new IOException("The connector exceeded its reviewed frame limit.");
            if (value == (byte)'\n') { _lineBytes = 0; frames++; }
        }
        budget.Charge(bytes.Length, frames);
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Math.Min(count, 8192));
        Check(buffer.AsSpan(offset, read));
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken).ConfigureAwait(false);
        Check(buffer.Span[..read]);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
