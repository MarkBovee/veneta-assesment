using Microsoft.Data.Sqlite;
using NEventStore;
using NEventStore.Persistence.Sql.SqlDialects;
using NEventStore.Serialization.Json;
using Veneta.Assessments.Backend.ConsumerService.Domain;

namespace Veneta.Assessments.Backend.ConsumerService.Tests;

/// <summary>
/// Tests NEventStore SQLite persistence and optimistic concurrency.
/// </summary>
public sealed class NEventStoreIntegrationTests
{
    /// <summary>
    /// Persists an event stream that can be reopened by another store instance.
    /// </summary>
    [Fact]
    public async Task CommitChangesAsync_PersistsEventAcrossStoreInstances()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"veneta-{Guid.NewGuid()}.db");
        var consumerId = Guid.NewGuid();

        try
        {
            using (var store = CreateStore(databasePath))
            using (var stream = store.CreateStream("consumer", consumerId.ToString()))
            {
                stream.Add(new EventMessage { Body = new ConsumerCreated(consumerId, "Mark", "Bovee", new ConsumerAddress("Main", "1234AB", "1")) });
                var commit = await stream.CommitChangesAsync(Guid.NewGuid());
                Assert.NotNull(commit);
                Assert.Equal(1, commit.StreamRevision);
            }

            using var reopenedStore = CreateStore(databasePath);
            using var reopenedStream = await reopenedStore.OpenStreamAsync("consumer", consumerId.ToString(), 0, int.MaxValue);
            Assert.Equal(1, reopenedStream.StreamRevision);
            Assert.IsType<ConsumerCreated>(Assert.Single(reopenedStream.CommittedEvents).Body);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Rejects second writer after another writer commits the observed stream revision.
    /// </summary>
    [Fact]
    public async Task CommitChangesAsync_RejectsStaleStreamRevision()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"veneta-{Guid.NewGuid()}.db");
        var consumerId = Guid.NewGuid();

        try
        {
            using var store = CreateStore(databasePath);
            using (var stream = store.CreateStream("consumer", consumerId.ToString()))
            {
                stream.Add(new EventMessage { Body = new ConsumerCreated(consumerId, "Mark", "Bovee", new ConsumerAddress("Main", "1234AB", "1")) });
                await stream.CommitChangesAsync(Guid.NewGuid());
            }

            using var firstWriter = await store.OpenStreamAsync("consumer", consumerId.ToString(), 0, int.MaxValue);
            using var secondWriter = await store.OpenStreamAsync("consumer", consumerId.ToString(), 0, int.MaxValue);
            firstWriter.Add(new EventMessage { Body = new ConsumerNameChanged(consumerId, "Markus", "Bovee") });
            secondWriter.Add(new EventMessage { Body = new ConsumerNameChanged(consumerId, "Marcus", "Bovee") });
            await firstWriter.CommitChangesAsync(Guid.NewGuid());

            await Assert.ThrowsAsync<ConcurrencyException>(() => secondWriter.CommitChangesAsync(Guid.NewGuid()));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// Creates NEventStore with same SQLite wireup used by API.
    /// </summary>
    /// <param name="databasePath">Temporary SQLite database path.</param>
    /// <returns>Configured event store.</returns>
    private static IStoreEvents CreateStore(string databasePath) =>
        Wireup.Init().UsingSqlPersistence(SqliteFactory.Instance, $"Data Source={databasePath}").WithDialect(new MicrosoftDataSqliteDialect()).InitializeStorageEngine().UsingJsonSerialization().Build();
}
