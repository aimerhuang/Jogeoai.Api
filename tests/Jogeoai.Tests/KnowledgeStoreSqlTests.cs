using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Jogeoai.Tests;

public sealed class KnowledgeStoreSqlTests
{
    // Requires the isolated stage-1 test database with migration 012 already applied.
    [Stage1MySqlFact]
    public async Task MigratedMySql_ClaimOnIdleQueueReturnsNullWithoutConnectionInUseError()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Knowledge:StorageRoot"] = Path.Combine(Path.GetTempPath(), $"jogeoai-knowledge-sql-{Guid.NewGuid():N}")
        }).Build();
        var store = new KnowledgeStore(new MySqlDatabase(connectionString), new LocalObjectStorage(configuration),
            new DeterministicEmbeddingProvider(), new InMemoryVectorIndex());

        // Hold every job row so SKIP LOCKED sees an idle queue regardless of
        // what other runs left behind; rolling back leaves the rows untouched.
        await using var locker = new MySqlConnection(connectionString);
        await locker.OpenAsync();
        await using var lockTransaction = await locker.BeginTransactionAsync();
        await using (var command = new MySqlCommand("SELECT id FROM knowledge_ingest_jobs FOR UPDATE;", locker, lockTransaction))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) { }

        // Each idle worker iteration must roll back cleanly, not fail on the open reader.
        Assert.Null(await store.ClaimIngestJobAsync("knowledge-sql-test", TimeSpan.FromMinutes(2)));
        Assert.Null(await store.ClaimIngestJobAsync("knowledge-sql-test", TimeSpan.FromMinutes(2)));

        await lockTransaction.RollbackAsync();
    }
}
