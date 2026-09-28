namespace Monze.Application.Commands;

public static class MonzeCommandNames
{
    public const string Monze = "monze";
    public const string Meeting = "meeting";
    public const string Summary = "summary";
    public const string Help = "help";
    public const string Setup = "setup";
    public const string Welcome = "welcome";
    public const string Event = "event";
    public const string Announce = "announce";
    public const string Outbox = "outbox";
    public const string Faq = "faq";
    public const string Info = "info";
    public const string Points = "points";
    public const string Leaderboard = "lb";
    public const string Spin = "spin";
    public const string Topic = "topic";
    public const string Summarize = "sum";
    public const string Translate = "dich";
    public const string Rewrite = "viet";
    public const string Shorten = "rutgon";
    public const string Role = "role";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> DirectModules { get; } =
    [
        Setup,
        Welcome,
        Event,
        Announce,
        Outbox,
        Faq,
        Info,
        Points,
        Leaderboard,
        Spin,
        Topic,
        Summarize,
        Translate,
        Rewrite,
        Shorten,
        Role
    ];

    public static string Normalize(string value)
    {
        if (value.Equals(Setup, StringComparison.OrdinalIgnoreCase)) return Setup;
        if (value.Equals(Welcome, StringComparison.OrdinalIgnoreCase)) return Welcome;
        if (value.Equals(Event, StringComparison.OrdinalIgnoreCase)) return Event;
        if (value.Equals(Announce, StringComparison.OrdinalIgnoreCase)) return Announce;
        if (value.Equals(Outbox, StringComparison.OrdinalIgnoreCase)) return Outbox;
        if (value.Equals(Faq, StringComparison.OrdinalIgnoreCase)) return Faq;
        if (value.Equals(Info, StringComparison.OrdinalIgnoreCase)) return Info;
        if (value.Equals(Points, StringComparison.OrdinalIgnoreCase)) return Points;
        if (value.Equals(Leaderboard, StringComparison.OrdinalIgnoreCase)) return Leaderboard;
        if (value.Equals(Spin, StringComparison.OrdinalIgnoreCase)) return Spin;
        if (value.Equals(Topic, StringComparison.OrdinalIgnoreCase)) return Topic;
        if (value.Equals(Summarize, StringComparison.OrdinalIgnoreCase)) return Summarize;
        if (value.Equals(Translate, StringComparison.OrdinalIgnoreCase)) return Translate;
        if (value.Equals(Rewrite, StringComparison.OrdinalIgnoreCase)) return Rewrite;
        if (value.Equals(Shorten, StringComparison.OrdinalIgnoreCase)) return Shorten;
        if (value.Equals(Role, StringComparison.OrdinalIgnoreCase)) return Role;
        if (value.Equals(Help, StringComparison.OrdinalIgnoreCase)) return Help;
        if (value.Equals(Meeting, StringComparison.OrdinalIgnoreCase)) return Meeting;
        if (value.Equals(Summary, StringComparison.OrdinalIgnoreCase)) return Summary;
        return value;
    }
}
