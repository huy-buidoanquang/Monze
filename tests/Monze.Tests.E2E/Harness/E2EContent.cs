using Mezon.Net.Client;
using Monze.Simulator;

namespace Monze.Tests.E2E.Harness;

/// <summary>Reads what a user sees in a recorded bot message.</summary>
internal static class E2EContent
{
    public static MessageContent Parse(SimAction action) => MessageContent.Parse(action.ContentJson!);

    /// <summary>Text, embed titles, descriptions, field names and values, in order, as one string.</summary>
    public static string Visible(SimAction action)
    {
        var content = Parse(action);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(content.Text))
        {
            parts.Add(content.Text);
        }

        foreach (var embed in content.Embeds ?? [])
        {
            parts.Add(embed.Title ?? string.Empty);
            parts.Add(embed.Description ?? string.Empty);
            foreach (var field in embed.Fields ?? [])
            {
                parts.Add(field.Name);
                parts.Add(field.Value);
            }
        }

        return string.Join("\n", parts.Where(static part => part.Length > 0));
    }

    /// <summary>Ids of every button in action rows and embed fields.</summary>
    public static IReadOnlyList<string> Buttons(SimAction action)
    {
        var content = Parse(action);
        var rows = (content.Components ?? []).SelectMany(static row => row.Components);
        var fields = (content.Embeds ?? []).SelectMany(static embed => embed.Fields ?? []).SelectMany(static field => field.Buttons ?? []);
        return rows.Concat(fields).OfType<ButtonMessageComponent>().Select(static button => button.Id).ToList();
    }
}
