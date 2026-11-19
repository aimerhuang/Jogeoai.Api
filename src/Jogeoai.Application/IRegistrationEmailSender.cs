namespace Jogeoai.Application;

/// <summary>发送注册验证邮件，凭据仅由服务端配置提供。</summary>
public interface IRegistrationEmailSender
{
    /// <summary>检查发件配置，缺失时返回安全的业务错误。</summary>
    void EnsureConfigured();

    /// <summary>向指定邮箱发送限时注册验证码。</summary>
    Task SendCodeAsync(string email, string code, CancellationToken cancellationToken = default);
}
