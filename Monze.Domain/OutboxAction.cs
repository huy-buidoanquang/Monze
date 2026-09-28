namespace Monze.Domain;

public enum OutboxAction
{
    Send,
    RetryOnce,
    HoldForAdmin,
    AlreadyDelivered
}
