using Monze.Application;
using Npgsql;

namespace Monze.Infrastructure.Persistence;

public sealed partial class PostgresOutboxRepository : IOutboxRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresOutboxRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;
}
