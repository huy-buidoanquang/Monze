namespace Monze.Testing.Harness;

/// <summary>One broken <see cref="MonzeInvariants"/> rule and how many rows break it.</summary>
public sealed record InvariantViolation(string Id, string Description, long Count);
