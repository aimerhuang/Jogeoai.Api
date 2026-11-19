using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>用户注册、密码登录及可撤销会话的持久化合同。</summary>
public interface IIdentityStore
{
    /// <summary>发送注册验证码，按邮箱限流并持久化验证状态。</summary>
    Task<RegistrationCodeSentView> SendRegistrationCodeAsync(SendRegistrationCodeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>创建用户、初始租户成员和登录会话。</summary>
    Task<AuthSessionView> RegisterAsync(RegisterRequest request, SessionMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>验证邮箱密码并创建登录会话；凭据不匹配时返回空值。</summary>
    Task<AuthSessionView?> LoginAsync(LoginRequest request, SessionMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>撤销当前会话；不存在或已撤销的令牌保持幂等。</summary>
    Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken = default);

    /// <summary>验证会话令牌并读取有效租户成员关系。</summary>
    Task<AuthenticatedRequestContext?> ValidateSessionAsync(string sessionToken,
        CancellationToken cancellationToken = default);
}
