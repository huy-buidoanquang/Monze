using Monze.Simulator;

namespace Monze.Tests.E2E.Harness;

/// <summary>An input pushed to the bot and the response it must get.</summary>
internal sealed record InputExpectation(SimPush Input, ResponseKind Response);
