using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>同步 Worker 用内存条目存储与代理知识库验证上传、新版本、停用与同步中途被编辑的分支。</summary>
public sealed class KnowledgeItemSyncWorkerTests
{
    [Fact]
    public async Task FirstSyncCreatesDocumentAndMarksSynced()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.ProductService, "3188DH 管道检测仪", "适用于 DN100 以上管道。", "https://example.test/p");
        var knowledge = new RecordingKnowledge();
        var logs = new CapturingLogger();
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, logs);

        Assert.True(await worker.RunOnceAsync());

        var upload = Assert.Single(knowledge.Uploads);
        Assert.Equal(row.ItemId, upload.ItemId);
        Assert.Null(upload.ExistingDocumentId);
        Assert.Equal(BrandKnowledgeDimension.ProductService, upload.Dimension);
        Assert.Equal(BrandKnowledgeRules.RenderMarkdown(row.Dimension, row.Title, row.Content, row.SourceUrl), upload.Markdown);
        Assert.Contains("来源：https://example.test/p", upload.Markdown, StringComparison.Ordinal);
        Assert.Equal("synced", row.Status);
        Assert.Equal(1, row.SyncedVersion);
        Assert.Equal(upload.ReturnedDocumentId, row.DocumentId);
        Assert.Null(row.LeaseOwner);
        Assert.False(await worker.RunOnceAsync());
        Assert.Single(knowledge.Uploads);
        // 日志只记录标识、维度键与版本，不记录标题或正文。
        Assert.DoesNotContain(logs.Messages, message => message.Contains("3188DH", StringComparison.Ordinal)
            || message.Contains("DN100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditUploadsNewVersionOfTheSameDocument()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.SellingPoint, "防水", "IP68 防水。");
        var knowledge = new RecordingKnowledge();
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, new CapturingLogger());
        await worker.RunOnceAsync();
        var firstDocument = row.DocumentId;
        Assert.NotNull(firstDocument);

        store.Edit(row, "防水等级", "IP68 防水，可在 2 米水深连续工作。");
        Assert.True(await worker.RunOnceAsync());

        Assert.Equal(2, knowledge.Uploads.Count);
        Assert.Equal(firstDocument, knowledge.Uploads[1].ExistingDocumentId);
        Assert.Contains("2 米水深", knowledge.Uploads[1].Markdown, StringComparison.Ordinal);
        Assert.Equal(firstDocument, row.DocumentId);
        Assert.Equal("synced", row.Status);
        Assert.Equal(2, row.SyncedVersion);
        Assert.Empty(knowledge.Disabled);
    }

    [Fact]
    public async Task DeleteDisablesDocumentWithoutUploading()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.Faq, "保修多久？", "整机保修两年。");
        var knowledge = new RecordingKnowledge();
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, new CapturingLogger());
        await worker.RunOnceAsync();
        var document = row.DocumentId!.Value;

        store.Delete(row);
        Assert.True(await worker.RunOnceAsync());

        Assert.Single(knowledge.Uploads);
        Assert.Equal([document], knowledge.Disabled);
        Assert.Equal("synced", row.Status);
        Assert.False(await worker.RunOnceAsync());
    }

    [Fact]
    public async Task DeletedBeforeFirstSyncCompletesWithoutTouchingKnowledge()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.Audience, "工程商", "市政管网施工单位。");
        store.Delete(row);
        var knowledge = new RecordingKnowledge();
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, new CapturingLogger());

        Assert.True(await worker.RunOnceAsync());

        Assert.Empty(knowledge.Uploads);
        Assert.Empty(knowledge.Disabled);
        Assert.Equal("synced", row.Status);
    }

    [Fact]
    public async Task ContentEditedDuringSyncStaysPendingAndNextRoundUploadsLatest()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.BrandIntro, "公司简介", "成立于 2010 年。");
        var knowledge = new RecordingKnowledge
        {
            // 上传进行中用户又保存了一次编辑。
            OnUpload = upload => { if (upload.ExistingDocumentId is null) store.Edit(row, "公司简介", "成立于 2010 年，总部位于深圳。"); }
        };
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, new CapturingLogger());

        Assert.True(await worker.RunOnceAsync());
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.SyncedVersion);
        Assert.Equal(2, row.ContentVersion);
        Assert.NotNull(row.DocumentId);
        var document = row.DocumentId;

        Assert.True(await worker.RunOnceAsync());
        Assert.Equal(2, knowledge.Uploads.Count);
        Assert.Equal(document, knowledge.Uploads[1].ExistingDocumentId);
        Assert.Contains("总部位于深圳", knowledge.Uploads[1].Markdown, StringComparison.Ordinal);
        Assert.Equal("synced", row.Status);
        Assert.Equal(2, row.SyncedVersion);
    }

    [Fact]
    public async Task UnavailableProjectionDocumentIsReplacedByANewDocument()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.Differentiator, "续航", "单次充电续航 8 小时。");
        var stale = Guid.NewGuid();
        row.DocumentId = stale;
        var knowledge = new RecordingKnowledge { DisabledDocuments = { stale } };
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, new CapturingLogger());

        Assert.True(await worker.RunOnceAsync());

        Assert.Equal(2, knowledge.Uploads.Count);
        Assert.Equal(stale, knowledge.Uploads[0].ExistingDocumentId);
        Assert.Null(knowledge.Uploads[1].ExistingDocumentId);
        Assert.Equal(knowledge.Uploads[1].ReturnedDocumentId, row.DocumentId);
        Assert.Equal("synced", row.Status);
    }

    [Fact]
    public async Task UploadFailureIsRecordedWithoutContent()
    {
        var store = new MemoryItemStore();
        var row = store.Add(BrandKnowledgeDimension.SellingPoint, "轻便", "整机仅重 1.2 千克。");
        var knowledge = new RecordingKnowledge { Failure = new CollectorStoreException("KNOWLEDGE_QUOTA_EXCEEDED", "知识库容量已满。", 413) };
        var worker = new KnowledgeItemSyncWorker(store, knowledge.Store, new CapturingLogger());

        Assert.True(await worker.RunOnceAsync());

        Assert.Equal("failed", row.Status);
        Assert.Equal("KNOWLEDGE_QUOTA_EXCEEDED", row.LastErrorCode);
        Assert.True(row.LastRetryable);
        Assert.DoesNotContain("1.2 千克", row.LastErrorMessage, StringComparison.Ordinal);
        Assert.Null(row.DocumentId);
    }

    /// <summary>按 BrandKnowledgeItemStore 的同步语义实现的内存存储（只实现同步相关成员）。</summary>
    private sealed class MemoryItemStore : IBrandKnowledgeItemStore
    {
        public sealed class Row
        {
            public Guid ItemId { get; init; } = Guid.NewGuid();
            public Guid TenantId { get; init; } = Guid.NewGuid();
            public Guid SiteId { get; init; } = Guid.NewGuid();
            public BrandKnowledgeDimension Dimension { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
            public string? SourceUrl { get; init; }
            public int ContentVersion { get; set; } = 1;
            public int SyncedVersion { get; set; }
            public Guid? DocumentId { get; set; }
            public bool Deleted { get; set; }
            public string Status { get; set; } = "pending";
            public int Attempts { get; set; }
            public string? LeaseOwner { get; set; }
            public string? LastErrorCode { get; set; }
            public string LastErrorMessage { get; set; } = string.Empty;
            public bool LastRetryable { get; set; }
        }

        public List<Row> Rows { get; } = [];

        public Row Add(BrandKnowledgeDimension dimension, string title, string content, string? sourceUrl = null)
        {
            var row = new Row { Dimension = dimension, Title = title, Content = content, SourceUrl = sourceUrl };
            Rows.Add(row);
            return row;
        }

        public void Edit(Row row, string title, string content)
        {
            row.Title = title;
            row.Content = content;
            row.ContentVersion++;
            row.Status = "pending";
            row.Attempts = 0;
        }

        public void Delete(Row row)
        {
            row.Deleted = true;
            row.Status = "pending";
            row.Attempts = 0;
        }

        public Task<BrandKnowledgeSyncLease?> ClaimSyncAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            var row = Rows.FirstOrDefault(item => item.Status is "pending" && item.LeaseOwner is null
                && item.Attempts < 5);
            if (row is null) return Task.FromResult<BrandKnowledgeSyncLease?>(null);
            row.LeaseOwner = workerId;
            row.Attempts++;
            return Task.FromResult<BrandKnowledgeSyncLease?>(new BrandKnowledgeSyncLease(row.ItemId, row.TenantId, row.SiteId, row.Dimension,
                row.Title, row.Content, row.SourceUrl, row.ContentVersion, row.SyncedVersion, row.DocumentId, row.Deleted, row.Attempts, workerId));
        }

        public Task<BrandKnowledgeSyncOutcome> CompleteSyncAsync(BrandKnowledgeSyncLease lease, Guid? documentId,
            CancellationToken cancellationToken = default)
        {
            var row = Rows.Single(item => item.ItemId == lease.ItemId);
            if (row.LeaseOwner != lease.LeaseOwner)
            {
                row.DocumentId ??= documentId;
                return Task.FromResult(BrandKnowledgeSyncOutcome.LeaseLost);
            }
            var current = row.ContentVersion == lease.ContentVersion && row.Deleted == lease.Deleted;
            row.DocumentId = documentId ?? row.DocumentId;
            if (current) row.SyncedVersion = lease.ContentVersion;
            row.Status = current ? "synced" : "pending";
            row.Attempts = 0;
            row.LeaseOwner = null;
            return Task.FromResult(current ? BrandKnowledgeSyncOutcome.Synced : BrandKnowledgeSyncOutcome.StillPending);
        }

        public Task FailSyncAsync(BrandKnowledgeSyncLease lease, string errorCode, string errorMessage, bool retryable,
            CancellationToken cancellationToken = default)
        {
            var row = Rows.Single(item => item.ItemId == lease.ItemId);
            row.Status = "failed";
            row.LastErrorCode = errorCode;
            row.LastErrorMessage = errorMessage;
            row.LastRetryable = retryable;
            row.LeaseOwner = null;
            return Task.CompletedTask;
        }

        public Task<BrandKnowledgeItemList> ListAsync(Guid tenantId, Guid siteId, BrandKnowledgeDimension? dimension = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrandKnowledgeItemView?> GetAsync(Guid tenantId, Guid siteId, Guid itemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BrandKnowledgeItemView>> CreateManyAsync(Guid tenantId, Guid siteId, Guid userId,
            IReadOnlyList<BrandKnowledgeItemCreate> items, BrandKnowledgeItemSource source, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<BrandKnowledgeItemView> UpdateAsync(Guid tenantId, Guid siteId, Guid itemId, Guid userId, BrandKnowledgeItemUpdate update,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid tenantId, Guid siteId, Guid itemId, Guid userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BrandKnowledgeResearchItem>> ListForResearchAsync(Guid tenantId, Guid siteId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeProductOption>> ListProductOptionsAsync(Guid tenantId, Guid siteId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ArticleProductRef?> GetProductRefAsync(Guid tenantId, Guid siteId, Guid itemId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record Upload(Guid ItemId, BrandKnowledgeDimension Dimension, string Title, string Markdown, Guid? ExistingDocumentId,
        Guid ReturnedDocumentId);

    /// <summary>只响应条目上传与条目专用停用的知识库代理；其他成员（包括会拒绝条目投影的公开 DisableDocumentAsync）被调用即失败。</summary>
    private sealed class RecordingKnowledge
    {
        public RecordingKnowledge()
        {
            Store = StudioFakes.Of<IKnowledgeStore>((method, args) => method switch
            {
                nameof(IKnowledgeStore.UploadKnowledgeItemDocumentAsync) => (object)UploadAsync(args!),
                nameof(IKnowledgeStore.DisableKnowledgeItemDocumentAsync) => (object)DisableAsync(args!),
                _ => throw new InvalidOperationException($"Unexpected call IKnowledgeStore.{method}")
            });
        }

        public IKnowledgeStore Store { get; }
        public List<Upload> Uploads { get; } = [];
        public List<Guid> Disabled { get; } = [];
        public HashSet<Guid> DisabledDocuments { get; } = [];
        public Action<Upload>? OnUpload { get; init; }
        public Exception? Failure { get; init; }

        private Task<KnowledgeDocumentView> UploadAsync(object?[] args)
        {
            if (Failure is not null) return Task.FromException<KnowledgeDocumentView>(Failure);
            var tenant = (Guid)args[0]!;
            var site = (Guid)args[1]!;
            var existing = (Guid?)args[6];
            var documentId = existing ?? Guid.NewGuid();
            var upload = new Upload((Guid)args[2]!, (BrandKnowledgeDimension)args[3]!, (string)args[4]!, (string)args[5]!, existing, documentId);
            Uploads.Add(upload);
            if (existing is Guid id && DisabledDocuments.Contains(id))
                return Task.FromException<KnowledgeDocumentView>(new CollectorStoreException("RESOURCE_NOT_FOUND", "资料不存在或不属于当前资料范围。", 404));
            OnUpload?.Invoke(upload);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new KnowledgeDocumentView(documentId, tenant, site, "site", null, upload.Title, "pending", "und",
                BrandKnowledgeRules.ToKey(upload.Dimension), upload.ItemId.ToString(), [], null, null, null, null, now, now, SourceKind: "item"));
        }

        private Task<KnowledgeDocumentView?> DisableAsync(object?[] args)
        {
            Disabled.Add((Guid)args[1]!);
            return Task.FromResult<KnowledgeDocumentView?>(null);
        }
    }

    private sealed class CapturingLogger : ILogger<KnowledgeItemSyncWorker>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
