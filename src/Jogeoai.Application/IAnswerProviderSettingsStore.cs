using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>按 Provider 一行持久化 API 回答 Provider（如 doubao_api）的全局设置；依赖迁移 025。</summary>
public interface IAnswerProviderSettingsStore
{
    /// <summary>读取设置的安全视图（不含凭据）；行不存在时返回空值。未注册的 Provider 抛出 400 PROVIDER_NOT_SUPPORTED。</summary>
    /// <param name="provider">Provider 标识，如 doubao_api。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>读取含凭据的完整配置供适配器使用；仅当已启用且已保存凭据时返回，否则为空值。</summary>
    /// <param name="provider">Provider 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>校验并保存设置；凭据为空时保留已有凭据。返回保存后的安全视图。</summary>
    /// <param name="provider">Provider 标识。</param>
    /// <param name="request">保存请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request,
        CancellationToken cancellationToken = default);
}
