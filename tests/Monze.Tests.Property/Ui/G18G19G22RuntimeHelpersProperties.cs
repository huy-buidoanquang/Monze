using CsCheck;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Monze.Ui;
using Xunit;

namespace Monze.Tests.Property.Ui;

/// <summary>
/// G18: VoiceSnapshotGuard against a model of epochs and per-clan generations;
/// a refresh that began before an invalidation is never reported ready, also
/// when the invalidation races the ready mark.
/// G19: MessageGapBatch keeps the highest positive message id per channel.
/// G22: button ids round-trip (cancel ids, welcome save tokens) and other ids
/// are never misread.
/// </summary>
public sealed class G18G19G22RuntimeHelpersProperties
{
    private static readonly string[] Operations = ["begin", "invalidate", "invalidate-all", "mark-latest", "mark-stale", "is-ready"];

    [Fact]
    [Req("REQ-MTG-005")]
    [Covers("sdk:client.VoiceJoined")]
    public void Voice_snapshot_guard_matches_its_model()
    {
        var operations = Gen.Select(Gen.OneOfConst(Operations), Gen.Long[1, 3]).Array[1, 30];
        PropertyRun.Run(
            "G18",
            operations,
            static ops =>
            {
                var guard = new Monze.VoiceSnapshotGuard();
                long epoch = 0;
                var generations = new Dictionary<long, long>();
                var ready = new Dictionary<long, (long, long)>();
                var begun = new Dictionary<long, List<(long, long)>>();
                var tags = new Dictionary<string, string> { ["first"] = ops[0].Item1, ["last"] = ops[^1].Item1 };
                var input = string.Join(" ", ops.Select(static op => $"{op.Item1}({op.Item2})"));
                foreach (var (operation, clan) in ops)
                {
                    var generation = generations.GetValueOrDefault(clan);
                    switch (operation)
                    {
                        case "begin":
                            var version = guard.BeginRefresh(clan);
                            ready.Remove(clan);
                            if (version != (epoch, generation))
                            {
                                return PropertyResult.Fail(input, tags, $"begin returned {version}, model {(epoch, generation)}");
                            }

                            (begun.TryGetValue(clan, out var list) ? list : begun[clan] = []).Add(version);
                            break;
                        case "invalidate":
                            guard.Invalidate(clan);
                            generations[clan] = generation + 1;
                            ready.Remove(clan);
                            break;
                        case "invalidate-all":
                            guard.InvalidateAll();
                            epoch++;
                            ready.Clear();
                            break;
                        case "mark-latest":
                        case "mark-stale":
                            if (!begun.TryGetValue(clan, out var versions) || versions.Count == 0)
                            {
                                break;
                            }

                            var candidate = operation == "mark-latest" ? versions[^1] : versions[0];
                            var current = candidate == (epoch, generation);
                            if (guard.TryMarkReady(clan, candidate) != current)
                            {
                                return PropertyResult.Fail(input, tags, $"mark {candidate} expected {current}");
                            }

                            if (current)
                            {
                                ready[clan] = candidate;
                            }

                            break;
                        default:
                            var expectedReady = ready.TryGetValue(clan, out var marked) && marked == (epoch, generation);
                            if (guard.IsReady(clan) != expectedReady)
                            {
                                return PropertyResult.Fail(input, tags, $"is-ready({clan}) expected {expectedReady}");
                            }

                            break;
                    }
                }

                return PropertyResult.Pass(input, tags);
            },
            iterations: 30_000,
            declare: static ledger => ledger.Dimension("first", Operations).Dimension("last", Operations));
    }

    [Fact]
    [Req("REQ-MTG-005")]
    public async Task An_invalidation_after_begin_is_never_lost_to_a_racing_ready_mark()
    {
        var rounds = 5_000 * CampaignEnvironment.PbtScale;
        using var ledger = CaseLedger.Open("property", "G18b");
        for (var round = 0; round < rounds; round++)
        {
            var guard = new Monze.VoiceSnapshotGuard();
            var version = guard.BeginRefresh(7);
            using var start = new Barrier(2);
            var mark = Task.Run(() =>
            {
                start.SignalAndWait();
                guard.TryMarkReady(7, version);
            });
            var invalidate = Task.Run(() =>
            {
                start.SignalAndWait();
                guard.Invalidate(7);
            });
            await Task.WhenAll(mark, invalidate);
            var readyAfter = guard.IsReady(7);
            ledger.Record(readyAfter ? "fail" : "pass", $"round {round}");
            Assert.False(readyAfter, $"round {round}: a refresh begun before the invalidation is ready");
        }
    }

    [Fact]
    [Req("REQ-ING-001")]
    [Covers("port:IMessageHistoryRepository.MarkChannelGapAsync")]
    public void Gap_batch_keeps_the_highest_positive_message_per_channel()
    {
        var gaps = Gen.Select(Gen.Long[-1, 3], Gen.Long[-1, 3], Gen.Long[-5, 1_000]).Array[0, 40];
        PropertyRun.Run(
            "G19",
            gaps,
            static items =>
            {
                var batch = new Monze.MessageGapBatch(4);
                var model = new Dictionary<(long, long), long>();
                foreach (var (clan, channel, message) in items)
                {
                    batch.Add(clan, channel, message);
                    if (clan > 0 && channel > 0 && message > 0 && (!model.TryGetValue((clan, channel), out var current) || message > current))
                    {
                        model[(clan, channel)] = message;
                    }
                }

                var actual = batch.Entries.ToDictionary(static pair => (pair.Key.ClanId, pair.Key.ChannelId), static pair => pair.Value);
                var tags = new Dictionary<string, string> { ["size"] = model.Count switch { 0 => "empty", <= 4 => "within-capacity", _ => "over-capacity" } };
                var same = actual.Count == model.Count && batch.Count == model.Count && model.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);
                batch.Clear();
                return PropertyResult.Check(same && batch.Count == 0, $"{items.Length} gaps", tags, static () => "gap batch differs from the model");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("size", "empty", "within-capacity", "over-capacity"));
    }

    [Fact]
    [Req("REQ-MTG-001", "REQ-WEL-002")]
    [Covers("btn:monze_meeting_cancel:")]
    [Covers("btn:monze_welcome_save:")]
    public void Button_ids_round_trip_and_reject_foreign_ids()
    {
        var foreign = Gen.OneOfConst(
            "monze_meeting_cancel:", "monze_meeting_cancel:0", "monze_meeting_cancel:-5", "monze_meeting_cancel:+5", "monze_meeting_cancel: 5",
            "monze_meeting_cancel:99999999999999999999", "monze_meeting_now", "monze_welcome_save", "monze_welcome_save:", "MONZE_MEETING_CANCEL:5", "");
        var cases = Gen.OneOf(
            Gen.Long[1, long.MaxValue].Select(static id => ("cancel", MeetingButtonId.CancelFor(id), (object)id)),
            Gen.String[Gen.Char.AlphaNumeric, 1, 24].Select(static token => ("welcome-token", MonzeButtonId.WelcomeSaveFor(token), (object)token)),
            foreign.Select(static id => ("foreign", id, (object)string.Empty)));
        PropertyRun.Run(
            "G22",
            cases,
            static value =>
            {
                var (kind, buttonId, expected) = value;
                var cancelOk = MeetingButtonId.TryReadCancelId(buttonId, out var scheduleId);
                var tokenOk = MonzeButtonId.TryReadWelcomeSaveToken(buttonId, out var token);
                var tags = new Dictionary<string, string> { ["kind"] = kind };
                var correct = kind switch
                {
                    "cancel" => cancelOk && scheduleId == (long)expected && !tokenOk,
                    "welcome-token" => tokenOk && token == (string)expected && !cancelOk,
                    _ => !cancelOk && !(tokenOk && token.Length == 0)
                };
                return PropertyResult.Check(correct, buttonId, tags, () => $"cancel={cancelOk}:{scheduleId} token={tokenOk}:'{token}'");
            },
            iterations: 20_000,
            declare: static ledger => ledger.Dimension("kind", "cancel", "welcome-token", "foreign"));
    }
}
