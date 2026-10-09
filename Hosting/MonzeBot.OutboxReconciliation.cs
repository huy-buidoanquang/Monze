using System.Text.Json;
using System.Text.Json.Nodes;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using Monze.Application;

namespace Monze;

public sealed partial class MonzeBot
{
    private const int ReconcileBatch = 20;
    private const int ReconcileHistory = 100;
    private const int ReconcilePages = 5;

    /// <summary>
    /// Settles outbox rows whose delivery is uncertain. The channel's latest
    /// messages are searched for a bot message with exactly the content the
    /// row was sent with, not yet recorded by another row and created no
    /// earlier than a minute before the row's due time (an expired lease keeps
    /// the due time its send started after; a lost ack is due 30 s after the
    /// send failed, and the SDK gives up on an ack after 7 s). The history is
    /// paged back to that time, at most <see cref="ReconcilePages"/> pages of
    /// <see cref="ReconcileHistory"/> per channel and pass, so a message a busy
    /// channel has pushed past its latest 100 is still found (DEF-07). Found:
    /// the row is completed with that message id. Absent: the message never
    /// reached the channel and the row is resent. A history read that fails
    /// leaves the row leased; it is tried again once the lease expires.
    /// </summary>
    private async Task ReconcileUncertainOutboxAsync(MezonClient client, CancellationToken cancellationToken)
    {
        var rows = await _outbox.ClaimUncertainOutboxAsync(ReconcileBatch, cancellationToken);
        var histories = new Dictionary<long, ChannelHistory>();
        foreach (var row in rows)
        {
            var item = row.Item;
            var expected = Canonical(BuildOutboxContent(item).ToJson());
            var notBefore = item.DueAt.AddMinutes(-1).ToUnixTimeSeconds();
            var candidates = new List<long>();
            try
            {
                if (!histories.TryGetValue(item.ChannelId, out var history))
                {
                    histories[item.ChannelId] = history = new ChannelHistory(row.ClanId, item.ChannelId);
                }

                await history.ReachAsync(client, notBefore);
                foreach (var message in history.Messages)
                {
                    if (message.SenderId == client.BotId
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
                await TryCompleteOutboxAsync(item.Id, item.LeaseToken, messageId, false, null);
            }
            else
            {
                _logger.LogInformation("Outbox {OutboxId} is not on its channel; it will be resent.", item.Id);
                await _outbox.RequeueUncertainOutboxAsync(item.Id, item.LeaseToken, cancellationToken);
            }
        }
    }

    /// <summary>
    /// A channel's latest messages, newest first, paged back on demand as far
    /// as the rows of one reconciliation pass need.
    /// </summary>
    private sealed class ChannelHistory(long clanId, long channelId)
    {
        private readonly HashSet<long> _seen = [];
        private int _pages;
        private bool _complete;

        public List<ChannelMessageResponse> Messages { get; } = [];

        /// <summary>Pages back until a message older than <paramref name="notBefore"/> (Unix seconds), the channel's start or the page cap.</summary>
        public async Task ReachAsync(MezonClient client, long notBefore)
        {
            while (!_complete
                && _pages < ReconcilePages
                && !Messages.Exists(message => message.CreateTimeSeconds > 0 && message.CreateTimeSeconds < notBefore))
            {
                // Direction 3 lists the anchor's older messages (mezon-api BEFORE_TIMESTAMP).
                long? anchor = Messages.Count == 0 ? null : Messages[^1].MessageId;
                var page = await client.ListChannelMessagesAsync(clanId, channelId, anchor, anchor is null ? null : 3, ReconcileHistory);
                _pages++;
                var added = 0;
                foreach (var message in page.Messages.OrderByDescending(static message => message.MessageId))
                {
                    if (message.MessageId > 0 && (anchor is null || message.MessageId < anchor) && _seen.Add(message.MessageId))
                    {
                        Messages.Add(message);
                        added++;
                    }
                }

                _complete = page.Messages.Count < ReconcileHistory || added == 0;
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
