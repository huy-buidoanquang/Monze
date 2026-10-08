using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    private static readonly TimeSpan MessageStoreCloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Flushes and closes the SDK message history store, each step bounded by
    /// <paramref name="timeout"/>. SDK 1.6.2 (CAND-18): after one failed write
    /// batch the store's write pump stops, FlushAsync never completes and
    /// DisposeAsync waits for it, so an unbounded close would hang shutdown.
    /// The store is left open when it cannot flush; the process is ending.
    /// </summary>
    internal static async Task CloseMessageStoreAsync(SqliteMessageStore store, TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await store.FlushAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            await store.DisposeAsync().AsTask().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogError(
                "Message history did not flush and close within {Seconds:0} s; writes still queued are lost.",
                timeout.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Shutdown ended before message history was flushed.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DrainPendingMessageGapsAsync(cancellationToken);
        _messageGapIngress.Writer.TryComplete();
        if (_messages is not null)
        {
            await CloseMessageStoreAsync(_messages, MessageStoreCloseTimeout, _logger, cancellationToken);
        }

        var droppedMessages = Interlocked.Read(ref _droppedMessages);
        if (droppedMessages != 0)
        {
            _logger.LogWarning(
                "Monze ingress dropped {Messages} best-effort messages.",
                droppedMessages);
        }

        var droppedMessageGaps = Interlocked.Read(ref _droppedMessageGaps);
        if (droppedMessageGaps != 0)
        {
            _logger.LogWarning(
                "Monze could not persist {MessageGaps} message-history gap markers.",
                droppedMessageGaps);
        }
    }
}

