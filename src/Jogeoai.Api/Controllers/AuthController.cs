using System.Globalization;
using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 处理邮箱密码注册、登录、退出和当前用户查询。全部响应带 <c>Cache-Control: no-store</c>，身份数据不进入任何缓存。
/// 登录按账号（规范化邮箱）统计连续失败：达到阈值（默认 30 次）后锁定 1、2、4、8、15 分钟（上限 15 分钟），
/// 锁定期内返回 429 <c>RATE_LIMITED</c> 并附 <c>Retry-After</c>；不按 IP 限流。依赖迁移 045，未执行时登录返回 503。
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(IIdentityStore identityStore, IHostEnvironment environment,
    ILoginThrottleStore loginThrottle, TimeProvider timeProvider, ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>浏览器会话 Cookie 名称。</summary>
    public const string CookieName = "jogeoai_session";

    /// <summary>
    /// 会话投递方式请求头：缺省或 <c>cookie</c> 时写入 HttpOnly Cookie、响应体不含 <c>sessionToken</c>（浏览器默认）；
    /// <c>token</c> 时不写 Cookie、响应体返回 <c>sessionToken</c>（非浏览器客户端，之后以 <c>Authorization: Bearer</c> 携带）。
    /// </summary>
    public const string SessionDeliveryHeader = "X-Jogeoai-Session-Delivery";

    private static readonly EventId LoginRejectedEvent = new(4201, "LoginRejected");
    private static readonly EventId LoginSessionRevokeFailedEvent = new(4202, "LoginSessionRevokeFailed");

    /// <summary>向注册邮箱发送 6 位验证码，有效期 10 分钟，同一邮箱间隔至少 60 秒。</summary>
    /// <param name="request">待注册邮箱。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("register/email-code")]
    [AllowAnonymous]
    [EnableRateLimiting("registration-email")]
    [ProducesResponseType(typeof(RegistrationCodeSentView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RegistrationCodeSentView>> SendCode([FromBody] SendRegistrationCodeRequest request,
        CancellationToken cancellationToken)
    {
        try { return Ok(await identityStore.SendRegistrationCodeAsync(request, cancellationToken)); }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }

    /// <summary>验证邮件验证码后创建用户、初始团队和服务端会话。</summary>
    /// <param name="request">邮箱、密码、邮件验证码和显示名称。</param>
    /// <param name="sessionDelivery">
    /// 会话投递方式（请求头 <c>X-Jogeoai-Session-Delivery</c>）：缺省或 <c>cookie</c> 写入 HttpOnly Cookie，响应体不含令牌；
    /// <c>token</c> 不写 Cookie，响应体返回 <c>sessionToken</c>；其他值返回 400 <c>INVALID_SESSION_DELIVERY</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="201">注册成功，会话按投递方式返回。</response>
    /// <response code="400">邮箱、密码或验证码无效，或投递方式不是 cookie / token（INVALID_SESSION_DELIVERY）。</response>
    /// <response code="409">邮箱已注册（EMAIL_ALREADY_REGISTERED）。</response>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("registration")]
    [ProducesResponseType(typeof(AuthSessionView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthSessionView>> Register([FromBody] RegisterRequest request,
        [FromHeader(Name = SessionDeliveryHeader)] string? sessionDelivery,
        CancellationToken cancellationToken)
    {
        if (!TryParseDelivery(sessionDelivery, out var delivery)) return InvalidDelivery();
        try
        {
            var result = await identityStore.RegisterAsync(request, Metadata(), cancellationToken);
            return StatusCode(StatusCodes.Status201Created, Deliver(result, delivery));
        }
        catch (CollectorStoreException exception)
        {
            return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
        }
    }

    /// <summary>
    /// 验证邮箱密码并建立新的服务端会话。同一账号连续失败达到阈值后进入锁定期，锁定期内直接返回 429（不校验密码），
    /// 成功登录复位计数；已知与未知邮箱返回相同的 401 <c>INVALID_CREDENTIALS</c>。
    /// </summary>
    /// <param name="request">邮箱和密码。</param>
    /// <param name="sessionDelivery">
    /// 会话投递方式（请求头 <c>X-Jogeoai-Session-Delivery</c>）：缺省或 <c>cookie</c> 写入 HttpOnly Cookie，响应体不含令牌；
    /// <c>token</c> 不写 Cookie，响应体返回 <c>sessionToken</c>；其他值返回 400 <c>INVALID_SESSION_DELIVERY</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">登录成功，会话按投递方式返回。</response>
    /// <response code="400">邮箱格式无效（INVALID_EMAIL），或投递方式不是 cookie / token（INVALID_SESSION_DELIVERY）。</response>
    /// <response code="401">邮箱或密码不正确（INVALID_CREDENTIALS），不区分账号是否存在。</response>
    /// <response code="429">该账号连续失败次数过多，处于锁定期（RATE_LIMITED）；<c>Retry-After</c> 给出剩余秒数。</response>
    /// <response code="503">迁移 045 尚未部署（AUTH_THROTTLE_SCHEMA_NOT_MIGRATED）或鉴权迁移缺失，暂时无法登录。</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthSessionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AuthSessionView>> Login([FromBody] LoginRequest request,
        [FromHeader(Name = SessionDeliveryHeader)] string? sessionDelivery,
        CancellationToken cancellationToken)
    {
        if (!TryParseDelivery(sessionDelivery, out var delivery)) return InvalidDelivery();
        try
        {
            var accountHash = LoginThrottleRules.AccountSubjectHash(IdentityStore.NormalizeEmail(request.Email));
            var decision = await loginThrottle.CheckAsync(accountHash, timeProvider.GetUtcNow(), cancellationToken);
            if (!decision.IsAllowed)
            {
                LogRejected("account_locked", accountHash);
                Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new { error = new { code = "RATE_LIMITED", message = "登录尝试过于频繁，请稍后再试。" } });
            }

            var result = await identityStore.LoginAsync(request, Metadata(), cancellationToken);
            try
            {
                // 不使用请求的取消令牌：客户端在密码校验后断开也必须计入失败。
                await loginThrottle.RecordResultAsync(accountHash, result is not null, timeProvider.GetUtcNow(), CancellationToken.None);
            }
            catch when (result is not null)
            {
                // 失败即拒绝：复位写入失败时登录按原错误返回（缺表 503，其他 500），已创建的会话不会交付给客户端，
                // 尽力撤销，避免留下无人持有的有效会话。
                await RevokeUndeliveredSessionAsync(result, accountHash);
                throw;
            }
            if (result is null)
            {
                LogRejected("invalid_credentials", accountHash);
                return Unauthorized(new { error = new { code = "INVALID_CREDENTIALS", message = "邮箱或密码不正确。" } });
            }
            return Ok(Deliver(result, delivery));
        }
        catch (CollectorStoreException exception)
        {
            return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
        }
    }

    /// <summary>撤销当前会话并清除浏览器 Cookie。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        try
        {
            var token = SessionAuthenticationMiddleware.ReadToken(HttpContext);
            if (token is not null) await identityStore.RevokeSessionAsync(token, cancellationToken);
            Response.Cookies.Delete(CookieName, CookieOptions());
            return NoContent();
        }
        catch (CollectorStoreException exception)
        {
            return StatusCode(exception.StatusCode,
                new { error = new { code = exception.Code, message = exception.Message } });
        }
    }

    /// <summary>读取当前会话用户及其有效租户成员关系。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("me")]
    [ProducesResponseType(typeof(AuthUserView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var context = SessionAuthenticationMiddleware.GetContext(HttpContext);
        return context is null
            ? Unauthorized(new { error = new { code = "AUTHENTICATION_REQUIRED", message = "请先登录。" } })
            : Ok(context.User);
    }

    private enum SessionDelivery
    {
        Cookie,
        Token
    }

    private static bool TryParseDelivery(string? value, out SessionDelivery delivery)
    {
        delivery = SessionDelivery.Cookie;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "cookie", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(trimmed, "token", StringComparison.OrdinalIgnoreCase)) return false;
        delivery = SessionDelivery.Token;
        return true;
    }

    private ObjectResult InvalidDelivery() => BadRequest(new
    {
        error = new { code = "INVALID_SESSION_DELIVERY", message = $"{SessionDeliveryHeader} 只能是 cookie 或 token。" }
    });

    // Cookie 投递：令牌只进入 HttpOnly Cookie，响应体置空（序列化时省略）；token 投递：不写 Cookie，原样返回令牌。
    private AuthSessionView Deliver(AuthSessionView result, SessionDelivery delivery)
    {
        if (delivery == SessionDelivery.Token) return result;
        SetCookie(result);
        return result with { SessionToken = null };
    }

    private async Task RevokeUndeliveredSessionAsync(AuthSessionView result, string accountHash)
    {
        if (result.SessionToken is not { } token) return;
        try
        {
            await identityStore.RevokeSessionAsync(token, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 撤销失败不掩盖原错误；令牌未交付且库内只存哈希，会话到期后自然失效。日志不含令牌与邮箱。
            logger.LogWarning(LoginSessionRevokeFailedEvent, "LoginSessionRevokeFailed account={AccountHashPrefix} error={ErrorType}",
                accountHash[..12], exception.GetType().Name);
        }
    }

    // 审计日志：只记哈希前 12 位与连接地址，不记录邮箱明文、密码或令牌。C3 之前 IP 可能是反向代理地址。
    private void LogRejected(string reason, string accountHash)
        => logger.LogWarning(LoginRejectedEvent, "LoginRejected reason={Reason} account={AccountHashPrefix} ip={RemoteIp}",
            reason, accountHash[..12], HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private SessionMetadata Metadata() => new(
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.FirstOrDefault());

    private void SetCookie(AuthSessionView result) => Response.Cookies.Append(CookieName,
        result.SessionToken ?? throw new InvalidOperationException("Identity store returned a session without a token."),
        CookieOptions(result.ExpiresAt));

    private CookieOptions CookieOptions(DateTimeOffset? expiresAt = null) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        Expires = expiresAt,
        Path = "/"
    };
}
