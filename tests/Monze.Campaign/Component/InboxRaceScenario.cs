using System.Diagnostics;
using Monze.Infrastructure.Persistence;
using Monze.Testing.Harness;

namespace Monze.Campaign.Component;

/// <summary>
/// C-INBOX: 2,000 concurrent claims of one command (and one interaction)
/// key. Exactly one lease may win per round, the winner completes, and a
/// later delivery of the same key is a duplicate.
/// </summary>
public static class InboxRaceScenario
{
    private const int Contenders = 2_000;

    public static async Task<IReadOnlyList<CampaignArtifact>> RunAsync(ComponentContext context)
    {
        var rounds = context.Full ? 20 : 5;
        var artifact = new CampaignArtifact("component", "C-INBOX", $"Inbox race: {rounds} vòng × {Contenders:N0} claim cùng key");
        var (database, dataSource) = await context.CreateDatabaseAsync("c_inbox");
        await using (database)
        await using (dataSource)
        {
            var commands = new PostgresCommandInboxRepository(dataSource);
            var interactions = new PostgresInteractionInboxRepository(dataSource);
            var claims = new LatencyHistogram();
            var badRounds = new List<string>();
            for (var round = 1; round <= rounds; round++)
            {
                var messageId = 9_000 + round;
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var commandRace = Enumerable.Range(0, Contenders).Select(_ => Timed(start.Task, claims, () => commands.TryClaimAsync(1, 10, messageId, CancellationToken.None))).ToArray();
                var interactionRace = Enumerable.Range(0, Contenders).Select(_ => Timed(start.Task, claims, () => interactions.TryClaimAsync(1, 10, messageId, "monze_help_close", CancellationToken.None))).ToArray();
                start.SetResult();
                var commandWinners = (await Task.WhenAll(commandRace)).Where(static lease => lease is not null).ToList();
                var interactionWinners = (await Task.WhenAll(interactionRace)).Where(static lease => lease is not null).ToList();

                var completed = commandWinners.Count == 1 && await commands.CompleteAsync(commandWinners[0]!.Value, CancellationToken.None);
                var interactionCompleted = interactionWinners.Count == 1 && await interactions.CompleteAsync(interactionWinners[0]!.Value, CancellationToken.None);
                var redelivered = await commands.TryClaimAsync(1, 10, messageId, CancellationToken.None);
                var interactionRedelivered = await interactions.TryClaimAsync(1, 10, messageId, "monze_help_close", CancellationToken.None);
                if (commandWinners.Count != 1 || interactionWinners.Count != 1 || !completed || !interactionCompleted || redelivered is not null || interactionRedelivered is not null)
                {
                    badRounds.Add($"vòng {round}: {commandWinners.Count}/{interactionWinners.Count} lease");
                }
            }

            var violations = await MonzeInvariants.CheckAsync(dataSource, TimeSpan.FromMinutes(1));
            artifact.Metric("rounds", rounds)
                .Metric("claims", claims.Count)
                .Latency("claim", claims)
                .Invariant("one-lease", "mỗi vòng đúng một lease cho command và interaction; giao lại là trùng", badRounds.Count == 0 ? $"{rounds} vòng đúng" : string.Join("; ", badRounds), badRounds.Count == 0)
                .DbInvariants(violations)
                .Note("Độ trễ claim gồm thời gian chờ pool 64 kết nối khi 4.000 claim cùng lúc.");
        }

        return [artifact];
    }

    private static async Task<T> Timed<T>(Task start, LatencyHistogram histogram, Func<Task<T>> operation)
    {
        await start;
        var started = Stopwatch.GetTimestamp();
        var result = await operation();
        histogram.Record(Stopwatch.GetElapsedTime(started));
        return result;
    }
}
