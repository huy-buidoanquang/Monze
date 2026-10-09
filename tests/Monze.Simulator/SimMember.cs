namespace Monze.Simulator;

/// <summary>Clan membership: join time, clan nickname and assigned role ids.</summary>
public sealed record SimMember(
    long ClanId,
    long UserId,
    string? ClanNick,
    DateTimeOffset JoinedAt,
    IReadOnlySet<long> RoleIds);
