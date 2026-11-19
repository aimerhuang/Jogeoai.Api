using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>Cloro 账号 key 池的系统管理存储；任何返回值都不含完整 key。</summary>
public interface ICloroKeyAdminStore
{
    /// <summary>列出账号；默认不含已归档账号。</summary>
    /// <param name="includeArchived">是否包含已归档账号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<CloroKeyView>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default);

    /// <summary>读取单个账号视图（含已归档）。</summary>
    /// <param name="id">key 池记录标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyView?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>判断账号名称或 key 是否已被占用（含已归档）。</summary>
    /// <param name="accountLabel">规范化后的账号名称。</param>
    /// <param name="apiKey">规范化后的 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> ExistsAsync(string accountLabel, string apiKey, CancellationToken cancellationToken = default);

    /// <summary>录入账号；名称或 key 重复时抛出 409 CLORO_KEY_DUPLICATE。</summary>
    /// <param name="command">录入命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyView> CreateAsync(CreateCloroKeyCommand command, CancellationToken cancellationToken = default);

    /// <summary>修改名称、优先级或启用状态；账号不存在或已归档时返回空值。仍有未记录 key 的在途任务时，default 账号不能改名（409 CLORO_KEY_IN_USE）。</summary>
    /// <param name="command">修改命令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyView?> UpdateAsync(UpdateCloroKeyCommand command, CancellationToken cancellationToken = default);

    /// <summary>软归档账号；有在途任务时不归档并返回在途任务数；不存在时返回空值。</summary>
    /// <param name="id">key 池记录标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyArchiveResult?> ArchiveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>读取池概览。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyPoolSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>兼容旧 PUT /settings：default 账号不存在时新增；存在且无在途任务时替换 key，否则抛出 409。</summary>
    /// <param name="apiKey">规范化后的 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task UpsertDefaultKeyAsync(string apiKey, CancellationToken cancellationToken = default);
}
