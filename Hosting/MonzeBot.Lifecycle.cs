using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await DrainPendingMessageGapsAsync(cancellationToken);
        _messageGapIngress.Writer.TryComplete();
        if (_messages is not null)
        {
            await _messages.FlushAsync(cancellationToken);
            await _messages.DisposeAsync();
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

