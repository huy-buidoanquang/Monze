using System.Text.RegularExpressions;
using CsCheck;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Infrastructure.Caching;
using Monze.Testing;
using Monze.Testing.Twin;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Application;

/// <summary>
/// G29: MonzeApp on the in-memory twin, over random sequences of 20 commands
/// across every module, five actors (owner, delegate, member, outsider, owner
/// of another clan) and three clans (one inactive). For every command:
/// no exception; an actor without the required permission gets the
/// documented denial and changes nothing; read-only commands change nothing;
/// rows of other clans never change; no raw id reaches the reply.
/// </summary>
public sealed partial class G29MonzeAppTwinProperties
{
    // Snowflake-sized ids so a leaked id is visible in replies.
    private const long ClanA = 2_104_288_434_238_525_000;
    private const long ClanB = 2_104_288_434_238_525_001;
    private const long ClanInactive = 2_104_288_434_238_525_002;
    private const long Owner = 2_104_288_434_238_525_100;
    private const long Delegate = 2_104_288_434_238_525_101;
    private const long Member = 2_104_288_434_238_525_102;
    private const long Outsider = 2_104_288_434_238_525_103;
    private const long OwnerB = 2_104_288_434_238_525_104;
    private const long Channel = 2_104_288_434_238_525_200;
    private const long MemberRole = 2_104_288_434_238_525_300;

    private static readonly long[] Clans = [ClanA, ClanB, ClanInactive];
    private static readonly long[] Actors = [Owner, Delegate, Member, Outsider, OwnerB];
    private static readonly string[] ActorNames = ["owner", "delegate", "member", "outsider", "owner-b"];
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 1, 0, 0, TimeSpan.Zero);

    private static readonly Command[] Catalogue =
    [
        new("help", Need.None, false, ["help"]),
        new("help-welcome", Need.None, false, ["help", "welcome"]),
        new("empty", Need.None, false, []),
        new("unknown", Need.None, false, ["xyz", "abc"]),
        new("setup-add-member", Need.Owner, true, ["setup", "admin", "add"], Mention: Member),
        new("setup-add-outsider", Need.Owner, true, ["setup", "admin", "add"], Mention: Outsider),
        new("setup-remove-delegate", Need.Owner, true, ["setup", "admin", "remove"], Mention: Delegate),
        new("setup-add-self", Need.Owner, false, ["setup", "admin", "add"], Mention: -1),
        new("welcome-on", Need.Admin, true, ["welcome", "on"]),
        new("welcome-off-text", Need.Admin, true, ["welcome", "off", "Xin", "chào", "{user}"]),
        new("welcome-message", Need.Admin, true, ["welcome", "message", "Chào", "{user}", "tới", "{channel:general}"]),
        new("welcome-message-remove", Need.Admin, true, ["welcome", "message", "remove"]),
        new("welcome-setup-remove", Need.Admin, true, ["welcome", "setup", "remove"]),
        new("welcome-setup", Need.None, false, ["welcome", "setup"]),
        new("welcome-preview", Need.None, false, ["welcome", "preview"]),
        new("role-on", Need.Admin, true, ["role", "on"]),
        new("role-off", Need.Admin, true, ["role", "off"]),
        new("role-join", Need.Admin, true, ["role", "join", "Member"]),
        new("role-tenure", Need.Admin, true, ["role", "tenure", "Member", "7"]),
        new("role-join-remove", Need.Admin, true, ["role", "join", "remove", "Member"]),
        new("role-help", Need.None, false, ["role"]),
        new("ai-translate", Need.None, true, ["ai", "translate", "xin", "chào"]),
        new("ai-help", Need.None, false, ["ai"]),
        new("meeting-list", Need.None, false, ["meeting"], Meeting: true),
        new("meeting-schedule", Need.None, true, ["meeting", "Sync", "15/10/2026", "10:00", "weekly"], Meeting: true),
        new("meeting-cancel", Need.None, true, ["meeting", "cancel", "1"], Meeting: true),
        new("meeting-now", Need.None, true, ["meeting", "now"], Meeting: true),
        new("summary", Need.Admin, false, ["summary", "1"], Summary: true)
    ];

    private static readonly Gen<(int Actor, int Clan, int Command)> Step =
        Gen.Select(Gen.Int[0, Actors.Length - 1], Gen.Frequency((6, Gen.Const(0)), (2, Gen.Const(1)), (1, Gen.Const(2))), Gen.Int[0, Catalogue.Length - 1]);

    [Fact]
    [Req("REQ-SETUP-001", "REQ-WEL-001", "REQ-ROLE-001", "REQ-AI-001", "REQ-MTG-001", "REQ-HELP-001", "REQ-CMD-001")]
    [Covers("port:IAuthorizationRepository.SetDelegateAsync")]
    [Covers("msg:OwnerOnly")]
    [Covers("msg:AdminOnly")]
    [Covers("msg:WelcomeAdminOnly")]
    public void Commands_respect_the_permission_matrix_on_the_twin()
    {
        PropertyRun.Run(
            "G29",
            Step.Array[20, 20],
            static steps =>
            {
                var time = new ManualTimeProvider(Start);
                var state = new InMemoryMonzeState(time);
                var gateway = new TwinRoleGateway();
                state.AddClan(ClanA, Owner);
                state.AddClan(ClanB, OwnerB);
                state.AddClan(ClanInactive, Owner, active: false);
                state.AddDelegate(ClanA, Delegate);
                foreach (var clan in Clans)
                {
                    gateway.AddRole(clan, MemberRole, "Member");
                }

                foreach (var user in new[] { Owner, Delegate, Member })
                {
                    gateway.AddMember(ClanA, user, Start.AddDays(-60));
                }

                gateway.AddMember(ClanB, OwnerB, Start.AddDays(-60));
                var app = new MonzeApp(
                    state, state, state, state, state, state, state,
                    new MemoryWelcomeDraftStore(),
                    new TwinAiProvider(),
                    timeProvider: time);
                app.AttachRoleGateway(gateway);

                for (var i = 0; i < steps.Length; i++)
                {
                    var (actorIndex, clanIndex, commandIndex) = steps[i];
                    var actor = Actors[actorIndex];
                    var clan = Clans[clanIndex];
                    var command = Catalogue[commandIndex];
                    var tags = new Dictionary<string, string>
                    {
                        ["actor"] = ActorNames[actorIndex],
                        ["command"] = command.Name,
                        ["clan"] = clanIndex switch { 0 => "a", 1 => "b", _ => "inactive" }
                    };
                    var input = $"step {i}: {ActorNames[actorIndex]} in clan {tags["clan"]} runs {command.Name}";
                    var isOwner = state.IsOwnerAsync(clan, actor, CancellationToken.None).GetAwaiter().GetResult();
                    var isAdmin = state.IsAdminAsync(clan, actor, CancellationToken.None).GetAwaiter().GetResult();
                    var before = state.Snapshot() + gateway.Snapshot();
                    CommandOutcome outcome;
                    try
                    {
                        outcome = Run(app, command, clan, actor);
                    }
                    catch (Exception ex)
                    {
                        return PropertyResult.Fail(input, tags, $"{ex.GetType().Name}: {ex.Message}");
                    }

                    var after = state.Snapshot() + gateway.Snapshot();
                    var denied = command.Need switch
                    {
                        Need.Owner => !isOwner,
                        Need.Admin => !isAdmin,
                        _ => false
                    };
                    if (denied)
                    {
                        var expected = command.Need == Need.Owner
                            ? new[] { MonzeMessages.OwnerOnly }
                            : [MonzeMessages.AdminOnly, MonzeMessages.WelcomeAdminOnly, MonzeMessages.SummaryAdminOnly];
                        if (!expected.Contains(outcome.Text) && !(outcome.Fields ?? []).Any(field => expected.Contains(field.Value)))
                        {
                            return PropertyResult.Fail(input, tags, $"denied actor got '{outcome.Text}'");
                        }
                    }

                    if ((denied || !command.Mutates) && before != after)
                    {
                        return PropertyResult.Fail(input, tags, $"state changed:{Environment.NewLine}{Difference(before, after)}");
                    }

                    foreach (var other in Clans.Where(other => other != clan))
                    {
                        if (ClanLines(before, other) != ClanLines(after, other))
                        {
                            return PropertyResult.Fail(input, tags, $"rows of another clan changed:{Environment.NewLine}{Difference(before, after)}");
                        }
                    }

                    var visible = outcome.Text + "\n" + string.Join("\n", (outcome.Fields ?? []).Select(static field => field.Name + " " + field.Value));
                    if (RawId().IsMatch(visible))
                    {
                        return PropertyResult.Fail(input, tags, $"raw id in reply: {visible}");
                    }

                    time.Advance(TimeSpan.FromSeconds(7));
                }

                var last = steps[^1];
                return PropertyResult.Pass(
                    $"{steps.Length} commands",
                    new Dictionary<string, string>
                    {
                        ["actor"] = ActorNames[last.Actor],
                        ["command"] = Catalogue[last.Command].Name,
                        ["clan"] = last.Clan switch { 0 => "a", 1 => "b", _ => "inactive" }
                    });
            },
            iterations: 5_000,
            declare: static ledger => ledger
                .Dimension("actor", ActorNames)
                .Dimension("command", Catalogue.Select(static command => command.Name).ToArray())
                .Dimension("clan", "a", "b", "inactive"));
    }

    private static CommandOutcome Run(MonzeApp app, Command command, long clan, long actor)
    {
        var mention = command.Mention == -1 ? actor : command.Mention;
        if (command.Meeting)
        {
            return app.HandleMeetingAsync(clan, Channel, actor, command.Args.Skip(1).ToArray(), static _ => Task.FromResult<MeetingVoiceCandidate?>(null), CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        if (command.Summary)
        {
            return app.HandleSummaryAsync(clan, actor, command.Args.Skip(1).ToArray(), CancellationToken.None).GetAwaiter().GetResult();
        }

        return app.HandleMonzeAsync(clan, Channel, actor, command.Args, CancellationToken.None, mention).GetAwaiter().GetResult();
    }

    private static string ClanLines(string snapshot, long clan)
        => string.Join("\n", snapshot.Split('\n').Where(line => line.Contains($"clan={clan} ", StringComparison.Ordinal) || line.EndsWith($"clan={clan}", StringComparison.Ordinal)));

    private static string Difference(string before, string after)
    {
        var old = before.Split('\n').ToHashSet(StringComparer.Ordinal);
        var current = after.Split('\n').ToHashSet(StringComparer.Ordinal);
        return string.Join(Environment.NewLine, old.Except(current).Select(static line => "- " + line).Concat(current.Except(old).Select(static line => "+ " + line)).Take(6));
    }

    [GeneratedRegex("[0-9]{15,}")]
    private static partial Regex RawId();

    private enum Need
    {
        None,
        Admin,
        Owner
    }

    private sealed record Command(string Name, Need Need, bool Mutates, string[] Args, long? Mention = null, bool Meeting = false, bool Summary = false);
}
