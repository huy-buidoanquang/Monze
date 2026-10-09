using BenchmarkDotNet.Attributes;
using Monze;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

/// <summary>Welcome rendering, help pages and result cards.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RendererBenchmarks
{
    private readonly WelcomeSettings _welcome = new(
        true,
        "Chào {user}, xem {channel:general} và nhận {role:Member}.",
        1,
        new WelcomeEmbedSettings(Title: "Xin chào"));

    private readonly Dictionary<string, (long Id, string Label)> _users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alice"] = (42, "Alice")
    };

    private readonly Dictionary<string, (long Id, string Label)> _roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Member"] = (7, "Member")
    };

    private readonly Dictionary<string, (long Id, string Label)> _channels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = (9, "general")
    };

    [Benchmark]
    public object WelcomeTemplate()
        => WelcomeMessageRenderer.Render(_welcome, 42, "Alice", _users, _roles, _channels);

    [Benchmark]
    public object HelpCommandsPage()
        => MonzeMessageBuilder.HelpPage("commands", MonzeCommandOptions.Default, isAdmin: true, canManageWelcome: true, isOwner: true);

    [Benchmark]
    public object ResultCard()
        => MonzeMessageBuilder.Card(MonzeMessages.TitleMonze, MonzeMessages.TemporaryFailure, MonzeTone.Error);
}
