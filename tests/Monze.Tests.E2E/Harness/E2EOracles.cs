using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mezon.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monze.Simulator;
using Monze.Testing;
using Monze.Ui;
using Xunit;

namespace Monze.Tests.E2E.Harness;

/// <summary>
/// The oracles every area scenario ends with. <see cref="AssertAsync"/>
/// applies all of them to everything recorded since a <see cref="E2EMark"/>:
/// <list type="number">
/// <item>No unhandled exception and no worker exit: no host log entry at
/// Error or above, no warning carrying an exception unless the scenario
/// allows it, no unmodelled call (socket or HTTP), no protocol violation,
/// every background service still running and the bot still connected
/// (unless stopped).</item>
/// <item>Each input gets exactly the declared response. Responses are the
/// bot's sends, edits and deletes in the input's channel between the input
/// and the next input there; a reply later edited, or an ephemeral later
/// updated by clicks, is one response. Recognised commands leave exactly one
/// completed command_inbox row, ignored ones none, redelivered ones their one
/// earlier row and no output. Outputs no input explains
/// must match <see cref="ScenarioExpectation.OtherOutputs"/>.</item>
/// <item>Private interactive output is ephemeral. The rule in the code:
/// ReplyCommandAsync sends a command answer as an ephemeral to the author
/// exactly when <c>HasInteractiveComponents</c> finds a button, select,
/// input, date picker or radio (Hosting/MonzeBot.InteractionResponses.cs);
/// every route is registered through <c>RegisterPrivateButton</c>
/// (Hosting/MonzeBot.Routing.cs) and answered with UpdateEphemeral or
/// DeleteEphemeral to the clicking user; public sends (meeting suggestion,
/// welcome, outbox deliveries, failure cards) carry no interactive
/// component. So: a public message or edit has no interactive component,
/// every ephemeral has exactly one receiver, a new ephemeral is interactive,
/// and every button id is a registered private route.</item>
/// <item>No raw id (a run of 15 or more digits) in user-visible text:
/// message text, embed title, description, field names and values, footer,
/// author and button labels. Ids in structured data (mentions, hashtags,
/// component ids, extra_data, URLs) are allowed.</item>
/// <item>No secret canary (<see cref="E2ECanaries"/>) in any bot output or
/// host log line.</item>
/// <item>Every sent or edited content is a JSON object the SDK's
/// MessageContent parses, within the limits Monze and the platform use:
/// text, description and field value at most 4096 characters (mezon web
/// client DEFAULT_MAX_MESSAGE_LENGTH, libs/utils/src/lib/types/config.ts),
/// at most 25 fields per embed (MonzeMessageBuilder.ToEmbedFields), title,
/// author, footer and field name at most 256 characters
/// (MonzeApp.NormalizeWelcomeEmbed), at most 5 components per action row
/// (MonzeMessageBuilder.MeetingSchedules) and at most 1 MiB serialized
/// (SDK MaxWebSocketBinaryPayloadLen).</item>
/// <item>Unauthorized actors change nothing: every business table equals the
/// mark's snapshot (<see cref="BusinessSnapshot"/>).</item>
/// </list>
/// </summary>
internal static partial class E2EOracles
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int MaxTextLength = 4096;
    private const int MaxShortText = 256;
    private const int MaxFields = 25;
    private const int MaxRowComponents = 5;
    private const int MaxPayloadBytes = 1 << 20;
    private static readonly Lazy<(IReadOnlySet<string> Exact, IReadOnlyList<string> Prefixes)> Routes = new(LoadPrivateRoutes);

    /// <summary>Starts a phase; captures the business tables when the phase is unauthorized.</summary>
    public static async Task<E2EMark> MarkAsync(MonzeE2EHost host, bool snapshot = false)
    {
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
        return new E2EMark(
            host.Recorder.LastSequence,
            host.Logs.Entries.Count,
            snapshot ? await BusinessSnapshot.CaptureAsync(host.Database.ConnectionString) : null);
    }

    /// <summary>Applies every oracle to the phase that started at <paramref name="mark"/>.</summary>
    public static async Task AssertAsync(MonzeE2EHost host, E2EMark mark, ScenarioExpectation expectation)
    {
        var failures = new List<string>();
        var all = host.Recorder.Actions;
        var pushes = all.Where(action => action.Sequence > mark.Sequence && action.Kind == SimActionKind.Push)
            .ToDictionary(static action => action.Sequence);
        await CommandsCompletedAsync(host, expectation, pushes, failures);
        await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        var actions = host.Recorder.Since(mark.Sequence);

        NoCrashOrUnmodelled(host, mark, expectation, failures);
        if (!expectation.ResponsesUnchecked)
        {
            ResponsesPerInput(host, actions, expectation, failures);
        }
        foreach (var output in actions.Where(IsOutput))
        {
            if (output.Kind is SimActionKind.DeleteEphemeral or SimActionKind.DeleteMessage)
            {
                continue;
            }

            if (Inspect(output, failures) is { } content)
            {
                PrivateOutputIsEphemeral(output, content, failures);
                NoRawIds(output, content, failures);
            }
        }

        NoSecrets(host, failures);
        if (expectation.Unauthorized)
        {
            if (mark.Snapshot is null)
            {
                failures.Add("Oracle 7: the phase is unauthorized but the mark has no snapshot (MarkAsync(host, snapshot: true)).");
            }
            else
            {
                var differences = mark.Snapshot.Differences(await BusinessSnapshot.CaptureAsync(host.Database.ConnectionString));
                failures.AddRange(differences.Select(static difference => $"Oracle 7: unauthorized actors changed {difference}"));
            }
        }

        if (failures.Count > 0)
        {
            var report = new StringBuilder();
            report.AppendLine($"{failures.Count} oracle failure(s):");
            foreach (var failure in failures)
            {
                report.Append("  - ").AppendLine(failure);
            }

            report.AppendLine(host.Recorder.Describe(80));
            report.AppendLine(host.Logs.Describe(60));
            Assert.Fail(E2ECanaries.Redact(host, report.ToString()));
        }
    }

    /// <summary>
    /// MonzeInvariants on the host's database once the run is quiescent: no
    /// violation except the ids in <paramref name="expected"/> (a known defect
    /// a scenario documents). Leases count as stuck once expired for
    /// <paramref name="leaseGrace"/> (default 5 s; workers poll every second).
    /// </summary>
    public static async Task AssertInvariantsAsync(MonzeE2EHost host, int? aiDailyTokenCap = null, TimeSpan? leaseGrace = null, params string[] expected)
    {
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(host.Database.ConnectionString);
        var violations = await Monze.Testing.Harness.MonzeInvariants.CheckAsync(dataSource, leaseGrace ?? TimeSpan.FromSeconds(5), aiDailyTokenCap);
        var unexpected = violations.Where(violation => !expected.Contains(violation.Id, StringComparer.Ordinal)).ToList();
        Assert.True(unexpected.Count == 0, $"Invariant violations: {string.Join(", ", unexpected)}");
    }

    /// <summary>Whether <paramref name="buttonId"/> is a route registered through RegisterPrivateButton.</summary>
    public static bool IsPrivateRoute(string buttonId)
        => Routes.Value.Exact.Contains(buttonId)
            || Routes.Value.Prefixes.Any(prefix => buttonId.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Exact private route ids (prefix routes excluded).</summary>
    public static IReadOnlySet<string> PrivateRouteIds => Routes.Value.Exact;

    private static bool IsOutput(SimAction action)
        => action.Kind is SimActionKind.SendMessage
            or SimActionKind.SendEphemeral
            or SimActionKind.UpdateEphemeral
            or SimActionKind.DeleteEphemeral
            or SimActionKind.UpdateMessage
            or SimActionKind.DeleteMessage;

    private static bool IsInputPush(SimAction action, long botId)
        => action.Kind == SimActionKind.Push
            && (action.Operation is nameof(SimPushKind.MessageButtonClicked) or nameof(SimPushKind.DropdownBoxSelected)
                || (action.Operation == nameof(SimPushKind.ChannelMessage) && action.TargetUserId != botId));

    private static async Task CommandsCompletedAsync(
        MonzeE2EHost host,
        ScenarioExpectation expectation,
        IReadOnlyDictionary<long, SimAction> pushes,
        List<string> failures)
    {
        foreach (var input in expectation.Inputs)
        {
            if (!pushes.TryGetValue(input.Input.Sequence, out var push) || push.Operation != nameof(SimPushKind.ChannelMessage))
            {
                continue;
            }

            if (input.Response == ResponseKind.Duplicate)
            {
                await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
                var rows = await host.ScalarAsync<long>(
                    "SELECT count(*) FROM command_inbox WHERE clan_id = @clan AND channel_id = @channel AND message_id = @message;",
                    ("clan", push.ClanId),
                    ("channel", push.ChannelId),
                    ("message", push.MessageId));
                if (rows != 1)
                {
                    failures.Add($"Oracle 2: redelivered command #{push.Sequence} has {rows} command_inbox row(s), expected its one earlier row.");
                }

                continue;
            }

            if (input.Response == ResponseKind.None)
            {
                await host.Recorder.WaitForQuietAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
                var rows = await host.ScalarAsync<long>(
                    "SELECT count(*) FROM command_inbox WHERE channel_id = @channel AND message_id = @message;",
                    ("channel", push.ChannelId),
                    ("message", push.MessageId));
                if (rows != 0)
                {
                    failures.Add($"Oracle 2: ignored command #{push.Sequence} left {rows} command_inbox row(s).");
                }

                continue;
            }

            try
            {
                var status = await host.WaitForCommandStatusAsync(push.ClanId, push.ChannelId, push.MessageId, Timeout);
                if (status != "completed")
                {
                    failures.Add($"Oracle 2: command #{push.Sequence} ended as '{status}', not 'completed'.");
                }
            }
            catch (TimeoutException)
            {
                failures.Add($"Oracle 2: command #{push.Sequence} never completed in command_inbox.");
            }
        }
    }

    private static void NoCrashOrUnmodelled(MonzeE2EHost host, E2EMark mark, ScenarioExpectation expectation, List<string> failures)
    {
        foreach (var entry in host.Logs.Entries.Skip(mark.LogCount))
        {
            if (entry.Level >= LogLevel.Error)
            {
                failures.Add($"Oracle 1: host logged {entry.Level}: {entry}");
            }
            else if (entry.Level == LogLevel.Warning
                && entry.Exception is not null
                && !expectation.AllowedWarnings.Any(allowed => entry.Message.Contains(allowed, StringComparison.Ordinal)))
            {
                failures.Add($"Oracle 1: warning with an exception not allowed by the scenario: {entry}");
            }
        }

        failures.AddRange(host.Recorder.UnmodelledCalls.Select(static call => $"Oracle 1: unmodelled call {call.Operation}: {call.Detail}"));
        failures.AddRange(host.Recorder.ProtocolViolations.Select(static violation => $"Oracle 1: protocol violation {violation.Operation}: {violation.Detail}"));
        failures.AddRange((host.Http?.Unmodelled ?? []).Select(static call => $"Oracle 1: unmodelled HTTP call {call}"));
        if (host.IsStopped)
        {
            return;
        }

        foreach (var worker in host.Services.GetServices<IHostedService>().OfType<BackgroundService>())
        {
            if (worker.ExecuteTask is not { IsCompleted: false })
            {
                failures.Add($"Oracle 1: background service {worker.GetType().Name} is not running ({worker.ExecuteTask?.Status.ToString() ?? "not started"}).");
            }
        }

        if (!host.OwnSessions.Any(static session => session.IsConnected))
        {
            failures.Add("Oracle 1: the bot is no longer connected.");
        }
    }

    private static void ResponsesPerInput(MonzeE2EHost host, IReadOnlyList<SimAction> actions, ScenarioExpectation expectation, List<string> failures)
    {
        var botId = host.World.Bot.Id;
        var inputs = actions.Where(action => IsInputPush(action, botId)).ToList();
        var declared = expectation.Inputs.ToDictionary(static input => input.Input.Sequence);
        foreach (var push in inputs)
        {
            var known = declared.ContainsKey(push.Sequence)
                || expectation.Inputs.Any(input => input.Input.MessageId == push.MessageId && input.Input.Kind.ToString() == push.Operation && push.Fault is not null);
            if (!known)
            {
                failures.Add($"Oracle 2: undeclared input #{push.Sequence} {push.Operation} in channel {push.ChannelId}.");
            }
        }

        var outputs = actions.Where(IsOutput).ToList();
        var attributed = new HashSet<long>();
        foreach (var input in expectation.Inputs.OrderBy(static input => input.Input.Sequence))
        {
            var push = actions.FirstOrDefault(action => action.Sequence == input.Input.Sequence);
            if (push is null)
            {
                failures.Add($"Oracle 2: declared input #{input.Input.Sequence} is not in this phase.");
                continue;
            }

            var end = inputs
                .Where(next => next.ChannelId == push.ChannelId && next.Sequence > push.Sequence)
                .Select(static next => next.Sequence)
                .DefaultIfEmpty(long.MaxValue)
                .Min();
            var mine = outputs.Where(output => output.ChannelId == push.ChannelId && output.Sequence > push.Sequence && output.Sequence < end).ToList();
            attributed.UnionWith(mine.Select(static output => output.Sequence));
            var problem = CheckResponse(input.Response, push, mine);
            if (problem is not null)
            {
                failures.Add($"Oracle 2: input #{push.Sequence} {push.Operation} by {push.TargetUserId} expected {input.Response}: {problem} [{string.Join("; ", mine)}]");
            }
        }

        var others = outputs.Where(output => !attributed.Contains(output.Sequence)).ToList();
        if (others.Count != expectation.OtherOutputs)
        {
            failures.Add($"Oracle 2: {others.Count} output(s) belong to no input, expected {expectation.OtherOutputs}: [{string.Join("; ", others)}]");
        }
    }

    private static string? CheckResponse(ResponseKind kind, SimAction push, IReadOnlyList<SimAction> mine)
    {
        var actor = push.TargetUserId;
        var sends = mine.Where(static output => output.Kind == SimActionKind.SendMessage).ToList();
        var ephemerals = mine.Where(static output => output.Kind == SimActionKind.SendEphemeral).ToList();
        var edits = mine.Where(static output => output.Kind == SimActionKind.UpdateMessage).ToList();
        var updates = mine.Where(static output => output.Kind == SimActionKind.UpdateEphemeral).ToList();
        var deletes = mine.Where(static output => output.Kind is SimActionKind.DeleteEphemeral or SimActionKind.DeleteMessage).ToList();
        if (kind is not (ResponseKind.None or ResponseKind.UpdateRejected) && mine.Any(static output => output.ResponseCode != 0))
        {
            return "the platform rejected part of the response";
        }

        bool ToActor(SimAction output) => output.ReceiverIds.Count == 1 && output.ReceiverIds[0] == actor;
        return kind switch
        {
            ResponseKind.None or ResponseKind.Duplicate => mine.Count == 0 ? null : "the bot answered",
            ResponseKind.Ephemeral => ephemerals.Count == 1 && mine.Count == 1 && ToActor(ephemerals[0])
                ? null
                : "expected exactly one ephemeral to the actor and nothing else",
            ResponseKind.Reply => sends.Count == 1 && mine.Count == 1 && sends[0].ReplyToMessageId == push.MessageId
                ? null
                : "expected exactly one public reply to the command and nothing else",
            ResponseKind.Public => sends.Count == 1 && mine.Count == 1 && sends[0].ReplyToMessageId is null
                ? null
                : "expected exactly one public message and nothing else",
            ResponseKind.EditedReply => sends.Count == 1
                    && sends[0].ReplyToMessageId == push.MessageId
                    && edits.Count >= 1
                    && edits.All(edit => edit.MessageId == sends[0].MessageId)
                    && mine.Count == 1 + edits.Count
                ? null
                : "expected one public reply edited in place and nothing else",
            ResponseKind.Update => updates.Count == 1 && mine.Count == 1 && updates[0].MessageId == push.MessageId && ToActor(updates[0])
                ? null
                : "expected exactly one update of the clicked ephemeral to the actor",
            ResponseKind.Delete => deletes.Count == 1 && mine.Count == 1 && deletes[0].MessageId == push.MessageId && ToActor(deletes[0])
                ? null
                : "expected exactly one delete of the clicked ephemeral",
            ResponseKind.UpdateRejected => updates.Count == 1 && mine.Count == 1 && updates[0].MessageId == push.MessageId && ToActor(updates[0]) && updates[0].ResponseCode != 0
                ? null
                : "expected exactly one refused update of the clicked message to the actor",
            ResponseKind.UpdateAndPublic => sends.Count == 1
                    && sends[0].ReplyToMessageId is null
                    && updates.Count == 1
                    && updates[0].MessageId == push.MessageId
                    && ToActor(updates[0])
                    && mine.Count == 2
                ? null
                : "expected one public message and one update of the clicked ephemeral",
            _ => $"unknown response kind {kind}"
        };
    }

    private static MessageContent? Inspect(SimAction output, List<string> failures)
    {
        var json = output.ContentJson ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
        {
            failures.Add($"Oracle 6: #{output.Sequence} content is larger than 1 MiB.");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failures.Add($"Oracle 6: #{output.Sequence} content is not a JSON object.");
                return null;
            }
        }
        catch (JsonException ex)
        {
            failures.Add($"Oracle 6: #{output.Sequence} content is not valid JSON ({ex.Message}).");
            return null;
        }

        MessageContent content;
        try
        {
            content = MessageContent.Parse(json);
            _ = content.Embeds;
            _ = content.Components;
        }
        catch (Exception ex)
        {
            failures.Add($"Oracle 6: #{output.Sequence} content does not parse as MessageContent ({ex.GetType().Name}).");
            return null;
        }

        Limit(output, "text", content.Text, MaxTextLength, failures);
        foreach (var embed in content.Embeds ?? [])
        {
            Limit(output, "embed title", embed.Title, MaxShortText, failures);
            Limit(output, "embed description", embed.Description, MaxTextLength, failures);
            Limit(output, "embed author", embed.Author?.Name, MaxShortText, failures);
            Limit(output, "embed footer", embed.Footer?.Text, MaxShortText, failures);
            if (embed.Fields is { Count: > MaxFields })
            {
                failures.Add($"Oracle 6: #{output.Sequence} embed has {embed.Fields.Count} fields (max {MaxFields}).");
            }

            foreach (var field in embed.Fields ?? [])
            {
                Limit(output, "field name", field.Name, MaxShortText, failures);
                Limit(output, "field value", field.Value, MaxTextLength, failures);
            }
        }

        foreach (var row in content.Components ?? [])
        {
            if (row.Components.Count > MaxRowComponents)
            {
                failures.Add($"Oracle 6: #{output.Sequence} action row has {row.Components.Count} components (max {MaxRowComponents}).");
            }
        }

        return content;
    }

    private static void Limit(SimAction output, string what, string? value, int max, List<string> failures)
    {
        if (value is not null && value.Length > max)
        {
            failures.Add($"Oracle 6: #{output.Sequence} {what} has {value.Length} characters (max {max}).");
        }
    }

    private static void PrivateOutputIsEphemeral(SimAction output, MessageContent content, List<string> failures)
    {
        var interactive = new List<string>();
        var buttons = new List<string>();
        foreach (var component in Components(content))
        {
            if (component.ComponentType is MessageComponentType.Button
                or MessageComponentType.Select
                or MessageComponentType.Input
                or MessageComponentType.DatePicker
                or MessageComponentType.Radio)
            {
                interactive.Add(component.Id);
            }

            if (component is ButtonMessageComponent)
            {
                buttons.Add(component.Id);
            }
        }

        foreach (var button in buttons.Where(static id => !IsPrivateRoute(id)))
        {
            failures.Add($"Oracle 3: #{output.Sequence} has button '{button}' that no RegisterPrivateButton route handles.");
        }

        if (output.Kind is SimActionKind.SendMessage or SimActionKind.UpdateMessage)
        {
            if (interactive.Count > 0)
            {
                failures.Add($"Oracle 3: public {output.Kind} #{output.Sequence} carries interactive component(s) {string.Join(", ", interactive)}.");
            }

            return;
        }

        if (output.ReceiverIds.Count != 1)
        {
            failures.Add($"Oracle 3: ephemeral #{output.Sequence} has {output.ReceiverIds.Count} receivers.");
        }

        if (output.Kind == SimActionKind.SendEphemeral && interactive.Count == 0)
        {
            failures.Add($"Oracle 3: #{output.Sequence} is a new ephemeral without interactive components (ReplyCommandAsync answers those publicly).");
        }
    }

    private static void NoRawIds(SimAction output, MessageContent content, List<string> failures)
    {
        void Check(string what, string? text)
        {
            if (text is not null && RawId().IsMatch(text))
            {
                failures.Add($"Oracle 4: #{output.Sequence} shows a raw id in {what}.");
            }
        }

        Check("text", content.Text);
        foreach (var embed in content.Embeds ?? [])
        {
            Check("embed title", embed.Title);
            Check("embed description", embed.Description);
            Check("embed author", embed.Author?.Name);
            Check("embed footer", embed.Footer?.Text);
            foreach (var field in embed.Fields ?? [])
            {
                Check("field name", field.Name);
                Check("field value", field.Value);
            }
        }

        foreach (var component in Components(content))
        {
            if (component is ButtonMessageComponent button)
            {
                Check("button label", button.Label);
            }
        }
    }

    private static void NoSecrets(MonzeE2EHost host, List<string> failures)
    {
        var secrets = E2ECanaries.Secrets(host);
        foreach (var action in host.Recorder.Actions.Where(static action => action.IsOutbound && action.ContentJson is not null))
        {
            foreach (var (name, value) in secrets)
            {
                if (action.ContentJson!.Contains(value, StringComparison.Ordinal))
                {
                    failures.Add($"Oracle 5: {name} appears in bot output #{action.Sequence} {action.Kind}.");
                }
            }
        }

        var entries = host.Logs.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            var line = entries[i].Message + (entries[i].Exception?.ToString() ?? string.Empty);
            foreach (var (name, value) in secrets)
            {
                if (line.Contains(value, StringComparison.Ordinal))
                {
                    failures.Add($"Oracle 5: {name} appears in host log entry {i} ({entries[i].Category}).");
                }
            }
        }
    }

    private static IEnumerable<MessageComponent> Components(MessageContent content)
    {
        foreach (var row in content.Components ?? [])
        {
            foreach (var component in row.Components)
            {
                yield return component;
            }
        }

        foreach (var embed in content.Embeds ?? [])
        {
            foreach (var field in embed.Fields ?? [])
            {
                if (field.Input is not null)
                {
                    yield return field.Input;
                }

                foreach (var button in field.Buttons ?? [])
                {
                    yield return button;
                }
            }
        }
    }

    /// <summary>
    /// Reads the routes from Hosting/MonzeBot.Routing.cs and resolves the
    /// button-id constants, so the oracle follows the code. Fails when a
    /// route is registered any other way than through RegisterPrivateButton.
    /// </summary>
    private static (IReadOnlySet<string> Exact, IReadOnlyList<string> Prefixes) LoadPrivateRoutes()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "Hosting", "MonzeBot.Routing.cs"));
        var direct = DirectRoute().Matches(source).Count;
        if (direct != 1 || source.Contains(".OnSelect(", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MonzeBot.Routing.cs registers routes outside RegisterPrivateButton; update E2EOracles oracle 3.");
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new List<string>();
        foreach (Match match in PrivateRoute().Matches(source))
        {
            var type = match.Groups["type"].Value == nameof(MeetingButtonId) ? typeof(MeetingButtonId) : typeof(MonzeButtonId);
            var field = type.GetField(match.Groups["member"].Value, BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Unknown button id constant {match.Value}.");
            var value = (string)field.GetRawConstantValue()!;
            if (match.Groups["wildcard"].Success)
            {
                prefixes.Add(value);
            }
            else
            {
                exact.Add(value);
            }
        }

        if (exact.Count < 10)
        {
            throw new InvalidOperationException("Could not read the private routes from MonzeBot.Routing.cs.");
        }

        return (exact, prefixes);
    }

    [GeneratedRegex("\\d{15,}")]
    private static partial Regex RawId();

    [GeneratedRegex("RegisterPrivateButton\\(\\s*interactions,\\s*(?<type>MonzeButtonId|MeetingButtonId)\\.(?<member>\\w+)(?<wildcard>\\s*\\+\\s*\"\\*\")?")]
    private static partial Regex PrivateRoute();

    [GeneratedRegex("\\.OnButton\\(")]
    private static partial Regex DirectRoute();
}
