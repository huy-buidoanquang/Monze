using Microsoft.Extensions.DependencyInjection;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Workflows;

/// <summary>
/// AI commands against the fake OpenAI-compatible provider (SimHttpHost
/// /v1/chat/completions, Bearer = the AI key canary): a completion edits the
/// loading card into the provider's text and spends (input length + 3) / 4
/// tokens of the per-user daily cap; an exhausted cap answers
/// AiBudgetExceeded without calling the provider; 429, 500, 503, a timeout,
/// an empty, a malformed and an oversized answer each end in AiProviderEmpty.
/// CAND-05 (budget spent on a failed call is not refunded) is a known defect.
/// </summary>
public sealed class AiWorkflowTests
{
    [DbFact]
    [Req("REQ-AI-001")]
    [Covers("msg:AiBudgetExceeded")]
    public async Task A_completion_spends_budget_and_an_exhausted_cap_skips_the_provider()
    {
        await using var http = await E2EActions.HttpAsync(static request => $"Bản dịch: {request.Input}");
        await using var host = await E2EActions.StartAsync(
            "wf_ai_budget",
            http: http,
            agent: false,
            configuration: new Dictionary<string, string?> { ["Monze:Ai:DailyTokenCap"] = "12" });
        var mark = await E2EOracles.MarkAsync(host);
        const string first = "xin chào mọi người!!";
        const string second = "một câu dài hơn hẳn câu trước một chút";
        var ok = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*ai translate " + first);
        var over = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*ai translate " + second);
        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((ok, ResponseKind.EditedReply), (over, ResponseKind.EditedReply)));

        Assert.Contains("Bản dịch: " + first, E2EContent.Visible(LastEdit(host, ok)));
        Assert.Contains(MonzeMessages.AiBudgetExceeded, E2EContent.Visible(LastEdit(host, over)));
        var request = Assert.Single(http.AiRequests);
        Assert.Equal("gpt-4o-mini", request.Model);
        Assert.Equal(first, request.Input);
        Assert.Equal(8192, request.MaxTokens);
        Assert.False(string.IsNullOrWhiteSpace(request.Instruction));
        Assert.Equal((first.Length + 3) / 4, await host.ScalarAsync<int>("SELECT tokens FROM ai_usage WHERE clan_id = @clan AND user_id = @user;", ("clan", ClanId), ("user", MemberId)));
        await E2EOracles.AssertInvariantsAsync(host, aiDailyTokenCap: 12);
    }

    /// <summary>
    /// Regression for CAND-05: MonzeApp.CompleteAiAsync
    /// (Monze.Application/Features/Ai/MonzeApp.Ai.cs) consumes the estimated
    /// tokens before calling the provider and used to keep them when the
    /// provider gave no answer. Every failed call below now refunds them.
    /// </summary>
    [DbFact]
    [Req("REQ-AI-001")]
    [Covers("msg:AiProviderEmpty")]
    public async Task Provider_failures_answer_provider_empty_and_refund_the_budget()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync(
            "wf_ai_failures",
            http: http,
            agent: false,
            // A 2 s client timeout instead of production's 30 s keeps the hang case short.
            services: static services => services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(2) }));
        var failures = new (string Name, Action<SimHttpFaultPlan> Fault)[]
        {
            ("429", static plan => plan.Status(SimHttpRoute.AiCompletion, 429)),
            ("500", static plan => plan.Status(SimHttpRoute.AiCompletion, 500)),
            ("503", static plan => plan.Status(SimHttpRoute.AiCompletion, 503)),
            ("timeout", static plan => plan.Hang(SimHttpRoute.AiCompletion)),
            ("empty", static plan => plan.Empty(SimHttpRoute.AiCompletion)),
            ("malformed", static plan => plan.Malformed(SimHttpRoute.AiCompletion)),
            ("oversized", static plan => plan.Oversized(SimHttpRoute.AiCompletion, 3 * 1024 * 1024))
        };

        var mark = await E2EOracles.MarkAsync(host);
        var inputs = new List<InputExpectation>();
        foreach (var (name, fault) in failures)
        {
            fault(http.Faults);
            var text = $"nội dung lỗi {name}";
            var command = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*ai simplify " + text);
            inputs.Add(new(command, ResponseKind.EditedReply));
            Assert.Contains(MonzeMessages.AiProviderEmpty, E2EContent.Visible(LastEdit(host, command)));
        }

        await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation
        {
            Inputs = inputs,
            AllowedWarnings = ["AI provider returned invalid JSON."]
        });
        Assert.Empty(http.Faults.Pending);
        Assert.Equal(failures.Length, http.Requests.Count(static request => request.Route == SimHttpRoute.AiCompletion));
        var used = await host.ScalarAsync<int>("SELECT tokens FROM ai_usage WHERE clan_id = @clan AND user_id = @user;", ("clan", ClanId), ("user", MemberId));
        Assert.Equal(0, used);
        await E2EOracles.AssertInvariantsAsync(host);
    }

    /// <summary>
    /// Regression for WF-06: an AI request still running when the host stops
    /// is cancelled once the stop's grace period (UncertainMarkTimeout) ends,
    /// and its loading card is closed with the temporary failure card while
    /// the client is still connected. The SDK gives commands no token, so it
    /// used to run on past the stop and the card was never updated.
    /// </summary>
    [DbFact]
    [Req("REQ-AI-001")]
    public async Task A_request_running_when_the_host_stops_closes_its_loading_card()
    {
        await using var http = await E2EActions.HttpAsync();
        await using var host = await E2EActions.StartAsync("wf_ai_stop", http: http, agent: false);
        http.Faults.Hang(SimHttpRoute.AiCompletion);
        // Not CommandAsync: it waits for the command to finish, which the hung provider prevents.
        var command = await host.Inbound.SayAsync(ClanId, GeneralId, MemberId, "*ai simplify nội dung đang xử lý");
        await E2EActions.WaitUntilAsync(
            host,
            () => Task.FromResult(http.Requests.Any(static request => request.Route == SimHttpRoute.AiCompletion)),
            "the AI request");

        await host.StopHostAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(MonzeMessages.TemporaryFailure, E2EContent.Visible(LastEdit(host, command)));
    }

    /// <summary>The final state of the command's loading card.</summary>
    private static SimAction LastEdit(MonzeE2EHost host, SimPush command)
    {
        var loading = E2EActions.NewMessageAfter(host, command, GeneralId);
        return host.Recorder.Since(command.Sequence).Last(action => action.Kind == SimActionKind.UpdateMessage && action.MessageId == loading.MessageId);
    }
}
