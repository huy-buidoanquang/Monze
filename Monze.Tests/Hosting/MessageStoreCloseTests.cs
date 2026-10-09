using System.Diagnostics;
using Mezon.Net.Sdk.Caching.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Monze.Testing;
using Xunit;

namespace Monze.Tests.Hosting;

/// <summary>
/// Monze's side of CAND-18: with the SDK store's write pump stopped by one
/// failed batch (SqliteMessageStoreFaultTests), shutdown still ends within
/// the close timeout and says that queued history writes were lost.
/// </summary>
public sealed class MessageStoreCloseTests
{
    [Fact]
    [Req("REQ-ING-001")]
    public async Task Closing_a_store_whose_writer_stopped_is_bounded_and_reported()
    {
        var directory = Directory.CreateTempSubdirectory("monze-sqlite-close-").FullName;
        var path = Path.Combine(directory, "messages.db");
        var store = await SqliteMessageStore.OpenAsync(path);
        await using (var injector = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await injector.OpenAsync();
            await using var command = injector.CreateCommand();
            command.CommandText = "CREATE TRIGGER monze_fault BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT, 'injected'); END;";
            await command.ExecuteNonQueryAsync();
            await store.UpsertMessageAsync(new MessageSnapshot { MessageId = 1, ChannelId = 10, ClanId = 1, SenderId = 5, Content = "a", CreateTimeSeconds = 1_700_000_000 }, 1);
            await Task.Delay(500);
        }

        await store.UpsertMessageAsync(new MessageSnapshot { MessageId = 2, ChannelId = 10, ClanId = 1, SenderId = 5, Content = "b", CreateTimeSeconds = 1_700_000_000 }, 2);
        var logger = new LevelLogger();
        var watch = Stopwatch.StartNew();

        await MonzeBot.CloseMessageStoreAsync(store, TimeSpan.FromSeconds(1), logger, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"closing took {watch.Elapsed}");
        Assert.Equal([LogLevel.Error], logger.Levels);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    [Req("REQ-ING-001")]
    public async Task A_healthy_store_is_flushed_and_closed_without_a_log()
    {
        var directory = Directory.CreateTempSubdirectory("monze-sqlite-close-").FullName;
        var store = await SqliteMessageStore.OpenAsync(Path.Combine(directory, "messages.db"));
        await store.UpsertMessageAsync(new MessageSnapshot { MessageId = 1, ChannelId = 10, ClanId = 1, SenderId = 5, Content = "a", CreateTimeSeconds = 1_700_000_000 }, 1);
        var logger = new LevelLogger();

        await MonzeBot.CloseMessageStoreAsync(store, TimeSpan.FromSeconds(5), logger, CancellationToken.None);

        Assert.Empty(logger.Levels);
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class LevelLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }
}
