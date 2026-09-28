using Monze.Domain;

namespace Monze.Application;

public interface IClanRegistryRepository
{
    Task<IReadOnlyList<KnownClan>> ListClansAsync(CancellationToken cancellationToken);
    Task ApplyClanScanAsync(ClanScanItem item, ClanScanDisposition disposition, CancellationToken cancellationToken);
    Task MarkMissingClansInactiveAsync(IReadOnlyCollection<long> listedClanIds, CancellationToken cancellationToken);
    Task MarkDiscoveryIncompleteAsync(bool incomplete, CancellationToken cancellationToken);
}
