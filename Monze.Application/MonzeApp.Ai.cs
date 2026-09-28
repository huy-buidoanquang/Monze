using Monze.Application.Commands;
using Monze.Domain;

namespace Monze.Application;

public sealed partial class MonzeApp
{
    private async Task<CommandOutcome> AiAsync(
        long clanId,
        long channelId,
        long userId,
        string module,
        CommandArguments rest,
        CancellationToken cancellationToken)
    {
        var ai = _ai;
        if (ai is null)
        {
            return Say(MonzeMessages.AiNotConfigured);
        }

        var input = rest.Join(' ');
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

        var gap = await _messageHistory.ChannelHasGapAsync(clanId, channelId, cancellationToken);
        var instruction = MonzeAiInstructions.For(module);
        var generated = await ai.CompleteAsync(instruction, input, cancellationToken);
        var body = generated ?? MonzeMessages.AiProviderEmpty;
        var note = MessageGap.CoverageNote(gap && module == "sum");
        return new CommandOutcome { Text = string.IsNullOrEmpty(note) ? body : body + "\n" + note, HasGap = gap };
    }
}
