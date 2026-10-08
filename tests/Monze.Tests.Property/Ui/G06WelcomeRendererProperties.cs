using System.Text;
using System.Text.RegularExpressions;
using CsCheck;
using Monze.Application;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G06: WelcomeMessageRenderer against a reference that replaces every
/// innermost {token}: {user}, {user:name}, {role:name}, {channel:name}
/// (kind case-insensitive, name trimmed, lookups through the caller's
/// dictionaries). Every resolvable token is replaced (regression D6), an
/// unresolvable one stays verbatim, each mention and hashtag span covers
/// exactly its replacement, and no raw id appears in the text.
/// Known gap CAND-13: a stray "{" before a token makes the renderer read
/// "{ {user}" as one unknown token, so the token is not replaced.
/// </summary>
public sealed partial class G06WelcomeRendererProperties
{
    private const long NewUserId = 2_104_288_434_238_525_441;
    private static readonly string[] Pieces = ["text", "user", "named-user", "role", "channel", "unknown", "stray-brace"];

    private static readonly Dictionary<string, (long Id, string Label)> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alice"] = (2_104_288_434_238_525_442, "Alice"),
        ["Bảo"] = (2_104_288_434_238_525_443, "Bảo 🎯")
    };

    private static readonly Dictionary<string, (long Id, string Label)> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Member"] = (2_104_288_434_238_525_450, "Member"),
        ["Quản trị"] = (2_104_288_434_238_525_451, "Quản trị")
    };

    private static readonly Dictionary<string, (long Id, string Label)> Channels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = (2_104_288_434_238_525_460, "general"),
        ["thông-báo"] = (2_104_288_434_238_525_461, "thông-báo")
    };

    private static readonly Gen<(string Kind, string Text)> Piece = Gen.OneOf(
        Gen.OneOfConst("Chào ", " và ", "!", "\n", "tới ", "😀", "{", "}", "{}", ":", "x").Select(static text => ("text", text)),
        Gen.OneOfConst("{user}", "{USER}", "{User}").Select(static text => ("user", text)),
        Gen.OneOfConst("{user:Alice}", "{user: alice }", "{USER:Bảo}").Select(static text => ("named-user", text)),
        Gen.OneOfConst("{role:Member}", "{Role:member}", "{role: Quản trị }").Select(static text => ("role", text)),
        Gen.OneOfConst("{channel:general}", "{CHANNEL:General}", "{channel: thông-báo}").Select(static text => ("channel", text)),
        Gen.OneOfConst("{role:Missing}", "{channel:missing}", "{user:Nobody}", "{role:}", "{unknown}", "{ user }", "{channel}").Select(static text => ("unknown", text)),
        Gen.OneOfConst("{ {user}", "{x{role:Member}", "{{channel:general}").Select(static text => ("stray-brace", text)));

    [Fact]
    [Req("REQ-WEL-003", "REQ-WEL-001")]
    [Covers("msg:DefaultWelcomeText")]
    public void Every_resolvable_token_is_replaced_with_an_exact_span()
    {
        PropertyRun.Run(
            "G06",
            Gen.Select(Piece.Array[0, 8], Gen.Bool, static (pieces, embed) => (pieces, embed)),
            static value =>
            {
                var (pieces, withEmbed) = value;
                var template = string.Concat(pieces.Select(static piece => piece.Text));
                var stray = HasStrayBrace(template);
                var tags = new Dictionary<string, string>
                {
                    ["first"] = pieces.Length == 0 ? "text" : pieces[0].Kind,
                    ["embed"] = withEmbed ? "yes" : "no",
                    ["stray"] = stray ? "yes" : "no"
                };
                var settings = new WelcomeSettings(true, template, 1, withEmbed ? new WelcomeEmbedSettings(Title: "Xin chào", Description: template) : null);
                var rendered = Monze.WelcomeMessageRenderer.Render(settings, NewUserId, "Newbie", Users, Roles, Channels);
                var effectiveTemplate = string.IsNullOrWhiteSpace(template) ? Monze.Application.Commands.MonzeMessages.DefaultWelcomeText : template;
                var expected = Reference(effectiveTemplate);
                var actualText = rendered.Content.Text ?? string.Empty;
                var mentions = rendered.Mentions.Select(static mention => (mention.UserId, mention.RoleId, mention.S, mention.E)).ToList();
                var hashtags = (rendered.Content.Hashtags ?? []).Select(static tag => (tag.ChannelId, tag.Start, tag.End)).ToList();
                var same = actualText == expected.Text
                    && mentions.SequenceEqual(expected.Mentions)
                    && hashtags.SequenceEqual(expected.Hashtags);
                var leaked = RawId().IsMatch(actualText);
                var note = $"expected '{expected.Text}' got '{actualText}'";
                var input = $"'{template}'";
                if (leaked)
                {
                    return PropertyResult.Fail(input, tags, "rendered text contains a raw id");
                }

                if (stray)
                {
                    return same ? PropertyResult.Pass(input, tags, "CAND-13") : PropertyResult.Known("CAND-13", input, tags, note);
                }

                return PropertyResult.Check(same, input, tags, () => note);
            },
            iterations: 100_000,
            declare: static ledger => ledger
                .Dimension("first", Pieces)
                .Dimension("embed", "yes", "no")
                .Dimension("stray", "yes", "no")
                .Infeasible("first", "stray-brace", "stray", "no"),
            knownDefects: ["CAND-13"]);
    }

    private static Rendered Reference(string template)
    {
        var text = new StringBuilder();
        var mentions = new List<(long?, long?, int?, int?)>();
        var hashtags = new List<(string?, int?, int?)>();
        var cursor = 0;
        foreach (Match match in InnermostToken().Matches(template))
        {
            text.Append(template, cursor, match.Index - cursor);
            cursor = match.Index + match.Length;
            var token = match.Groups["token"].Value;
            var start = text.Length;
            if (token.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                text.Append("@Newbie");
                mentions.Add((NewUserId, null, start, text.Length));
            }
            else if (Named(token, "user", Users) is { } user)
            {
                text.Append('@').Append(user.Label);
                mentions.Add((user.Id, null, start, text.Length));
            }
            else if (Named(token, "role", Roles) is { } role)
            {
                text.Append('@').Append(role.Label);
                mentions.Add((null, role.Id, start, text.Length));
            }
            else if (Named(token, "channel", Channels) is { } channel)
            {
                text.Append('#').Append(channel.Label);
                hashtags.Add((channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), start, text.Length));
            }
            else
            {
                text.Append(match.Value);
            }
        }

        text.Append(template, cursor, template.Length - cursor);
        return new Rendered(text.ToString(), mentions, hashtags);
    }

    /// <summary>An "{" whose next "}" has another "{" in between: the CAND-13 shape.</summary>
    private static bool HasStrayBrace(string template)
    {
        for (var open = template.IndexOf('{'); open >= 0; open = template.IndexOf('{', open + 1))
        {
            var close = template.IndexOf('}', open + 1);
            if (close > 0 && template.IndexOf('{', open + 1, close - open - 1) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static (long Id, string Label)? Named(string token, string kind, Dictionary<string, (long Id, string Label)> values)
    {
        if (!token.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = token[(kind.Length + 1)..].Trim();
        return name.Length > 0 && values.TryGetValue(name, out var value) ? value : null;
    }

    [GeneratedRegex("\\{(?<token>[^{}]*)\\}")]
    private static partial Regex InnermostToken();

    [GeneratedRegex("[0-9]{15,}")]
    private static partial Regex RawId();

    private sealed record Rendered(string Text, List<(long?, long?, int?, int?)> Mentions, List<(string?, int?, int?)> Hashtags);
}
