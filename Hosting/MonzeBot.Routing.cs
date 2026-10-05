using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Commands;
using Mezon.Net.Sdk.Interactions;
using Microsoft.Extensions.Logging;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

public sealed partial class MonzeBot
{
    private void ConfigureCommands(MezonClient client)
    {
        var commands = new CommandService(_commandOptions.Prefix);
        if (_commandOptions.HasRoot)
        {
            commands.AddCommand(_commandOptions.Root!, HandleMonzeAsync);
        }
        else
        {
            commands.AddCommand(MonzeCommandNames.Help, HandleDirectHelpAsync);
        }

        foreach (var module in MonzeCommandNames.DirectModules)
        {
            var directModule = module;
            commands.AddCommand(directModule, context => HandleDirectMonzeAsync(context, directModule));
        }

        commands.AddCommand(MonzeCommandNames.Meeting, HandleMeetingAsync);
        commands.AddCommand(MonzeCommandNames.Summary, HandleSummaryAsync);
        client.UseCommands(commands);
    }

    private void ConfigureInteractions(MezonClient client)
    {
        var interactions = new InteractionRouter();
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpMeeting,
            ctx => HandleHelpPageAsync(ctx, "meeting"));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpSummary,
            ctx => HandleHelpPageAsync(ctx, "summary"));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpWelcome,
            ctx => HandleHelpPageAsync(ctx, "welcome"));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpRole,
            ctx => HandleHelpPageAsync(ctx, "role"));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpAi,
            ctx => HandleHelpPageAsync(ctx, "ai"));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpSetup,
            ctx => HandleHelpPageAsync(ctx, "setup"));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.HelpClose,
            HandleHelpCloseAsync);
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.Refresh,
            HandleMeetingListAsync);
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.StartNow,
            HandleMeetingNowInteractionAsync);
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.Schedule,
            HandleMeetingScheduleFormAsync);
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.ScheduleSubmit,
            HandleMeetingScheduleSubmitAsync);
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.ScheduleCancel,
            HandleMeetingScheduleCancelAsync);
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.Help,
            ctx => HandleHelpPageAsync(ctx, "meeting"));
        RegisterPrivateButton(
            interactions,
            MeetingButtonId.CancelPrefix + "*",
            HandleMeetingCancelInteractionAsync);
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeHelp,
            HandleWelcomeSettingsAsync);
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeSettings,
            HandleWelcomeSettingsAsync);
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeGeneral,
            ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.General));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeImages,
            ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.Images));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeAuthorSection,
            ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.Author));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeAdvanced,
            ctx => HandleWelcomeSectionAsync(ctx, WelcomeSetupSection.Advanced));
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomePreview,
            HandleWelcomePreviewAsync);
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeSave,
            HandleWelcomeSaveAsync);
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeSavePrefix + "*",
            HandleWelcomeSaveAsync);
        RegisterPrivateButton(
            interactions,
            MonzeButtonId.WelcomeCancel,
            HandleWelcomeCancelAsync);
        client.MessageButtonClicked += evt =>
            DispatchButtonInteractionAsync(client, interactions, evt);
        client.DropdownBoxSelected += evt =>
            DispatchSelectInteractionAsync(client, interactions, evt);
    }

    private void RegisterPrivateButton(
        InteractionRouter router,
        string customId,
        InteractionHandler handler)
    {
        router.OnButton(customId, context => InvokePrivateButtonAsync(customId, handler, context))
            .RequireServerAuthenticatedActor();
    }

    private async Task InvokePrivateButtonAsync(
        string customId,
        InteractionHandler handler,
        IInteractionContext context)
    {
        _logger.LogDebug(
            "Private button route invoked. Channel={ChannelId}, Message={MessageId}, User={UserId}, CustomId={CustomId}.",
            context.Channel.Id,
            context.Interaction.MessageId,
            context.User.Id,
            customId);
        await handler(context).ConfigureAwait(false);
    }
}
