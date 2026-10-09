using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Xunit;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Areas;

/// <summary>
/// AI without a provider (Monze:Ai:BaseUrl unset; the API key is only a
/// canary): an AI command posts the loading card and edits it into the
/// documented "not configured" answer without spending budget; the AI help
/// is a private menu.
/// </summary>
public sealed class AiAreaTests
{
    [DbFact]
    [Req("REQ-AI-001")]
    [Covers("cmd:ai", "cmd:translate", "msg:AiNotConfigured")]
    public async Task Ai_without_a_provider_edits_the_loading_card_into_not_configured()
    {
        await using var host = await E2EActions.StartAsync("ai_off");
        var mark = await E2EOracles.MarkAsync(host);

        var translate = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*ai translate xin chào mọi người");
        var help = await E2EActions.CommandAsync(host, GeneralId, MemberId, "*monze ai");

        await E2EOracles.AssertAsync(host, mark, ScenarioExpectation.Of((translate, ResponseKind.EditedReply), (help, ResponseKind.Ephemeral)));
        var loading = E2EActions.NewMessageAfter(host, translate, GeneralId);
        Assert.Contains("Đang xử lý yêu cầu", E2EContent.Visible(loading));
        var result = Assert.Single(host.Recorder.Since(translate.Sequence), action => action.Kind == SimActionKind.UpdateMessage);
        Assert.Equal(loading.MessageId, result.MessageId);
        Assert.Contains(MonzeMessages.AiNotConfigured, E2EContent.Visible(result));
        var stored = host.World.FindMessage(loading.MessageId)!;
        Assert.True(stored.Updated);
        Assert.Equal(result.ContentJson, stored.ContentJson);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM ai_usage;"));
        Assert.Contains(
            MonzeHelpCatalog.ForModule(MonzeCommandNames.Ai, MonzeCommandOptions.Default, isAdmin: false, canManageWelcome: false)[0].Name,
            E2EContent.Visible(E2EActions.NewMessageAfter(host, help, GeneralId)));
    }
}
