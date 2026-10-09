namespace Monze.Domain;

public static class OutboxPolicy
{
    /// <summary>
    /// Deliveries that ended without the message reaching the channel before
    /// a row is held for an administrator. Each retry waits longer
    /// (5 s × 2^attempts, at most 320 s).
    /// </summary>
    public const int MaxAttempts = 6;

    public static OutboxAction Decide(OutboxKind kind, bool hasExternalMessageId, int failedAttempts)
    {
        if (hasExternalMessageId)
        {
            return OutboxAction.AlreadyDelivered;
        }

        return failedAttempts < MaxAttempts ? OutboxAction.Send : OutboxAction.HoldForAdmin;
    }
}
