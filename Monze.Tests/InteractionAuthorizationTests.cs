using Monze.Application;
using Xunit;

namespace Monze.Tests;

public sealed class InteractionAuthorizationTests
{
    [Fact]
    public void Rejects_client_supplied_button_actor_even_when_user_id_matches()
    {
        Assert.False(InteractionAuthorization.CanHandle(10, 20, 20, serverAuthenticated: false));
    }

    [Fact]
    public void Accepts_server_authenticated_owner_for_bound_message()
    {
        Assert.True(InteractionAuthorization.CanHandle(10, 20, 20, serverAuthenticated: true));
    }

    [Theory]
    [InlineData(10, 20, 21)]
    [InlineData(11, 20, 0)]
    [InlineData(0, 20, 20)]
    public void Rejects_wrong_message_or_user(
        long messageId,
        long boundUserId,
        long interactionUserId)
    {
        Assert.False(InteractionAuthorization.CanHandle(
            messageId,
            boundUserId,
            interactionUserId,
            serverAuthenticated: true));
    }
}
