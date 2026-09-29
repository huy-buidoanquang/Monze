using Monze.Application.Commands;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private async Task<CommandOutcome> SetupAsync(
        long clanId,
        long channelId,
        long userId,
        CommandArguments rest,
        long? mentionedUserId,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsOwnerAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.OwnerOnly, tone: MonzeTone.Error);
        }

        if (rest.Length >= 2 && rest[0].Equals(MonzeCommandActions.Admin, StringComparison.OrdinalIgnoreCase))
        {
            if (!rest[1].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase)
                && !rest[1].Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase))
            {
                return await SetupHelpAsync(clanId, userId, cancellationToken);
            }

            long parsedUserId = 0;
            var targetUserId = mentionedUserId;
            if (targetUserId is null
                && (rest.Length < 3 || !long.TryParse(rest[2], out parsedUserId) || parsedUserId <= 0))
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Setup, cancellationToken);
            }

            targetUserId ??= parsedUserId;
            if (targetUserId == userId)
            {
                return Say(MonzeMessages.OwnerAlreadyAdmin);
            }

            var enabled = rest[1].Equals(MonzeCommandActions.Add, StringComparison.OrdinalIgnoreCase);
            var changed = await _authorization.SetDelegateAsync(
                clanId,
                targetUserId.Value,
                enabled,
                cancellationToken);
            return Say(changed
                ? enabled ? MonzeMessages.DelegateAdded : MonzeMessages.DelegateRemoved
                : enabled ? MonzeMessages.DelegateAlreadyExists : MonzeMessages.DelegateNotFound);
        }

        return await SetupHelpAsync(clanId, userId, cancellationToken);
    }

    private async Task<CommandOutcome> WelcomeAsync(
        long clanId,
        long channelId,
        long userId,
        CommandArguments args,
        CancellationToken cancellationToken)
    {
        if (args.Length > 0 && args[0].Equals(MonzeCommandActions.Setup, StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1
                && args[1].Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase))
            {
                return await RemoveWelcomeEmbedAsync(clanId, userId, cancellationToken);
            }

            return await GetWelcomeSettingsAsync(clanId, userId, cancellationToken);
        }

        if (args.Length > 0
            && args[0].Equals(MonzeCommandActions.Preview, StringComparison.OrdinalIgnoreCase))
        {
            return await GetWelcomePreviewAsync(clanId, userId, cancellationToken);
        }

        if (args.Length > 0
            && args[0].Equals(MonzeCommandActions.Message, StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length == 2
                && args[1].Equals(MonzeCommandActions.Remove, StringComparison.OrdinalIgnoreCase))
            {
                return await RemoveWelcomeMessageAsync(clanId, userId, cancellationToken);
            }

            if (args.Length < 2)
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Welcome, cancellationToken);
            }

            return await SetWelcomeMessageAsync(
                clanId,
                userId,
                args.Join(' ', 1),
                cancellationToken);
        }

        if (args.Length > 0
            && args[0].Equals(MonzeCommandActions.Apply, StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2)
            {
                return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Welcome, cancellationToken);
            }

            return await SaveWelcomeDraftAsync(
                clanId,
                channelId,
                userId,
                args[1],
                cancellationToken);
        }

        if (args.Length == 0
            || (!args[0].Equals(MonzeCommandActions.On, StringComparison.OrdinalIgnoreCase)
                && !args[0].Equals(MonzeCommandActions.Off, StringComparison.OrdinalIgnoreCase)))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Welcome, cancellationToken);
        }

        var on = args[0].Equals(MonzeCommandActions.On, StringComparison.OrdinalIgnoreCase);
        return await SetWelcomeAsync(
            clanId,
            userId,
            on,
            args.Length > 1 ? args.Join(' ', 1) : null,
            cancellationToken);
    }

    private async Task<CommandOutcome> SetupHelpAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        var isOwner = await _authorization.IsOwnerAsync(clanId, userId, cancellationToken);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleHelp,
            // Setup help is rendered from HelpTopic by MonzeMessageBuilder.
            // Avoid constructing the retired newline-based help text.
            Text = string.Empty,
            HelpTopic = MonzeCommandNames.Setup,
            HelpForAdmin = isOwner,
            HelpForOwner = isOwner,
            CanManageWelcome = isOwner,
            ShowWelcomeHelp = isOwner
        };
    }

    public async Task<CommandOutcome> GetWelcomeSettingsAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var settings = await _authorization.GetWelcomeAsync(clanId, cancellationToken)
            ?? new WelcomeSettings(false, null, 0);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcomeSetup,
            Text = MonzeMessages.WelcomeSetupText(),
            ShowWelcomeSettings = true,
            WelcomeSettings = settings
        };
    }

    public Task<CommandOutcome> PreviewWelcomeAsync(
        long clanId,
        long channelId,
        bool enabled,
        WelcomeEmbedSettings draft,
        CancellationToken cancellationToken,
        string? messageText = null)
    {
        var normalized = NormalizeWelcomeEmbed(draft);
        var ticket = _welcomeDrafts.Create(
            clanId,
            channelId,
            enabled,
            normalized,
            DateTimeOffset.UtcNow,
            messageText);

        return Task.FromResult(new CommandOutcome
        {
            Title = MonzeMessages.WelcomePreview,
            Text = MonzeMessages.WelcomeDraftReady(),
            ShowWelcomePreview = true,
            WelcomeDraft = normalized,
            WelcomeDraftToken = ticket.Token,
            WelcomeMessageText = messageText
        });
    }

    public async Task<CommandOutcome> GetWelcomePreviewAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var settings = await _authorization.GetWelcomeAsync(clanId, cancellationToken)
            ?? new WelcomeSettings(false, null, 0);
        return new CommandOutcome
        {
            Title = MonzeMessages.WelcomePreview,
            Text = settings.Text ?? MonzeMessages.DefaultWelcomeText,
            ShowWelcomePreview = true,
            WelcomeDraft = settings.Embed ?? new WelcomeEmbedSettings(
                Title: "Chào mừng",
                Description: settings.Text ?? MonzeMessages.DefaultWelcomeText),
            WelcomeMessageText = settings.Text
        };
    }

    public Task<CommandOutcome> GetWelcomeDraftPreviewAsync(
        long clanId,
        long channelId,
        string token,
        CancellationToken cancellationToken)
    {
        if (!_welcomeDrafts.TryGet(
                token,
                clanId,
                channelId,
                DateTimeOffset.UtcNow,
                out var ticket))
        {
            return Task.FromResult(Say(MonzeMessages.WelcomeDraftInvalid, tone: MonzeTone.Warn));
        }

        return Task.FromResult(new CommandOutcome
        {
            Title = MonzeMessages.WelcomePreview,
            Text = MonzeMessages.WelcomeDraftReady(),
            ShowWelcomePreview = true,
            WelcomeDraft = ticket.Draft,
            WelcomeDraftToken = ticket.Token,
            WelcomeMessageText = ticket.Text
        });
    }

    public async Task<CommandOutcome> SaveWelcomeDraftAsync(
        long clanId,
        long channelId,
        long userId,
        string token,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        if (!_welcomeDrafts.TryClaim(
                token,
                clanId,
                channelId,
                DateTimeOffset.UtcNow,
                out var ticket))
        {
            return Say(MonzeMessages.WelcomeDraftInvalid, tone: MonzeTone.Warn);
        }

        try
        {
            var outcome = await SaveWelcomeEmbedAsync(
                clanId,
                userId,
                ticket.Enabled,
                ticket.Draft,
                cancellationToken,
                ticket.Text);
            if (outcome.Tone == MonzeTone.Error)
            {
                _welcomeDrafts.Release(ticket);
                return outcome;
            }

            _welcomeDrafts.Complete(ticket);
            return outcome;
        }
        catch
        {
            _welcomeDrafts.Release(ticket);
            throw;
        }
    }

    public async Task<CommandOutcome> SaveWelcomeEmbedAsync(
        long clanId,
        long userId,
        bool enabled,
        WelcomeEmbedSettings draft,
        CancellationToken cancellationToken,
        string? welcomeText = null)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var normalized = NormalizeWelcomeEmbed(draft);
        var version = await _authorization.SetWelcomeConfigurationAsync(
            clanId,
            enabled,
            welcomeText,
            normalized,
            cancellationToken);
        try
        {
            await _readModelCache.InvalidateAsync(
                clanId,
                "welcome",
                "settings",
                version,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Cache invalidation is best effort; PostgreSQL remains authoritative.
        }

        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcome,
            Text = MonzeMessages.WelcomeEmbedSaved,
            Tone = MonzeTone.Ok,
            ShowWelcomeHelp = true
        };
    }

    public async Task<CommandOutcome> SetWelcomeMessageAsync(
        long clanId,
        long userId,
        string text,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var normalized = Limit(text, 4_000);
        if (normalized is null)
        {
            return Say(MonzeMessages.WelcomeMessageRequired, tone: MonzeTone.Warn);
        }

        var version = await _authorization.SetWelcomeMessageAsync(
            clanId,
            normalized,
            cancellationToken);
        await InvalidateWelcomeAsync(clanId, version, cancellationToken);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcome,
            Text = MonzeMessages.WelcomeMessageSaved,
            Tone = MonzeTone.Ok,
            ShowWelcomeHelp = true
        };
    }

    public async Task<CommandOutcome> RemoveWelcomeEmbedAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var version = await _authorization.RemoveWelcomeEmbedAsync(clanId, cancellationToken);
        await InvalidateWelcomeAsync(clanId, version, cancellationToken);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcome,
            Text = MonzeMessages.WelcomeEmbedRemoved,
            Tone = MonzeTone.Ok,
            ShowWelcomeHelp = true
        };
    }

    public async Task<CommandOutcome> RemoveWelcomeMessageAsync(
        long clanId,
        long userId,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAdminAsync(clanId, userId, cancellationToken))
        {
            return Say(MonzeMessages.WelcomeAdminOnly, tone: MonzeTone.Error);
        }

        var version = await _authorization.RemoveWelcomeMessageAsync(clanId, cancellationToken);
        await InvalidateWelcomeAsync(clanId, version, cancellationToken);
        return new CommandOutcome
        {
            Title = MonzeMessages.TitleWelcome,
            Text = MonzeMessages.WelcomeMessageRemoved,
            Tone = MonzeTone.Ok,
            ShowWelcomeHelp = true
        };
    }

    private async Task InvalidateWelcomeAsync(
        long clanId,
        long version,
        CancellationToken cancellationToken)
    {
        if (version == 0)
        {
            return;
        }

        try
        {
            await _readModelCache.InvalidateAsync(
                clanId,
                "welcome",
                "settings",
                version,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // PostgreSQL remains authoritative when the read-model cache is unavailable.
        }
    }

    private static WelcomeEmbedSettings NormalizeWelcomeEmbed(WelcomeEmbedSettings draft)
        => draft with
        {
            Title = Limit(draft.Title, 256),
            Description = Limit(draft.Description, 4_000),
            Url = NormalizeUrl(draft.Url),
            Color = NormalizeColor(draft.Color),
            AuthorName = Limit(draft.AuthorName, 256),
            AuthorIconUrl = NormalizeUrl(draft.AuthorIconUrl),
            AuthorUrl = NormalizeUrl(draft.AuthorUrl),
            ThumbnailUrl = NormalizeUrl(draft.ThumbnailUrl),
            ImageUrl = NormalizeUrl(draft.ImageUrl),
            FooterText = Limit(draft.FooterText, 256),
            FooterIconUrl = NormalizeUrl(draft.FooterIconUrl),
            FieldName = Limit(draft.FieldName, 256),
            FieldValue = Limit(draft.FieldValue, 1_024)
        };

    private static string? Limit(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..max];
    }

    private static string? NormalizeUrl(string? value)
    {
        var text = Limit(value, 2_048);
        return text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? text
            : null;
    }

    private static string? NormalizeColor(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length == 7 && text[0] == '#'
            && text.Skip(1).All(static c => char.IsAsciiHexDigit(c)))
        {
            return text.ToUpperInvariant();
        }

        return null;
    }
}
