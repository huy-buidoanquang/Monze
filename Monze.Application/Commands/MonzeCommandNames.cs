namespace Monze.Application.Commands;

public static class MonzeCommandNames
{
    public const string Monze = "monze";
    public const string Meeting = "meeting";
    public const string Summary = "summary";
    public const string Help = "help";
    public const string Setup = "setup";
    public const string Welcome = "welcome";
    public const string Ai = "ai";
    public const string AiSummary = "summary";
    public const string Translate = "translate";
    public const string Composer = "composer";
    public const string Simplify = "simplify";
    public const string Role = "role";
    public const string Avatar = "avatar";
    public const string AvatarAliasAva = "ava";
    public const string AvatarAliasAvt = "avt";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> DirectModules { get; } =
    [
        Setup,
        Welcome,
        Ai,
        Role,
        Avatar,
        AvatarAliasAva,
        AvatarAliasAvt
    ];

    public static string Normalize(string value)
    {
        if (value.Equals(Setup, StringComparison.OrdinalIgnoreCase)) return Setup;
        if (value.Equals(Welcome, StringComparison.OrdinalIgnoreCase)) return Welcome;
        if (value.Equals(Ai, StringComparison.OrdinalIgnoreCase)) return Ai;
        if (value.Equals(AiSummary, StringComparison.OrdinalIgnoreCase)) return AiSummary;
        if (value.Equals(Translate, StringComparison.OrdinalIgnoreCase)) return Translate;
        if (value.Equals(Composer, StringComparison.OrdinalIgnoreCase)) return Composer;
        if (value.Equals(Simplify, StringComparison.OrdinalIgnoreCase)) return Simplify;
        if (value.Equals(Role, StringComparison.OrdinalIgnoreCase)) return Role;
        if (value.Equals(Avatar, StringComparison.OrdinalIgnoreCase)
            || value.Equals(AvatarAliasAva, StringComparison.OrdinalIgnoreCase)
            || value.Equals(AvatarAliasAvt, StringComparison.OrdinalIgnoreCase)) return Avatar;
        if (value.Equals(Help, StringComparison.OrdinalIgnoreCase)) return Help;
        if (value.Equals(Meeting, StringComparison.OrdinalIgnoreCase)) return Meeting;
        if (value.Equals(Summary, StringComparison.OrdinalIgnoreCase)) return Summary;
        return value;
    }
}
