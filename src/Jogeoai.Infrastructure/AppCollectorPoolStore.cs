using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// App 全局账号池的 MySQL 实现（迁移 049，design 第 6、7 节）：节点、绑定、领取与按次预留、失败上报、结果入库。
/// 所有写路径使用 READ COMMITTED，加锁顺序固定为 绑定 → 平台 → 任务 → 租户的积分账户（credit_accounts）→ 检测与
/// site_analysis_state → Run，死锁整体重试。积分账户排在检测和分析状态之前，与 API 监控路径（先结算或释放，再入分析队列）一致。
/// 时间一律取数据库 UTC_TIMESTAMP(6)。
/// 每一次状态变化都向 collector_app_events 追加一行；这里没有对该表的 UPDATE 或 DELETE。
/// </summary>
/// <param name="database">业务数据库。</param>
/// <param name="appChannel">App 渠道的开放范围（Collector:AppTenantIds）；不提供时没有任何租户的任务可以被领取。</param>
public sealed partial class AppCollectorPoolStore(MySqlDatabase database, IOptions<AppChannelOptions>? appChannel = null)
    : IAppCollectorPoolStore
{
    /// <summary>绑定不存在或不属于令牌对应的节点（403）。</summary>
    internal const string BindingForbiddenCode = "APP_BINDING_FORBIDDEN";

    private const int TokenBytes = 32;
    private const int MaxTokenLength = 512;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>令牌的存储形式：UTF-8 字节的 SHA-256，小写十六进制（64 个字符）。</summary>
    internal static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    // 32 random bytes, base64url without padding (43 characters).
    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <inheritdoc />
    public async Task<AppNodeIdentity?> AuthenticateNodeAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength) return null;
        var hash = HashToken(token);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
        var row = await QuerySingleAsync(connection, null,
            "SELECT node_id, status, token_hash FROM collector_app_nodes WHERE token_hash = @hash;", cancellationToken, ("@hash", hash));
        // The lookup is by the hash of a random 256-bit token; the stored hash is compared once more in fixed time.
        if (row is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row[2]), Encoding.ASCII.GetBytes(hash))) return null;
        return new AppNodeIdentity(row[0], row[1] == "active" ? AppNodeStatus.Active : AppNodeStatus.Disabled);
    }

    /// <inheritdoc />
    public Task<AppNodeCredential> RegisterNodeAsync(string nodeId, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        nodeId = RequireNodeId(nodeId);
        var token = NewToken();
        return InTransactionAsync(async (c, t, ct) =>
        {
            try
            {
                await ExecuteAsync(c, t, """
                    INSERT INTO collector_app_nodes (node_id, token_hash, status, created_at, updated_at)
                    VALUES (@node, @hash, 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ct, ("@node", nodeId), ("@hash", HashToken(token)));
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                throw new CollectorStoreException("APP_NODE_EXISTS", "该节点标识已登记。", 409);
            }
            await InsertEventAsync(c, t, new AppEvent("node_registered", Operator(operatorUserId), nodeId), ct);
            return Done(new AppNodeCredential(nodeId, token));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AppNodeCredential> RotateNodeTokenAsync(string nodeId, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        nodeId = RequireNodeId(nodeId);
        var token = NewToken();
        return InTransactionAsync(async (c, t, ct) =>
        {
            await LockNodeAsync(c, t, nodeId, ct);
            await ExecuteAsync(c, t, "UPDATE collector_app_nodes SET token_hash = @hash, updated_at = UTC_TIMESTAMP(6) WHERE node_id = @node;",
                ct, ("@hash", HashToken(token)), ("@node", nodeId));
            await InsertEventAsync(c, t, new AppEvent("node_token_rotated", Operator(operatorUserId), nodeId), ct);
            return Done(new AppNodeCredential(nodeId, token));
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetNodeStatusAsync(string nodeId, AppNodeStatus status, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        nodeId = RequireNodeId(nodeId);
        var value = AppContractNames.ToStorage(status);
        return InTransactionAsync(async (c, t, ct) =>
        {
            var from = await LockNodeAsync(c, t, nodeId, ct);
            if (from == value) return Done(false);
            await ExecuteAsync(c, t, "UPDATE collector_app_nodes SET status = @status, updated_at = UTC_TIMESTAMP(6) WHERE node_id = @node;",
                ct, ("@status", value), ("@node", nodeId));
            await InsertEventAsync(c, t, new AppEvent("node_status_changed", Operator(operatorUserId), nodeId,
                Detail: new { from, to = value }), ct);
            return Done(true);
        }, cancellationToken);
    }

    private static string RequireNodeId(string? nodeId)
        => nodeId?.Trim() is { Length: >= 1 and <= 128 } value
            ? value
            : throw new CollectorStoreException("INVALID_APP_NODE", "nodeId 必须为 1–128 个字符。", 400);

    private static async Task<string> LockNodeAsync(MySqlConnection c, MySqlTransaction t, string nodeId, CancellationToken ct)
        => (await QuerySingleAsync(c, t, "SELECT status FROM collector_app_nodes WHERE node_id = @node FOR UPDATE;", ct, ("@node", nodeId))
            ?? throw new CollectorStoreException("APP_NODE_NOT_FOUND", "采集节点不存在。", 404))[0];

    private static string Operator(Guid? operatorUserId) => operatorUserId is { } id ? $"operator:{id:N}" : "operator";

    private static string NodeActor(string nodeId) => $"node:{nodeId}"[..Math.Min(128, nodeId.Length + 5)];

    /// <summary>审计事件（只追加）。detail 不含提问原文、回答内容或可识别的账号信息。</summary>
    internal sealed record AppEvent(string Kind, string Actor, string? NodeId = null, Guid? BindingId = null, string? PlatformCode = null,
        Guid? TaskId = null, object? Detail = null);

    internal static Task<int> InsertEventAsync(MySqlConnection c, MySqlTransaction? t, AppEvent e, CancellationToken ct)
        => ExecuteAsync(c, t, """
            INSERT INTO collector_app_events (id, occurred_at, event_kind, node_id, binding_id, platform_code, task_id, actor, detail)
            VALUES (UUID_TO_BIN(@id), UTC_TIMESTAMP(6), @kind, @node, UUID_TO_BIN(@binding), @platform, UUID_TO_BIN(@task), @actor, @detail);
            """, ct, ("@id", Guid.NewGuid().ToString()), ("@kind", e.Kind), ("@node", e.NodeId), ("@binding", e.BindingId?.ToString()),
            ("@platform", e.PlatformCode), ("@task", e.TaskId?.ToString()), ("@actor", e.Actor),
            ("@detail", e.Detail is null ? null : JsonSerializer.Serialize(e.Detail, Web)));

    // ISO 8601 UTC with millisecond precision, produced in SQL: the shared row reader stringifies DATETIME to seconds.
    private static string Iso(string column)
        => $"CASE WHEN {column} IS NULL THEN NULL ELSE CONCAT(DATE_FORMAT({column}, '%Y-%m-%dT%H:%i:%s.'), LPAD(FLOOR(MICROSECOND({column}) / 1000), 3, '0'), 'Z') END";

    private static DateTimeOffset ParseIso(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? ParseIsoOrNull(string value) => value.Length == 0 ? null : ParseIso(value);

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    private static bool Flag(string value) => value is "1" or "True" or "true";

    /// <summary>事务的结果：正常返回值，或者“先提交已写入的审计事件，再向调用方抛出”的拒绝。</summary>
    private readonly record struct Outcome<T>(T Value, CollectorStoreException? Rejection);

    private static Outcome<T> Done<T>(T value) => new(value, null);

    private static Outcome<T> Reject<T>(CollectorStoreException rejection) => new(default!, rejection);

    /// <summary>READ COMMITTED 事务；InnoDB 死锁（1213）整体重试，最多 3 次。未执行 049 时返回 503。</summary>
    private async Task<T> InTransactionAsync<T>(Func<MySqlConnection, MySqlTransaction, CancellationToken, Task<Outcome<T>>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = database.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                var outcome = await work(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (outcome.Rejection is not null) throw outcome.Rejection;
                return outcome.Value;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < 3)
            {
            }
        }
    }
}
