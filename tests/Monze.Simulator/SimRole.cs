namespace Monze.Simulator;

/// <summary>A clan role as ListRoles returns it.</summary>
public sealed record SimRole(
    long Id,
    long ClanId,
    string Title,
    bool Active);
