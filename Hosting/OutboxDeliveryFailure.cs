using Mezon.Net.Core;

namespace Monze;

/// <summary>
/// Classifies an exception from an outbox delivery attempt. Only failures
/// that prove the frame never left the process, or that the platform refused
/// it, allow a resend; anything that may have reached the platform is
/// uncertain and goes through reconciliation against the channel history.
/// </summary>
internal static class OutboxDeliveryFailure
{
    public static OutboxFailureKind Classify(Exception exception)
        => exception switch
        {
            // An InvalidOperationException too, but a transport disposed mid-send may have delivered.
            ObjectDisposedException => OutboxFailureKind.Uncertain,

            // Mezon.Net 1.6.2 MezonNetworkWebSocketTransporter.SendAsync throws this before
            // the frame is queued when the socket is not open ("Cannot send on WebSocket").
            InvalidOperationException => OutboxFailureKind.NotSent,
            MezonApiException api => api.StatusCode switch
            {
                MezonStatusCode.PermissionDenied
                    or MezonStatusCode.NotFound
                    or MezonStatusCode.InvalidArgument
                    or MezonStatusCode.Unauthenticated
                    or MezonStatusCode.FailedPrecondition
                    or MezonStatusCode.AlreadyExists
                    or MezonStatusCode.OutOfRange
                    or MezonStatusCode.Unimplemented => OutboxFailureKind.Rejected,
                MezonStatusCode.DeadlineExceeded or MezonStatusCode.Unknown => OutboxFailureKind.Uncertain,
                _ => OutboxFailureKind.Retryable
            },

            // In-flight requests failed by a disconnect (OperationCanceledException),
            // timeouts and transport errors: the frame may have been delivered.
            _ => OutboxFailureKind.Uncertain
        };
}
