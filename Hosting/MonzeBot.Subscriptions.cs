using Mezon.Net.Sdk;

namespace Monze;

public sealed partial class MonzeBot
{
    private void SubscribeRealtimeEvents(MezonClient client)
    {
        client.AgentSessionStarted += evt => EnqueueAgentAsync(evt, AgentEventKind.Started);
        client.AgentSessionEnded += evt => EnqueueAgentAsync(evt, AgentEventKind.Ended);
        client.AgentSessionSummaryDone += evt => EnqueueAgentAsync(evt, AgentEventKind.SummaryDone);
        client.ChannelMessageReceived += EnqueueMessageAsync;
        client.ChannelCreated += OnChannelCreatedAsync;
        client.ChannelUpdated += OnChannelUpdatedAsync;
        client.ChannelDeleted += OnChannelDeletedAsync;
        client.ClanUserAdded += EnqueueWelcomeAsync;
        client.VoiceJoined += OnVoiceJoinedAsync;
        client.VoiceLeaved += OnVoiceLeavedAsync;
        client.VoiceEnded += OnVoiceEndedAsync;
    }

    private void SubscribeConnectionEvents(MezonClient client)
    {
        client.Connected += () => OnClientConnectedAsync(client);
        client.Disconnected += OnClientDisconnectedAsync;
        client.Reconnecting += OnClientReconnectingAsync;
    }
}
