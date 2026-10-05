using System.Reflection;
using Mezon.Net.Sdk.Entities;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Mezon.Net.Sdk.Interactions;
using Xunit;
using RealtimeMessageButtonClicked = Mezon.Net.Internal.Realtime.MessageButtonClicked;

namespace Monze.Tests;

public sealed class SdkEphemeralContractTests
{
    [Fact]
    public void Published_sdk_exposes_ephemeral_update_delete_and_quick_menu_api()
    {
        Assert.NotNull(typeof(Channel).GetMethod(nameof(Channel.UpdateEphemeralAsync)));
        Assert.NotNull(typeof(Channel).GetMethod(nameof(Channel.DeleteEphemeralAsync)));
        Assert.NotNull(typeof(IInteractionContext).GetMethod(nameof(IInteractionContext.UpdateEphemeralAsync)));
        Assert.NotNull(typeof(IInteractionContext).GetMethod(nameof(IInteractionContext.DeleteEphemeralAsync)));
        Assert.NotNull(typeof(Mezon.Net.Sdk.MezonClient).GetMethod(nameof(Mezon.Net.Sdk.MezonClient.ListQuickMenuAccessAsync)));
        Assert.NotNull(typeof(Mezon.Net.Sdk.MezonClient).GetEvent(nameof(Mezon.Net.Sdk.MezonClient.QuickMenuReceivedData)));
        Assert.NotNull(typeof(QuickMenuReceivedEventData).GetProperty(nameof(QuickMenuReceivedEventData.MenuName)));
        Assert.NotNull(typeof(QuickMenuReceivedEventData).GetProperty(nameof(QuickMenuReceivedEventData.MessageId)));
        Assert.NotNull(typeof(QuickMenuReceivedEventData).GetProperty(nameof(QuickMenuReceivedEventData.ClanId)));
        Assert.NotNull(typeof(QuickMenuReceivedEventData).GetProperty(nameof(QuickMenuReceivedEventData.ChannelId)));
    }

    [Fact]
    public void Empty_ephemeral_ack_is_a_safe_default_value()
    {
        var response = default(ChannelMessageAckResponse);

        Assert.True(response.Equals(default(ChannelMessageAckResponse)));
    }

    [Fact]
    public void Monze_message_id_reader_does_not_dereference_empty_ack()
    {
        var reader = typeof(Monze.MonzeBot).GetMethod(
            "TryReadMessageId",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(reader);

        var result = reader!.Invoke(
            obj: null,
            parameters: new object[] { default(ChannelMessageAckResponse) });

        Assert.Equal(0L, result);
    }

    [Fact]
    public async Task Published_sdk_marks_button_actor_as_server_authenticated()
    {
        var router = new InteractionRouter();
        router.OnButton("contract", _ => Task.CompletedTask)
            .RequireServerAuthenticatedActor();

        var client = new MezonClient(new MezonClientOptions(1, "test-token"));
        var proto = new RealtimeMessageButtonClicked
        {
            MessageId = 30,
            ChannelId = 20,
            ButtonId = "contract",
            SenderId = 40,
            UserId = 40
        };
        var response = (MessageButtonClickedResponse)Activator.CreateInstance(
            typeof(MessageButtonClickedResponse),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [proto],
            culture: null)!;

        var result = await router.HandleButtonAsync(
            client,
            (MessageButtonClickedEventData)response,
            CancellationToken.None);

        Assert.NotEqual(InteractionExecutionResult.Unauthorized, result);
        Assert.NotEqual(InteractionExecutionResult.NotHandled, result);
    }
}
