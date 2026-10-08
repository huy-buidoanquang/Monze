using System.Text.Json.Nodes;
using CsCheck;
using Mezon.Net.Client;
using Monze.Application;
using Monze.Application.Commands;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Monze.Ui;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G05: welcome setup form round trip. The form built for a section shows
/// some inputs; a client that submits those inputs unchanged (in any of the
/// payload encodings the parser accepts) must read back exactly the values
/// shown, while fields of other sections keep their current values. The
/// status radio reads back the submitted choice.
/// </summary>
public sealed class G05WelcomeFormRoundTripProperties
{
    private static readonly string[] Encodings = ["data-value", "data-string", "suffix", "components"];

    private static readonly (string Id, Func<WelcomeEmbedSettings, string?> Read)[] Properties =
    [
        (MonzeButtonId.WelcomeTitle, static e => e.Title),
        (MonzeButtonId.WelcomeDescription, static e => e.Description),
        (MonzeButtonId.WelcomeUrl, static e => e.Url),
        (MonzeButtonId.WelcomeColor, static e => e.Color),
        (MonzeButtonId.WelcomeAuthor, static e => e.AuthorName),
        (MonzeButtonId.WelcomeAuthorIcon, static e => e.AuthorIconUrl),
        (MonzeButtonId.WelcomeAuthorUrl, static e => e.AuthorUrl),
        (MonzeButtonId.WelcomeThumbnail, static e => e.ThumbnailUrl),
        (MonzeButtonId.WelcomeImage, static e => e.ImageUrl),
        (MonzeButtonId.WelcomeFooter, static e => e.FooterText),
        (MonzeButtonId.WelcomeFooterIcon, static e => e.FooterIconUrl),
        (MonzeButtonId.WelcomeFieldName, static e => e.FieldName),
        (MonzeButtonId.WelcomeFieldValue, static e => e.FieldValue)
    ];

    private static readonly Gen<string?> Value = Gen.OneOfConst<string?>(
        null, "", "  ", "Chào {user}", "https://example.test/a.png", "#5865F2", "Dòng 1\nDòng 2", "\"quoted\" \\ back", "🎯");

    private static readonly Gen<WelcomeEmbedSettings?> Embeds = Gen.OneOf(
        Gen.Bool.Select(static _ => (WelcomeEmbedSettings?)null),
        Value.Array[13, 13].Select(static v => (WelcomeEmbedSettings?)new WelcomeEmbedSettings(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12])));

    [Fact]
    [Req("REQ-WEL-002")]
    [Covers("btn:monze_welcome_save")]
    [Covers("btn:monze_welcome_status")]
    public void Submitting_the_shown_form_reads_back_the_shown_values()
    {
        PropertyRun.Run(
            "G05",
            Gen.Select(Embeds, Gen.Bool, Gen.Bool, Gen.Enum<WelcomeSetupSection>(), Gen.OneOfConst(Encodings), Gen.OneOfConst<string?>(null, "Văn bản chào")),
            static value =>
            {
                var (embed, enabled, choice, section, encoding, text) = value;
                var current = new WelcomeSettings(enabled, text, 3, embed);
                var form = MonzeMessageBuilder.WelcomeSettings(current, section);
                var shown = Inputs(form);
                var extra = Submit(shown, choice ? "on" : "off", encoding);
                var parsed = MonzeWelcomeFormParser.ReadEmbed(extra, current);
                var draft = embed ?? new WelcomeEmbedSettings(Title: "Chào mừng");
                var fallback = embed ?? new WelcomeEmbedSettings(Title: "Chào mừng", Description: text ?? MonzeMessages.DefaultWelcomeText);
                var tags = new Dictionary<string, string>
                {
                    ["section"] = section.ToString(),
                    ["encoding"] = encoding,
                    ["embed"] = embed is null ? "none" : "saved"
                };
                foreach (var (id, read) in Properties)
                {
                    var expected = Normalize(shown.ContainsKey(id) ? read(draft) : read(fallback));
                    var actual = Normalize(read(parsed));
                    if (actual != expected)
                    {
                        return PropertyResult.Fail($"{section} {encoding}", tags, $"{id}: expected '{expected}', got '{actual}'");
                    }
                }

                var status = MonzeWelcomeFormParser.ReadEnabled(extra, !choice);
                return PropertyResult.Check(status == choice && shown.Count > 0, $"{section} {encoding}", tags, () => $"status read {status}, submitted {choice}; {shown.Count} inputs");
            },
            iterations: 20_000,
            declare: static ledger => ledger
                .Dimension("section", Enum.GetNames<WelcomeSetupSection>())
                .Dimension("encoding", Encodings)
                .Dimension("embed", "none", "saved"));
    }

    private static Dictionary<string, string?> Inputs(MessageContent form)
    {
        var inputs = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var field in form.Embeds?.SelectMany(static embed => embed.Fields ?? []) ?? [])
        {
            if (field.Input is InputMessageComponent input)
            {
                inputs[input.Id] = input.DefaultValue;
            }
        }

        return inputs;
    }

    private static string Submit(Dictionary<string, string?> inputs, string status, string encoding)
    {
        var data = new JsonObject();
        var components = new JsonArray();
        foreach (var (id, value) in inputs.Append(KeyValuePair.Create(MonzeButtonId.WelcomeStatus, (string?)status)))
        {
            var text = value ?? string.Empty;
            switch (encoding)
            {
                case "data-value":
                    data[id] = new JsonObject { ["value"] = text };
                    break;
                case "data-string":
                    data[id] = text;
                    break;
                case "suffix":
                    data[id + "-component"] = new JsonObject { ["value"] = text };
                    break;
                default:
                    components.Add(new JsonObject { ["id"] = id, ["value"] = text });
                    break;
            }
        }

        return encoding == "components"
            ? new JsonObject { ["components"] = components }.ToJsonString()
            : new JsonObject { ["data"] = data }.ToJsonString();
    }

    private static string? Normalize(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
