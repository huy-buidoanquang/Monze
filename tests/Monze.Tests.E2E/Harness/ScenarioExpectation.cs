namespace Monze.Tests.E2E.Harness;

/// <summary>
/// What <see cref="E2EOracles.AssertAsync"/> checks for one scenario phase:
/// every input pushed since the mark with its expected response, the number
/// of bot outputs that belong to no input (welcome messages, scheduled
/// meeting suggestions), warnings that may carry an exception, and whether
/// the actors were unauthorized (then no business table may change).
/// </summary>
internal sealed record ScenarioExpectation
{
    public IReadOnlyList<InputExpectation> Inputs { get; init; } = [];

    /// <summary>Exact number of message sends, edits and deletes not caused by any declared input.</summary>
    public int OtherOutputs { get; init; }

    /// <summary>Substrings of warning messages that may carry an exception in this scenario.</summary>
    public IReadOnlyList<string> AllowedWarnings { get; init; } = [];

    /// <summary>The actors had no right to change anything: business tables must equal the mark's snapshot.</summary>
    public bool Unauthorized { get; init; }

    /// <summary>
    /// Random walks: inputs are not declared one by one, so oracle 2 (one
    /// response per input) is skipped; every other oracle still applies and
    /// the walk checks its own exactly-once properties and the invariants.
    /// </summary>
    public bool ResponsesUnchecked { get; init; }

    public static ScenarioExpectation Of(params (Monze.Simulator.SimPush Input, ResponseKind Response)[] inputs)
        => new() { Inputs = inputs.Select(static input => new InputExpectation(input.Input, input.Response)).ToList() };
}
