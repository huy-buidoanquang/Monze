using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task ConsumeAutomaticRoleRulesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _app.ApplyAutomaticRoleRulesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic role rule scan failed; retrying on the next interval.");
            }

            await Task.Delay(_timings.RoleScanInterval, _time, cancellationToken);
        }
    }
}
