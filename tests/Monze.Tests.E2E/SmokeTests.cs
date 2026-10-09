using System.Text.Json;
using Mezon.Net.Client;
using Monze.Application.Commands;
using Monze.Simulator;
using Monze.Testing;
using Monze.Tests.E2E.Harness;
using Monze.Ui;
using Xunit;
using Xunit.Abstractions;

namespace Monze.Tests.E2E;

/// <summary>
/// End-to-end smoke tests: the real Monze host on a campaign database talks
/// to the offline Mezon simulator; each test checks the visible response on
/// the simulated wire, the database state and the host log.
/// </summary>
public sealed class SmokeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [DbFact]
    [Req("REQ-HELP-001")]
    public async Task Monze_help_from_a_member_sends_one_help_card_only_that_member_sees()
    {
        await using var host = await MonzeE2EHost.StartAsync(SmokeWorld.Create(), "help");
        var mark = host.Recorder.LastSequence;

        var command = await host.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "*monze help");

        Assert.Equal(1, command.DeliveredSessions);
        await host.Recorder.WaitForAsync(IsChannelOutput(SmokeWorld.GeneralId), Timeout, mark);
        Assert.Equal("completed", await host.WaitForCommandStatusAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, command.MessageId, Timeout));
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(5));

        var reply = Assert.Single(host.Recorder.Since(mark), action => IsChannelOutput(SmokeWorld.GeneralId)(action));
        Assert.Equal(SimActionKind.SendEphemeral, reply.Kind);
        Assert.Equal(0, reply.ResponseCode);
        Assert.Equal(new[] { SmokeWorld.MemberId }, reply.ReceiverIds);
        var values = JsonScalars(reply.ContentJson!);
        Assert.Contains(MonzeMessages.TitleHelp, values);
        Assert.Contains(MonzeButtonId.HelpMeeting, values);
        Assert.Contains(MonzeButtonId.HelpClose, values);
        var stored = host.World.FindMessage(reply.MessageId);
        Assert.NotNull(stored);
        Assert.Equal(SmokeWorld.MemberId, stored.EphemeralReceiverId);
        Assert.DoesNotContain(host.Recorder.Since(mark), action => action.Kind == SimActionKind.SendMessage);
        host.AssertClean();

        // A graceful stop disconnects the socket; Monze logs that as a warning.
        await host.StopHostAsync();
        Report(host);
        host.AssertClean(allowWarningsWithExceptions: true);
    }

    [DbFact]
    [Req("REQ-MTG-001")]
    public async Task Meeting_now_with_one_empty_voice_room_posts_the_meeting_suggestion()
    {
        await using var host = await MonzeE2EHost.StartAsync(SmokeWorld.Create(), "meeting");
        var mark = host.Recorder.LastSequence;

        var command = await host.Inbound.SayAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, SmokeWorld.MemberId, "*meeting now");

        Assert.Equal(1, command.DeliveredSessions);
        await host.Recorder.WaitForAsync(IsChannelOutput(SmokeWorld.GeneralId), Timeout, mark);
        Assert.Equal("completed", await host.WaitForCommandStatusAsync(SmokeWorld.ClanId, SmokeWorld.GeneralId, command.MessageId, Timeout));
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(5));

        var actions = host.Recorder.Since(mark);
        var suggestion = Assert.Single(actions, action => IsChannelOutput(SmokeWorld.GeneralId)(action));
        Assert.Equal(SimActionKind.SendMessage, suggestion.Kind);
        Assert.Equal(0, suggestion.ResponseCode);
        Assert.True(suggestion.MentionEveryone);
        Assert.Null(suggestion.ReplyToMessageId);
        Assert.Equal(
            $"@here Mọi người tham gia phòng {SmokeWorld.VoiceLabel} để bắt đầu cuộc hội thoại.",
            MessageContent.Parse(suggestion.ContentJson!).Text);
        var values = JsonScalars(suggestion.ContentJson!);
        Assert.Contains(SmokeWorld.VoiceId.ToString(System.Globalization.CultureInfo.InvariantCulture), values);
        Assert.Contains(MonzeMessages.MeetingAgentInstruction, values);

        // The room was chosen from the live channel list and voice occupancy.
        Assert.Contains(actions, action => action.Operation == SimOperations.ListChannelDescs && action.ClanId == SmokeWorld.ClanId && action.ResponseCode == 0);
        Assert.Contains(actions, action => action.Operation == SimOperations.ListChannelVoiceUsers && action.ClanId == SmokeWorld.ClanId && action.ResponseCode == 0);

        var session = Assert.Single(await host.RowsAsync(
            "SELECT status, voice_channel_id, notification_message_id FROM meeting_session WHERE clan_id = @clan AND text_channel_id = @channel;",
            ("clan", SmokeWorld.ClanId),
            ("channel", SmokeWorld.GeneralId)));
        Assert.Equal("suggested", session[0]);
        Assert.Equal(SmokeWorld.VoiceId, session[1]);
        Assert.Equal(suggestion.MessageId, session[2]);
        Assert.Equal(1L, await host.ScalarAsync<long>(
            "SELECT count(*) FROM voice_claim WHERE clan_id = @clan AND voice_channel_id = @voice AND expires_at > now();",
            ("clan", SmokeWorld.ClanId),
            ("voice", SmokeWorld.VoiceId)));
        host.AssertClean();

        // A graceful stop disconnects the socket; Monze logs that as a warning.
        await host.StopHostAsync();
        Report(host);
        host.AssertClean(allowWarningsWithExceptions: true);
    }

    private void Report(MonzeE2EHost host)
    {
        output.WriteLine(host.Recorder.Describe(200));
        output.WriteLine(host.Logs.Describe(200));
    }

    /// <summary>Messages, ephemerals, edits and deletes the bot produced in a channel.</summary>
    internal static Func<SimAction, bool> IsChannelOutput(long channelId)
        => action => action.ChannelId == channelId
            && action.Kind is SimActionKind.SendMessage
                or SimActionKind.SendEphemeral
                or SimActionKind.UpdateEphemeral
                or SimActionKind.DeleteEphemeral
                or SimActionKind.UpdateMessage
                or SimActionKind.DeleteMessage;

    /// <summary>Every string and number of a content JSON document.</summary>
    internal static IReadOnlyList<string> JsonScalars(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new List<string>();
        Collect(document.RootElement, values);
        return values;

        static void Collect(JsonElement element, List<string> values)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Collect(property.Value, values);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, values);
                    }

                    break;
                case JsonValueKind.String:
                    values.Add(element.GetString()!);
                    break;
                case JsonValueKind.Number:
                    values.Add(element.GetRawText());
                    break;
            }
        }
    }
}
