using System.Text.Json;
using System.Text.Json.Nodes;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using Monze.Application;

namespace Monze;

public sealed partial class MonzeBot
{
    private const int ReconcileBatch = 20;
    private const int ReconcileHistory = 100;

    /// <summary>
    /// Settles outbox rows whose delivery is uncertain. The channel's latest
    /// messages are searched for a bot message with exactly the content the
    /// row was sent with, not yet recorded by another row and created no
    /// earlier than a minute before the row's due time (an expired lease keeps
    /// the due time its send started after; a lost ack is due 30 s after the
    /// send failed, and the SDK gives up on an ack after 7 s). Found: the row
    /// is completed with that message id. Absent: the message never reached
    /// the channel and the row is resent. A history read that fails leaves
    /// the row leased; it is tried again once the lease expires.
    /// </summary>
    private async Task ReconcileUncertainOutboxAsync(MezonClient client, CancellationToken cancellationToken)
    {
        var rows = await _outbox.ClaimUncertainOutboxAsync(ReconcileBatch, cancellationToken);
        foreach (var row in rows)
        {
            var item = row.Item;
            var expected = Canonical(BuildOutboxContent(item).ToJson());
            var notBefore = item.DueAt.AddMinutes(-1).ToUnixTimeSeconds();
            var candidates = new List<long>();
            try
            {
                var history = await client.ListChannelMessagesAsync(row.ClanId, item.ChannelId, limit: ReconcileHistory);
                for (var i = 0; i < history.Messages.Count; i++)
                {
                    var message = history.Messages[i];
                    if (message.SenderId == client.BotId
                        && message.MessageId > 0
                        && (message.CreateTimeSeconds <= 0 || message.CreateTimeSeconds >= notBefore)
                        && Canonical(message.Content) == expected)
                    {
                        candidates.Add(message.MessageId);
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Outbox {OutboxId} reconciliation could not read the channel; retrying later.", item.Id);
                continue;
            }

            var recorded = await _outbox.FindRecordedMessagesAsync(candidates, cancellationToken);
            var found = candidates.Where(id => !recorded.Contains(id)).Order().Cast<long?>().FirstOrDefault();
            if (found is { } messageId)
            {
                _logger.LogInformation("Outbox {OutboxId} was delivered before its ack was lost; recorded without resending.", item.Id);
                MonzeMetrics.OutboxDelivered.Add(1);
                await TryCompleteOutboxAsync(item.Id, item.LeaseToken, messageId, false, cancellationToken, null);
            }
            else
            {
                _logger.LogInformation("Outbox {OutboxId} is not on its channel; it will be resent.", item.Id);
                await _outbox.RequeueUncertainOutboxAsync(item.Id, item.LeaseToken, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The content in a comparable form: parsed and re-serialized the way the
    /// SDK does, without embed colours (informational cards pick a random
    /// colour each time they are built).
    /// </summary>
    private static string Canonical(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(Mezon.Net.Client.MessageContent.Parse(contentJson).ToJson());
            RemoveColors(node);
            return node?.ToJsonString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return contentJson;
        }
    }

    private static void RemoveColors(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                value.Remove("color");
                foreach (var (_, child) in value.ToList())
                {
                    RemoveColors(child);
                }

                break;
            case JsonArray items:
                foreach (var child in items)
                {
                    RemoveColors(child);
                }

                break;
        }
    }
}
