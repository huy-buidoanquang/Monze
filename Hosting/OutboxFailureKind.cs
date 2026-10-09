namespace Monze;

/// <summary>What a failed outbox delivery means for the message.</summary>
internal enum OutboxFailureKind
{
    /// <summary>Nothing left the process (for example the socket was closed before the send); retry.</summary>
    NotSent,

    /// <summary>The platform refused the request for a reason that may pass; retry.</summary>
    Retryable,

    /// <summary>The platform refused the request for good (permissions, missing channel); hold for an administrator.</summary>
    Rejected,

    /// <summary>The request may have reached the platform; never resend blindly, reconcile against the channel.</summary>
    Uncertain
}
