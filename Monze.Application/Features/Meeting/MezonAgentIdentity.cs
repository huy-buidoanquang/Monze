namespace Monze.Application;

public static class MezonAgentIdentity
{
    public const long UserId = 2090694093138038784L;
    public const string UserIdText = "2090694093138038784";
    public const string ParticipantPrefix = "agent-e";

    public static bool IsAgent(string identity)
        => string.Equals(identity, UserIdText, StringComparison.Ordinal)
            || identity.StartsWith(ParticipantPrefix, StringComparison.OrdinalIgnoreCase);
}
