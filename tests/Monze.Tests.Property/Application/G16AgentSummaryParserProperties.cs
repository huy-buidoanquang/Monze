using System.Globalization;
using System.Text.Json.Nodes;
using CsCheck;
using Monze.Application;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Application;

/// <summary>
/// G16: AgentSummaryParser over generated Agent payloads. The generator builds
/// each document from a model and knows the expected result: the envelope
/// (direct, ok + object, ok + array, wrong status, non-object root, invalid
/// JSON), the summary text, full text, timestamps, participants, speech
/// durations and action items (trimmed, blanks and negatives skipped).
/// Known gap CAND-11: elements of an unexpected JSON kind (a non-string status,
/// a number in data/participants/speech_durations) throw
/// InvalidOperationException instead of being rejected or skipped.
/// </summary>
public sealed class G16AgentSummaryParserProperties
{
    private const string Room = "room-1";
    private static readonly string[] Envelopes =
        ["direct", "ok-object", "ok-array", "ok-array-missing", "status-error", "status-non-string", "array-root", "primitive-root", "invalid-json"];

    private static readonly Gen<string?> Text = Gen.OneOf(
        Gen.OneOfConst<string?>("đã xong", "  có khoảng trắng  ", "Họp tuần 🎯", "", "   ", null),
        Gen.String[Gen.Char.AlphaNumeric, 1, 30].Select(static text => (string?)text));

    private static readonly Gen<SummaryModel> Summaries =
        from summary in Gen.OneOf(Text.Select(static text => (object?)text), Gen.Const<object?>(42))
        from fullText in Gen.OneOf(Text.Select(static text => (object?)text), Gen.Const<object?>(7))
        from created in Gen.OneOfConst<string?>("2026-10-01T10:00:00Z", "2026-10-01 17:00:00+07:00", "not a date", null)
        from finalized in Gen.OneOfConst<string?>("2026-10-01T10:30:00Z", "bad", null)
        from completed in Gen.OneOfConst<string?>("2026-10-01T10:45:00Z", null)
        from participants in Gen.OneOf(
            Gen.OneOfConst<object?>(" 100 ", "200", "  ", "id:300").Array[0, 4].Select(static items => (object?)items),
            Gen.Const<object?>(new object?[] { new Dictionary<string, object?> { ["participant_identity"] = "400" }, new Dictionary<string, object?> { ["id"] = " 500 " } }),
            Gen.Const<object?>(new object?[] { "100", 5 }),
            Gen.Const<object?>(new object?[] { null }),
            Gen.Const<object?>("not-an-array"))
        from durations in Gen.OneOf(
            Gen.Select(Gen.OneOfConst("100", " 200 ", ""), Gen.OneOf(Gen.Double[-10, 5_000].Select(static d => (object?)d), Gen.OneOfConst<object?>("12.5", "x", null)))
                .Array[0, 4]
                .Select(static items => (object?)items.Select(static item => (object?)new Dictionary<string, object?> { ["participant_identity"] = item.Item1, ["duration"] = item.Item2 }).ToArray()),
            Gen.Const<object?>(new object?[] { 3 }))
        from actions in Gen.OneOf(
            Gen.Const<object?>(new Dictionary<string, object?> { ["100"] = new object?[] { " Việc một ", "", "Việc hai" }, ["200"] = "  Việc ba ", ["300"] = Array.Empty<object?>(), ["400"] = 5 }),
            Gen.Const<object?>(new object?[] { "not", "an", "object" }),
            Gen.Bool.Select(static _ => (object?)null))
        select new SummaryModel(summary, fullText, created, finalized, completed, participants, durations, actions);

    private static readonly Gen<PayloadCase> Cases =
        from envelope in Gen.OneOfConst(Envelopes)
        from summary in Summaries
        from statusCase in Gen.OneOfConst("ok", "OK", "Ok")
        from others in Gen.Int[0, 2]
        from nonObjectItems in Gen.Bool
        select Build(envelope, summary, statusCase, others, nonObjectItems);

    [Fact]
    [Req("REQ-MTG-004")]
    [Covers("port:ITranscriptClient.FetchSummaryAsync")]
    public void Summary_parser_matches_the_payload_model()
    {
        PropertyRun.Run(
            "G16",
            Cases,
            static item =>
            {
                var tags = new Dictionary<string, string>
                {
                    ["envelope"] = item.Envelope,
                    ["valid"] = item.Expected is null ? "reject" : "accept",
                    ["shape"] = item.UnexpectedKinds ? "unexpected-kinds" : "regular"
                };
                var input = $"{item.Envelope}: {Truncate(item.Json)}";
                bool accepted;
                AgentSummaryResult? result;
                try
                {
                    accepted = AgentSummaryParser.TryParse(item.Json, Room, out var parsed);
                    result = accepted ? parsed : null;
                }
                catch (InvalidOperationException ex) when (item.UnexpectedKinds)
                {
                    return PropertyResult.Known("CAND-11", input, tags, ex.Message);
                }

                var difference = Compare(item.Expected, result);
                if (item.UnexpectedKinds)
                {
                    return difference is null
                        ? PropertyResult.Pass(input, tags, "CAND-11")
                        : PropertyResult.Fail(input, tags, difference);
                }

                return difference is null ? PropertyResult.Pass(input, tags) : PropertyResult.Fail(input, tags, difference);
            },
            iterations: 40_000,
            declare: static ledger => ledger
                .Dimension("envelope", Envelopes)
                .Dimension("valid", "accept", "reject")
                .Infeasible("envelope", "ok-array-missing", "valid", "accept")
                .Infeasible("envelope", "status-error", "valid", "accept")
                .Infeasible("envelope", "status-non-string", "valid", "accept")
                .Infeasible("envelope", "array-root", "valid", "accept")
                .Infeasible("envelope", "primitive-root", "valid", "accept")
                .Infeasible("envelope", "invalid-json", "valid", "accept"),
            knownDefects: ["CAND-11"]);
    }

    private static PayloadCase Build(string envelope, SummaryModel model, string statusCase, int others, bool nonObjectItems)
    {
        var summaryNode = SummaryNode(model, Room);
        var unexpected = model.HasUnexpectedKinds;
        JsonNode? root = envelope switch
        {
            "direct" => summaryNode,
            "ok-object" => new JsonObject { ["status"] = statusCase, ["data"] = summaryNode },
            "ok-array" or "ok-array-missing" => new JsonObject
            {
                ["status"] = statusCase,
                ["data"] = DataArray(envelope == "ok-array" ? summaryNode : null, others, nonObjectItems)
            },
            "status-error" => new JsonObject { ["status"] = "error", ["data"] = summaryNode },
            "status-non-string" => new JsonObject { ["status"] = 1, ["data"] = summaryNode },
            "array-root" => new JsonArray(summaryNode),
            "primitive-root" => JsonValue.Create(Room),
            _ => null
        };
        unexpected |= envelope == "status-non-string" || (envelope.StartsWith("ok-array", StringComparison.Ordinal) && nonObjectItems);
        var json = root?.ToJsonString() ?? "{\"room_id\": \"room-1\", ";
        var expected = envelope is "direct" or "ok-object" or "ok-array" ? Expected(model, summaryNode.ToJsonString()) : null;
        return new PayloadCase(envelope, json, expected, unexpected);
    }

    private static JsonArray DataArray(JsonNode? target, int others, bool nonObjectItems)
    {
        var array = new JsonArray();
        if (nonObjectItems)
        {
            array.Add(1);
            array.Add("text");
        }

        for (var i = 0; i < others; i++)
        {
            array.Add(new JsonObject { ["room_id"] = $"other-{i}", ["summary_data"] = new JsonObject { ["summary"] = "sai" } });
        }

        if (target is not null)
        {
            array.Add(target);
        }

        return array;
    }

    private static JsonObject SummaryNode(SummaryModel model, string room)
    {
        var summaryData = new JsonObject();
        if (model.Summary is not null || model.SummaryIsNumber)
        {
            summaryData["summary"] = model.Summary is string text ? JsonValue.Create(text) : JsonValue.Create(42);
        }

        if (model.Actions is not null)
        {
            summaryData["action_items"] = ToNode(model.Actions);
        }

        var node = new JsonObject { ["room_id"] = room, ["summary_data"] = summaryData };
        if (model.FullText is not null)
        {
            node["full_text"] = ToNode(model.FullText);
        }

        if (model.Created is not null)
        {
            node["created_at"] = model.Created;
        }

        if (model.Finalized is not null)
        {
            node["finalized_at"] = model.Finalized;
        }

        if (model.Completed is not null)
        {
            node["completed_at"] = model.Completed;
        }

        node["participants"] = ToNode(model.Participants);
        node["speech_durations"] = ToNode(model.Durations);
        return node;
    }

    private static ExpectedSummary? Expected(SummaryModel model, string raw)
    {
        if (model.Summary is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var participants = new List<string>();
        if (model.Participants is object?[] items)
        {
            foreach (var item in items)
            {
                var identity = item switch
                {
                    string value => value,
                    Dictionary<string, object?> map => NonBlank(map.GetValueOrDefault("participant_identity")) ?? NonBlank(map.GetValueOrDefault("id")),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    participants.Add(identity.Trim());
                }
            }
        }

        var durations = new List<(string, double)>();
        if (model.Durations is object?[] durationItems)
        {
            foreach (var item in durationItems.OfType<Dictionary<string, object?>>())
            {
                var identity = NonBlank(item.GetValueOrDefault("participant_identity"));
                var duration = item.GetValueOrDefault("duration") switch
                {
                    double number => number,
                    string value when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => (double?)null
                };
                if (identity is not null && duration is >= 0)
                {
                    durations.Add((identity.Trim(), duration.Value));
                }
            }
        }

        var actions = new List<(string, string[])>();
        if (model.Actions is Dictionary<string, object?> groups)
        {
            foreach (var (participant, value) in groups)
            {
                var entries = value switch
                {
                    object?[] list => list.OfType<string>().Where(static entry => !string.IsNullOrWhiteSpace(entry)).Select(static entry => entry.Trim()).ToArray(),
                    string single when !string.IsNullOrWhiteSpace(single) => [single.Trim()],
                    _ => []
                };
                if (entries.Length > 0)
                {
                    actions.Add((participant, entries));
                }
            }
        }

        return new ExpectedSummary(
            text.Trim(),
            NonBlank(model.FullText),
            Timestamp(model.Created),
            Timestamp(model.Finalized) ?? Timestamp(model.Completed),
            participants,
            durations,
            actions,
            raw);
    }

    private static string? Compare(ExpectedSummary? expected, AgentSummaryResult? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null == actual is null ? null : $"expected {(expected is null ? "reject" : "accept")}, got {(actual is null ? "reject" : "accept")}";
        }

        if (actual.RoomId != Room || actual.Summary != expected.Summary || actual.FullText != expected.FullText)
        {
            return "summary or full text differ";
        }

        if (actual.CreatedAt != expected.Created || actual.FinalizedAt != expected.Finalized)
        {
            return $"timestamps differ: {actual.CreatedAt}/{actual.FinalizedAt}";
        }

        if (!actual.Participants.SequenceEqual(expected.Participants))
        {
            return $"participants [{string.Join(",", actual.Participants)}]";
        }

        if (!actual.SpeechDurations.Select(static item => (item.ParticipantIdentity, item.DurationSeconds)).SequenceEqual(expected.Durations))
        {
            return "speech durations differ";
        }

        if (actual.ActionItems.Count != expected.Actions.Count
            || actual.ActionItems.Zip(expected.Actions).Any(static pair => pair.First.ParticipantIdentity != pair.Second.Item1 || !pair.First.Items.SequenceEqual(pair.Second.Item2)))
        {
            return "action items differ";
        }

        return actual.FullTranscriptJson == expected.Raw ? null : "raw transcript differs";
    }

    private static DateTimeOffset? Timestamp(string? value)
        => value is not null
            && !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

    private static string? NonBlank(object? value)
        => value is string text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static JsonNode? ToNode(object? value)
        => value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            int number => JsonValue.Create(number),
            double number => JsonValue.Create(number),
            object?[] items => new JsonArray(items.Select(ToNode).ToArray()),
            Dictionary<string, object?> map => new JsonObject(map.Select(static pair => KeyValuePair.Create(pair.Key, ToNode(pair.Value)))),
            _ => throw new InvalidOperationException(value.GetType().Name)
        };

    private static string Truncate(string json) => json.Length <= 160 ? json : json[..160] + "…";

    private sealed record SummaryModel(
        object? SummaryValue,
        object? FullText,
        string? Created,
        string? Finalized,
        string? Completed,
        object? Participants,
        object? Durations,
        object? Actions)
    {
        public string? Summary => SummaryValue as string;

        public bool SummaryIsNumber => SummaryValue is int;

        public bool HasUnexpectedKinds
            => (Participants is object?[] items && items.Any(static item => item is null or int))
                || (Durations is object?[] durations && durations.Any(static item => item is int));
    }

    private sealed record ExpectedSummary(
        string Summary,
        string? FullText,
        DateTimeOffset? Created,
        DateTimeOffset? Finalized,
        IReadOnlyList<string> Participants,
        IReadOnlyList<(string, double)> Durations,
        IReadOnlyList<(string, string[])> Actions,
        string Raw);

    private sealed record PayloadCase(string Envelope, string Json, ExpectedSummary? Expected, bool UnexpectedKinds);
}
