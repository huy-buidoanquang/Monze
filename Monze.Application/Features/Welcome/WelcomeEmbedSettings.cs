namespace Monze.Application;

public sealed record WelcomeEmbedSettings(
    string? Title = null,
    string? Description = null,
    string? Url = null,
    string? Color = null,
    string? AuthorName = null,
    string? AuthorIconUrl = null,
    string? AuthorUrl = null,
    string? ThumbnailUrl = null,
    string? ImageUrl = null,
    string? FooterText = null,
    string? FooterIconUrl = null,
    string? FieldName = null,
    string? FieldValue = null);
