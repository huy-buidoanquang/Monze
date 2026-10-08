using System.Text.Json;
using System.Threading.Channels;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Agent;
using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private Task EnqueueMessageAsync(ChannelMessageEventData message)
    {
        var response = (ChannelMessageResponse)message;
        if (response.Code is 12 or 14 or 15)
        {
            _logger.LogDebug(
                "Ephemeral message event received. Clan={ClanId}, Channel={ChannelId}, Message={MessageId}, Code={Code}, Sender={SenderId}.",
                response.ClanId,
                response.ChannelId,
                response.MessageId,
                response.Code,
                response.SenderId);
        }

        if (_messageIngress.TryWrite(response.ClanId, message))
        {
            Interlocked.Increment(ref _messageIngressDepth);
        }
        else
        {
            Interlocked.Increment(ref _droppedMessages);
            MonzeMetrics.MessageIngressDropped.Add(1);
            TryQueueMessageGap(response.ClanId, response.ChannelId, response.MessageId);
        }

        return Task.CompletedTask;
    }
    private async Task ConsumeMessagesAsync(CancellationToken cancellationToken)
    {
        var workers = new Task[_messageIngress.PartitionCount];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = ConsumeMessagePartitionAsync(
                _messageIngress.GetReader(i),
                cancellationToken);
        }

        await Task.WhenAll(workers);
    }

    private async Task ConsumeMessageGapsAsync(CancellationToken cancellationToken)
    {
        var pending = new MessageGapBatch(256);
        var reader = _messageGapIngress.Reader;
        while (!_overflowedGaps.IsEmpty || await reader.WaitToReadAsync(cancellationToken))
        {
            pending.Clear();
            while (pending.Count < 256 && reader.TryRead(out var item))
            {
                Interlocked.Decrement(ref _messageGapIngressDepth);
                pending.Add(item.ClanId, item.ChannelId, item.MessageId);
            }

            TakeOverflowedGaps(pending, 256);

            try
            {
                foreach (var item in pending.Entries)
                {
                    await MarkMessageGapAsync(item.Key, item.Value, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (var item in pending.Entries)
                {
                    TryQueueMessageGap(item.Key.ClanId, item.Key.ChannelId, item.Value);
                }

                return;
            }
        }
    }

    private void TryQueueMessageGap(long clanId, long channelId, long messageId)
    {
        if (clanId <= 0 || channelId <= 0 || messageId <= 0)
        {
            return;
        }

        if (_messageGapIngress.Writer.TryWrite(new MessageGapIngressItem(
                clanId,
                channelId,
                messageId)))
        {
            Interlocked.Increment(ref _messageGapIngressDepth);
            return;
        }

        // The gap queue is full: keep one marker per channel aside instead of
        // dropping it, so the channel's has_gap is still set once the queue
        // drains (WF-07). Only beyond MessageGapOverflowLimit channels is a
        // marker dropped.
        var key = new ChannelPolicyKey(clanId, channelId);
        if (_overflowedGaps.Count < MessageGapOverflowLimit || _overflowedGaps.ContainsKey(key))
        {
            _overflowedGaps.AddOrUpdate(key, static (_, latest) => latest, static (_, current, latest) => Math.Max(current, latest), messageId);
            return;
        }

        Interlocked.Increment(ref _droppedMessageGaps);
        MonzeMetrics.MessageGapIngressDropped.Add(1);
    }

    private void TakeOverflowedGaps(MessageGapBatch batch, int capacity)
    {
        foreach (var key in _overflowedGaps.Keys)
        {
            if (batch.Count >= capacity)
            {
                return;
            }

            if (_overflowedGaps.TryRemove(key, out var messageId))
            {
                batch.Add(key.ClanId, key.ChannelId, messageId);
            }
        }
    }

    private async Task MarkMessageGapAsync(
        ChannelPolicyKey key,
        long messageId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _messageHistory.MarkChannelGapAsync(
                    key.ClanId,
                    key.ChannelId,
                    messageId,
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogDebug(
                    ex,
                    "Message-gap persistence attempt {Attempt} failed for clan {ClanId} and channel {ChannelId}.",
                    attempt,
                    key.ClanId,
                    key.ChannelId);
                await Task.Delay(_timings.MessageGapRetryBase * attempt, _time, cancellationToken);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _droppedMessageGaps);
                MonzeMetrics.MessageGapIngressDropped.Add(1);
                _logger.LogWarning(
                    ex,
                    "Message-gap persistence exhausted retries for clan {ClanId} and channel {ChannelId}.",
                    key.ClanId,
                    key.ChannelId);
            }
        }
    }

    private async Task ConsumeMessagePartitionAsync(
        ChannelReader<ChannelMessageEventData> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var evt in reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _messageIngressDepth);
            try
            {
                await PersistMessageAsync(evt, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var message = (ChannelMessageResponse)evt;
                TryQueueMessageGap(message.ClanId, message.ChannelId, message.MessageId);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Message persistence worker failed.");
                var message = (ChannelMessageResponse)evt;
                TryQueueMessageGap(message.ClanId, message.ChannelId, message.MessageId);
            }
        }
    }

    private bool IsCommandText(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
        {
            return false;
        }

        try
        {
            var text = Mezon.Net.Client.MessageContent.Parse(contentJson).Text;
            return text is not null && text.TrimStart().StartsWith(_commandOptions.Prefix, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task DrainPendingMessageGapsAsync(CancellationToken cancellationToken)
    {
        var pending = new MessageGapBatch(256);
        for (var partition = 0; partition < _messageIngress.PartitionCount; partition++)
        {
            var reader = _messageIngress.GetReader(partition);
            while (reader.TryRead(out var evt))
            {
                Interlocked.Decrement(ref _messageIngressDepth);
                var message = (ChannelMessageResponse)evt;
                pending.Add(message.ClanId, message.ChannelId, message.MessageId);
            }
        }

        while (_messageGapIngress.Reader.TryRead(out var gap))
        {
            Interlocked.Decrement(ref _messageGapIngressDepth);
            pending.Add(gap.ClanId, gap.ChannelId, gap.MessageId);
        }

        TakeOverflowedGaps(pending, int.MaxValue);

        foreach (var item in pending.Entries)
        {
            await MarkMessageGapAsync(item.Key, item.Value, cancellationToken);
        }
    }
    private async Task PersistMessageAsync(
        ChannelMessageEventData evt,
        CancellationToken cancellationToken)
    {
        var message = (ChannelMessageResponse)evt;
        if (message.ClanId == 0 && IsCommandText(message.Content))
        {
            // The SDK drops commands outside a clan before any handler runs (CAND-26).
            _logger.LogInformation("A Monze command sent in a direct message was ignored; commands work in clan channels.");
        }

        if (_messages is null || message.ClanId == 0 || message.ChannelId == 0)
        {
            return;
        }

        if (message.SenderId > 0)
        {
            var profileKey = $"monze:profile-sync:{message.ClanId}:{message.SenderId}";
            if (!_policyCache.TryGetValue(profileKey, out UserProfileSyncState? state)
                || state is null
                || !state.Matches(
                    message.ClanNick,
                    message.DisplayName,
                    message.Username,
                    message.Avatar))
            {
                await _userProfiles.UpsertAsync(
                    message.ClanId,
                    message.SenderId,
                    message.ClanNick,
                    message.DisplayName,
                    message.Username,
                    message.Avatar,
                    cancellationToken);
                _policyCache.Set(
                    profileKey,
                    new UserProfileSyncState(
                        message.ClanNick,
                        message.DisplayName,
                        message.Username,
                        message.Avatar),
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                        Size = 1
                    });
            }
        }

        var key = new ChannelPolicyKey(message.ClanId, message.ChannelId);
        if (!_policyCache.TryGetValue(key, out bool persists))
        {
            persists = await _messageHistory.ChannelPersistsAsync(
                message.ClanId,
                message.ChannelId,
                cancellationToken);
            _policyCache.Set(
                key,
                persists,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
                    Size = 1
                });
        }

        if (!persists)
        {
            return;
        }

        await _messages.UpsertMessageAsync(
            new MessageSnapshot
            {
                MessageId = message.MessageId,
                ChannelId = message.ChannelId,
                ClanId = message.ClanId,
                SenderId = message.SenderId,
                Content = message.Content ?? string.Empty,
                CreateTimeSeconds = message.CreateTimeSeconds
            },
            message.MessageId,
            cancellationToken);
    }
}
