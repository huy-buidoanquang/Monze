using Microsoft.Extensions.Logging;
using Mezon.Net.Models;
using Mezon.Net.Sdk;
using Monze.Application;
using Monze.Domain;

namespace Monze;

public sealed partial class MonzeBot
{
    private async Task LoginWithRetryAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        var delay = _connectionRetryOptions.InitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation(
                    "Mezon login attempt started. Transport={Transport}, Host={Host}, Port={Port}.",
                    client.Options.TransportType,
                    client.Options.Host,
                    client.Options.Port);
                if (await client.LoginAsync(cancellationToken))
                {
                    _logger.LogInformation(
                        "Mezon login and first socket connection completed. State={ConnectionState}.",
                        client.ConnectionState);
                    return;
                }

                _logger.LogWarning("Mezon bot login returned false; retrying in {RetryDelay}.", delay);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Mezon bot login failed; retrying in {RetryDelay}.", delay);
            }

            await Task.Delay(delay, cancellationToken);
            delay = NextRetryDelay(delay);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task RunStartupOperationWithRetryAsync(
        Func<Task> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        var delay = _connectionRetryOptions.InitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Monze startup operation {Operation} failed; retrying in {RetryDelay}.",
                    operationName,
                    delay);
            }

            await Task.Delay(delay, cancellationToken);
            delay = NextRetryDelay(delay);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private TimeSpan NextRetryDelay(TimeSpan current)
        => TimeSpan.FromTicks(Math.Min(
            _connectionRetryOptions.MaxDelay.Ticks,
            Math.Max(current.Ticks * 2, _connectionRetryOptions.InitialDelay.Ticks)));

    private async Task RefreshClansAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        var known = await _clans.ListClansAsync(cancellationToken);
        _logger.LogInformation("Refreshing Monze clan registry. StoredClans={StoredClans}.", known.Count);
        _knownClans = known;
        await Parallel.ForEachAsync(
            known,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 16,
                CancellationToken = cancellationToken
            },
            async (clan, ct) =>
            {
                try
                {
                    await client.JoinClanAsync(clan.ClanId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not rejoin clan {ClanId}", clan.ClanId);
                }
            });

        var list = await client.ListClanDescsAsync(new ListClanDescParams());
        _logger.LogInformation("Mezon clan discovery response received. ListedClans={ListedClans}.", list.Clandesc.Count);
        var listed = new List<ClanScanItem>(list.Clandesc.Count);
        var listedIds = new HashSet<long>();
        for (var i = 0; i < list.Clandesc.Count; i++)
        {
            var desc = list.Clandesc[i];
            if (desc.WelcomeChannelId > 0)
            {
                _welcomeChannelIds[desc.ClanId] = desc.WelcomeChannelId;
            }
            else
            {
                _welcomeChannelIds.TryRemove(desc.ClanId, out _);
            }
            listed.Add(new ClanScanItem(desc.ClanId, desc.CreatorId));
            listedIds.Add(desc.ClanId);
        }

        var incomplete = ClanDiscovery.ListLooksIncomplete(listed.Count, known.Count);
        await _clans.MarkDiscoveryIncompleteAsync(incomplete, cancellationToken);
        foreach (var (item, disposition) in ClanDiscovery.Merge(known, listed, incomplete))
        {
            await _clans.ApplyClanScanAsync(item, disposition, cancellationToken);
        }

        if (!incomplete)
        {
            await _clans.MarkMissingClansInactiveAsync(listedIds, cancellationToken);
        }

        _knownClans = await _clans.ListClansAsync(cancellationToken);
        _logger.LogInformation("Monze clan registry refresh completed. ActiveOrKnownClans={KnownClans}.", _knownClans.Count);
        await JoinKnownClansAsync(client, cancellationToken);
    }

    private async Task OnClientConnectedAsync(MezonClient client)
    {
        _logger.LogInformation("Mezon socket connected; preparing clan rejoin. KnownClans={KnownClans}.", _knownClans.Count);
        _voiceOccupancy.Clear();
        _voiceChannelsByClan.Clear();
        _welcomeChannelIds.Clear();
        if (_knownClans.Count == 0)
        {
            return;
        }

        await JoinKnownClansAsync(client, _runtimeToken);
    }

    private async Task JoinKnownClansAsync(
        MezonClient client,
        CancellationToken cancellationToken)
    {
        await _clanJoinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Parallel.ForEachAsync(
                _knownClans,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 16,
                    CancellationToken = cancellationToken
                },
                async (clan, reconnectCancellationToken) =>
                {
                    try
                    {
                        await client.JoinClanAsync(clan.ClanId, reconnectCancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not rejoin clan {ClanId} after reconnect", clan.ClanId);
                    }
                });
            _logger.LogInformation("Mezon clan rejoin completed. KnownClans={KnownClans}.", _knownClans.Count);
        }
        finally
        {
            _clanJoinGate.Release();
        }
    }

    private Task OnClientDisconnectedAsync(Exception exception)
    {
        _voiceOccupancy.Clear();
        _voiceChannelsByClan.Clear();
        MonzeMetrics.SocketDisconnects.Add(1);
        _logger.LogWarning(exception, "Mezon socket disconnected; live voice occupancy was invalidated.");
        return Task.CompletedTask;
    }

    private Task OnClientReconnectingAsync(Exception exception)
    {
        MonzeMetrics.SocketReconnects.Add(1);
        _logger.LogInformation(exception, "Mezon socket reconnecting; stored clan registry will be rejoined after connect.");
        return Task.CompletedTask;
    }
}

