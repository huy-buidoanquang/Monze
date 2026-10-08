using System.Diagnostics;
using Monze.Application.Commands;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// Rate limit: with the user bucket at 2 commands per 3 seconds
/// (Monze:RateLimit:UserLimit, UserWindowSeconds), the third help in the
/// window gets the public "too fast" answer and help works again after it.
/// </summary>
public sealed class RateLimitAreaTests
{
    [DbFact]
    [Req("REQ-RL-001")]
    [Covers("msg:RateLimited", "msg:TitleRateLimited")]
    public async Task A_user_over_the_bucket_is_told_once_and_can_continue_after_the_window()
    {
        await using var host = await E2EActions.StartAsync("rate_limit", configuration: new Dictionary<string, string?>
        {
            ["Monze:RateLimit:UserLimit"] = "2",
            ["Monze:RateLimit:UserWindowSeconds"] = "3"
        });
        var mark = await E2EOracles.MarkAsync(host);
        var window = Stopwatch.StartNew();
        var first = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        var second = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        var limited = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");
        Assert.True(window.Elapsed < TimeSpan.FromSeconds(3), "The three commands did not fit into one window.");
        var other = await E2EActions.CommandAsync(host, GeneralId, Member2Id, "*monze help");
        await Task.Delay(TimeSpan.FromSeconds(3.2) - window.Elapsed);
        var again = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze help");

        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of(
            (first, ResponseKind.Ephemeral),
            (second, ResponseKind.Ephemeral),
            (limited, ResponseKind.Reply),
            (other, ResponseKind.Ephemeral),
            (again, ResponseKind.Ephemeral)));
        var answer = E2EActions.NewMessageAfter(host, limited, GeneralId);
        Assert.Equal(MonzeMessages.TitleRateLimited, E2EContent.Parse(answer).Embeds![0].Title);
        Assert.StartsWith("Bạn thao tác quá nhanh. Hãy thử lại sau ", E2EContent.Parse(answer).Embeds![0].Fields![0].Value, StringComparison.Ordinal);
    }
}
