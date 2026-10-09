using Microsoft.Extensions.Logging;

namespace Monze;

/// <summary>
/// The body of one Agent SSE response, read by the SDK's AgentSseManager.
/// It watches the bytes as they pass: comment lines (keepalives) and the
/// <c>id:</c> of every dispatched event. Once the server has proven it sends
/// keepalives (two seen), a stream silent for longer than
/// <see cref="AgentSseResumeState.IdleTimeout"/> and three times the latest
/// keepalive gap is ended as if the server had closed it, so the SDK reconnects; the
/// reconnect carries the last event id (<see cref="AgentSseResumeHandler"/>).
/// A server that never sends keepalives is never timed out.
/// </summary>
internal sealed class AgentSseIdleStream(
    Stream inner,
    AgentSseResumeState state,
    TimeProvider time,
    ILogger logger,
    IDisposable? owner = null) : Stream
{
    private const int MaxFieldLine = 128;

    private readonly byte[] _line = new byte[MaxFieldLine];
    private int _lineLength;
    private bool _lineOverflow;
    private string? _pendingId;
    private int _keepAlives;
    private long _lastKeepAlive;
    private long _lastKeepAliveGap;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>How long the stream may be silent now; null until two keepalives were seen.</summary>
    internal TimeSpan? IdleTimeout
        => _keepAlives < 2
            ? null
            : TimeSpan.FromTicks(Math.Max(state.IdleTimeout.Ticks, 3 * TicksOf(_lastKeepAliveGap)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        if (IdleTimeout is not { } idle)
        {
            read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var silence = new CancellationTokenSource(idle, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, silence.Token);
            try
            {
                read = await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (silence.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Agent event stream silent for {Seconds:0} s after keepalives; reconnecting.",
                    idle.TotalSeconds);
                return 0;
            }
        }

        Scan(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Scan(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != (byte)'\n')
            {
                if (_lineLength < MaxFieldLine)
                {
                    _line[_lineLength++] = value;
                }
                else
                {
                    _lineOverflow = true;
                }

                continue;
            }

            var line = _line.AsSpan(0, _lineLength);
            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.IsEmpty)
            {
                // A blank line dispatches the event; only then is its id the last one seen.
                if (_pendingId is not null)
                {
                    state.LastEventId = _pendingId;
                    _pendingId = null;
                }
            }
            else if (line[0] == (byte)':')
            {
                KeepAlive();
            }
            else if (!_lineOverflow && line.StartsWith("id:"u8))
            {
                var id = line[3..];
                if (!id.IsEmpty && id[0] == (byte)' ')
                {
                    id = id[1..];
                }

                _pendingId = id.IsEmpty ? null : System.Text.Encoding.UTF8.GetString(id);
            }

            _lineLength = 0;
            _lineOverflow = false;
        }
    }

    private void KeepAlive()
    {
        var now = time.GetTimestamp();
        if (_keepAlives > 0)
        {
            _lastKeepAliveGap = now - _lastKeepAlive;
        }

        _lastKeepAlive = now;
        _keepAlives++;
    }

    private long TicksOf(long timestampDelta)
        => (long)(timestampDelta * ((double)TimeSpan.TicksPerSecond / time.TimestampFrequency));
}
