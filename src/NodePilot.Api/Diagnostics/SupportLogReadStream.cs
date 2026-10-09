namespace NodePilot.Api.Diagnostics;

/// <summary>Reads ordered log segments with at most one open file and no content buffering.</summary>
internal sealed class SupportLogReadStream(IReadOnlyList<string> paths) : Stream
{
    private FileStream? _current;
    private int _next;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private bool OpenNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null) return true;
        if (_next >= paths.Count) return false;
        _current = new FileStream(paths[_next++], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _ = buffer.AsSpan(offset, count);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0) return 0;
        while (OpenNext())
        {
            var read = _current!.Read(buffer, offset, count);
            if (read > 0) return read;
            _current.Dispose();
            _current = null;
        }
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0) return 0;
        while (OpenNext())
        {
            var read = await _current!.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            await _current.DisposeAsync();
            _current = null;
        }
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        _current = null;
        _disposed = true;
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
