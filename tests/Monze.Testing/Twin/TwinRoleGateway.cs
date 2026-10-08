using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Monze.Application;

namespace Monze.Testing.Twin;

/// <summary>
/// In-memory twin of the Mezon platform state behind
/// <see cref="IMezonRoleGateway"/>: clan roles and members. Role selectors
/// resolve like Features/Roles/SdkRoleGateway.cs (numeric selector by id,
/// otherwise case-insensitive title, first match among the first 100 roles,
/// which must be active). <see cref="FailAssignments"/> makes every role
/// assignment fail as if the Mezon API call threw.
/// </summary>
public sealed class TwinRoleGateway : IMezonRoleGateway
{
    // SdkRoleGateway calls ListRolesAsync(limit: 100).
    private const int RoleListLimit = 100;
    private const string DefaultRoleLabel = "vai trò";
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly Dictionary<long, List<RoleRow>> _roles = new();
    private readonly Dictionary<long, List<MemberRow>> _members = new();
    private bool _failAssignments;

    /// <summary>When true, AddUserToRoleAsync returns a failed result and changes nothing.</summary>
    public bool FailAssignments
    {
        get
        {
            lock (_gate)
            {
                return _failAssignments;
            }
        }
        set
        {
            lock (_gate)
            {
                _failAssignments = value;
            }
        }
    }

    /// <summary>Adds a role to the clan's role list, or replaces one with the same id in place.</summary>
    public void AddRole(long clanId, long roleId, string title, bool active = true)
    {
        lock (_gate)
        {
            var roles = GetOrAdd(_roles, clanId);
            var row = new RoleRow(roleId, title, active);
            var index = roles.FindIndex(role => role.Id == roleId);
            if (index >= 0)
            {
                roles[index] = row;
            }
            else
            {
                roles.Add(row);
            }
        }
    }

    /// <summary>
    /// Adds a clan member, or replaces one with the same id in place. Like
    /// the SDK's join_time, the join time keeps whole seconds and the Unix
    /// epoch reads back as unknown.
    /// </summary>
    public void AddMember(
        long clanId,
        long userId,
        DateTimeOffset? joinedAt = null,
        bool isBot = false,
        IReadOnlyCollection<long>? roleIds = null)
    {
        var joinSeconds = joinedAt?.ToUnixTimeSeconds() ?? 0;
        var row = new MemberRow(
            userId,
            isBot,
            joinSeconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(joinSeconds),
            roleIds is null ? new HashSet<long>() : new HashSet<long>(roleIds));
        lock (_gate)
        {
            var members = GetOrAdd(_members, clanId);
            var index = members.FindIndex(member => member.UserId == userId);
            if (index >= 0)
            {
                members[index] = row;
            }
            else
            {
                members.Add(row);
            }
        }
    }

    public Task<bool> IsMemberAsync(long clanId, long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clanId <= 0 || userId <= 0)
        {
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            return Task.FromResult(FindMemberLocked(clanId, userId) is not null);
        }
    }

    public Task<IReadOnlyList<MemberRoleSnapshot>> ListMembersAsync(long clanId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new List<MemberRoleSnapshot>();
        if (clanId <= 0)
        {
            return Task.FromResult<IReadOnlyList<MemberRoleSnapshot>>(snapshot);
        }

        lock (_gate)
        {
            if (_members.TryGetValue(clanId, out var members))
            {
                foreach (var member in members)
                {
                    if (member.UserId <= 0)
                    {
                        continue;
                    }

                    // SdkRoleGateway always reports IsBot = false; the twin
                    // returns the seeded flag.
                    snapshot.Add(new MemberRoleSnapshot(
                        member.UserId,
                        member.IsBot,
                        member.JoinedAt,
                        new HashSet<long>(member.RoleIds)));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<MemberRoleSnapshot>>(snapshot);
    }

    public Task<RoleResolutionResult> ResolveRoleAsync(
        long clanId,
        string roleSelector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notFound = new RoleResolutionResult(false, 0, string.Empty);
        if (clanId <= 0 || string.IsNullOrWhiteSpace(roleSelector))
        {
            return Task.FromResult(notFound);
        }

        var selector = roleSelector.Trim();
        var hasNumericSelector = long.TryParse(selector, out var numericRoleId);
        lock (_gate)
        {
            if (!_roles.TryGetValue(clanId, out var roles))
            {
                return Task.FromResult(notFound);
            }

            var count = Math.Min(roles.Count, RoleListLimit);
            for (var i = 0; i < count; i++)
            {
                var role = roles[i];
                if ((hasNumericSelector && role.Id != numericRoleId)
                    || (!hasNumericSelector && !string.Equals(role.Title, selector, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // The first match decides; an inactive first match is not found.
                if (role.Active && role.Id > 0)
                {
                    var label = string.IsNullOrWhiteSpace(role.Title) ? DefaultRoleLabel : role.Title;
                    return Task.FromResult(new RoleResolutionResult(true, role.Id, label));
                }

                break;
            }
        }

        return Task.FromResult(notFound);
    }

    public Task<RoleAssignmentResult> AddUserToRoleAsync(
        long clanId,
        long roleId,
        long userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failed = new RoleAssignmentResult(false, 0);
        if (clanId <= 0 || roleId <= 0 || userId <= 0)
        {
            return Task.FromResult(failed);
        }

        lock (_gate)
        {
            if (!IsActiveListedRoleLocked(clanId, roleId) || _failAssignments)
            {
                return Task.FromResult(failed);
            }

            // Twin choice: assigning a non-member fails like an API error.
            var member = FindMemberLocked(clanId, userId);
            if (member is null)
            {
                return Task.FromResult(failed);
            }

            member.RoleIds.Add(roleId);
            return Task.FromResult(new RoleAssignmentResult(true, roleId));
        }
    }

    /// <summary>Renders roles and members canonically (sorted by clan, then id).</summary>
    public string Snapshot()
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        lock (_gate)
        {
            foreach (var (clanId, roles) in _roles.OrderBy(static pair => pair.Key))
            {
                for (var i = 0; i < roles.Count; i++)
                {
                    var role = roles[i];
                    text.Append(culture, $"role clan={clanId} position={i} id={role.Id} title={JsonSerializer.Serialize(role.Title, SnapshotJson)} active={(role.Active ? "true" : "false")}").Append('\n');
                }
            }

            foreach (var (clanId, members) in _members.OrderBy(static pair => pair.Key))
            {
                foreach (var member in members.OrderBy(static member => member.UserId))
                {
                    var joinedAt = member.JoinedAt is { } at ? at.UtcDateTime.ToString("O", culture) : "null";
                    var roleIds = string.Join(',', member.RoleIds.Order().Select(id => id.ToString(culture)));
                    text.Append(culture, $"member clan={clanId} user={member.UserId} bot={(member.IsBot ? "true" : "false")} joined_at={joinedAt} roles=[{roleIds}]").Append('\n');
                }
            }
        }

        return text.ToString();
    }

    // SdkRoleGateway assigns only roles found active among the first 100 listed.
    private bool IsActiveListedRoleLocked(long clanId, long roleId)
    {
        if (!_roles.TryGetValue(clanId, out var roles))
        {
            return false;
        }

        var count = Math.Min(roles.Count, RoleListLimit);
        for (var i = 0; i < count; i++)
        {
            if (roles[i].Id == roleId)
            {
                return roles[i].Active;
            }
        }

        return false;
    }

    private MemberRow? FindMemberLocked(long clanId, long userId)
        => _members.TryGetValue(clanId, out var members)
            ? members.Find(member => member.UserId == userId)
            : null;

    private static List<T> GetOrAdd<T>(Dictionary<long, List<T>> map, long clanId)
    {
        if (!map.TryGetValue(clanId, out var list))
        {
            list = new List<T>();
            map.Add(clanId, list);
        }

        return list;
    }

    private sealed record RoleRow(long Id, string Title, bool Active);

    private sealed record MemberRow(long UserId, bool IsBot, DateTimeOffset? JoinedAt, HashSet<long> RoleIds);
}
