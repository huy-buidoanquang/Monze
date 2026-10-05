using Monze.Application.Commands;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private async Task<CommandOutcome> SetupAsync(
        long clanId,
        long channelId,
        long userId,
        CommandArguments rest,
        long? mentionedUserId,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsOwnerAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.OwnerOnly, tone: MonzeTone.Error);
        }

        if (rest.Length >= 2 && rest[0].Equals(MonzeCommandActions.Admin, StringComparison.OrdinalIgnoreCase))
        {
            if (!rest[1].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase)
                && !rest[1].Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase))
            {
                return await SetupHelpAsync(clanId, userId, cancellationToken);
            }

            long parsedUserId = 0;
            var targetUserId = mentionedUserId;
            if (targetUserId is null
                && (rest.Length < 3 || !long.TryParse(rest[2], out parsedUserId) || parsedUserId <= 0))
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Setup, cancellationToken);
            }

            targetUserId ??= parsedUserId;
            if (targetUserId == userId)
            {
                return Say(MonzeMessages.OwnerAlreadyAdmin);
            }

            var enabled = rest[1].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase);
            if (enabled
                && (_roleGateway is null
                    || !await _roleGateway.IsMemberAsync(clanId, targetUserId.Value, cancellationToken)))
            {
                return Say(
                    MonzeMessages.DelegateMustBeClanMember,
                    title: MonzeMessages.TitleClanSetup,
                    tone: MonzeTone.Warn);
            }

            var changed = await _authorization.SetDelegateAsync(
                clanId,
                userId,
                targetUserId.Value,
                enabled,
                cancellationToken);
            return Say(changed
                ? enabled ? MonzeMessages.DelegateAdded : MonzeMessages.DelegateRemoved
                : enabled ? MonzeMessages.DelegateAlreadyExists : MonzeMessages.DelegateNotFound);
        }

        return await SetupHelpAsync(clanId, userId, cancellationToken);
    }

    private async Task<CommandOutcome> SetupHelpAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        var isOwner = await _authorization.IsOwnerAsync(clanId, userId, cancellationToken);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleHelp,
            Text = string.Empty,
            HelpTopic = MonzeCommandNames.Setup,
            HelpForAdmin = isOwner,
            HelpForOwner = isOwner,
            CanManageWelcome = isOwner,
            ShowWelcomeHelp = isOwner
        };
    }
}
