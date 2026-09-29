using Monze.Application;
using Monze.Ui;
using Xunit;

namespace Monze.Tests;

public sealed class MonzeWelcomeFormParserTests
{
    [Fact]
    public void Read_form_supports_radio_and_nested_input_values()
    {
        const string extra = """
            {"data":{"monze_welcome_status":{"value":"on"},"monze_welcome_title":{"value":"Chào thành viên"},"monze_welcome_description":"Nội dung mới"}}
            """;
        var current = new WelcomeSettings(false, "Cũ", 1);

        Assert.True(MonzeWelcomeFormParser.ReadEnabled(extra, false));
        var draft = MonzeWelcomeFormParser.ReadEmbed(extra, current);
        Assert.Equal("Chào thành viên", draft.Title);
        Assert.Equal("Nội dung mới", draft.Description);
    }

    [Fact]
    public void Read_form_keeps_saved_values_when_client_sends_partial_payload()
    {
        const string extra = """{"monze_welcome_status":"off"}""";
        var current = new WelcomeSettings(
            true,
            null,
            2,
            new WelcomeEmbedSettings(Title: "Đã lưu", Color: "#2563EB"));

        Assert.False(MonzeWelcomeFormParser.ReadEnabled(extra, current.Enabled));
        var draft = MonzeWelcomeFormParser.ReadEmbed(extra, current);
        Assert.Equal("Đã lưu", draft.Title);
        Assert.Equal("#2563EB", draft.Color);
    }

    [Fact]
    public void Read_form_supports_the_nested_component_id_used_by_message_inputs()
    {
        const string extra = """{"data":{"monze_welcome_title-component":{"value":"Nested title"}}}""";

        var draft = MonzeWelcomeFormParser.ReadEmbed(
            extra,
            new WelcomeSettings(false, null, 1));

        Assert.Equal("Nested title", draft.Title);
    }
}
