namespace Monze.Application;

public sealed record AgentActionItemGroup(
    string ParticipantIdentity,
    IReadOnlyList<string> Items);
