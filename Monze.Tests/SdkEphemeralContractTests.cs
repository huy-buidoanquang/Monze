using System.Reflection;
using Mezon.Net.Sdk.Entities;
using Mezon.Net.Models;
using Mezon.Net.Sdk.Interactions;
using Xunit;

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
}
