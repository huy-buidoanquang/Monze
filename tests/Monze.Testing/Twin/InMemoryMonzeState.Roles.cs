using Monze.Domain;

namespace Monze.Testing.Twin;

// Mirrors Monze.Infrastructure/Persistence/PostgresRoleRepository.cs.
public sealed partial class InMemoryMonzeState
{
    // role_rule keyed by (clan_id, role_id, rule_kind storage name).
    private readonly Dictionary<(long ClanId, long RoleId, string Kind), RoleRuleRow> _roleRules = new();

    // role_grant (clan_id, role_id, user_id) -> granted_at.
    private readonly Dictionary<(long ClanId, long RoleId, long UserId), DateTimeOffset> _roleGrants = new();

    public Task<bool> IsRoleAutomationEnabledAsync(long clanId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // No clan_settings row reads as enabled (the column default),
            // with no clan activity filter.
            return Task.FromResult(!_settings.TryGetValue(clanId, out var row) || row.RoleEnabled);
        }
    }

    public Task<bool> SetRoleAutomationEnabledAsync(
        long clanId,
        long actorUserId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsAdminLocked(clanId, actorUserId))
            {
                return Task.FromResult(false);
            }

            if (_settings.TryGetValue(clanId, out var row))
            {
                row.RoleEnabled = enabled;
                row.Version++;
            }
            else
            {
                _settings.Add(clanId, new SettingsRow { RoleEnabled = enabled });
            }

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // The SQL has no ORDER BY here; the twin uses role_id, rule_kind.
            return Task.FromResult(ReadEnabledRoleRulesLocked(clanId));
        }
    }

    public Task<IReadOnlyList<AutoRoleRule>> ListEnabledRoleRulesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(ReadEnabledRoleRulesLocked(null));
        }
    }

    public Task<bool> SetRoleRuleAsync(
        long clanId,
        long actorUserId,
        long roleId,
        RoleRuleKind kind,
        string? conditionValue,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageName = RoleRules.ToStorageName(kind);
        lock (_gate)
        {
            if (!IsAdminLocked(clanId, actorUserId))
            {
                return Task.FromResult(false);
            }

            var now = NowLocked();
            if (_roleRules.TryGetValue((clanId, roleId, storageName), out var rule))
            {
                rule.Enabled = true;
                rule.ConditionValue = conditionValue;
                rule.Version++;
                rule.UpdatedAt = now;
            }
            else
            {
                _roleRules.Add((clanId, roleId, storageName), new RoleRuleRow
                {
                    Enabled = true,
                    ConditionValue = conditionValue,
                    UpdatedAt = now
                });
            }

            return Task.FromResult(true);
        }
    }

    public Task<bool> RemoveRoleRuleAsync(
        long clanId,
        long actorUserId,
        long roleId,
        RoleRuleKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageName = RoleRules.ToStorageName(kind);
        lock (_gate)
        {
            return Task.FromResult(
                IsAdminLocked(clanId, actorUserId)
                && _roleRules.Remove((clanId, roleId, storageName)));
        }
    }

    public Task RecordRoleGrantAsync(long clanId, long roleId, long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // ON CONFLICT DO NOTHING keeps the first granted_at.
            _roleGrants.TryAdd((clanId, roleId, userId), NowLocked());
        }

        return Task.CompletedTask;
    }

    // role_rule JOIN clan_registry JOIN clan_settings: the rule is enabled,
    // the clan is active and a settings row exists with role_enabled. A clan
    // without a settings row therefore lists no rules even though
    // IsRoleAutomationEnabledAsync reports it as enabled.
    private IReadOnlyList<AutoRoleRule> ReadEnabledRoleRulesLocked(long? clanId)
    {
        var rules = new List<AutoRoleRule>();
        foreach (var (key, rule) in _roleRules
            .OrderBy(static pair => pair.Key.ClanId)
            .ThenBy(static pair => pair.Key.RoleId)
            .ThenBy(static pair => pair.Key.Kind, StringComparer.Ordinal))
        {
            if ((clanId is { } only && key.ClanId != only)
                || !rule.Enabled
                || !_clans.TryGetValue(key.ClanId, out var clan)
                || clan.InactiveReason is not null
                || !_settings.TryGetValue(key.ClanId, out var settings)
                || !settings.RoleEnabled
                || !RoleRules.TryParseKind(key.Kind, out var kind))
            {
                continue;
            }

            rules.Add(new AutoRoleRule(key.ClanId, key.RoleId, kind, rule.ConditionValue, rule.Version, rule.UpdatedAt));
        }

        return rules;
    }

    private sealed class RoleRuleRow
    {
        public bool Enabled { get; set; }

        public string? ConditionValue { get; set; }

        public long Version { get; set; } = 1;

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
