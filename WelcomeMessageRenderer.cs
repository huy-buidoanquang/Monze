using System.Text;
using Mezon.Net.Client;
using Mezon.Net.Models;
using Mezon.Net.Sdk.Builders;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Ui;

namespace Monze;

internal static class WelcomeMessageRenderer
{
    public static bool RequiresLookup(WelcomeSettings settings)
    {
        var text = settings.Text ?? string.Empty;
        var description = settings.Embed?.Description ?? string.Empty;
        return text.Contains("{user", StringComparison.OrdinalIgnoreCase)
            || text.Contains("{role:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("{channel:", StringComparison.OrdinalIgnoreCase)
            || description.Contains("{user", StringComparison.OrdinalIgnoreCase)
            || description.Contains("{role:", StringComparison.OrdinalIgnoreCase)
            || description.Contains("{channel:", StringComparison.OrdinalIgnoreCase);
    }

    public static WelcomeRenderedMessage Render(
        WelcomeSettings settings,
        long newUserId,
        string newUserLabel,
        IReadOnlyDictionary<string, (long Id, string Label)> users,
        IReadOnlyDictionary<string, (long Id, string Label)> roles,
        IReadOnlyDictionary<string, (long Id, string Label)> channels)
    {
        var template = string.IsNullOrWhiteSpace(settings.Text)
            ? MonzeMessages.DefaultWelcomeText
            : settings.Text!;
        var text = RenderText(
            template,
            newUserId,
            newUserLabel,
            users,
            roles,
            channels,
            out var hashtags,
            out var mentions);

        var embed = settings.Embed;
        if (embed is null)
        {
            embed = new WelcomeEmbedSettings(Title: "Chào mừng");
        }
        else if (embed.Description is not null)
        {
            embed = embed with
            {
                Description = RenderText(
                    embed.Description,
                    newUserId,
                    newUserLabel,
                    users,
                    roles,
                    channels,
                    out _,
                    out _)
            };
        }

        var embedOnly = MonzeMessageBuilder.WelcomeCard(
            settings with { Text = null, Embed = embed });
        var builder = new MessageContentBuilder()
            .SetText(text);
        if (embedOnly.Embeds is { Count: > 0 } embeds)
        {
            builder.AddEmbed(embeds[0]);
        }

        for (var i = 0; i < hashtags.Count; i++)
        {
            var hashtag = hashtags[i];
            builder.AddHashtag(hashtag.ChannelId, hashtag.Start, hashtag.End);
        }

        return new WelcomeRenderedMessage(builder.Build(), mentions);
    }

    private static string RenderText(
        string template,
        long newUserId,
        string newUserLabel,
        IReadOnlyDictionary<string, (long Id, string Label)> users,
        IReadOnlyDictionary<string, (long Id, string Label)> roles,
        IReadOnlyDictionary<string, (long Id, string Label)> channels,
        out List<HashtagOnMessage> hashtags,
        out List<MessageMentionParams> mentions)
    {
        hashtags = new List<HashtagOnMessage>();
        mentions = new List<MessageMentionParams>();
        var result = new StringBuilder(template.Length + 32);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var open = template.IndexOf('{', cursor);
            if (open < 0)
            {
                result.Append(template, cursor, template.Length - cursor);
                break;
            }

            result.Append(template, cursor, open - cursor);
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(template, open, template.Length - open);
                break;
            }

            var token = template[(open + 1)..close];
            if (!TryResolveToken(
                    token,
                    newUserId,
                    newUserLabel,
                    users,
                    roles,
                    channels,
                    out var replacement,
                    out var mention,
                    out var hashtagChannelId))
            {
                result.Append(template, open, close - open + 1);
                cursor = close + 1;
                continue;
            }

            var start = result.Length;
            result.Append(replacement);
            var end = result.Length;
            if (mention is MessageMentionParams resolvedMention)
            {
                mentions.Add(new MessageMentionParams(
                    id: resolvedMention.Id,
                    userId: resolvedMention.UserId,
                    username: resolvedMention.Username,
                    roleId: resolvedMention.RoleId,
                    rolename: resolvedMention.Rolename,
                    createTimeSeconds: resolvedMention.CreateTimeSeconds,
                    s: start,
                    e: end));
            }

            if (hashtagChannelId is not null)
            {
                hashtags.Add(new HashtagOnMessage(hashtagChannelId, start, end));
            }

            cursor = close + 1;
        }

        return result.ToString();
    }

    private static bool TryResolveToken(
        string token,
        long newUserId,
        string newUserLabel,
        IReadOnlyDictionary<string, (long Id, string Label)> users,
        IReadOnlyDictionary<string, (long Id, string Label)> roles,
        IReadOnlyDictionary<string, (long Id, string Label)> channels,
        out string replacement,
        out MessageMentionParams? mention,
        out string? hashtagChannelId)
    {
        replacement = string.Empty;
        mention = null;
        hashtagChannelId = null;
        if (token.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            replacement = "@" + newUserLabel;
            mention = new MessageMentionParams(
                userId: newUserId,
                username: newUserLabel);
            return true;
        }

        if (TryReadNamedToken(token, "user", users, out var user))
        {
            replacement = "@" + user.Label;
            mention = new MessageMentionParams(
                userId: user.Id,
                username: user.Label);
            return true;
        }

        if (TryReadNamedToken(token, "role", roles, out var role))
        {
            replacement = "@" + role.Label;
            mention = new MessageMentionParams(
                roleId: role.Id,
                rolename: role.Label);
            return true;
        }

        if (TryReadNamedToken(token, "channel", channels, out var channel))
        {
            replacement = "#" + channel.Label;
            hashtagChannelId = channel.Id.ToString();
            return true;
        }

        return false;
    }

    private static bool TryReadNamedToken(
        string token,
        string kind,
        IReadOnlyDictionary<string, (long Id, string Label)> values,
        out (long Id, string Label) value)
    {
        value = default;
        var prefix = kind + ":";
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = token[prefix.Length..].Trim();
        return name.Length > 0 && values.TryGetValue(name, out value);
    }
}
