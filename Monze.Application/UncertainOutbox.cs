namespace Monze.Application;

/// <summary>A leased outbox row whose delivery must be reconciled against its channel.</summary>
public sealed record UncertainOutbox(long ClanId, DueOutbox Item);
