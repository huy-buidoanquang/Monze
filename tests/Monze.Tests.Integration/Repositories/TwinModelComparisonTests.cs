using System.Globalization;
using Monze.Application;
using Monze.Domain;
using Monze.Infrastructure.Persistence;
using Monze.Testing;
using Monze.Testing.Twin;
using Xunit;

namespace Monze.Tests.Repositories;

/// <summary>
/// Runs the same random sequences of repository operations against
/// PostgreSQL and against the in-memory twin and compares every result, so the
/// twin used by property and E2E tests is known to behave like the database
/// (authorization, welcome compare-and-set, role rules, schedules, AI budget,
/// welcome claims). Schedule ids are matched by creation order.
/// </summary>
public sealed class TwinModelComparisonTests
{
    private const long Owner = 2_104_288_434_238_800_100;
    private const long Candidate = 2_104_288_434_238_800_101;
    private const long Stranger = 2_104_288_434_238_800_102;
    private static readonly long[] Actors = [Owner, Candidate, Stranger];
    private static readonly string[] Operations =
    [
        "is-owner", "is-admin", "delegate-add", "delegate-remove",
        "welcome-set", "welcome-message", "welcome-message-remove", "welcome-config", "welcome-embed-remove", "welcome-get",
        "role-enable", "role-rule", "role-rule-remove", "role-list", "role-enabled",
        "schedule-create", "schedule-list", "schedule-cancel",
        "ai-consume", "welcome-claim", "welcome-release"
    ];

    [DbFact]
    [Req("REQ-SETUP-001", "REQ-WEL-001", "REQ-ROLE-001", "REQ-MTG-002", "REQ-AI-001", "REQ-WEL-003")]
    [Covers("port:IWelcomeRepository.SetWelcomeMessageAsync")]
    [Covers("port:IWelcomeRepository.RemoveWelcomeMessageAsync")]
    [Covers("port:IWelcomeRepository.RemoveWelcomeEmbedAsync")]
    [Covers("port:IWelcomeRepository.ReleaseWelcomeClaimAsync")]
    [Covers("port:IRoleRepository.SetRoleAutomationEnabledAsync")]
    [Covers("port:IRoleRepository.RemoveRoleRuleAsync")]
    [Covers("port:IRoleRepository.IsRoleAutomationEnabledAsync")]
    public async Task Database_and_twin_agree_on_every_operation()
    {
        await using var db = await AuditedDatabase.CreateAsync("twin_model");
        var sequences = 2_000 * CampaignEnvironment.PbtScale;
        using var ledger = CaseLedger.Open("integration", "twin-model")
            .Dimension("first", Operations)
            .Dimension("actor", "owner", "candidate", "stranger");
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var random = RunSeed.RandomFor("twin-model");
        var seeds = Enumerable.Range(0, sequences).Select(_ => random.Next()).ToArray();
        await Parallel.ForEachAsync(Enumerable.Range(0, sequences), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (sequence, _) =>
        {
            var steps = Steps(new Random(seeds[sequence]));
            var clan = 2_104_288_434_238_800_000 - sequence;
            var outcome = await CompareAsync(db, clan, steps);
            var tags = new Dictionary<string, string> { ["first"] = steps[0].Operation, ["actor"] = ActorName(steps[0].Actor) };
            var input = $"sequence {sequence}: {string.Join(" ", steps.Select(static step => step.Operation))}";
            if (outcome is null)
            {
                ledger.Pass(input, tags);
            }
            else
            {
                ledger.Fail(input, outcome, tags);
                failures.Enqueue($"{input}{Environment.NewLine}  {outcome}");
            }
        });

        Assert.True(failures.IsEmpty, $"{failures.Count} sequences differ:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(5))}");
        var (required, covered, missing) = ledger.PairwiseCoverage();
        Assert.True(covered == required, $"pairwise {covered}/{required}: {string.Join(", ", missing)}");
        await db.AssertTransitionsAsync();
    }

    private static async Task<string?> CompareAsync(AuditedDatabase db, long clan, IReadOnlyList<Step> steps)
    {
        await db.ExecuteAsync("INSERT INTO clan_registry(clan_id, owner_id) VALUES (@clan, @owner);", ("clan", clan), ("owner", Owner));
        var twin = new InMemoryMonzeState();
        twin.AddClan(clan, Owner);
        var authorization = new PostgresAuthorizationRepository(db.DataSource);
        var welcome = new PostgresWelcomeRepository(db.DataSource);
        var roles = new PostgresRoleRepository(db.DataSource);
        var scheduling = new PostgresSchedulingRepository(db.DataSource);
        var ai = new PostgresAiUsageRepository(db.DataSource);
        var databaseSchedules = new List<long>();
        var twinSchedules = new List<long>();
        var ct = CancellationToken.None;
        for (var i = 0; i < steps.Count; i++)
        {
            var (operation, actor, role, number, text) = steps[i];
            var kind = role % 2 == 0 ? RoleRuleKind.OnJoin : RoleRuleKind.Tenure;
            (string Database, string Twin) results = operation switch
            {
                "is-owner" => (Show(await authorization.IsOwnerAsync(clan, actor, ct)), Show(await twin.IsOwnerAsync(clan, actor, ct))),
                "is-admin" => (Show(await authorization.IsAdminAsync(clan, actor, ct)), Show(await twin.IsAdminAsync(clan, actor, ct))),
                "delegate-add" => (Show(await authorization.SetDelegateAsync(clan, actor, Candidate, true, ct)), Show(await twin.SetDelegateAsync(clan, actor, Candidate, true, ct))),
                "delegate-remove" => (Show(await authorization.SetDelegateAsync(clan, actor, Candidate, false, ct)), Show(await twin.SetDelegateAsync(clan, actor, Candidate, false, ct))),
                "welcome-set" => (Show(await welcome.SetWelcomeAsync(clan, actor, number % 2 == 0, text, ct)), Show(await twin.SetWelcomeAsync(clan, actor, number % 2 == 0, text, ct))),
                "welcome-message" => (Show(await welcome.SetWelcomeMessageAsync(clan, actor, text ?? "Chào", ct)), Show(await twin.SetWelcomeMessageAsync(clan, actor, text ?? "Chào", ct))),
                "welcome-message-remove" => (Show(await welcome.RemoveWelcomeMessageAsync(clan, actor, ct)), Show(await twin.RemoveWelcomeMessageAsync(clan, actor, ct))),
                "welcome-config" => (
                    Show(await welcome.SetWelcomeConfigurationAsync(clan, actor, true, text, new WelcomeEmbedSettings(Title: text, Color: "#5865F2"), ct, number % 3 == 0 ? null : number % 4)),
                    Show(await twin.SetWelcomeConfigurationAsync(clan, actor, true, text, new WelcomeEmbedSettings(Title: text, Color: "#5865F2"), ct, number % 3 == 0 ? null : number % 4))),
                "welcome-embed-remove" => (Show(await welcome.RemoveWelcomeEmbedAsync(clan, actor, ct)), Show(await twin.RemoveWelcomeEmbedAsync(clan, actor, ct))),
                "welcome-get" => (Show(await welcome.GetWelcomeAsync(clan, ct)), Show(await twin.GetWelcomeAsync(clan, ct))),
                "role-enable" => (Show(await roles.SetRoleAutomationEnabledAsync(clan, actor, number % 2 == 0, ct)), Show(await twin.SetRoleAutomationEnabledAsync(clan, actor, number % 2 == 0, ct))),
                "role-rule" => (
                    Show(await roles.SetRoleRuleAsync(clan, actor, role, kind, kind == RoleRuleKind.Tenure ? (number % 60).ToString(CultureInfo.InvariantCulture) : null, ct)),
                    Show(await twin.SetRoleRuleAsync(clan, actor, role, kind, kind == RoleRuleKind.Tenure ? (number % 60).ToString(CultureInfo.InvariantCulture) : null, ct))),
                "role-rule-remove" => (Show(await roles.RemoveRoleRuleAsync(clan, actor, role, kind, ct)), Show(await twin.RemoveRoleRuleAsync(clan, actor, role, kind, ct))),
                "role-list" => (Rules(await roles.ListEnabledRoleRulesAsync(clan, ct)), Rules(await twin.ListEnabledRoleRulesAsync(clan, ct))),
                "role-enabled" => (Show(await roles.IsRoleAutomationEnabledAsync(clan, ct)), Show(await twin.IsRoleAutomationEnabledAsync(clan, ct))),
                "schedule-create" => await CreateScheduleAsync(),
                "schedule-list" => (
                    Schedules(await scheduling.ListMeetingSchedulesAsync(clan, 10, actor, 1 + (number % 5), ct), databaseSchedules),
                    Schedules(await twin.ListMeetingSchedulesAsync(clan, 10, actor, 1 + (number % 5), ct), twinSchedules)),
                "schedule-cancel" when databaseSchedules.Count > 0 => (
                    Show(await scheduling.CancelMeetingScheduleAsync(clan, 10, actor, databaseSchedules[number % databaseSchedules.Count], ct)),
                    Show(await twin.CancelMeetingScheduleAsync(clan, 10, actor, twinSchedules[number % twinSchedules.Count], ct))),
                "ai-consume" => (Show(await ai.ConsumeAiAsync(clan, actor, 1 + (number % 9), 20, ct)), Show(await twin.ConsumeAiAsync(clan, actor, 1 + (number % 9), 20, ct))),
                "welcome-claim" => (Show(await welcome.TryClaimWelcomeAsync(clan, actor, ct)), Show(await twin.TryClaimWelcomeAsync(clan, actor, ct))),
                "welcome-release" => await ReleaseAsync(),
                _ => ("skip", "skip")
            };
            if (results.Database != results.Twin)
            {
                return $"step {i} {operation} by {ActorName(actor)}: database {results.Database}, twin {results.Twin}";
            }

            async Task<(string, string)> CreateScheduleAsync()
            {
                var next = DateTimeOffset.UtcNow.AddHours(1 + number);
                databaseSchedules.Add(await scheduling.CreateMeetingScheduleAsync(clan, 10, actor, $"Lịch {number}", MeetingScheduleKind.Weekly, "1 09:00", "Asia/Ho_Chi_Minh", next, ct));
                twinSchedules.Add(await twin.CreateMeetingScheduleAsync(clan, 10, actor, $"Lịch {number}", MeetingScheduleKind.Weekly, "1 09:00", "Asia/Ho_Chi_Minh", next, ct));
                return ("created", "created");
            }

            async Task<(string, string)> ReleaseAsync()
            {
                await welcome.ReleaseWelcomeClaimAsync(clan, actor, ct);
                await twin.ReleaseWelcomeClaimAsync(clan, actor, ct);
                return ("released", "released");
            }
        }

        return null;
    }

    private static List<Step> Steps(Random random)
        => Enumerable.Range(0, 25)
            .Select(_ => new Step(
                Operations[random.Next(Operations.Length)],
                Actors[random.Next(Actors.Length)],
                7 + random.Next(3),
                random.Next(0, 1_000),
                random.Next(4) switch { 0 => null, 1 => "Chào {user}", 2 => "  ", _ => "Xin chào" }))
            .ToList();

    private static string Show<T>(T value) => value switch
    {
        null => "null",
        WelcomeSettings settings => $"{settings.Enabled}|{settings.Text ?? "null"}|{settings.Version}|{settings.Embed}",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
    };

    private static string Rules(IReadOnlyList<AutoRoleRule> rules)
        => string.Join(";", rules.Select(static rule => $"{rule.RoleId}:{rule.Kind}:{rule.ConditionValue}:{rule.Version}").Order(StringComparer.Ordinal));

    private static string Schedules(IReadOnlyList<MeetingScheduleSummary> schedules, List<long> ids)
        => string.Join(";", schedules.Select(schedule => $"#{ids.IndexOf(schedule.Id)}:{schedule.Name}:{schedule.Kind}:{schedule.NextRunAt.UtcTicks / TimeSpan.TicksPerSecond}"));

    private static string ActorName(long actor) => actor == Owner ? "owner" : actor == Candidate ? "candidate" : "stranger";

    private sealed record Step(string Operation, long Actor, long Role, int Number, string? Text);
}
