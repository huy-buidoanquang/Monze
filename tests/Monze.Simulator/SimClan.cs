namespace Monze.Simulator;

/// <summary>A clan as ListClanDescs returns it: id, name, creator (owner) and welcome channel.</summary>
public sealed record SimClan(
    long Id,
    string Name,
    long OwnerId,
    long WelcomeChannelId,
    DateTimeOffset CreatedAt);
