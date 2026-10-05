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
    private Task EnqueueWelcomeAsync(AddClanUserEventEventData evt)
    {
        var added = (AddClanUserEventResponse)evt;
        var item = new WelcomeIngressItem(
            added.ClanId,
            added.User.UserId,
            added.User.IsBot,
            added.User.Username,
            added.User.DisplayName);
        _logger.LogInformation(
            "Welcome join event received and queued. IsBot={IsBot}.",
            item.IsBot);
        if (_welcomeIngress.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _welcomeIngressDepth);
            return Task.CompletedTask;
        }

        MonzeMetrics.WelcomeIngressBackpressure.Add(1);
        return WriteWelcomeAsync(item);
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
                    _logger.LogDebug(
                        "Welcome event ignored. IsBot={IsBot}, HasClan={HasClan}, HasUser={HasUser}.",
                        item.IsBot,
                        item.ClanId != 0,
                        item.UserId != 0);
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
                _logger.LogInformation(
                    "Welcome processing started. Enabled={Enabled} HasText={HasText} HasEmbed={HasEmbed}.",
                    settings?.Enabled == true,
                    !string.IsNullOrWhiteSpace(settings?.Text),
                    settings?.Embed is not null);
                if (settings is not { Enabled: true })
                {
                    _logger.LogInformation("Welcome event ignored because welcome is disabled or unconfigured.");
                    continue;
                }

                var channel = await ResolveWelcomeChannelAsync(
                    client,
                    item.ClanId,
                    cancellationToken);
                if (channel is null)
                {
                    _logger.LogWarning(
                        "Welcome event skipped because no public text channel is available for the clan.");
                    continue;
                }

                _logger.LogInformation("Welcome target channel resolved.");
                if (!await _welcome.TryClaimWelcomeAsync(
                        item.ClanId,
                        item.UserId,
                        cancellationToken))
                {
                    _logger.LogInformation("Welcome event ignored because delivery was already claimed.");
                    continue;
                }

                try
                {
                    var targets = await ResolveWelcomeTargetsAsync(
                        client,
                        item,
                        settings,
                        cancellationToken);
                    var rendered = WelcomeMessageRenderer.Render(
                        settings,
                        item.UserId,
                        targets.NewUserLabel,
                        targets.Users,
                        targets.Roles,
                        targets.Channels);
                    await channel.SendAsync(
                        rendered.Content,
                        mentions: rendered.Mentions);
                    _logger.LogInformation(
                        "Welcome message sent to the resolved public text channel.");
                }
                catch
                {
                    // A transient send failure must not permanently suppress the
                    // welcome for this member. The next event/retry can claim it.
                    await _welcome.ReleaseWelcomeClaimAsync(
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

    private async Task<Mezon.Net.Sdk.Entities.Channel?> ResolveWelcomeChannelAsync(
        MezonClient client,
        long clanId,
        CancellationToken cancellationToken)
    {
        if (_welcomeChannelIds.TryGetValue(clanId, out var configuredChannelId)
            && configuredChannelId > 0)
        {
            try
            {
                return await client.GetChannelAsync(configuredChannelId, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _welcomeChannelIds.TryRemove(clanId, out _);
            }
        }

        Mezon.Net.Sdk.Entities.Clan clan;
        if (!client.Clans.TryGet(clanId, out clan!))
        {
            clan = await client.GetClanAsync(clanId, cancellationToken);
        }

        if (clan.WelcomeChannelId > 0)
        {
            _welcomeChannelIds[clanId] = clan.WelcomeChannelId;
            try
            {
                return await client.GetChannelAsync(clan.WelcomeChannelId, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _welcomeChannelIds.TryRemove(clanId, out _);
            }
        }

        var channels = await clan.LoadChannelsAsync(
            options: new RequestOptions { SocketSendTimeout = 5_000 });
        var fallback = SelectWelcomeFallbackChannel(channels);
        if (fallback.ChannelId == 0)
        {
            return null;
        }

        _welcomeChannelIds[clanId] = fallback.ChannelId;
        return await client.GetChannelAsync(fallback.ChannelId, cancellationToken);
    }

    private static ChannelDescriptionResponse SelectWelcomeFallbackChannel(
        ChannelDescListResponse channels)
    {
        ChannelDescriptionResponse firstPublicText = default;
        for (var i = 0; i < channels.Channeldesc.Count; i++)
        {
            var candidate = channels.Channeldesc[i];
            if (candidate.ChannelId == 0
                || candidate.Type != (int)ChannelType.Channel
                || candidate.ChannelPrivate != 0)
            {
                continue;
            }

            if (candidate.ChannelLabel.Equals(
                    "general",
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            if (firstPublicText.ChannelId == 0)
            {
                firstPublicText = candidate;
            }
        }

        return firstPublicText;
    }

    private static async Task<(
        IReadOnlyDictionary<string, (long Id, string Label)> Users,
        IReadOnlyDictionary<string, (long Id, string Label)> Roles,
        IReadOnlyDictionary<string, (long Id, string Label)> Channels,
        string NewUserLabel)> ResolveWelcomeTargetsAsync(
        MezonClient client,
        WelcomeIngressItem item,
        WelcomeSettings settings,
        CancellationToken cancellationToken)
    {
        var users = new Dictionary<string, (long Id, string Label)>(StringComparer.OrdinalIgnoreCase);
        var roles = new Dictionary<string, (long Id, string Label)>(StringComparer.OrdinalIgnoreCase);
        var channels = new Dictionary<string, (long Id, string Label)>(StringComparer.OrdinalIgnoreCase);
        var newUserLabel = FirstWelcomeLabel(item.DisplayName, item.Username);
        if (!WelcomeMessageRenderer.RequiresLookup(settings))
        {
            return (users, roles, channels, newUserLabel);
        }

        try
        {
            var memberList = await client.ListClanUsersAsync(
                item.ClanId,
                new RequestOptions { SocketSendTimeout = 5_000 });
            for (var i = 0; i < memberList.ClanUsers.Count; i++)
            {
                var member = memberList.ClanUsers[i];
                var label = FirstWelcomeLabel(
                    member.ClanNick,
                    member.User.DisplayName,
                    member.User.Username);
                if (member.User.Id == item.UserId)
                {
                    newUserLabel = label;
                }

                AddWelcomeTarget(users, member.User.Id, label);
                AddWelcomeTarget(users, member.User.Id, member.User.Username);
                AddWelcomeTarget(users, member.User.Id, member.User.DisplayName);
                AddWelcomeTarget(users, member.User.Id, member.ClanNick);
            }

            if (client.Clans.TryGet(item.ClanId, out var clan))
            {
                var roleList = await clan.ListRolesAsync(
                    limit: 1000,
                    options: new RequestOptions { SocketSendTimeout = 5_000 });
                for (var i = 0; i < roleList.Roles.Roles.Count; i++)
                {
                    var role = roleList.Roles.Roles[i];
                    if (role.Id != 0 && role.Active != 0)
                    {
                        AddWelcomeTarget(roles, role.Id, role.Title);
                    }
                }

                var channelList = await clan.LoadChannelsAsync(
                    options: new RequestOptions { SocketSendTimeout = 5_000 });
                for (var i = 0; i < channelList.Channeldesc.Count; i++)
                {
                    var channel = channelList.Channeldesc[i];
                    AddWelcomeTarget(channels, channel.ChannelId, channel.ChannelLabel);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Target resolution is optional. The welcome still goes out with the
            // configured text when a directory lookup is temporarily unavailable.
        }

        AddWelcomeTarget(users, item.UserId, newUserLabel);
        return (users, roles, channels, newUserLabel);
    }

    private static void AddWelcomeTarget(
        Dictionary<string, (long Id, string Label)> targets,
        long id,
        string? label)
    {
        if (id <= 0 || string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var normalized = label.Trim().TrimStart('@', '#');
        if (normalized.Length > 0)
        {
            targets.TryAdd(normalized, (id, normalized));
        }
    }

    private static string FirstWelcomeLabel(params string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                return values[i]!.Trim().TrimStart('@', '#');
            }
        }

        return MonzeMessages.MemberFallbackLabel;
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
                // An empty payload is the versioned invalidation tombstone. It
                // must force an L3 read instead of being treated as a durable
                // "no settings" result; otherwise a just-saved welcome can be
                // invisible to the next join event until the tombstone expires.
                if (entry.Payload.Length > 0)
                {
                    var settings = JsonSerializer.Deserialize<WelcomeSettings>(entry.Payload);
                    if (settings is not null && settings.Version == entry.Version)
                    {
                        return settings;
                    }
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Redis is an optimization. Continue with PostgreSQL on cache errors.
        }

        var loaded = await _welcome.GetWelcomeAsync(clanId, cancellationToken);
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
