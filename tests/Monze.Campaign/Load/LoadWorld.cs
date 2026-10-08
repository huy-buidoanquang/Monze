using Monze.Simulator;
using Monze.Testing.Postgres;
using Npgsql;

namespace Monze.Campaign.Load;

/// <summary>
/// The simulated platform of one load stage: <c>registered</c> clans in
/// clan_registry, of which at most 100 are visible to the bot (Mezon
/// discovery is capped at 100). Every active clan has an owner, 30 members
/// and a spamming member, command channels (enough for 40 commands in flight
/// across the stage), three chat channels that persist history, a welcome
/// channel with welcome enabled, and three voice rooms. Ids are 19-digit
/// snowflake-sized values; nothing here is a real account.
/// </summary>
public sealed class LoadWorld
{
    public const int MaxActiveClans = 100;
    private const int MembersPerClan = 30;
    private const long Base = 1_850_000_000_000_000_000L;

    private LoadWorld(SimWorld world, int registered, IReadOnlyList<LoadClan> clans)
    {
        World = world;
        Registered = registered;
        Clans = clans;
    }

    public SimWorld World { get; }

    public int Registered { get; }

    public IReadOnlyList<LoadClan> Clans { get; }

    public long BotId => World.Bot.Id;

    public static LoadWorld Create(int registered)
    {
        var active = Math.Min(registered, MaxActiveClans);
        var commandChannels = Math.Max(2, (int)Math.Ceiling(40.0 / active));
        var world = new SimWorld().WithBot(Base + 1, "monze-load", "simulated-load-token-not-a-secret", "Monze");
        var clans = new List<LoadClan>(active);
        var nextUser = Base + 10_000;
        for (var c = 0; c < active; c++)
        {
            var clanId = Base + 1_000_000 + c;
            var channelBase = Base + 2_000_000 + c * 100L;
            var owner = nextUser++;
            world.User(owner, $"owner{c}", $"Owner {c}");
            var builder = world.Clan(clanId, $"Load clan {c}", owner);
            var members = new List<long>(MembersPerClan);
            for (var m = 0; m < MembersPerClan; m++)
            {
                var member = nextUser++;
                world.User(member, $"member{c}x{m}", $"Member {c}.{m}");
                builder.Member(member);
                members.Add(member);
            }

            var spammer = nextUser++;
            world.User(spammer, $"spam{c}", $"Spammer {c}");
            builder.Member(spammer);
            var commands = Enumerable.Range(0, commandChannels).Select(i => channelBase + i).ToArray();
            foreach (var channel in commands)
            {
                builder.TextChannel(channel, $"lenh-{channel - channelBase}");
            }

            var chat = new[] { channelBase + 50, channelBase + 51, channelBase + 52 };
            foreach (var channel in chat)
            {
                builder.TextChannel(channel, $"chat-{channel - channelBase - 49}");
            }

            var welcome = channelBase + 60;
            builder.TextChannel(welcome, "chao-mung").WelcomeChannel(welcome);
            var voice = new[] { channelBase + 70, channelBase + 71, channelBase + 72 };
            foreach (var channel in voice)
            {
                builder.VoiceChannel(channel, $"Phòng {channel - channelBase - 69}");
            }

            clans.Add(new LoadClan(clanId, owner, members, spammer, commands, chat, welcome, voice));
        }

        return new LoadWorld(world, registered, clans);
    }

    /// <summary>
    /// Seeds the migrated stage database: every registered clan in
    /// clan_registry (registered-only clans are not visible to the bot),
    /// welcome on for active clans, and history persistence on chat channels.
    /// </summary>
    public async Task SeedAsync(CampaignDatabase database)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        var registry = Clans.Select(static clan => clan.Id)
            .Concat(Enumerable.Range(0, Registered - Clans.Count).Select(static i => Base + 5_000_000 + i))
            .ToArray();
        var owners = Clans.Select(static clan => clan.Owner)
            .Concat(Enumerable.Range(0, Registered - Clans.Count).Select(static i => Base + 6_000_000 + i))
            .ToArray();
        await using (var command = new NpgsqlCommand("""
            INSERT INTO clan_registry(clan_id, owner_id) SELECT * FROM unnest(@clans, @owners);
            INSERT INTO clan_settings(clan_id, welcome_enabled, welcome_text)
            SELECT clan, TRUE, 'Chào mừng {user} đến {channel:chat-1}!' FROM unnest(@active) AS clan;
            INSERT INTO channel_policy(clan_id, channel_id, persist_messages)
            SELECT * , TRUE FROM unnest(@chatClans, @chatChannels);
            """, connection))
        {
            command.Parameters.AddWithValue("clans", registry);
            command.Parameters.AddWithValue("owners", owners);
            command.Parameters.AddWithValue("active", Clans.Select(static clan => clan.Id).ToArray());
            command.Parameters.AddWithValue("chatClans", Clans.SelectMany(static clan => clan.Chat.Select(_ => clan.Id)).ToArray());
            command.Parameters.AddWithValue("chatChannels", Clans.SelectMany(static clan => clan.Chat).ToArray());
            await command.ExecuteNonQueryAsync();
        }
    }
}
