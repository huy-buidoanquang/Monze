namespace Monze.Tests.E2E.Harness;

/// <summary>
/// The start of a scenario phase: the last simulator sequence, the host log
/// position and, for unauthorized scenarios, a snapshot of the business tables.
/// </summary>
internal sealed record E2EMark(long Sequence, int LogCount, BusinessSnapshot? Snapshot);
