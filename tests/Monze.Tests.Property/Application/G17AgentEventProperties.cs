using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CsCheck;
using Monze.Application;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Application;

/// <summary>
/// G17: Agent event payload, durable identity and scope policy.
/// AgentEventPayload reads room id (string or integer, top level or room.room_id),
/// voice channel (voice_channel_id, else numeric room.room_name) and clan id,
/// keeping only positive ids; AgentEventIdentity prefers event_id/eventId and
/// otherwise hashes kind, type and payload; the scope policy accepts only voice
/// channels of known clans whose payload clan, if any, matches.
/// A JSON array or scalar root is a payload without fields (regression for
/// CAND-01: both readers used to throw InvalidOperationException).
/// </summary>
public sealed class G17AgentEventProperties
{
    private static readonly string[] Roots = ["object", "array", "scalar", "invalid"];

    private static readonly Gen<object?> IdValue = Gen.OneOf(
        Gen.Long[1, long.MaxValue].Select(static id => (object?)id),
        Gen.Long[1, 10_000].Select(static id => (object?)id.ToString(CultureInfo.InvariantCulture)),
        Gen.OneOfConst<object?>(0L, -5L, "0", "-7", " +45 ", "12a", "99999999999999999999", 1.5, true, null));

    private static readonly Gen<object?> RoomValue = Gen.OneOf(
        Gen.OneOfConst<object?>("room-1", "", "  ", 123L, 1.5, false, null),
        Gen.String[Gen.Char.AlphaNumeric, 1, 12].Select(static text => (object?)text));

    private static readonly Gen<EventCase> Cases =
        from root in Gen.OneOfConst(Roots)
        from room in RoomValue
        from nested in Gen.Bool
        from voice in IdValue
        from roomName in IdValue
        from clan in IdValue
        from eventId in Gen.OneOfConst<object?>(null, "evt-1", "  ", 42L, "evt-2")
        from eventIdKey in Gen.OneOfConst("event_id", "eventId")
        from kind in Gen.Byte[0, 3]
        select Build(root, room, nested, voice, roomName, clan, eventId, eventIdKey, kind);

    [Fact]
    [Req("REQ-MTG-003")]
    [Covers("sdk:client.AgentSessionStarted")]
    public void Payload_and_identity_follow_the_event_contract()
    {
        PropertyRun.Run(
            "G17",
            Cases,
            static item =>
            {
                var tags = new Dictionary<string, string> { ["root"] = item.Root, ["event-id"] = item.ExpectedEventId is null ? "absent" : "present" };
                var input = item.Json.Length <= 160 ? item.Json : item.Json[..160] + "…";
                var parsed = AgentEventPayload.TryParse(item.Json, out var payload);
                var identity = AgentEventIdentity.Compute(item.Kind, "session_started", item.Json);

                var expectedIdentity = item.ExpectedEventId is { } eventId
                    ? $"event:{item.Kind.ToString(CultureInfo.InvariantCulture)}:{eventId}"
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{item.Kind}\nsession_started\n{item.Json}")));
                var expectedParsed = item.Root != "invalid";
                var correct = parsed == expectedParsed
                    && (!parsed || payload == item.Expected)
                    && identity == expectedIdentity
                    && AgentEventIdentity.Compute((byte)(item.Kind + 1), "session_started", item.Json) != identity;
                var note = $"parsed={parsed} payload={payload} expected={item.Expected}; identity {(identity == expectedIdentity ? "ok" : "differs")}";
                return PropertyResult.Check(correct, input, tags, () => note);
            },
            iterations: 40_000,
            declare: static ledger => ledger
                .Dimension("root", Roots)
                .Dimension("event-id", "absent", "present")
                .Infeasible("root", "array", "event-id", "present")
                .Infeasible("root", "scalar", "event-id", "present")
                .Infeasible("root", "invalid", "event-id", "present"));
    }

    [Fact]
    [Req("REQ-MTG-003")]
    public void Scope_policy_accepts_only_voice_channels_of_known_clans()
    {
        var voice = (int)Mezon.Net.Core.ChannelType.MezonVoice;
        PropertyRun.Run(
            "G17b",
            Gen.Select(Gen.Int[0, 12], Gen.OneOfConst<long?>(null, 1, 2, 3), Gen.Long[1, 3], Gen.Long[1, 3].ArrayUnique[0, 3]),
            value =>
            {
                var (channelType, payloadClan, resolved, known) = value;
                var knownSet = known.ToHashSet();
                var expected = channelType == voice && knownSet.Contains(resolved) && (payloadClan is null || payloadClan == resolved);
                var actual = Monze.AgentEventScopePolicy.Allows(payloadClan, resolved, channelType, knownSet);
                var tags = new Dictionary<string, string>
                {
                    ["voice"] = channelType == voice ? "voice" : "other",
                    ["known"] = knownSet.Contains(resolved) ? "known" : "unknown",
                    ["payload-clan"] = payloadClan is null ? "absent" : payloadClan == resolved ? "same" : "different"
                };
                return PropertyResult.Check(actual == expected, $"type={channelType} payload={payloadClan} resolved={resolved} known=[{string.Join(",", known)}]", tags, () => $"expected {expected}, got {actual}");
            },
            iterations: 20_000,
            declare: static ledger => ledger
                .Dimension("voice", "voice", "other")
                .Dimension("known", "known", "unknown")
                .Dimension("payload-clan", "absent", "same", "different"));
    }

    private static EventCase Build(string root, object? room, bool nested, object? voice, object? roomName, object? clan, object? eventId, string eventIdKey, byte kind)
    {
        if (root == "invalid")
        {
            return new EventCase(root, "{\"room_id\": ", kind, null, default);
        }

        if (root == "array")
        {
            return new EventCase(root, new JsonArray(JsonValue.Create("room-1"), JsonValue.Create(1)).ToJsonString(), kind, null, default);
        }

        if (root == "scalar")
        {
            return new EventCase(root, room is string text ? JsonValue.Create(text).ToJsonString() : "17", kind, null, default);
        }

        var node = new JsonObject();
        if (nested)
        {
            node["room"] = new JsonObject { ["room_id"] = Value(room), ["room_name"] = Value(roomName) };
        }
        else
        {
            node["room_id"] = Value(room);
        }

        node["voice_channel_id"] = Value(voice);
        node["clan_id"] = Value(clan);
        if (eventId is not null)
        {
            node[eventIdKey] = Value(eventId);
        }

        var expected = new AgentEventPayload(
            room switch
            {
                string text => text,
                long number => number.ToString(CultureInfo.InvariantCulture),
                _ => null
            },
            PositiveId(voice) ?? (nested ? PositiveId(roomName) : null),
            PositiveId(clan));
        var expectedEventId = eventId switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            long number => number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
        return new EventCase(root, node.ToJsonString(), kind, expectedEventId, expected);
    }

    private static long? PositiveId(object? value)
        => value switch
        {
            long number when number > 0 => number,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
            _ => null
        };

    private static JsonNode? Value(object? value)
        => value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            long number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            _ => throw new InvalidOperationException(value.GetType().Name)
        };

    private sealed record EventCase(string Root, string Json, byte Kind, string? ExpectedEventId, AgentEventPayload Expected);
}
