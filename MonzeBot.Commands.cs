using Microsoft.Extensions.Logging;
using Mezon.Net.Core;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Microsoft.Extensions.Caching.Memory;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Domain;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private const long MaxPolicyCacheEntryBytes = 64 * 1024;

    private Task HandleMonzeAsync(ICommandContext context)
        => ExecuteCommandOnceAsync(
            context,
            () => HandleMonzeCoreAsync(
                context,
                new CommandArguments(context.Args),
                ResolveMonzeCommand(context)));

    private Task HandleDirectMonzeAsync(ICommandContext context, string module)
        => ExecuteCommandOnceAsync(
            context,
            () => HandleMonzeCoreAsync(
                context,
                new CommandArguments(module, context.Args),
                module));

    private Task HandleDirectHelpAsync(ICommandContext context)
        => ExecuteCommandOnceAsync(
            context,
            () => HandleMonzeCoreAsync(
                context,
                new CommandArguments(MonzeCommandNames.Help, context.Args),
                MonzeCommandNames.Help));

    private async Task HandleMonzeCoreAsync(
        ICommandContext context,
        CommandArguments args,
        string commandKey)
    {
        var clanId = context.Clan?.Id ?? 0;
        if (clanId == 0)
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.UnknownClan,
                MonzeTone.Error));
            return;
        }

        if (!_commandRateLimiter.TryAcquire(
                clanId,
                context.Author.Id,
                commandKey,
                DateTimeOffset.UtcNow,
                out var retryAfter))
        {
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleRateLimited,
                MonzeMessages.RateLimited(retryAfter),
                MonzeTone.Warn));
            return;
        }

        try
        {
            var userLabelResolver = await CreateUserLabelResolverAsync(
                context,
                clanId,
                args,
                context.CancellationToken);
            var outcome = await _app.HandleMonzeAsync(
                clanId,
                context.Channel.Id,
                context.Author.Id,
                context.Message.Id,
                args,
                context.CancellationToken,
                userLabelResolver,
                TryGetSingleMentionedUserId(context));
            await context.ReplyAsync(MonzeMessageBuilder.Card(outcome, _commandOptions));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monze command failed for clan {ClanId} and channel {ChannelId}.", clanId, context.Channel.Id);
            await context.ReplyAsync(MonzeMessageBuilder.Card(
                MonzeMessages.TitleMonze,
                MonzeMessages.TemporaryFailure,
                MonzeTone.Error));
        }
    }

    private string ResolveMonzeCommand(ICommandContext context)
    {
        if (!_commandOptions.HasRoot && !string.IsNullOrWhiteSpace(context.Name))
        {
            return context.Name;
        }

        return context.Args.Count == 0 ? MonzeCommandNames.Help : context.Args[0];
    }

    private async Task<Func<long, CancellationToken, Task<string>>?> CreateUserLabelResolverAsync(
        ICommandContext context,
        long clanId,
        CommandArguments args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0
            || !args[0].Equals(MonzeCommandNames.Leaderboard, StringComparison.OrdinalIgnoreCase)
            || (args.Count > 1 && args[1].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var cacheKey = (Kind: "leaderboard-labels", ClanId: clanId);
        IReadOnlyDictionary<long, string> labels;
        if (_policyCache.TryGetValue(cacheKey, out var cached)
            && cached is IReadOnlyDictionary<long, string> cachedLabels)
        {
            labels = cachedLabels;
        }
        else
        {
            labels = await LoadUserLabelsAsync(context.Client, clanId, cancellationToken);
            var estimatedBytes = EstimateLabelCacheSize(labels);
            if (estimatedBytes <= MaxPolicyCacheEntryBytes)
            {
                _policyCache.Set(
                    cacheKey,
                    labels,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15),
                        Size = estimatedBytes
                    });
            }
        }

        return (targetUserId, _) => Task.FromResult(
            labels.TryGetValue(targetUserId, out var label)
                ? label
                : MonzeMessages.MemberFallbackLabel);
    }

    private static async Task<IReadOnlyDictionary<long, string>> LoadUserLabelsAsync(
        MezonClient client,
        long clanId,
        CancellationToken cancellationToken)
    {
        var members = await client.ListClanUsersAsync(
            clanId,
            new RequestOptions { SocketSendTimeout = 5_000 });
        var labels = new Dictionary<long, string>(members.ClanUsers.Count);
        for (var i = 0; i < members.ClanUsers.Count; i++)
        {
            var member = members.ClanUsers[i];
            if (member.User.Id <= 0 || labels.ContainsKey(member.User.Id))
            {
                continue;
            }

            labels[member.User.Id] = FirstNonEmpty(member.ClanNick, member.User.DisplayName, member.User.Username);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return labels;
    }

    private static long EstimateLabelCacheSize(IReadOnlyDictionary<long, string> labels)
    {
        var estimatedBytes = 32L;
        foreach (var pair in labels)
        {
            estimatedBytes += 32L + (long)pair.Value.Length * sizeof(char);
            if (estimatedBytes > MaxPolicyCacheEntryBytes)
            {
                return estimatedBytes;
            }
        }

        return Math.Max(1, estimatedBytes);
    }

    private static string FirstNonEmpty(string clanNick, string displayName, string username)
    {
        if (!string.IsNullOrWhiteSpace(clanNick))
        {
            return clanNick;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            return username;
        }

        return MonzeMessages.MemberFallbackLabel;
    }

    private static long? TryGetSingleMentionedUserId(ICommandContext context)
    {
        var mentions = context.Message.Mentions;
        if (mentions.Count != 1 || mentions[0].UserId <= 0)
        {
            return null;
        }

        return mentions[0].UserId;
    }

}

