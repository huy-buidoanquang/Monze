using System.Globalization;
using Monze.Application.Commands;
using static Monze.Tests.E2E.Harness.AreaWorld;

namespace Monze.Tests.E2E.Generated;

/// <summary>Which MonzeBot entry point a command text reaches (Hosting/MonzeBot.Routing.cs, MonzeBot.Commands.cs).</summary>
internal enum FuzzRoute
{
    /// <summary>No registered command name: the SDK ignores the message.</summary>
    Unregistered,

    /// <summary>MonzeApp.HandleMonzeAsync (help, setup, welcome, role, ai help, unknown modules).</summary>
    Monze,

    /// <summary>An AI module command: loading card, then HandleMonzeAsync, then an edit.</summary>
    Ai,

    /// <summary>MonzeApp.HandleMeetingAsync (or the meeting help).</summary>
    Meeting,

    /// <summary>MonzeApp.HandleSummaryAsync (or the summary help).</summary>
    Summary,

    /// <summary>MonzeBot.HandleAvatarAsync, outside MonzeApp (not on the twin).</summary>
    Avatar
}

/// <summary>A generated command message and how MonzeBot routes it.</summary>
internal sealed record FuzzCommand(
    string Production,
    long ClanId,
    long ChannelId,
    long ActorId,
    string Text,
    IReadOnlyList<long> Mentions,
    FuzzRoute Route,
    IReadOnlyList<string> Args)
{
    /// <summary>The single mentioned user MonzeBot passes on (TryGetSingleMentionedUserId).</summary>
    public long? MentionedUserId => Mentions.Count == 1 ? Mentions[0] : null;

    /// <summary>"meeting now": the reply depends on live voice occupancy the twin does not model.</summary>
    public bool IsMeetingNow => Route == FuzzRoute.Meeting && Args.Count > 0 && Args[0].Equals("now", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A grammar of every command in docs/commands.md and
/// Monze.Application/Commands (prefix "*", root "monze", direct modules,
/// meeting and summary), with valid and invalid arguments, sent by random
/// actors: in clan A the owner, the delegate, two members; in clan B its
/// owner (an outsider to clan A). Long digit runs never appear in generated
/// text, so oracle 4 (no raw ids) judges Monze's output, not echoed input.
/// </summary>
internal sealed class CommandGrammar(Random random)
{
    private static readonly string[] Words = ["Chào", "mọi", "người", "{user}", "{user:member-nick}", "{channel:general}", "{role:Developer}", "{role:Ghost}", "hôm", "nay", "họp", "nhé", "{channel:lobby}", "🎉"];
    private static readonly string[] Modules = ["meeting", "summary", "welcome", "role", "ai", "setup", "avatar", "help", "xyz"];
    private static readonly string[] Roles = ["Developer", "Veteran", "developer", "Ghost"];
    private static readonly string[] Days = ["0", "1", "7", "30", "365", "-3", "abc", "100000"];
    private static readonly string[] Kinds = ["once", "daily", "weekly", "monthly", "ONCE"];
    private static readonly string[] Ids = ["1", "2", "3", "4", "5", "6", "0", "-1", "abc", "99999"];
    private static readonly (long Id, string Name)[] MentionTargets =
    [
        (MemberId, "member"), (Member2Id, "member2"), (AdminId, "admin"), (OwnerId, "owner"), (OutsiderId, "outsider")
    ];

    public FuzzCommand Next()
    {
        var inClanA = random.Next(100) < 85;
        var clanId = inClanA ? ClanId : OtherClanId;
        var channelId = inClanA ? GeneralId : OtherGeneralId;
        var actor = inClanA ? Pick([OwnerId, AdminId, MemberId, Member2Id]) : OutsiderId;
        var (production, text, mentions) = Production();
        return Route(production, clanId, channelId, actor, text, mentions);
    }

    private (string Production, string Text, long[] Mentions) Production()
    {
        var roll = random.Next(100);
        return roll switch
        {
            < 8 => Help(),
            < 18 => Setup(),
            < 40 => Welcome(),
            < 58 => Role(),
            < 66 => Ai(),
            < 88 => Meeting(),
            < 92 => Summary(),
            < 96 => Avatar(),
            _ => Unregistered()
        };
    }

    private (string, string, long[]) Help()
        => random.Next(5) switch
        {
            0 => ("help", "*monze help", []),
            1 => ("help-module", $"*monze help {Pick(Modules)}", []),
            2 => ("root-only", "*monze", []),
            3 => ("module-help", $"*{Pick(["welcome", "role", "ai", "setup", "meeting", "summary", "avatar"])} help", []),
            _ => ("unknown-module", $"*monze {Pick(["dance", "xyz", "WELCOME", "Meeting"])}", [])
        };

    private (string, string, long[]) Setup()
    {
        var root = random.Next(3) == 0 ? "*monze setup" : "*setup";
        var target = Pick(MentionTargets);
        return random.Next(6) switch
        {
            0 or 1 => ("setup-add", $"{root} admin add @{target.Name}", [target.Id]),
            2 or 3 => ("setup-remove", $"{root} admin remove @{target.Name}", [target.Id]),
            4 => ("setup-add-no-mention", $"{root} admin add", []),
            _ => ("setup-invalid", $"{root} {Pick(["admin", "admins", "list", "admin promote"])}", [])
        };
    }

    private (string, string, long[]) Welcome()
    {
        var root = random.Next(4) == 0 ? "*monze welcome" : "*welcome";
        return random.Next(10) switch
        {
            0 or 1 => ("welcome-on", $"{root} on", []),
            2 => ("welcome-off", $"{root} off", []),
            3 => ("welcome-message", $"{root} message {Sentence()}", []),
            4 => ("welcome-message-remove", $"{root} message remove", []),
            5 => ("welcome-setup", $"{root} setup", []),
            6 => ("welcome-preview", $"{root} preview", []),
            7 => ("welcome-setup-remove", $"{root} setup remove", []),
            8 => ("welcome-message-empty", $"{root} message", []),
            _ => ("welcome-invalid", $"{root} {Pick(["dance", "ON", "enable", "message  "])}".TrimEnd(), [])
        };
    }

    private (string, string, long[]) Role()
    {
        var root = random.Next(4) == 0 ? "*monze role" : "*role";
        return random.Next(9) switch
        {
            0 => ("role-on", $"{root} on", []),
            1 => ("role-off", $"{root} off", []),
            2 or 3 => ("role-join", $"{root} join {Pick(Roles)}", []),
            4 or 5 => ("role-tenure", $"{root} tenure {Pick(Roles)} {Pick(Days)}", []),
            6 => ("role-remove", $"{root} {Pick(["join", "tenure"])} remove {Pick(Roles)}", []),
            7 => ("role-help", root, []),
            _ => ("role-invalid", $"{root} {Pick(["tenure", "join", "dance", "tenure Developer"])}", [])
        };
    }

    private (string, string, long[]) Ai()
    {
        var root = random.Next(4) == 0 ? "*monze ai" : "*ai";
        return random.Next(4) switch
        {
            0 => ("ai-help", root, []),
            1 => ("ai-unknown", $"{root} {Pick(["dance", "poem"])} {Sentence()}", []),
            _ => ("ai-module", $"{root} {Pick(["translate", "composer", "simplify", "summary"])} {Sentence()}", [])
        };
    }

    private (string, string, long[]) Meeting()
    {
        var root = random.Next(5) == 0 ? "*monze meeting" : "*meeting";
        return random.Next(10) switch
        {
            0 or 1 => ("meeting-list", root, []),
            2 or 3 or 4 => ("meeting-schedule", $"{root} {Title()} {Date(valid: true)} {Time(valid: true)} {Pick(Kinds)}", []),
            5 => ("meeting-schedule-invalid", $"{root} {Title()} {Date(valid: false)} {Time(valid: random.Next(2) == 0)} {Pick(Kinds)}", []),
            6 or 7 => ("meeting-cancel", $"{root} cancel {Pick(Ids)}", []),
            8 => ("meeting-now", $"{root} now", []),
            _ => ("meeting-help", $"{root} help", [])
        };
    }

    private (string, string, long[]) Summary()
    {
        var root = random.Next(4) == 0 ? "*monze summary" : "*summary";
        return random.Next(3) switch
        {
            0 => ("summary-id", $"{root} {Pick(Ids)}", []),
            1 => ("summary-empty", root, []),
            _ => ("summary-help", $"{root} help", [])
        };
    }

    private (string, string, long[]) Avatar()
        => ($"avatar", $"*{Pick(["avatar", "ava", "avt"])}{Pick(["", " member", " member2", " nobody", " owner"])}", []);

    private (string, string, long[]) Unregistered()
        => ("unregistered", Pick(["*dance", "*monzee help", "*welcomeon", "hello *monze", "monze help"]), []);

    private static FuzzCommand Route(string production, long clanId, long channelId, long actor, string text, long[] mentions)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!tokens[0].StartsWith('*') || tokens[0].Length < 2)
        {
            return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Unregistered, []);
        }

        var name = tokens[0][1..];
        var rest = tokens.Skip(1).ToList();
        List<string> args;
        if (name == MonzeCommandNames.Monze)
        {
            args = rest;
            if (args.Count > 0 && args[0].Equals(MonzeCommandNames.Meeting, StringComparison.OrdinalIgnoreCase))
            {
                return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Meeting, args.Skip(1).ToList());
            }

            if (args.Count > 0 && args[0].Equals(MonzeCommandNames.Summary, StringComparison.OrdinalIgnoreCase))
            {
                return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Summary, args.Skip(1).ToList());
            }
        }
        else if (MonzeCommandNames.DirectModules.Contains(name))
        {
            args = [name, .. rest];
        }
        else if (name == MonzeCommandNames.Meeting)
        {
            return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Meeting, rest);
        }
        else if (name == MonzeCommandNames.Summary)
        {
            return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Summary, rest);
        }
        else
        {
            return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Unregistered, []);
        }

        var isAvatar = args.Count > 0
            && (args[0].Equals(MonzeCommandNames.Avatar, StringComparison.OrdinalIgnoreCase)
                || args[0].Equals(MonzeCommandNames.AvatarAliasAva, StringComparison.OrdinalIgnoreCase)
                || args[0].Equals(MonzeCommandNames.AvatarAliasAvt, StringComparison.OrdinalIgnoreCase))
            && !(args.Count > 1 && args[1].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase));
        if (isAvatar)
        {
            return new FuzzCommand(production, clanId, channelId, actor, text, mentions, FuzzRoute.Avatar, args);
        }

        var isAi = args.Count > 1
            && args[0].Equals(MonzeCommandNames.Ai, StringComparison.OrdinalIgnoreCase)
            && MonzeCommandNames.Normalize(args[1]) is MonzeCommandNames.AiSummary or MonzeCommandNames.Translate or MonzeCommandNames.Composer or MonzeCommandNames.Simplify;
        return new FuzzCommand(production, clanId, channelId, actor, text, mentions, isAi ? FuzzRoute.Ai : FuzzRoute.Monze, args);
    }

    private string Sentence()
        => string.Join(' ', Enumerable.Range(0, random.Next(1, 6)).Select(_ => Pick(Words)));

    private string Title()
        => Pick(["Standup", "Retro", "Họp-nhóm", "Sync", "Demo", "Kế-hoạch"]);

    private string Date(bool valid)
    {
        if (!valid)
        {
            return Pick(["31/02/2027", "01/01/2020", "2027-13-01", "tomorrow", "32/12/2027", "15-10-2027"]);
        }

        var date = new DateOnly(2027, 1, 1).AddDays(random.Next(0, 700));
        return random.Next(3) == 0
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private string Time(bool valid)
        => valid
            ? $"{random.Next(0, 24):00}:{random.Next(0, 4) * 15:00}"
            : Pick(["25:00", "12:61", "9h", "noon"]);

    private T Pick<T>(IReadOnlyList<T> items) => items[random.Next(items.Count)];
}
