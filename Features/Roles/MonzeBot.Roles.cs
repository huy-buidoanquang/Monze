using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task ConsumeAutomaticRoleRulesAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.GetValue("Monze:Roles:ScanIntervalSeconds", 300),
            60,
            3600));

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

            await Task.Delay(interval, cancellationToken);
        }
    }
}
