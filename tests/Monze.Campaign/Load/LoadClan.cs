namespace Monze.Campaign.Load;

/// <summary>One active clan of a <see cref="LoadWorld"/>.</summary>
public sealed record LoadClan(
    long Id,
    long Owner,
    IReadOnlyList<long> Members,
    long Spammer,
    IReadOnlyList<long> CommandChannels,
    IReadOnlyList<long> Chat,
    long Welcome,
    IReadOnlyList<long> Voice);
