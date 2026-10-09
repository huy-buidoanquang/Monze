using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private async Task<CommandOutcome> AiAsync(
        long clanId,
        long channelId,
        long userId,
        CommandArguments rest,
        AiRequestContext? request,
        CancellationToken cancellationToken)
    {
        if (rest.Length == 0 || rest[0].Equals(MonzeCommandNames.Help, StringComparison.OrdinalIgnoreCase))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Ai, cancellationToken);
        }

        var module = MonzeCommandNames.Normalize(rest[0]);
        if (module is not (MonzeCommandNames.AiSummary
            or MonzeCommandNames.Translate
            or MonzeCommandNames.Composer
            or MonzeCommandNames.Simplify))
        {
            return await HelpOutcomeAsync(clanId, userId, MonzeCommandNames.Ai, cancellationToken);
        }

        if (module == MonzeCommandNames.AiSummary
            && request?.ReplyToMessageId is > 0
            && !request.ReplyWithinOneHour)
        {
            return Say(MonzeMessages.AiHistoryWindow, tone: MonzeTone.Warn);
        }

        var ai = _ai;
        if (ai is null)
        {
            return Say(MonzeMessages.AiNotConfigured);
        }

        var input = rest.Join(' ', 1);
        if (module == MonzeCommandNames.AiSummary && !string.IsNullOrWhiteSpace(request?.HistoryText))
        {
            input = string.IsNullOrWhiteSpace(input)
                ? request!.HistoryText!
                : request.HistoryText + "\n" + input;
        }
        if (string.IsNullOrWhiteSpace(input))
        {
            return Say(MonzeMessages.AiInputEmpty);
        }

        if (input.Length > _aiOptions.MaxInputCharacters)
        {
            return Say(MonzeMessages.AiInputTooLong);
        }

        if (!await _aiConcurrency.WaitAsync(0, cancellationToken))
        {
            return Say(MonzeMessages.AiBusy, tone: MonzeTone.Warn);
        }

        try
        {
            return await CompleteAiAsync(clanId, channelId, userId, module, input, ai, cancellationToken);
        }
        finally
        {
            _aiConcurrency.Release();
        }
    }

    private async Task<CommandOutcome> CompleteAiAsync(
        long clanId,
        long channelId,
        long userId,
        string module,
        string input,
        IAiProvider ai,
        CancellationToken cancellationToken)
    {
        var estimatedTokens = Math.Max(1, (input.Length + 3) / 4);
        var budget = await _aiUsage.ConsumeAiAsync(
            clanId,
            userId,
            estimatedTokens,
            _aiOptions.DailyTokenCap,
            cancellationToken);
        if (!budget.Allowed)
        {
            return Say(MonzeMessages.AiBudgetExceeded);
        }

        var instruction = MonzeAiInstructions.For(module);
        string? generated;
        bool gap;
        try
        {
            gap = await _messageHistory.ChannelHasGapAsync(clanId, channelId, cancellationToken);
            generated = await ai.CompleteAsync(instruction, input, cancellationToken);
        }
        catch
        {
            await _aiUsage.RefundAiAsync(clanId, userId, estimatedTokens, CancellationToken.None);
            throw;
        }

        if (generated is null)
        {
            // Nothing was answered (error status, timeout, empty or malformed
            // body): the request does not count against the budget (CAND-05).
            await _aiUsage.RefundAiAsync(clanId, userId, estimatedTokens, cancellationToken);
        }

        var body = LimitAiOutput(generated ?? MonzeMessages.AiProviderEmpty);
        var note = MessageGap.CoverageNote(gap && module == MonzeCommandNames.AiSummary);
        var fields = new List<CommandField>(string.IsNullOrEmpty(note) ? 1 : 2)
        {
            new(string.Empty, body)
        };
        if (!string.IsNullOrEmpty(note))
        {
            fields.Add(new("Lưu ý", note));
        }

        return new CommandOutcome
        {
            Text = string.Empty,
            Fields = fields,
            HasGap = gap
        };
    }

    private static string LimitAiOutput(string value)
    {
        if (value.Length <= AiExecutionOptions.MaxOutputCharacters)
        {
            return value;
        }

        const string suffix = " ...";
        var contentLength = AiExecutionOptions.MaxOutputCharacters - suffix.Length;
        if (contentLength > 0
            && char.IsHighSurrogate(value[contentLength - 1])
            && char.IsLowSurrogate(value[contentLength]))
        {
            contentLength--;
        }

        return value[..contentLength] + suffix;
    }
}
