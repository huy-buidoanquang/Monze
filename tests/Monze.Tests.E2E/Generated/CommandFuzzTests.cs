using System.Diagnostics;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Generated;

/// <summary>
/// Generated commands (<see cref="CommandGrammar"/>) through the simulator
/// into the real host, each mirrored on MonzeApp over the in-memory twin
/// (<see cref="TwinMirror"/>). After every command:
/// <list type="bullet">
/// <item>the normalised business state of the host database equals the
/// twin's (what is compared and what is out of scope: <see cref="TwinMirror"/>);</item>
/// <item>the reply is the one the twin's outcome predicts: an ephemeral when
/// MonzeMessageBuilder.Card(outcome) is interactive, else a reply; an edited
/// reply for AI module commands; nothing for an unregistered command; a
/// public invitation or a reply for "meeting now"; a reply for avatar
/// commands; and, for every command served by MonzeApp, the visible text of
/// the final message equals the twin's card;</item>
/// <item>every command satisfies the E2E oracles (checked in batches of 250).</item>
/// </list>
/// 3,000 commands times MONZE_PBT_SCALE, seeded by MONZE_CAMPAIGN_SEED; a
/// failure names the seed and the step.
/// </summary>
public sealed class CommandFuzzTests(ITestOutputHelper output)
{
    private const int BaseCommands = 3_000;
    private const int Batch = 250;

    [DbFact]
    [Trait("Speed", "Slow")]
    [Req("REQ-CMD-001", "REQ-PBT-001", "REQ-SETUP-001", "REQ-WEL-001", "REQ-ROLE-001", "REQ-MTG-001", "REQ-AI-001", "REQ-HELP-001")]
    public async Task Generated_commands_match_the_twin_and_satisfy_the_oracles()
    {
        var seed = RunSeed.For("E2E-COMMAND-FUZZ");
        var random = RunSeed.RandomFor("E2E-COMMAND-FUZZ");
        var total = BaseCommands * CampaignEnvironment.PbtScale;
        var limits = new Dictionary<string, string?>
        {
            ["Monze:RateLimit:UserLimit"] = "1000000",
            ["Monze:RateLimit:AdminLimit"] = "1000000",
            ["Monze:RateLimit:MeetingLimit"] = "1000000",
            ["Monze:RateLimit:AiLimit"] = "1000000"
        };
        await using var host = await E2EActions.StartAsync("gen_fuzz", configuration: limits, seedDelegate: true);
        await using var connection = new NpgsqlConnection(host.Database.ConnectionString);
        await connection.OpenAsync();
        var twin = new TwinMirror(host.World, [(ClanId, AdminId)]);
        var grammar = new CommandGrammar(random);
        var productions = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        var mark = await E2EOracles.MarkAsync(host);
        var inputs = new List<InputExpectation>();
        for (var step = 0; step < total; step++)
        {
            var command = grammar.Next();
            productions[command.Production] = productions.GetValueOrDefault(command.Production) + 1;
            string Where() => $"seed={seed} step={step} production={command.Production} actor={command.ActorId} text=\"{command.Text}\"";
            try
            {
                inputs.Add(await RunStepAsync(host, connection, twin, command));
            }
            catch (Exception ex) when (ex is not XunitException)
            {
                throw new XunitException($"{Where()}: {ex}");
            }
            catch (XunitException ex)
            {
                throw new XunitException($"{Where()}: {ex.Message}");
            }

            if (inputs.Count == Batch || step == total - 1)
            {
                try
                {
                    await E2EOracles.AssertAsync(host, mark, new ScenarioExpectation { Inputs = inputs });
                }
                catch (XunitException ex)
                {
                    throw new XunitException($"seed={seed} batch ending at step {step}: {ex.Message}");
                }

                mark = await E2EOracles.MarkAsync(host);
                inputs.Clear();
            }
        }

        output.WriteLine($"seed={seed} commands={total} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");
        var finalState = (await TwinMirror.RealStateAsync(connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        output.WriteLine($"final compared rows: {string.Join(", ", finalState.GroupBy(static line => line.Split(' ')[0]).Select(static group => $"{group.Key}={group.Count()}"))}");
        foreach (var (production, count) in productions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {production}: {count}");
        }

        await E2EOracles.AssertInvariantsAsync(host);
    }

    private static async Task<InputExpectation> RunStepAsync(MonzeE2EHost host, NpgsqlConnection connection, TwinMirror twin, FuzzCommand command)
    {
        var push = await host.Inbound.SayAsync(command.ClanId, command.ChannelId, command.ActorId, command.Text, mentions: command.Mentions);
        Assert.Equal(1, push.DeliveredSessions);
        if (command.Route == FuzzRoute.Unregistered)
        {
            await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(host.Recorder.Since(push.Sequence), static action => action.IsOutbound && action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral);
            return new InputExpectation(push, ResponseKind.None);
        }

        // Monze writes business state before it answers, so the first answer
        // (and, for AI, the edit of the loading card) marks the end of the
        // command; the batch oracle later checks command_inbox completion.
        var first = await host.Recorder.WaitForAsync(
            action => action.ChannelId == command.ChannelId && action.Kind is SimActionKind.SendMessage or SimActionKind.SendEphemeral,
            E2EOracles.Timeout,
            push.Sequence);
        var final = command.Route == FuzzRoute.Ai
            ? await host.Recorder.WaitForAsync(action => action.Kind == SimActionKind.UpdateMessage && action.MessageId == first.MessageId, E2EOracles.Timeout, first.Sequence)
            : first;

        if (command.Route == FuzzRoute.Avatar)
        {
            return new InputExpectation(push, ResponseKind.Reply);
        }

        var outcome = await twin.RunAsync(command);
        Assert.Equal(twin.State(), await TwinMirror.RealStateAsync(connection));
        if (command.IsMeetingNow)
        {
            return new InputExpectation(push, first.Kind == SimActionKind.SendMessage && first.ReplyToMessageId is null ? ResponseKind.Public : ResponseKind.Reply);
        }

        var card = MonzeMessageBuilder.Card(outcome, MonzeCommandOptions.Default);
        Assert.Equal(E2EContent.Visible(card), E2EContent.Visible(final));
        var kind = command.Route == FuzzRoute.Ai
            ? ResponseKind.EditedReply
            : E2EContent.IsInteractive(card) ? ResponseKind.Ephemeral : ResponseKind.Reply;
        return new InputExpectation(push, kind);
    }
}
