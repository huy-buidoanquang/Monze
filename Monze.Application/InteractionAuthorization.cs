namespace Monze.Application;

public static class InteractionAuthorization
{
    public static bool CanHandle(
        long messageId,
        long boundUserId,
        long interactionUserId,
        bool serverAuthenticated)
        => serverAuthenticated
            && messageId > 0
            && boundUserId > 0
            && interactionUserId > 0
            && boundUserId == interactionUserId;
}
