using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Infrastructure.Caching;
using Monze.Simulator;
using Monze.Testing.Twin;
using Npgsql;

namespace Monze.Tests.E2E.Generated;

/// <summary>
/// MonzeApp on the in-memory twin (tests/Monze.Testing/Twin), wired like
/// tests/Monze.Tests.Property/Application/G29MonzeAppTwinProperties.cs and
/// seeded from the simulator world: the clans with their owners as
/// discovery registers them, the delegate, every member with join time,
/// bot flag and roles, and the clan roles. No AI provider (the host runs
/// with Monze:Ai:BaseUrl unset) and the system clock (dates are validated
/// against "now" on both sides).
/// <para>
/// Compared business state, normalised to one line per row: clan_settings
/// (welcome on/off, welcome text, whether an embed is set, role automation,
/// version; rows still at their defaults are skipped because the twin
/// creates them lazily), clan_admin, meeting_schedule (id, clan, channel,
/// requester, title, kind, when text, time zone, next run, status) and
/// role_rule (clan, role, kind, enabled, condition, version). Out of scope:
/// role grants and member roles (the host's role scan worker changes them
/// in the background), meeting sessions and voice claims ("meeting now"
/// depends on live voice occupancy), AI usage, inboxes, the outbox, user
/// profiles, timestamps other than next_run_at, and avatar commands (served
/// by MonzeBot, not MonzeApp).
/// </para>
/// </summary>
internal sealed partial class TwinMirror
{
    private readonly InMemoryMonzeState _state = new(TimeProvider.System);
    private readonly TwinRoleGateway _gateway = new();
    private readonly MonzeApp _app;

    public TwinMirror(SimWorld world, IReadOnlyList<(long ClanId, long UserId)> delegates)
    {
        foreach (var clan in world.ClansOf(world.Bot.Id))
        {
            _state.AddClan(clan.Id, clan.OwnerId);

            // Discovery inserts a default clan_settings row for every clan
            // (PostgresClanRegistryRepository.ApplyClanScanAsync); the twin
            // creates its row on first write. Enabling role automation on a
            // missing row creates exactly that default row (version 1).
            ((IRoleRepository)_state).SetRoleAutomationEnabledAsync(clan.Id, clan.OwnerId, true, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var role in world.RolesOf(clan.Id))
            {
                _gateway.AddRole(clan.Id, role.Id, role.Title, role.Active);
            }

            foreach (var member in world.MembersOf(clan.Id))
            {
                _gateway.AddMember(clan.Id, member.UserId, member.JoinedAt, world.FindUser(member.UserId)?.IsBot ?? false, member.RoleIds.ToList());
            }
        }

        foreach (var (clanId, userId) in delegates)
        {
            _state.AddDelegate(clanId, userId);
        }

        _app = new MonzeApp(_state, _state, _state, _state, _state, _state, _state, new MemoryWelcomeDraftStore(), ai: null, timeProvider: TimeProvider.System);
        _app.AttachRoleGateway(_gateway);
    }

    /// <summary>Runs the command through the same MonzeApp entry point MonzeBot would call.</summary>
    public async Task<CommandOutcome> RunAsync(FuzzCommand command)
    {
        var ct = CancellationToken.None;
        switch (command.Route)
        {
            case FuzzRoute.Monze:
            case FuzzRoute.Ai:
                return await _app.HandleMonzeAsync(command.ClanId, command.ChannelId, command.ActorId, new CommandArguments(command.Args.ToArray()), ct, command.MentionedUserId);
            case FuzzRoute.Meeting when command.Args.Count > 0 && command.Args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase):
                return await _app.HandleMonzeAsync(command.ClanId, command.ChannelId, command.ActorId, new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Meeting }), ct);
            case FuzzRoute.Meeting:
                return await _app.HandleMeetingAsync(command.ClanId, command.ChannelId, command.ActorId, command.Args.ToArray(), static _ => Task.FromResult<MeetingVoiceCandidate?>(null), ct);
            case FuzzRoute.Summary when command.Args.Count > 0 && command.Args[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase):
                return await _app.HandleMonzeAsync(command.ClanId, command.ChannelId, command.ActorId, new CommandArguments(new[] { MonzeCommandNames.Help, MonzeCommandNames.Summary }), ct);
            case FuzzRoute.Summary:
                return await _app.HandleSummaryAsync(command.ClanId, command.ActorId, command.Args.ToArray(), ct);
            default:
                throw new InvalidOperationException($"Route {command.Route} is not served by MonzeApp.");
        }
    }

    /// <summary>The twin's business state as canonical lines.</summary>
    public string State()
    {
        var lines = new List<string>();
        foreach (var line in _state.Snapshot().Split('\n'))
        {
            if (SettingsLine().Match(line) is { Success: true } settings)
            {
                AddSettings(lines, long.Parse(settings.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(settings.Groups[2].Value, CultureInfo.InvariantCulture), settings.Groups[3].Value == "true", Unquote(settings.Groups[4].Value), Unquote(settings.Groups[5].Value) is not null, settings.Groups[6].Value == "true");
            }
            else if (AdminLine().Match(line) is { Success: true } admin)
            {
                lines.Add($"admin clan={admin.Groups[1].Value} user={admin.Groups[2].Value}");
            }
            else if (ScheduleLine().Match(line) is { Success: true } schedule)
            {
                lines.Add(Schedule(schedule.Groups[1].Value, schedule.Groups[2].Value, schedule.Groups[3].Value, schedule.Groups[4].Value, Unquote(schedule.Groups[5].Value), Unquote(schedule.Groups[6].Value), Unquote(schedule.Groups[7].Value), Unquote(schedule.Groups[8].Value), schedule.Groups[9].Value, Unquote(schedule.Groups[10].Value)));
            }
            else if (RuleLine().Match(line) is { Success: true } rule)
            {
                lines.Add(Rule(rule.Groups[1].Value, rule.Groups[2].Value, Unquote(rule.Groups[3].Value), rule.Groups[4].Value == "true", Unquote(rule.Groups[5].Value), rule.Groups[6].Value));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>The host database's business state as the same canonical lines.</summary>
    public static async Task<string> RealStateAsync(NpgsqlConnection connection)
    {
        var lines = new List<string>();
        await using var command = new NpgsqlCommand("""
            SELECT clan_id, version, welcome_enabled, welcome_text, welcome_embed IS NOT NULL, role_enabled FROM clan_settings;
            SELECT clan_id, user_id FROM clan_admin;
            SELECT id, clan_id, channel_id, requester_id, title, kind, when_text, timezone, next_run_at, status FROM meeting_schedule;
            SELECT clan_id, role_id, rule_kind, enabled, condition_value, version FROM role_rule;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            AddSettings(lines, reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5));
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            lines.Add($"admin clan={reader.GetInt64(0)} user={reader.GetInt64(1)}");
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(Schedule(
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                reader.GetInt64(3).ToString(CultureInfo.InvariantCulture),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                reader.GetString(9)));
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(Rule(
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5).ToString(CultureInfo.InvariantCulture)));
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static void AddSettings(List<string> lines, long clanId, long version, bool welcome, string? text, bool embed, bool role)
    {
        if (version == 1 && !welcome && text is null && !embed && role)
        {
            return;
        }

        lines.Add($"settings clan={clanId} version={version} welcome={welcome} text={JsonSerializer.Serialize(text)} embed={embed} role={role}");
    }

    private static string Schedule(string id, string clan, string channel, string requester, string? title, string? kind, string? when, string? zone, string next, string? status)
        => $"schedule id={id} clan={clan} channel={channel} requester={requester} title={JsonSerializer.Serialize(title)} kind={kind} when={JsonSerializer.Serialize(when)} zone={zone} next={next} status={status}";

    private static string Rule(string clan, string role, string? kind, bool enabled, string? condition, string version)
        => $"rule clan={clan} role={role} kind={kind} enabled={enabled} condition={JsonSerializer.Serialize(condition)} version={version}";

    private static string? Unquote(string value) => value == "null" ? null : JsonSerializer.Deserialize<string>(value);

    private const string Quoted = "(null|\"(?:[^\"\\\\]|\\\\.)*\")";

    [GeneratedRegex("^clan_settings clan=(\\d+) version=(\\d+) welcome_enabled=(true|false) welcome_text=" + Quoted + " welcome_embed=" + Quoted + " role_enabled=(true|false)$")]
    private static partial Regex SettingsLine();

    [GeneratedRegex("^clan_admin clan=(\\d+) user=(\\d+)$")]
    private static partial Regex AdminLine();

    [GeneratedRegex("^meeting_schedule id=(\\d+) clan=(\\d+) channel=(\\d+) requester=(\\d+) title=" + Quoted + " kind=" + Quoted + " when_text=" + Quoted + " timezone=" + Quoted + " next_run_at=(\\S+) status=" + Quoted + " ")]
    private static partial Regex ScheduleLine();

    [GeneratedRegex("^role_rule clan=(\\d+) role=(\\d+) kind=" + Quoted + " enabled=(true|false) condition_value=" + Quoted + " version=(\\d+) ")]
    private static partial Regex RuleLine();
}
