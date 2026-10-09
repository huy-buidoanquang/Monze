using Mezon.Net.Core;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

/// <summary>
/// Regression for CAND-21: only a failure that proves nothing left the
/// process may be retried without reconciliation; anything that may have
/// reached the platform is uncertain.
/// </summary>
public sealed class OutboxDeliveryFailureTests
{
    public static TheoryData<Exception, string> Failures() => new()
    {
        { new InvalidOperationException("Cannot send on WebSocket"), nameof(OutboxFailureKind.NotSent) },
        { new ObjectDisposedException("transport"), nameof(OutboxFailureKind.Uncertain) },
        { new OperationCanceledException(), nameof(OutboxFailureKind.Uncertain) },
        { new TaskCanceledException(), nameof(OutboxFailureKind.Uncertain) },
        { new TimeoutException(), nameof(OutboxFailureKind.Uncertain) },
        { new IOException(), nameof(OutboxFailureKind.Uncertain) },
        { new MezonApiException(MezonStatusCode.DeadlineExceeded), nameof(OutboxFailureKind.Uncertain) },
        { new MezonApiException(MezonStatusCode.Unknown), nameof(OutboxFailureKind.Uncertain) },
        { new MezonApiException(MezonStatusCode.PermissionDenied), nameof(OutboxFailureKind.Rejected) },
        { new MezonApiException(MezonStatusCode.NotFound), nameof(OutboxFailureKind.Rejected) },
        { new MezonApiException(MezonStatusCode.InvalidArgument), nameof(OutboxFailureKind.Rejected) },
        { new MezonApiException(MezonStatusCode.ResourceExhausted), nameof(OutboxFailureKind.Retryable) },
        { new MezonApiException(MezonStatusCode.Unavailable), nameof(OutboxFailureKind.Retryable) }
    };

    [Theory]
    [Req("REQ-OUT-001")]
    [MemberData(nameof(Failures))]
    public void Delivery_failures_are_classified_by_what_may_have_reached_the_platform(Exception exception, string expected)
    {
        Assert.Equal(expected, OutboxDeliveryFailure.Classify(exception).ToString());
    }
}
