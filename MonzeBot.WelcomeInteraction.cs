using Mezon.Net.Sdk.Interactions;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task HandleHelpPageAsync(IInteractionContext context, string page)
    {
        await context.UpdateMessageAsync(
            MonzeMessageBuilder.HelpPage(page, _commandOptions, isAdmin: false));
    }

    private async Task HandleWelcomeSettingsAsync(IInteractionContext context)
    {
        var outcome = await _app.GetWelcomeSettingsForInteractionAsync(
            context.Channel.ClanId,
            context.CancellationToken);
        await context.UpdateMessageAsync(MonzeMessageBuilder.Card(outcome, _commandOptions));
    }

    private async Task HandleWelcomePreviewAsync(IInteractionContext context)
    {
        if (context.Interaction is not ButtonInteraction button)
        {
            await context.UpdateMessageAsync(
                MonzeMessageBuilder.Card(
                    MonzeMessages.TitleWelcome,
                    MonzeMessages.WelcomeDraftInvalid,
                    MonzeTone.Warn));
            return;
        }

        var current = await _authorization.GetWelcomeAsync(
            context.Channel.ClanId,
            context.CancellationToken);
        var draft = MonzeWelcomeFormParser.ReadEmbed(button.ExtraData, current);
        var enabled = MonzeWelcomeFormParser.ReadEnabled(
            button.ExtraData,
            current?.Enabled ?? false);
        var outcome = await _app.PreviewWelcomeAsync(
            context.Channel.ClanId,
            context.Channel.Id,
            enabled,
            draft,
            context.CancellationToken);
        await context.UpdateMessageAsync(MonzeMessageBuilder.Card(outcome, _commandOptions));
    }

    private async Task HandleWelcomeSaveAsync(IInteractionContext context)
    {
        if (context.Interaction is not ButtonInteraction button)
        {
            await context.UpdateMessageAsync(
                MonzeMessageBuilder.Card(
                    MonzeMessages.TitleWelcome,
                    MonzeMessages.WelcomeDraftInvalid,
                    MonzeTone.Warn));
            return;
        }

        if (MonzeButtonId.TryReadWelcomeSaveToken(button.CustomId, out var token))
        {
            var existingOutcome = await _app.GetWelcomeDraftPreviewAsync(
                context.Channel.ClanId,
                context.Channel.Id,
                token,
                context.CancellationToken);
            await context.UpdateMessageAsync(
                MonzeMessageBuilder.Card(existingOutcome, _commandOptions));
            return;
        }

        var current = await _authorization.GetWelcomeAsync(
            context.Channel.ClanId,
            context.CancellationToken);
        var enabled = MonzeWelcomeFormParser.ReadEnabled(
            button.ExtraData,
            current?.Enabled ?? false);
        var draft = MonzeWelcomeFormParser.ReadEmbed(button.ExtraData, current);
        var outcome = await _app.PreviewWelcomeAsync(
            context.Channel.ClanId,
            context.Channel.Id,
            enabled,
            draft,
            context.CancellationToken);
        await context.UpdateMessageAsync(MonzeMessageBuilder.Card(outcome, _commandOptions));
    }
}
