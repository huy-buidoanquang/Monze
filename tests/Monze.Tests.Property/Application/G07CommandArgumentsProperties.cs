using CsCheck;
using Monze.Application.Commands;
using Monze.Testing;
using Monze.Tests.Property.Harness;
using Xunit;

namespace Monze.Tests.Property.Application;

/// <summary>
/// G07: CommandArguments behaves exactly like the list it views (with or
/// without a module prefix, after any chain of slices), Normalize maps every
/// documented alias in any ASCII casing and leaves other text untouched, and
/// MonzeBot.IsAiCommand recognises only "ai" followed by an AI action.
/// </summary>
public sealed class G07CommandArgumentsProperties
{
    private static readonly string[] Shapes = ["plain", "prefixed"];
    private static readonly string[] Aliases =
        ["setup", "welcome", "ai", "summary", "translate", "composer", "simplify", "role", "avatar", "ava", "avt", "help", "meeting"];

    private static readonly Gen<string> Item = Gen.OneOf(
        Gen.OneOfConst(Aliases),
        Gen.OneOfConst("", " ", "Ai", "SUMMARY", "tRaNsLaTe", "xin chào", "🎯", "*monze", "role:Member"),
        Gen.String[Gen.Char.AlphaNumeric, 0, 8]);

    [Fact]
    [Req("REQ-CMD-001", "REQ-PERF-001")]
    public void Arguments_view_matches_the_underlying_list()
    {
        var cases =
            from prefixed in Gen.Bool
            from prefix in Gen.OneOfConst(Aliases)
            from items in Item.Array[0, 8]
            from slices in Gen.Int[0, 3].Array[0, 3]
            from separator in Gen.OneOfConst(' ', ',', '\n')
            from joinStart in Gen.Int[0, 10]
            select (prefixed, prefix, items, slices, separator, joinStart);
        PropertyRun.Run(
            "G07",
            cases,
            static value =>
            {
                var (prefixed, prefix, items, slices, separator, joinStart) = value;
                var tags = new Dictionary<string, string>
                {
                    ["shape"] = prefixed ? "prefixed" : "plain",
                    ["slices"] = slices.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                var view = prefixed ? new CommandArguments(prefix, items) : new CommandArguments(items);
                var model = prefixed ? new List<string> { prefix } : new List<string>();
                model.AddRange(items);
                foreach (var slice in slices)
                {
                    if (slice > model.Count)
                    {
                        if (!Throws<ArgumentOutOfRangeException>(() => view.Slice(slice)))
                        {
                            return PropertyResult.Fail(Describe(model), tags, $"Slice({slice}) past the end did not throw");
                        }

                        continue;
                    }

                    view = view.Slice(slice);
                    model = model.Skip(slice).ToList();
                }

                var input = Describe(model);
                if (view.Count != model.Count || view.Length != model.Count || !view.SequenceEqual(model))
                {
                    return PropertyResult.Fail(input, tags, "items differ from the model");
                }

                for (var i = 0; i < model.Count; i++)
                {
                    if (!ReferenceEquals(view[i], model[i]))
                    {
                        return PropertyResult.Fail(input, tags, $"index {i} copied the string");
                    }
                }

                if (!Throws<ArgumentOutOfRangeException>(() => _ = view[model.Count]) || !Throws<ArgumentOutOfRangeException>(() => _ = view[-1]))
                {
                    return PropertyResult.Fail(input, tags, "indexer outside the view did not throw");
                }

                if (joinStart > model.Count)
                {
                    return PropertyResult.Check(Throws<ArgumentOutOfRangeException>(() => view.Join(separator, joinStart)), input, tags, static () => "Join past the end did not throw");
                }

                var joined = view.Join(separator, joinStart);
                return PropertyResult.Check(joined == string.Join(separator, model.Skip(joinStart)), input, tags, () => $"Join gave '{joined}'");
            },
            iterations: 50_000,
            declare: static ledger => ledger.Dimension("shape", Shapes).Dimension("slices", "0", "1", "2", "3"));
    }

    [Fact]
    [Req("REQ-CMD-001", "REQ-AI-001")]
    [Covers("cmd:translate")]
    public void Aliases_normalize_and_ai_actions_are_recognised()
    {
        var casing = Gen.OneOfConst("lower", "upper", "mixed");
        PropertyRun.Run(
            "G07b",
            Gen.Select(Item, casing, Item),
            static value =>
            {
                var (raw, casingName, second) = value;
                var text = casingName switch
                {
                    "upper" => raw.ToUpperInvariant(),
                    "mixed" when raw.Length > 0 => char.ToUpperInvariant(raw[0]) + raw[1..],
                    _ => raw
                };
                var lower = text.ToLowerInvariant();
                var expected = lower switch
                {
                    "ava" or "avt" or "avatar" => MonzeCommandNames.Avatar,
                    _ when Aliases.Contains(lower) => lower,
                    _ => text
                };
                var normalized = MonzeCommandNames.Normalize(text);
                var aiAction = second.ToLowerInvariant() is "summary" or "translate" or "composer" or "simplify";
                var expectedAi = lower == "ai" && aiAction;
                var isAi = Monze.MonzeBot.IsAiCommand(new CommandArguments([text, second]));
                var tags = new Dictionary<string, string>
                {
                    ["known"] = Aliases.Contains(lower) ? "alias" : "other",
                    ["casing"] = casingName,
                    ["ai"] = expectedAi ? "yes" : "no"
                };
                return PropertyResult.Check(
                    normalized == expected && isAi == expectedAi && !Monze.MonzeBot.IsAiCommand(new CommandArguments([text])),
                    $"'{text}' '{second}'",
                    tags,
                    () => $"Normalize gave '{normalized}' (expected '{expected}'), IsAiCommand {isAi} (expected {expectedAi})");
            },
            iterations: 30_000,
            declare: static ledger => ledger
                .Dimension("known", "alias", "other")
                .Dimension("casing", "lower", "upper", "mixed")
                .Dimension("ai", "yes", "no")
                .Infeasible("known", "other", "ai", "yes"));
    }

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static string Describe(IEnumerable<string> items) => "[" + string.Join("|", items) + "]";
}
