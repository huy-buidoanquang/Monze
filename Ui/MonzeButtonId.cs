namespace Monze.Ui;

public static class MonzeButtonId
{
    public const string HelpCommands = "monze_help_commands";
    public const string HelpEvent = "monze_help_event";
    public const string HelpMeeting = "monze_help_meeting";
    public const string WelcomeHelp = "monze_welcome_help";
    public const string WelcomeSettings = "monze_welcome_settings";
    public const string WelcomePreview = "monze_welcome_preview";
    public const string WelcomeSave = "monze_welcome_save";
    public const string WelcomeSavePrefix = "monze_welcome_save:";
    public const string WelcomeStatus = "monze_welcome_status";
    public const string WelcomeTitle = "monze_welcome_title";
    public const string WelcomeDescription = "monze_welcome_description";
    public const string WelcomeUrl = "monze_welcome_url";
    public const string WelcomeColor = "monze_welcome_color";
    public const string WelcomeAuthor = "monze_welcome_author";
    public const string WelcomeAuthorIcon = "monze_welcome_author_icon";
    public const string WelcomeAuthorUrl = "monze_welcome_author_url";
    public const string WelcomeThumbnail = "monze_welcome_thumbnail";
    public const string WelcomeImage = "monze_welcome_image";
    public const string WelcomeFooter = "monze_welcome_footer";
    public const string WelcomeFooterIcon = "monze_welcome_footer_icon";
    public const string WelcomeFieldName = "monze_welcome_field_name";
    public const string WelcomeFieldValue = "monze_welcome_field_value";

    public static string WelcomeSaveFor(string token)
        => WelcomeSavePrefix + token;

    public static bool TryReadWelcomeSaveToken(string buttonId, out string token)
    {
        if (buttonId.StartsWith(WelcomeSavePrefix, StringComparison.Ordinal)
            && buttonId.Length > WelcomeSavePrefix.Length)
        {
            token = buttonId[WelcomeSavePrefix.Length..];
            return true;
        }

        token = string.Empty;
        return false;
    }
}
