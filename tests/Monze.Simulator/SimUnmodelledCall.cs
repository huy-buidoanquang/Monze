namespace Monze.Simulator;

/// <summary>
/// A call the simulator does not model. It was answered with a failure (or
/// not at all) instead of a silent success; a normal test asserts the list
/// of these is empty.
/// </summary>
public sealed record SimUnmodelledCall(
    DateTimeOffset At,
    int SessionId,
    string Operation,
    string Detail);
