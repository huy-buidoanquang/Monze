namespace Monze.Domain;

public static class OutboxPolicy
{
    public static OutboxAction Decide(OutboxKind kind, bool hasExternalMessageId, int failedAttempts)
    {
        if (hasExternalMessageId)
        {
            return OutboxAction.AlreadyDelivered;
        }

        if (kind == OutboxKind.CommandReply && failedAttempts < 1)
        {
            return OutboxAction.RetryOnce;
        }

        if (failedAttempts == 0)
        {
            return OutboxAction.Send;
        }

        return OutboxAction.HoldForAdmin;
    }
}
