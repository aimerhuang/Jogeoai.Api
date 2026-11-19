using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>邮箱密码注册请求。</summary>
/// <param name="Email">登录邮箱。</param>
/// <param name="Password">登录密码。</param>
/// <param name="EmailCode">发送到该邮箱的 6 位注册验证码。</param>
/// <param name="DisplayName">团队和用户显示名称。</param>
public sealed record RegisterRequest(string Email, string Password, string EmailCode, string? DisplayName = null);

/// <summary>发送注册邮箱验证码。</summary>
/// <param name="Email">待验证的注册邮箱。</param>
public sealed record SendRegistrationCodeRequest(string Email);

/// <summary>验证码邮件已发送；不返回验证码本身。</summary>
/// <param name="ExpiresAt">验证码过期时间。</param>
/// <param name="ResendAfterSeconds">再次发送前应等待的秒数。</param>
public sealed record RegistrationCodeSentView(DateTimeOffset ExpiresAt, int ResendAfterSeconds);

/// <summary>邮箱密码登录请求。</summary>
/// <param name="Email">登录邮箱。</param>
/// <param name="Password">登录密码。</param>
public sealed record LoginRequest(string Email, string Password);

/// <summary>保存会话时记录的客户端信息。</summary>
/// <param name="IpAddress">客户端 IP 地址。</param>
/// <param name="UserAgent">客户端 User-Agent。</param>
public sealed record SessionMetadata(string? IpAddress = null, string? UserAgent = null);

/// <summary>租户成员摘要。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="TenantName">租户名称。</param>
/// <param name="Role">成员角色。</param>
/// <param name="Status">成员状态。</param>
public sealed record TenantMembershipView(Guid TenantId, string TenantName, string Role, string Status);

/// <summary>已登录用户摘要。</summary>
/// <param name="UserId">用户标识。</param>
/// <param name="Email">规范化登录邮箱。</param>
/// <param name="DisplayName">显示名称。</param>
/// <param name="Status">用户状态。</param>
/// <param name="Memberships">用户所属的有效租户。</param>
/// <param name="IsSystemAdmin">是否为系统管理员；决定前端是否显示兑换码管理等管理员入口。</param>
public sealed record AuthUserView(Guid UserId, string Email, string? DisplayName, string Status,
    IReadOnlyList<TenantMembershipView> Memberships, bool IsSystemAdmin = false);

/// <summary>注册或登录成功后的会话响应。</summary>
/// <param name="SessionToken">
/// 会话令牌，仅当请求头 <c>X-Jogeoai-Session-Delivery: token</c>（非浏览器客户端）时返回，之后以 <c>Authorization: Bearer</c> 携带；
/// 默认的 Cookie 投递方式下令牌只写入 HttpOnly Cookie，响应体省略该字段。
/// </param>
/// <param name="ExpiresAt">会话过期时间。</param>
/// <param name="User">当前用户摘要。</param>
public sealed record AuthSessionView(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionToken,
    DateTimeOffset ExpiresAt,
    AuthUserView User);

/// <summary>请求上下文中的已验证身份。</summary>
/// <param name="User">已登录用户摘要。</param>
/// <param name="SessionId">服务端会话标识。</param>
/// <param name="SessionToken">本次请求使用的原始令牌，响应中不得回显。</param>
public sealed record AuthenticatedRequestContext(AuthUserView User, Guid SessionId, string SessionToken);
