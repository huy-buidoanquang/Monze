using System.Text.Json;
using System.Threading.Channels;
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
        if (_messageIngress.TryWrite(response.ClanId, message))
        {
            Interlocked.Increment(ref _messageIngressDepth);
        }
        else
        {
            Interlocked.Increment(ref _droppedMessages);
            MonzeMetrics.MessageIngressDropped.Add(1);
        }

        return Task.CompletedTask;
    }

    private Task EnqueueAgentAsync(AgentSseSessionEvent evt, AgentEventKind kind)
    {
        var item = new AgentIngressItem(evt, kind);
        if (_agentIngress.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _agentIngressDepth);
            return Task.CompletedTask;
        }

        MonzeMetrics.AgentIngressBackpressure.Add(1);
        return WriteAgentAsync(item);
    }

    private Task EnqueueWelcomeAsync(AddClanUserEventEventData evt)
    {
        var added = (AddClanUserEventResponse)evt;
        var item = new WelcomeIngressItem(
            added.ClanId,
            added.User.UserId,
            added.User.IsBot);
        if (_welcomeIngress.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _welcomeIngressDepth);
            return Task.CompletedTask;
        }

        MonzeMetrics.WelcomeIngressBackpressure.Add(1);
        return WriteWelcomeAsync(item);
    }

    private async Task WriteAgentAsync(AgentIngressItem item)
    {
        try
        {
            await _agentIngress.Writer.WriteAsync(item);
            Interlocked.Increment(ref _agentIngressDepth);
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task WriteWelcomeAsync(WelcomeIngressItem item)
    {
        try
        {
            await _welcomeIngress.Writer.WriteAsync(item);
            Interlocked.Increment(ref _welcomeIngressDepth);
        }
        catch (ChannelClosedException)
        {
        }
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
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Message persistence worker failed.");
            }
        }
    }

    private async Task ConsumeAgentEventsAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in _agentIngress.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _agentIngressDepth);
            try
            {
                await ProcessAgentAsync(item.Event, item.Kind, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent event worker failed.");
            }
        }
    }

    private async Task ConsumeWelcomeAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        await foreach (var item in _welcomeIngress.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _welcomeIngressDepth);
            try
            {
                if (item.IsBot || item.ClanId == 0 || item.UserId == 0)
                {
                    continue;
                }

                try
                {
                    await _app.ApplyOnJoinRoleRulesAsync(
                        item.ClanId,
                        item.UserId,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Automatic role-on-join processing failed for clan {ClanId}.", item.ClanId);
                }

                var settings = await GetWelcomeAsync(item.ClanId, cancellationToken);
                if (settings is not { Enabled: true })
                {
                    continue;
                }

                if (!client.Clans.TryGet(item.ClanId, out var clan))
                {
                    clan = await client.GetClanAsync(item.ClanId, cancellationToken);
                }

                if (clan.WelcomeChannelId == 0)
                {
                    continue;
                }

                var channel = await client.GetChannelAsync(
                    clan.WelcomeChannelId,
                    cancellationToken);
                if (!await _authorization.TryClaimWelcomeAsync(
                        item.ClanId,
                        item.UserId,
                        cancellationToken))
                {
                    continue;
                }

                try
                {
                    var text = settings.Text ?? MonzeMessages.DefaultWelcomeText;
                    await channel.SendAsync(MonzeMessageBuilder.WelcomeCard(
                        settings with { Text = text }));
                }
                catch
                {
                    // A transient send failure must not permanently suppress the
                    // welcome for this member. The next event/retry can claim it.
                    await _authorization.ReleaseWelcomeClaimAsync(
                        item.ClanId,
                        item.UserId,
                        cancellationToken);
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Welcome worker failed.");
            }
        }
    }
    private async Task PersistMessageAsync(
        ChannelMessageEventData evt,
        CancellationToken cancellationToken)
    {
        if (_messages is null)
        {
            return;
        }

        var message = (ChannelMessageResponse)evt;
        if (message.ClanId == 0 || message.ChannelId == 0)
        {
            return;
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

    private async Task<WelcomeSettings?> GetWelcomeAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _readModelCache.GetAsync(
                clanId,
                "welcome",
                "settings",
                cancellationToken);
            if (cached is { } entry)
            {
                if (entry.Payload.Length == 0)
                {
                    return null;
                }

                var settings = JsonSerializer.Deserialize<WelcomeSettings>(entry.Payload);
                if (settings is not null && settings.Version == entry.Version)
                {
                    return settings;
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Redis is an optimization. Continue with PostgreSQL on cache errors.
        }

        var loaded = await _authorization.GetWelcomeAsync(clanId, cancellationToken);
        try
        {
            await _readModelCache.SetAsync(
                clanId,
                "welcome",
                "settings",
                new ReadModelCacheEntry(
                    loaded?.Version ?? 0,
                    loaded is null ? string.Empty : JsonSerializer.Serialize(loaded)),
                TimeSpan.FromMinutes(5),
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Redis is an optimization. Continue after a cache write failure.
        }

        return loaded;
    }
}

