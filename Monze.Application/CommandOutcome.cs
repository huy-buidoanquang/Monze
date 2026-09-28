using Monze.Application.Commands;

namespace Monze.Application;

public sealed class CommandOutcome
{
    public string Title { get; init; } = MonzeMessages.TitleMonze;
    public required string Text { get; init; }
    public MonzeTone Tone { get; init; } = MonzeTone.Info;
    public bool ShowHelpButtons { get; init; }
    public bool ShowWelcomeHelp { get; init; }
    public bool ShowWelcomeSettings { get; init; }
    public bool ShowWelcomePreview { get; init; }
    public string? HelpTopic { get; init; }
    public bool HelpForAdmin { get; init; }
    public WelcomeSettings? WelcomeSettings { get; init; }
    public WelcomeEmbedSettings? WelcomeDraft { get; init; }
    public string? WelcomeDraftToken { get; init; }
    public bool HasGap { get; init; }
}
