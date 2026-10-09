using Monze.Simulator;

namespace Monze.Campaign.Chaos;

/// <summary>
/// A clan reserved for the Agent and AI traffic of HTTP chaos scenarios, so
/// its invitations, status edits, summaries and AI cards never land in the
/// command channels the background load measures. The first two voice
/// rooms carry the meeting cycles, the last two the bursts.
/// </summary>
public sealed record AgentClan(
    long Id,
    long Owner,
    IReadOnlyList<long> Members,
    long MeetingChannel,
    long AiChannel,
    IReadOnlyList<long> Voices)
{
    private const long Base = 1_860_000_000_000_000_000L;

    /// <summary>Adds two Agent clans (owner, ten members, a meeting and an AI channel, four voice rooms) to the world.</summary>
    public static IReadOnlyList<AgentClan> AddTo(SimWorld world)
    {
        var clans = new List<AgentClan>(2);
        for (var c = 0; c < 2; c++)
        {
            var clanId = Base + 1_000 + c;
            var owner = Base + 10_000 + c * 100L;
            var channels = Base + 20_000 + c * 100L;
            world.User(owner, $"agent-owner{c}", $"Agent Owner {c}");
            var builder = world.Clan(clanId, $"Agent clan {c}", owner)
                .TextChannel(channels, "hop-agent")
                .TextChannel(channels + 1, "ai");
            var voices = new List<long>(4);
            for (var v = 0; v < 4; v++)
            {
                builder.VoiceChannel(channels + 10 + v, $"Phòng Agent {v + 1}");
                voices.Add(channels + 10 + v);
            }

            var members = new List<long>(10);
            for (var m = 1; m <= 10; m++)
            {
                world.User(owner + m, $"agent{c}x{m}", $"Agent Member {c}.{m}");
                builder.Member(owner + m);
                members.Add(owner + m);
            }

            clans.Add(new AgentClan(clanId, owner, members, channels, channels + 1, voices));
        }

        return clans;
    }
}
