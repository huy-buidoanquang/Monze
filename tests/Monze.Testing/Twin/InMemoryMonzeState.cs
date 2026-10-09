using Monze.Application;

namespace Monze.Testing.Twin;

/// <summary>
/// In-memory twin of Monze's PostgreSQL repositories. Each port copies the
/// semantics of the SQL in its Postgres repository: authorization checked
/// inside the statement, version increments, the value returned when the
/// actor is not authorized, ON CONFLICT behavior, inactive clans and clan
/// scoping. "now()" is read from the injected <see cref="TimeProvider"/> and
/// truncated to PostgreSQL's microsecond precision. A single lock guards all
/// state; <see cref="Snapshot"/> renders it canonically for before/after
/// comparisons.
/// </summary>
public sealed partial class InMemoryMonzeState :
    IAuthorizationRepository,
    IWelcomeRepository,
    IRoleRepository,
    ISchedulingRepository,
    IAiUsageRepository,
    IMessageHistoryRepository,
    IMeetingRepository
{
    // Value written by PostgresClanRegistryRepository when a clan disappears.
    private const string InactiveReason = "not returned by complete discovery";

    private readonly object _gate = new();
    private readonly TimeProvider _time;

    // clan_registry, clan_admin and clan_settings are shared by several ports.
    private readonly Dictionary<long, ClanRow> _clans = new();
    private readonly HashSet<(long ClanId, long UserId)> _admins = new();
    private readonly Dictionary<long, SettingsRow> _settings = new();

    public InMemoryMonzeState(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Seeds or replaces a clan_registry row. Clan ids must be positive, so
    /// clan 0 never authorizes anyone.
    /// </summary>
    public void AddClan(long clanId, long ownerId, bool active = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clanId);
        lock (_gate)
        {
            _clans[clanId] = new ClanRow
            {
                OwnerId = ownerId,
                InactiveReason = active ? null : InactiveReason
            };
        }
    }

    /// <summary>Sets inactive_reason on a seeded clan.</summary>
    public void DeactivateClan(long clanId)
    {
        lock (_gate)
        {
            RequireClanLocked(clanId).InactiveReason = InactiveReason;
        }
    }

    /// <summary>
    /// Seeds a clan_admin row directly, without the owner check. The clan
    /// must exist because clan_admin references clan_registry.
    /// </summary>
    public void AddDelegate(long clanId, long userId)
    {
        lock (_gate)
        {
            RequireClanLocked(clanId);
            _admins.Add((clanId, userId));
        }
    }

    private ClanRow RequireClanLocked(long clanId)
        => _clans.TryGetValue(clanId, out var clan)
            ? clan
            : throw new InvalidOperationException("The clan is not seeded; call AddClan first.");

    // clan_registry WHERE owner_id = @user AND inactive_reason IS NULL.
    private bool IsOwnerLocked(long clanId, long userId)
        => _clans.TryGetValue(clanId, out var clan)
            && clan.InactiveReason is null
            && clan.OwnerId == userId;

    // clan_registry LEFT JOIN clan_admin: owner or delegate of an active clan.
    private bool IsAdminLocked(long clanId, long userId)
        => _clans.TryGetValue(clanId, out var clan)
            && clan.InactiveReason is null
            && (clan.OwnerId == userId || _admins.Contains((clanId, userId)));

    // PostgreSQL now(): one instant per statement, microsecond precision.
    private DateTimeOffset NowLocked()
        => TruncateToMicroseconds(_time.GetUtcNow());

    // Npgsql refuses non-UTC DateTimeOffset values for timestamptz and
    // truncates the rest to microseconds.
    private static DateTimeOffset ToTimestamptz(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Cannot write DateTimeOffset with Offset={value.Offset} to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported.",
                parameterName);
        }

        return TruncateToMicroseconds(value);
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        return new DateTimeOffset(ticks - (ticks % 10), TimeSpan.Zero);
    }

    private sealed class ClanRow
    {
        public long OwnerId { get; set; }

        public string? InactiveReason { get; set; }
    }

    // clan_settings with its column defaults.
    private sealed class SettingsRow
    {
        public long Version { get; set; } = 1;

        public bool WelcomeEnabled { get; set; }

        public string? WelcomeText { get; set; }

        public string? WelcomeEmbedJson { get; set; }

        public bool RoleEnabled { get; set; } = true;
    }
}
