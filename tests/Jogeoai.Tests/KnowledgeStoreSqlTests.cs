using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Jogeoai.Tests;

// 领取总是选全库最早到期的任务，所以和其他共用隔离库的知识库 SQL 测试类归入同一个串行集合。
[Collection("Knowledge isolated SQL")]
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

    // Requires the isolated stage-1 test database with migration 012 already applied.
    // 租约过期且已执行 3 次的任务：领取时记为失败并让出队列，任务、版本、资料三处状态一致，后面的任务可以被领取。
    [Stage1MySqlFact]
    public async Task MigratedMySql_ExpiredRunningJobAtAttemptCapIsFailedAtClaimAndFreesTheQueue()
    {
        var f = await IngestFixture.CreateAsync();
        try
        {
            // A：资料没有已发布版本。B：资料已有 ready 的第 1 版，卡住的是第 2 版。C：排在后面的正常待处理任务。
            var a = await f.AddDocumentAsync("processing");
            var aVersion = await f.AddVersionAsync(a, 1, "parsing");
            var aJob = await f.AddJobAsync(a, aVersion, "running", KnowledgeStore.MaxIngestAttempts, leaseExpired: true, order: 1);
            var b = await f.AddDocumentAsync("ready");
            var bCurrent = await f.AddVersionAsync(b, 1, "ready");
            await f.SetCurrentVersionAsync(b, bCurrent);
            var bVersion = await f.AddVersionAsync(b, 2, "indexing");
            var bJob = await f.AddJobAsync(b, bVersion, "running", KnowledgeStore.MaxIngestAttempts, leaseExpired: true, order: 2);
            // D：发布时版本已写成 ready、任务却没来得及写成 done 的残留（租约恰好在两条语句之间过期）；放弃任务时不能把已发布的当前版本改成失败。
            var d = await f.AddDocumentAsync("ready");
            var dVersion = await f.AddVersionAsync(d, 1, "ready");
            await f.SetCurrentVersionAsync(d, dVersion);
            var dJob = await f.AddJobAsync(d, dVersion, "running", KnowledgeStore.MaxIngestAttempts, leaseExpired: true, order: 3);
            var c = await f.AddDocumentAsync("pending");
            var cVersion = await f.AddVersionAsync(c, 1, "pending");
            var cJob = await f.AddJobAsync(c, cVersion, "pending", 0, leaseExpired: false, order: 4);
            await f.AssertOwnJobsSortFirstAsync();

            // 每次领取只处理一个任务：先后放弃 A、B、D，都返回 null。
            Assert.Null(await f.Store.ClaimIngestJobAsync("knowledge-reclaim-test", TimeSpan.FromMinutes(5)));
            Assert.Null(await f.Store.ClaimIngestJobAsync("knowledge-reclaim-test", TimeSpan.FromMinutes(5)));
            Assert.Null(await f.Store.ClaimIngestJobAsync("knowledge-reclaim-test", TimeSpan.FromMinutes(5)));

            foreach (var (job, version) in new[] { (aJob, aVersion), (bJob, bVersion) })
            {
                var jobRow = await f.RowAsync("""
                    SELECT status, error_code, error_message, lease_owner, lease_until, completed_at, attempt_count
                    FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                    """, ("@id", job.ToString()));
                Assert.Equal("failed", jobRow[0]);
                Assert.Equal("KNOWLEDGE_INGEST_INTERRUPTED", jobRow[1]);
                Assert.Equal("资料处理多次中断，请重新上传。", jobRow[2]);
                Assert.Null(jobRow[3]);
                Assert.Null(jobRow[4]);
                Assert.NotNull(jobRow[5]);
                // 放弃不算一次新的执行。
                Assert.Equal(KnowledgeStore.MaxIngestAttempts, Convert.ToInt32(jobRow[6], System.Globalization.CultureInfo.InvariantCulture));

                var versionRow = await f.RowAsync("""
                    SELECT status, error_code, error_message, completed_at
                    FROM knowledge_document_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                    """, ("@id", version.ToString()));
                Assert.Equal("failed", versionRow[0]);
                Assert.Equal("KNOWLEDGE_INGEST_INTERRUPTED", versionRow[1]);
                Assert.Equal("资料处理多次中断，请重新上传。", versionRow[2]);
                Assert.NotNull(versionRow[3]);
            }
            // 资料状态与 FailIngestJobAsync 的失败分支一致：没有已发布版本 → failed；已有已发布版本 → 保持 ready，原版本不受影响。
            Assert.Equal("failed", await f.StatusAsync("knowledge_documents", a));
            Assert.Equal("ready", await f.StatusAsync("knowledge_documents", b));
            Assert.Equal("ready", await f.StatusAsync("knowledge_document_versions", bCurrent));
            // D：任务让出队列，已发布的版本和资料保持 ready。
            Assert.Equal("failed", await f.StatusAsync("knowledge_ingest_jobs", dJob));
            Assert.Equal("ready", await f.StatusAsync("knowledge_document_versions", dVersion));
            Assert.Equal("ready", await f.StatusAsync("knowledge_documents", d));

            // 队列不再被挡住：后面的待处理任务被正常领取。
            var lease = await f.Store.ClaimIngestJobAsync("knowledge-reclaim-test", TimeSpan.FromMinutes(5));
            Assert.NotNull(lease);
            Assert.Equal(cJob, lease!.JobId);
            Assert.Equal(1, lease.AttemptCount);
            Assert.Equal("running", await f.StatusAsync("knowledge_ingest_jobs", cJob));
            Assert.Equal("parsing", await f.StatusAsync("knowledge_document_versions", cVersion));
            Assert.Equal("processing", await f.StatusAsync("knowledge_documents", c));
            // 被放弃的任务保持失败，不会回到队列。
            Assert.Equal("failed", await f.StatusAsync("knowledge_ingest_jobs", aJob));
            Assert.Equal("failed", await f.StatusAsync("knowledge_ingest_jobs", bJob));
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    // Requires the isolated stage-1 test database with migration 012 already applied.
    // 已执行 2 次的过期任务仍可重领（第 3 次执行）；这一次也中断后，第 4 次领取把它记为失败。
    [Stage1MySqlFact]
    public async Task MigratedMySql_ExpiredRunningJobBelowAttemptCapIsReclaimedUntilTheCapIsReached()
    {
        var f = await IngestFixture.CreateAsync();
        try
        {
            var document = await f.AddDocumentAsync("processing");
            var version = await f.AddVersionAsync(document, 1, "parsing");
            var job = await f.AddJobAsync(document, version, "running", KnowledgeStore.MaxIngestAttempts - 1, leaseExpired: true, order: 1);
            await f.AssertOwnJobsSortFirstAsync();

            var lease = await f.Store.ClaimIngestJobAsync("knowledge-reclaim-test", TimeSpan.FromMinutes(5));

            Assert.NotNull(lease);
            Assert.Equal(job, lease!.JobId);
            Assert.Equal(KnowledgeStore.MaxIngestAttempts, lease.AttemptCount);
            var claimed = await f.RowAsync("""
                SELECT status, lease_owner, lease_until > UTC_TIMESTAMP(6), attempt_count, error_code
                FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                """, ("@id", job.ToString()));
            Assert.Equal("running", claimed[0]);
            Assert.Equal("knowledge-reclaim-test", claimed[1]);
            Assert.Equal(1L, Convert.ToInt64(claimed[2], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(KnowledgeStore.MaxIngestAttempts, Convert.ToInt32(claimed[3], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(claimed[4]);
            Assert.Equal("parsing", await f.StatusAsync("knowledge_document_versions", version));
            Assert.Equal("processing", await f.StatusAsync("knowledge_documents", document));

            // 第 3 次执行同样没有写回结果、租约过期：下一次领取不再执行，直接记为失败。
            await f.ExpireLeaseAsync(job);
            Assert.Null(await f.Store.ClaimIngestJobAsync("knowledge-reclaim-test", TimeSpan.FromMinutes(5)));
            Assert.Equal("failed", await f.StatusAsync("knowledge_ingest_jobs", job));
            Assert.Equal("failed", await f.StatusAsync("knowledge_document_versions", version));
            Assert.Equal("failed", await f.StatusAsync("knowledge_documents", document));
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    /// <summary>每个测试一个新租户：只用 012 的列直接插入资料、版本和任务，结束时只删除本租户的行。</summary>
    private sealed class IngestFixture
    {
        private readonly string connectionString;
        private readonly string storageRoot = Path.Combine(Path.GetTempPath(), $"jogeoai-knowledge-sql-{Guid.NewGuid():N}");

        private IngestFixture(string connection)
        {
            connectionString = connection;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:StorageRoot"] = storageRoot
            }).Build();
            Store = new KnowledgeStore(new MySqlDatabase(connection), new LocalObjectStorage(configuration),
                new DeterministicEmbeddingProvider(), new InMemoryVectorIndex());
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public KnowledgeStore Store { get; }

        public static async Task<IngestFixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
            Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
            var fixture = new IngestFixture(connectionString);
            await fixture.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'knowledge ingest reclaim fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """);
            return fixture;
        }

        public async Task<Guid> AddDocumentAsync(string status)
        {
            var id = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO knowledge_documents (id, tenant_id, site_id, title, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), NULL, 'reclaim cap fixture', @status, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@status", status));
            return id;
        }

        public async Task<Guid> AddVersionAsync(Guid document, int number, string status)
        {
            var id = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO knowledge_document_versions
                    (id, tenant_id, document_id, version_number, original_file_name, extension, detected_content_type,
                     byte_length, sha256, object_key, status, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), @number, 'fixture.pdf', '.pdf', 'application/pdf',
                        1, @sha, @object_key, @status, UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@document", document.ToString()), ("@number", number),
                ("@sha", new string('a', 64)), ("@object_key", $"{Tenant:N}/{id:N}/source"), ("@status", status));
            return id;
        }

        public Task SetCurrentVersionAsync(Guid document, Guid version)
            => ExecAsync("""
                UPDATE knowledge_documents SET current_version_id=UUID_TO_BIN(@version)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document);
                """, ("@document", document.ToString()), ("@version", version.ToString()));

        /// <summary>插入任务。next_attempt_at 用 2000 年的时间并按 order 递增，使本测试的任务排在全库队列最前。</summary>
        public async Task<Guid> AddJobAsync(Guid document, Guid version, string status, int attemptCount, bool leaseExpired, int order)
        {
            var id = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO knowledge_ingest_jobs
                    (id, tenant_id, document_id, version_id, step, status, attempt_count, lease_owner, lease_until,
                     next_attempt_at, idempotency_key, started_at, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), UUID_TO_BIN(@version), 'upload', @status, @attempts,
                        @owner, CASE WHEN @expired = 1 THEN DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE) ELSE NULL END,
                        TIMESTAMP('2000-01-01 00:00:00') + INTERVAL @order SECOND, @key,
                        CASE WHEN @attempts > 0 THEN UTC_TIMESTAMP(6) ELSE NULL END, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@document", document.ToString()), ("@version", version.ToString()),
                ("@status", status), ("@attempts", attemptCount), ("@owner", leaseExpired ? "crashed-worker" : null),
                ("@expired", leaseExpired ? 1 : 0), ("@order", order), ("@key", $"reclaim-fixture:{id:N}"));
            return id;
        }

        public Task ExpireLeaseAsync(Guid job)
            => ExecAsync("""
                UPDATE knowledge_ingest_jobs SET lease_until=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND status='running';
                """, ("@id", job.ToString()));

        /// <summary>领取选全库最早的任务：若隔离库里留有比本测试更早的可领任务，断言会失去意义，直接报出来。</summary>
        public async Task AssertOwnJobsSortFirstAsync()
        {
            var row = await RowAsync("""
                SELECT COUNT(*) FROM knowledge_ingest_jobs j
                JOIN knowledge_documents d ON d.tenant_id=j.tenant_id AND d.id=j.document_id
                WHERE j.tenant_id <> UUID_TO_BIN(@tenant) AND d.status <> 'disabled'
                  AND ((j.status='pending' AND j.next_attempt_at <= UTC_TIMESTAMP(6))
                    OR (j.status='running' AND j.lease_until < UTC_TIMESTAMP(6)))
                  AND j.next_attempt_at < '2001-01-01 00:00:00';
                """);
            Assert.True(Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture) == 0,
                "隔离库中存在 next_attempt_at 早于 2001 年的其他可领摄取任务，会先于本测试的任务被领取。");
        }

        public async Task<string?> StatusAsync(string table, Guid id)
        {
            Assert.Contains(table, new[] { "knowledge_ingest_jobs", "knowledge_document_versions", "knowledge_documents" });
            var row = await RowAsync($"SELECT status FROM {table} WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                ("@id", id.ToString()));
            return row[0] as string;
        }

        /// <summary>只删除本夹具租户的行；先清空 current_version_id，版本才能删除。</summary>
        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "UPDATE knowledge_documents SET current_version_id=NULL WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_document_versions WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_documents WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql);
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }

        /// <summary>执行语句；@tenant 始终绑定为本夹具租户。</summary>
        public async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>读取恰好一行，NULL 转为 null。</summary>
        public async Task<object?[]> RowAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "预期恰好读到一行。");
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            Assert.False(await reader.ReadAsync(), "预期恰好读到一行。");
            return values;
        }
    }
}
