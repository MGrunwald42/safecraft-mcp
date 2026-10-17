namespace SafeCraftMcp.ChildRpc;

/// <summary>
/// A read-only wrapper around the app's stdout that fails once a single line (one JSON-RPC
/// message) grows past a limit. The MCP SDK reads a whole line before it parses it, so without
/// this an app writing gigabytes without a newline would fill the gateway's memory long before
/// <c>--max-response-mb</c> could look at the result. On overflow it calls
/// <paramref name="onOverflow"/> (which kills the app) and reports end of stream.
/// </summary>
public sealed class LineLimitStream(Stream inner, long maxLineBytes, Action onOverflow) : Stream
{
    private long _lineBytes;
    private bool _overflowed;

    public bool Overflowed => _overflowed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

    public override int Read(Span<byte> buffer) => Count(buffer[..inner.Read(buffer)]);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        return Count(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(Span<byte> read)
    {
        if (_overflowed)
            return 0;
        var rest = (ReadOnlySpan<byte>)read;
        while (true)
        {
            var newline = rest.IndexOf((byte)'\n');
            _lineBytes += newline < 0 ? rest.Length : newline;
            if (_lineBytes > maxLineBytes)
            {
                _overflowed = true;
                onOverflow();
                return 0;
            }
            if (newline < 0)
                return read.Length;
            _lineBytes = 0;
            rest = rest[(newline + 1)..];
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
