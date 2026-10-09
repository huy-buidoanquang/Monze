using Mezon.Net.Client;
using Mezon.Net.Core;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using Monze.Application;
using SdkMezonClient = Mezon.Net.Sdk.MezonClient;

namespace Monze;

public sealed class SdkRoleGateway : IMezonRoleGateway
{
    private readonly SdkMezonClient _client;
    private readonly ILogger _logger;

    public SdkRoleGateway(SdkMezonClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> IsMemberAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0 || userId <= 0)
        {
            return false;
        }

        var response = await _client.ListClanUsersAsync(
            clanId,
            new RequestOptions { SocketSendTimeout = 15_000 });
        for (var i = 0; i < response.ClanUsers.Count; i++)
        {
            if (response.ClanUsers[i].User.Id == userId)
            {
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public async Task<IReadOnlyList<MemberRoleSnapshot>> ListMembersAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0)
        {
            return Array.Empty<MemberRoleSnapshot>();
        }

        var response = await _client.ListClanUsersAsync(
            clanId,
            new RequestOptions { SocketSendTimeout = 15_000 });
        var members = new List<MemberRoleSnapshot>(response.ClanUsers.Count);
        for (var i = 0; i < response.ClanUsers.Count; i++)
        {
            var member = response.ClanUsers[i];
            if (member.User.Id <= 0)
            {
                continue;
            }

            var roleIds = new HashSet<long>();
            for (var roleIndex = 0; roleIndex < member.RoleId.Count; roleIndex++)
            {
                roleIds.Add(member.RoleId[roleIndex]);
            }

            var joinedAt = member.User.JoinTimeSeconds == 0
                ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeSeconds(member.User.JoinTimeSeconds);
            // ClanUserList has no bot flag (docs/mezon-net-handoff.md, clan roster
            // bot identity); Monze can at least recognise itself (CAND-23).
            members.Add(new MemberRoleSnapshot(member.User.Id, member.User.Id == _client.BotId, joinedAt, roleIds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return members;
    }

    public async Task<RoleResolutionResult> ResolveRoleAsync(
        long clanId,
        string roleSelector,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0 || string.IsNullOrWhiteSpace(roleSelector))
        {
            return new RoleResolutionResult(false, 0, string.Empty);
        }

        var clan = await GetClanAsync(clanId, cancellationToken);
        if (clan is null)
        {
            return new RoleResolutionResult(false, 0, string.Empty);
        }

        var roles = await clan.ListRolesAsync(
            limit: 100,
            options: new RequestOptions { SocketSendTimeout = 15_000 });
        var roleId = 0L;
        var roleLabel = "vai trò";
        var selector = roleSelector.Trim();
        var hasNumericSelector = long.TryParse(selector, out var numericRoleId);
        for (var i = 0; i < roles.Roles.Roles.Count; i++)
        {
            var role = roles.Roles.Roles[i];
            if ((hasNumericSelector && role.Id != numericRoleId)
                || (!hasNumericSelector && !string.Equals(role.Title, selector, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (role.ClanId == clanId && role.Active != 0)
            {
                roleId = role.Id;
                roleLabel = string.IsNullOrWhiteSpace(role.Title) ? "vai trò" : role.Title;
            }
            break;
        }

        if (roleId <= 0)
        {
            return new RoleResolutionResult(false, 0, string.Empty);
        }

        return new RoleResolutionResult(true, roleId, roleLabel);
    }

    public async Task<RoleAssignmentResult> AddUserToRoleAsync(
        long clanId,
        long roleId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (clanId <= 0 || roleId <= 0 || userId <= 0)
        {
            return new RoleAssignmentResult(false, 0);
        }

        var clan = await GetClanAsync(clanId, cancellationToken);
        if (clan is null)
        {
            return new RoleAssignmentResult(false, 0);
        }

        var roles = await clan.ListRolesAsync(
            limit: 100,
            options: new RequestOptions { SocketSendTimeout = 15_000 });
        var activeInClan = false;
        for (var i = 0; i < roles.Roles.Roles.Count; i++)
        {
            var role = roles.Roles.Roles[i];
            if (role.Id == roleId)
            {
                activeInClan = role.ClanId == clanId && role.Active != 0;
                break;
            }
        }

        if (!activeInClan)
        {
            _logger.LogWarning(
                "Role assignment skipped because role is not active in clan. ClanId={ClanId} RoleId={RoleId} UserId={UserId}.",
                clanId,
                roleId,
                userId);
            return new RoleAssignmentResult(false, 0);
        }

        try
        {
            await clan.UpdateRoleAsync(new UpdateRoleParams(
                roleId: roleId,
                addUserIds: new long?[] { userId },
                clanId: clanId));
            return new RoleAssignmentResult(true, roleId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Role assignment API failed. ClanId={ClanId} RoleId={RoleId} UserId={UserId}.",
                clanId,
                roleId,
                userId);
            return new RoleAssignmentResult(false, 0);
        }
    }

    private async Task<Mezon.Net.Sdk.Entities.Clan?> GetClanAsync(
        long clanId,
        CancellationToken cancellationToken)
    {
        if (_client.Clans.TryGet(clanId, out var cached))
        {
            return cached;
        }

        try
        {
            return await _client.GetClanAsync(clanId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
