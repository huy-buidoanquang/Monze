/// <summary>
/// Marks a benchmark whose allocation must be exactly 0 B/op. The campaign
/// micro tier fails when a marked benchmark allocates or does not run.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ZeroAllocationGateAttribute : Attribute;
