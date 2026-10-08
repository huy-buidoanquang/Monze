namespace Monze.Campaign.Load;

/// <summary>
/// The traffic of one load stage. Rates are the documented production
/// workload (docs/performance-review.md: 200 message events/s, 20
/// commands/s, 200 outbox deliveries/s, 100 meetings) plus the campaign's
/// own assumptions for clicks, joins and voice changes. The command mix is a
/// campaign assumption, recorded in every artifact, because no document
/// gives one.
/// </summary>
public sealed record LoadWorkload(
    TimeSpan Warmup,
    TimeSpan Measure,
    TimeSpan Drain,
    double MessagesPerSecond = 200,
    double CommandsPerSecond = 20,
    double ClicksPerSecond = 2,
    double JoinsPerSecond = 1,
    double VoiceChangesPerSecond = 5,
    double OutboxPerSecond = 200)
{
    /// <summary>Command text and weight; 2 % of commands come from one spamming member per clan.</summary>
    public static IReadOnlyList<(string Text, int Weight)> CommandMix { get; } =
    [
        ("*monze help", 25),
        ("*meeting now", 20),
        ("*avatar", 15),
        ("*meeting", 10),
        ("*ai summary", 10),
        ("*welcome help", 10),
        ("*role help", 5),
        ("*monze nothing", 5)
    ];

    public const double SpamShare = 0.02;

    public static LoadWorkload Quick { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(15));

    public static LoadWorkload Full { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(30));

    public static string DescribeMix()
        => string.Join(", ", CommandMix.Select(static entry => $"{entry.Text} {entry.Weight} %")) + $"; spam {SpamShare:P0}";
}
