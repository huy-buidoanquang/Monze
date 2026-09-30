using Mezon.Net.Sdk.Interactions;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task HandleHelpPageAsync(IInteractionContext context, string page)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        var isAdmin = await _authorization.IsAdminAsync(
            context.Channel.ClanId,
            context.User.Id,
            context.CancellationToken);
        var isOwner = await _authorization.IsOwnerAsync(
            context.Channel.ClanId,
            context.User.Id,
            context.CancellationToken);
        await UpdatePrivateInteractionAsync(
            context,
            MonzeMessageBuilder.HelpPage(
                page,
                _commandOptions,
                isAdmin,
                canManageWelcome: isAdmin,
                isOwner));
    }

    private async Task HandleHelpCloseAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        await DeletePrivateInteractionAsync(context);
    }

    private async Task HandleWelcomeSettingsAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        if (!await EnsureWelcomeAdministratorAsync(context))
        {
            return;
        }

        var current = await _authorization.GetWelcomeAsync(
            context.Channel.ClanId,
            context.CancellationToken)
            ?? new WelcomeSettings(false, null, 0);
        var key = GetWelcomeSetupDraftKey(context);
        if (_welcomeSetupDrafts.TryGet(key, DateTimeOffset.UtcNow, out var draft))
        {
            current = draft;
        }

        await UpdatePrivateInteractionAsync(
            context,
            MonzeMessageBuilder.WelcomeSettings(current));
    }

    private async Task HandleWelcomeSectionAsync(
        IInteractionContext context,
        WelcomeSetupSection section)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        if (!await EnsureWelcomeAdministratorAsync(context))
        {
            return;
        }

        var current = await _authorization.GetWelcomeAsync(
            context.Channel.ClanId,
            context.CancellationToken)
            ?? new WelcomeSettings(false, null, 0);
        var key = GetWelcomeSetupDraftKey(context);
        if (_welcomeSetupDrafts.TryGet(key, DateTimeOffset.UtcNow, out var savedDraft))
        {
            current = savedDraft;
        }

        var draft = current.Embed ?? new WelcomeEmbedSettings(Title: "Chào mừng");
        var enabled = current.Enabled;
        if (context.Interaction is ButtonInteraction button)
        {
            draft = MonzeWelcomeFormParser.ReadEmbed(button.ExtraData, current);
            enabled = MonzeWelcomeFormParser.ReadEnabled(button.ExtraData, current.Enabled);
        }

        current = current with { Enabled = enabled, Embed = draft };
        _welcomeSetupDrafts.Set(key, current, DateTimeOffset.UtcNow);
        var content = MonzeMessageBuilder.WelcomeSettings(current, section);
        if (context.Message is not null)
        {
            await UpdatePrivateInteractionAsync(context, content);
            return;
        }

        await UpdatePrivateInteractionAsync(context, content);
    }

    private async Task HandleWelcomePreviewAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        if (!await EnsureWelcomeAdministratorAsync(context))
        {
            return;
        }

        if (context.Interaction is not ButtonInteraction button)
        {
            await UpdatePrivateInteractionAsync(
                context,
                MonzeMessageBuilder.Card(
                    MonzeMessages.TitleWelcome,
                    MonzeMessages.WelcomeDraftInvalid,
                    MonzeTone.Warn));
            return;
        }

        var current = await _authorization.GetWelcomeAsync(
            context.Channel.ClanId,
            context.CancellationToken);
        var key = GetWelcomeSetupDraftKey(context);
        if (current is null)
        {
            current = new WelcomeSettings(false, null, 0);
        }

        if (_welcomeSetupDrafts.TryGet(key, DateTimeOffset.UtcNow, out var savedDraft))
        {
            current = savedDraft;
        }

        var draft = MonzeWelcomeFormParser.ReadEmbed(button.ExtraData, current);
        var enabled = MonzeWelcomeFormParser.ReadEnabled(
            button.ExtraData,
            current.Enabled);
        current = current with { Enabled = enabled, Embed = draft };
        _welcomeSetupDrafts.Set(key, current, DateTimeOffset.UtcNow);
        var outcome = await _app.PreviewWelcomeAsync(
            context.Channel.ClanId,
            context.Channel.Id,
            enabled,
            draft,
            context.CancellationToken,
            current.Text);
        await UpdatePrivateInteractionAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
    }

    private async Task HandleWelcomeCancelAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        if (!await EnsureWelcomeAdministratorAsync(context))
        {
            return;
        }

        _welcomeSetupDrafts.Remove(GetWelcomeSetupDraftKey(context));
        await UpdatePrivateInteractionAsync(
            context,
            MonzeMessageBuilder.Card(
                new CommandOutcome
                {
                    Title = MonzeMessages.TitleWelcome,
                    Text = MonzeMessages.WelcomeSetupCancelled,
                    Tone = MonzeTone.Warn,
                    ShowWelcomeHelp = true
                },
                _commandOptions));
    }

    private async Task HandleWelcomeSaveAsync(IInteractionContext context)
    {
        if (!await EnsurePrivateInteractionAsync(context))
        {
            return;
        }

        if (!await EnsureWelcomeAdministratorAsync(context))
        {
            return;
        }

        if (context.Interaction is not ButtonInteraction button)
        {
            await UpdatePrivateInteractionAsync(
                context,
                MonzeMessageBuilder.Card(
                    MonzeMessages.TitleWelcome,
                    MonzeMessages.WelcomeDraftInvalid,
                    MonzeTone.Warn));
            return;
        }

        if (MonzeButtonId.TryReadWelcomeSaveToken(button.CustomId, out var token))
        {
            var saveOutcome = await _app.SaveWelcomeDraftAsync(
                context.Channel.ClanId,
                context.Channel.Id,
                context.User.Id,
                token,
                context.CancellationToken);
            await UpdatePrivateInteractionAsync(
                context,
                MonzeMessageBuilder.Card(saveOutcome, _commandOptions));
            return;
        }

        var current = await _authorization.GetWelcomeAsync(
            context.Channel.ClanId,
            context.CancellationToken);
        var key = GetWelcomeSetupDraftKey(context);
        if (current is null)
        {
            current = new WelcomeSettings(false, null, 0);
        }

        if (_welcomeSetupDrafts.TryGet(key, DateTimeOffset.UtcNow, out var savedDraft))
        {
            current = savedDraft;
        }

        var enabled = MonzeWelcomeFormParser.ReadEnabled(
            button.ExtraData,
            current.Enabled);
        var draft = MonzeWelcomeFormParser.ReadEmbed(button.ExtraData, current);
        current = current with { Enabled = enabled, Embed = draft };
        _welcomeSetupDrafts.Set(key, current, DateTimeOffset.UtcNow);
        var outcome = await _app.SaveWelcomeEmbedAsync(
            context.Channel.ClanId,
            context.User.Id,
            enabled,
            draft,
            context.CancellationToken,
            current.Text);
        if (outcome.Tone != MonzeTone.Error)
        {
            _welcomeSetupDrafts.Remove(key);
        }

        await UpdatePrivateInteractionAsync(context, MonzeMessageBuilder.Card(outcome, _commandOptions));
    }

    private static WelcomeSetupDraftKey GetWelcomeSetupDraftKey(IInteractionContext context)
        => new(context.Channel.ClanId, context.Channel.Id, context.User.Id);

    private async Task<bool> EnsureWelcomeAdministratorAsync(IInteractionContext context)
    {
        if (await _authorization.IsAdminAsync(
                context.Channel.ClanId,
                context.User.Id,
                context.CancellationToken))
        {
            return true;
        }

        await UpdatePrivateInteractionAsync(
            context,
            MonzeMessageBuilder.Card(
                MonzeMessages.TitleWelcome,
                MonzeMessages.WelcomeAdminOnly,
                MonzeTone.Error));
        return false;
    }
}
