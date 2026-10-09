using System.Collections.Concurrent;
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

            await Task.Delay(delay, _time, cancellationToken);
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

            await Task.Delay(delay, _time, cancellationToken);
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
        SetKnownClans(known);
        var joinedBeforeDiscovery = await JoinClanBatchAsync(client, known, cancellationToken);

        var list = await client.ListClanDescsAsync(new ListClanDescParams());
        _logger.LogInformation("Mezon clan discovery response received. ListedClans={ListedClans}.", list.Clandesc.Count);
        if (ClanDiscovery.ListLooksCapped(list.Clandesc.Count))
        {
            // mezon-api answers ListClanDescs with at most 100 clans, ignoring limit and
            // cursor (DEF-06); clans beyond it are joined only once they are registered.
            _logger.LogWarning(
                "Mezon clan discovery returned the server's cap of {Cap} clans; clans beyond it are not discovered.",
                ClanDiscovery.AssumedClanCap);
        }

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
        foreach (var (item, disposition) in ClanDiscovery.Merge(known, listed))
        {
            await _clans.ApplyClanScanAsync(item, disposition, cancellationToken);
        }

        if (!incomplete)
        {
            await _clans.MarkMissingClansInactiveAsync(listedIds, cancellationToken);
        }

        SetKnownClans(await _clans.ListClansAsync(cancellationToken));
        var pendingJoins = ClanDiscovery.PendingJoins(_knownClans, joinedBeforeDiscovery);
        if (pendingJoins.Count > 0)
        {
            await JoinClanBatchAsync(client, pendingJoins, cancellationToken);
        }

        _logger.LogInformation("Monze clan registry refresh completed. ActiveOrKnownClans={KnownClans}.", _knownClans.Count);
    }

    private async Task OnClientConnectedAsync(MezonClient client)
    {
        _logger.LogInformation("Mezon socket connected; preparing clan rejoin. KnownClans={KnownClans}.", _knownClans.Count);
        _voiceOccupancy.Clear();
        _voiceChannelsByClan.Clear();
        _voiceClanByChannel.Clear();
        _voiceSnapshots.InvalidateAll();
        _welcomeChannelIds.Clear();
        if (_knownClans.Count == 0)
        {
            return;
        }

        await JoinClanBatchAsync(client, _knownClans, _runtimeToken);
        _logger.LogInformation("Mezon clan rejoin completed. KnownClans={KnownClans}.", _knownClans.Count);
    }

    private async Task<IReadOnlySet<long>> JoinClanBatchAsync(
        MezonClient client,
        IReadOnlyList<KnownClan> clans,
        CancellationToken cancellationToken)
    {
        await _clanJoinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var joined = new ConcurrentDictionary<long, byte>();
            await Parallel.ForEachAsync(
                clans,
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
                        joined.TryAdd(clan.ClanId, 0);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Could not join clan {ClanId}", clan.ClanId);
                    }
                });
            var result = new HashSet<long>(joined.Keys);
            _logger.LogInformation(
                "Mezon clan join batch completed. RequestedClans={RequestedClans}, JoinedClans={JoinedClans}.",
                clans.Count,
                result.Count);
            return result;
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
        _voiceClanByChannel.Clear();
        _voiceSnapshots.InvalidateAll();
        MonzeMetrics.SocketDisconnects.Add(1);
        _logger.LogWarning(exception, "Mezon socket disconnected; live voice occupancy was invalidated.");
        return EnqueueMeetingContextResetAsync();
    }

    private Task OnClientReconnectingAsync(Exception exception)
    {
        MonzeMetrics.SocketReconnects.Add(1);
        _logger.LogInformation(exception, "Mezon socket reconnecting; stored clan registry will be rejoined after connect.");
        return Task.CompletedTask;
    }

    private void SetKnownClans(IReadOnlyList<KnownClan> clans)
    {
        var ids = new HashSet<long>(clans.Count);
        for (var i = 0; i < clans.Count; i++)
        {
            ids.Add(clans[i].ClanId);
        }

        _knownClans = clans;
        Volatile.Write(ref _knownClanIds, ids);
    }

    private bool IsKnownClan(long clanId)
        => Volatile.Read(ref _knownClanIds).Contains(clanId);
}

