using CsCheck;
using Monze.Domain;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Domain;

/// <summary>
/// G13: MeetingFlow over random operation sequences from every status
/// (8 statuses × 6 operations). Each step must match the transition table,
/// a posted summary is final (no second post), and nothing returns to
/// Requested. MeetingFlow has no production caller today; the persisted state
/// machine is checked by the integration tier.
/// </summary>
public sealed class G13MeetingFlowProperties
{
    private static readonly string[] Operations = ["suggest", "bind-match", "bind-other", "summary-stored", "summary-failed", "expire"];

    [Fact]
    [Req("REQ-MTG-006")]
    public void Meeting_status_follows_the_transition_table()
    {
        PropertyRun.Run(
            "G13",
            Gen.Select(Gen.Enum<MeetingStatus>(), Gen.OneOfConst(Operations).Array[1, 12]),
            static value =>
            {
                var (start, operations) = value;
                var status = start;
                var tags = new Dictionary<string, string> { ["start"] = start.ToString(), ["first"] = operations[0] };
                var input = $"{start}: {string.Join(" > ", operations)}";
                foreach (var operation in operations)
                {
                    var expected = Expected(status, operation);
                    MeetingStatus? actual;
                    try
                    {
                        actual = Apply(status, operation);
                    }
                    catch (InvalidOperationException)
                    {
                        actual = null;
                    }

                    if (actual != expected)
                    {
                        return PropertyResult.Fail(input, tags, $"{status} {operation}: expected {expected?.ToString() ?? "invalid"}, got {actual?.ToString() ?? "invalid"}");
                    }

                    if (actual is { } next)
                    {
                        if (status == MeetingStatus.Posted && next != MeetingStatus.Posted)
                        {
                            return PropertyResult.Fail(input, tags, "left Posted");
                        }

                        if (next == MeetingStatus.Requested && status != MeetingStatus.Requested)
                        {
                            return PropertyResult.Fail(input, tags, "returned to Requested");
                        }

                        status = next;
                    }
                }

                return PropertyResult.Pass(input, tags);
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("start", Enum.GetNames<MeetingStatus>()).Dimension("first", Operations));
    }

    private static MeetingStatus Apply(MeetingStatus status, string operation)
        => operation switch
        {
            "suggest" => MeetingFlow.Suggest(status),
            "bind-match" => MeetingFlow.TryBindRoom(status, true, out var bound) ? bound : status,
            "bind-other" => MeetingFlow.TryBindRoom(status, false, out var unchanged) ? unchanged : status,
            "summary-stored" => MeetingFlow.OnSummaryStored(status),
            "summary-failed" => MeetingFlow.OnSummaryFailed(status),
            _ => MeetingFlow.ExpireSuggestion(status)
        };

    private static MeetingStatus? Expected(MeetingStatus status, string operation)
        => operation switch
        {
            "suggest" => status == MeetingStatus.Requested ? MeetingStatus.Suggested : null,
            "bind-match" => status is MeetingStatus.Suggested or MeetingStatus.Live ? MeetingStatus.Live : status,
            "bind-other" => status,
            "summary-stored" => status is MeetingStatus.Live or MeetingStatus.SummaryPending or MeetingStatus.SummaryFailed ? MeetingStatus.Posted : null,
            "summary-failed" => status is MeetingStatus.Live or MeetingStatus.SummaryPending ? MeetingStatus.SummaryPending : null,
            _ => status == MeetingStatus.Suggested ? MeetingStatus.Expired : status
        };
}
