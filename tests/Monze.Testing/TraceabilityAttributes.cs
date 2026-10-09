namespace Monze.Testing;

/// <summary>Links a test to requirement ids in tests/traceability/requirements.json.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ReqAttribute : Attribute
{
    public ReqAttribute(params string[] ids)
    {
        Ids = ids;
    }

    public IReadOnlyList<string> Ids { get; }
}

/// <summary>
/// Links a test to inventory items it exercises, e.g. "port:IOutboxRepository.ClaimDueOutboxAsync",
/// "btn:monze_meeting_now", "msg:WelcomeAdminOnly".
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class CoversAttribute : Attribute
{
    public CoversAttribute(params string[] items)
    {
        Items = items;
    }

    public IReadOnlyList<string> Items { get; }
}
