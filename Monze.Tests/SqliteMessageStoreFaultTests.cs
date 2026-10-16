using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Data.Sqlite;
using Monze.Testing;
using Xunit;

namespace Monze.Tests;

/// <summary>
/// Monze keeps message history in the SDK's SqliteMessageStore, which writes
/// on one background pump. A batch that fails is rolled back, the pump keeps
/// running and the next FlushAsync reports the failure once (CAND-18, fixed in
/// SDK 1.7.0; in 1.6.2 the pump stopped and FlushAsync and DisposeAsync hung).
/// </summary>
public sealed class SqliteMessageStoreFaultTests
{
    [Fact]
    [Req("REQ-ING-001")]
    public async Task One_failed_write_batch_does_not_stop_message_history()
    {
        var directory = Directory.CreateTempSubdirectory("monze-sqlite-fault-").FullName;
        var path = Path.Combine(directory, "messages.db");
        var store = await SqliteMessageStore.OpenAsync(path);
        await using (var injector = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await injector.OpenAsync();
            await ExecuteAsync(injector, "CREATE TRIGGER monze_fault BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            await store.UpsertMessageAsync(Message(1), 1);
            await Task.Delay(500);
            await ExecuteAsync(injector, "DROP TRIGGER monze_fault;");
        }

        await store.UpsertMessageAsync(Message(2), 2);
        await Assert.ThrowsAsync<SqliteException>(() => store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(await store.TryGetMessageAsync(10, 2));
        await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MessageSnapshot Message(long id) => new()
    {
        MessageId = id,
        ChannelId = 10,
        ClanId = 1,
        SenderId = 5,
        Content = "lịch sử " + id,
        CreateTimeSeconds = 1_700_000_000
    };

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
