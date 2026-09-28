using System.Security.Cryptography;
using System.Globalization;
using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private async Task<CommandOutcome> EventAsync(
        long clanId,
        long channelId,
        long userId,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        if (rest.Length == 0)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Event, cancellationToken);
        }

        if (rest[0].Equals(MonzeCommandActions.Create, StringComparison.OrdinalIgnoreCase))
        {
            if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
            {
                return Say(MonzeMessages.AdminOnly);
            }

            var raw = rest.Join(' ', 1);
            var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Event, cancellationToken);
            }

            var scheduleText = parts[1];
            int? capacity = null;
            var scheduleTokens = scheduleText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (scheduleTokens.Length >= 3
                && int.TryParse(scheduleTokens[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCapacity))
            {
                if (parsedCapacity <= 0 || parsedCapacity > 10_000)
                {
                    return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Event, cancellationToken);
                }

                capacity = parsedCapacity;
                scheduleText = string.Join(' ', scheduleTokens, 0, scheduleTokens.Length - 1);
            }

            if (!DateTimeOffset.TryParse(
                    scheduleText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var starts))
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Event, cancellationToken);
            }

            if (!LocalSchedule.TryToUtc(DateOnly.FromDateTime(starts.DateTime), TimeOnly.FromDateTime(starts.DateTime), "Asia/Ho_Chi_Minh", out var utc, out var note))
            {
                return Say(note ?? MonzeMessages.InvalidTime);
            }

            await _community.CreateEventAsync(clanId, channelId, userId, parts[0], utc, capacity, cancellationToken);
            return Say(MonzeMessages.EventOpened(
                parts[0],
                _commandOptions.Command(MonzeCommandNames.Event, MonzeCommandActions.Join),
                note,
                capacity));
        }

        if (rest[0].Equals(MonzeCommandActions.Join, StringComparison.OrdinalIgnoreCase))
        {
            var joined = await _community.JoinEventAsync(clanId, userId, cancellationToken);
            return Say(joined switch
            {
                EventJoinStatus.Confirmed => MonzeMessages.EventJoined,
                EventJoinStatus.Waitlisted => MonzeMessages.EventWaitlisted,
                EventJoinStatus.AlreadyRegistered => MonzeMessages.EventAlreadyJoined,
                _ => MonzeMessages.NoEvent
            });
        }

        if (rest[0].Equals(MonzeCommandActions.Recap, StringComparison.OrdinalIgnoreCase))
        {
            if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
            {
                return Say(MonzeMessages.AdminOnly);
            }

            return Say(await _community.RecapEventAsync(clanId, cancellationToken) ?? MonzeMessages.EventNotFound);
        }

        return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Event, cancellationToken);
    }

    private async Task<CommandOutcome> AdminTextAsync(
        long clanId,
        long userId,
        long channelId,
        OutboxKind kind,
        string dedupe,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly);
        }

        var body = rest.Join(' ').Trim();
        if (body.Length == 0)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Announce, cancellationToken);
        }

        await _outbox.EnqueueAsync(clanId, channelId, kind, dedupe, body, cancellationToken);
        return Say(MonzeMessages.OutboxQueued);
    }

    private async Task<CommandOutcome> OutboxAsync(long clanId, long userId, CommandArguments rest, CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly);
        }

        if (rest.Length == 0)
        {
            var rows = await _outbox.ListUncertainAsync(clanId, cancellationToken);
            return Say(rows.Count == 0 ? MonzeMessages.OutboxUncertainEmpty : string.Join('\n', rows));
        }

        if (rest[0].Equals(MonzeCommandActions.Resend, StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length != 2 || !long.TryParse(rest[1], out var id) || id <= 0)
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Outbox, cancellationToken);
            }

            var ok = await _outbox.ResendAsync(clanId, id, cancellationToken);
            return Say(ok ? MonzeMessages.OutboxResent : MonzeMessages.OutboxUncertainNotFound);
        }

        return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Outbox, cancellationToken);
    }

    private async Task<CommandOutcome> FaqAsync(long clanId, long userId, CommandArguments rest, CancellationToken cancellationToken)
    {
        if (rest.Length == 0 || !rest[0].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Faq, cancellationToken);
        }

        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly);
        }

        var raw = rest.Join(' ', 1);
        var parts = raw.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return Say(MonzeMessages.FaqSeparatorMissing);
        }

        await _community.AddFaqAsync(clanId, parts[0], parts[1], cancellationToken);
        return Say(MonzeMessages.FaqSaved);
    }

    private async Task<CommandOutcome> TopicAsync(
        long clanId,
        long userId,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        if (rest.Length == 0)
        {
            return Say(await _community.NextTopicAsync(clanId, cancellationToken) ?? MonzeMessages.NoTopic);
        }

        if (rest.Length < 2
            || (!rest[0].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase)
                && !rest[0].Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase)))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Topic, cancellationToken);
        }

        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.AdminOnly);
        }

        var text = rest.Join(' ', 1).Trim();
        if (text.Length is 0 or > 500)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Topic, cancellationToken);
        }

        if (rest[0].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase))
        {
            var added = await _community.AddTopicAsync(clanId, text, cancellationToken);
            return Say(added ? MonzeMessages.TopicAdded : MonzeMessages.TopicAlreadyExists);
        }

        var removed = await _community.RemoveTopicAsync(clanId, text, cancellationToken);
        return Say(removed ? MonzeMessages.TopicRemoved : MonzeMessages.TopicNotFound);
    }

    private async Task<CommandOutcome> PointsAsync(long clanId, long userId, CommandArguments rest, CancellationToken cancellationToken)
    {
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        var result = await _community.AddPointsAsync(clanId, userId, 1, "command", $"{userId}:{day}", 20, cancellationToken);
        return Say(result.Applied
            ? MonzeMessages.PointsApplied(result.Balance)
            : MonzeMessages.PointsAlreadyApplied(result.Balance));
    }

    private async Task<CommandOutcome> LeaderboardAsync(
        long clanId,
        Func<long, CancellationToken, Task<string>>? resolveUserLabel,
        CancellationToken cancellationToken)
    {
        var rows = await _community.LeaderboardAsync(clanId, 10, cancellationToken);
        if (rows.Count == 0)
        {
            return Say(MonzeMessages.NoPoints);
        }

        var lines = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var label = resolveUserLabel is null
                ? "thành viên"
                : await resolveUserLabel(row.UserId, cancellationToken);
            lines[i] = MonzeMessages.LeaderboardRow(i + 1, label, row.Points);
        }

        return Say(string.Join('\n', lines));
    }

    private async Task<CommandOutcome> SpinAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        var weights = new[] { 5, 3, 1 };
        var roll = RandomNumberGenerator.GetInt32(9);
        var index = await _community.SpinAsync(clanId, userId, weights, roll, cancellationToken);
        return Say(index is null ? MonzeMessages.SpinCooldown : MonzeMessages.SpinResult(index.Value));
    }

    private async Task<CommandOutcome> RoleAsync(long clanId, long userId, CommandArguments rest, CancellationToken cancellationToken)
    {
        if (rest.Length == 0)
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        if (rest[0].Equals(MonzeCommandActions.Rule, StringComparison.OrdinalIgnoreCase))
        {
            return await ConfigureRoleRuleAsync(clanId, userId, rest, cancellationToken);
        }

        if (rest.Length < 2
            || (!rest[0].Equals(MonzeCommandActions.Self, StringComparison.OrdinalIgnoreCase)
                && !rest[0].Equals(MonzeCommandActions.Allow, StringComparison.OrdinalIgnoreCase)
                && !rest[0].Equals(MonzeCommandActions.Deny, StringComparison.OrdinalIgnoreCase)))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Role, cancellationToken);
        }

        if (_roleGateway is null)
        {
            return Say(MonzeMessages.RoleGatewayNotReady);
        }

        var role = await _roleGateway.ResolveRoleAsync(
            clanId,
            rest.Join(' ', 1),
            cancellationToken);
        if (!role.Found)
        {
            return Say(MonzeMessages.RoleNotFound);
        }

        if (!rest[0].Equals(MonzeCommandActions.Self, StringComparison.OrdinalIgnoreCase))
        {
            if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
            {
                return Say(MonzeMessages.AdminOnly);
            }

            var enabled = rest[0].Equals(MonzeCommandActions.Allow, StringComparison.OrdinalIgnoreCase);
            await _authorization.SetRoleSelfAssignableAsync(clanId, role.RoleId, enabled, cancellationToken);
            return Say(MonzeMessages.RoleSelfAssignableChanged(enabled, role.Label));
        }

        if (!await _authorization.IsRoleSelfAssignableAsync(clanId, role.RoleId, cancellationToken))
        {
            return Say(MonzeMessages.RoleSelfNotAllowed, tone: MonzeTone.Warn);
        }

        var assignment = await _roleGateway.AddUserToRoleAsync(
            clanId,
            role.RoleId,
            userId,
            cancellationToken);
        if (!assignment.Succeeded)
        {
            return Say(MonzeMessages.RoleAssignFailed);
        }

        await _authorization.RecordRoleGrantAsync(clanId, assignment.RoleId, userId, cancellationToken);
        return Say(MonzeMessages.RoleAssigned);
    }
}
