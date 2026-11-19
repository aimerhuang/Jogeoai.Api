using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>
/// 登录频控编排与身份响应（C2 R5/R6）的宿主级测试：进程内宿主复用 C1 的 <c>AddJogeoaiSessionAuthorization()</c> /
/// <c>RequireJogeoaiSession()</c>；身份存储为 DispatchProxy 桩，频控存储为按 <see cref="LoginThrottleRules"/> 实现的内存桩，
/// 时间由可控 TimeProvider 提供。不连数据库、不发出站请求。
/// </summary>
public sealed class AuthLoginThrottleTests : IAsyncLifetime
{
    private const string OwnerEmail = "owner@example.test";
    private const string OwnerPassword = "correct-password";
    private const string WrongPassword = "wrong-password-xyz";
    private const string IssuedToken = "issued-session-token-for-throttle-tests";

    private Host host = null!;

    public async Task InitializeAsync() => host = await Host.StartAsync();

    public async Task DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task TwentyNinthFailureIs401ThirtiethLocksAndLockedRequestsNeverReachTheIdentityStore()
    {
        for (var i = 1; i <= 29; i++)
            await AssertErrorAsync(await host.LoginAsync(OwnerEmail, WrongPassword), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(29, host.Throttle.Failures(OwnerEmail));
        Assert.Null(host.Throttle.LockedUntil(OwnerEmail));

        // 第 30 次仍然是一次真实的失败（401），失败后进入锁定。
        await AssertErrorAsync(await host.LoginAsync(OwnerEmail, WrongPassword), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(1), host.Throttle.LockedUntil(OwnerEmail));
        Assert.Equal(30, host.LoginCalls);

        // 锁定期内：正确密码也返回 429，不调用 LoginAsync，不增加计数。
        using var locked = await host.LoginAsync(OwnerEmail, OwnerPassword);
        await AssertErrorAsync(locked, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal("60", Assert.Single(locked.Headers.GetValues("Retry-After")));
        Assert.Equal(30, host.LoginCalls);
        Assert.Equal(30, host.Throttle.Failures(OwnerEmail));

        // 换一个来源 IP、大小写与空白不同的同一邮箱，同样被锁。
        using var otherIp = await host.LoginAsync("  OWNER@Example.Test ", OwnerPassword, ip: "203.0.113.77");
        await AssertErrorAsync(otherIp, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal(30, host.LoginCalls);

        // 30 秒后 Retry-After 递减。
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        using var later = await host.LoginAsync(OwnerEmail, OwnerPassword);
        Assert.Equal("30", Assert.Single(later.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task LockExpiresThenTheNextFailureDoublesTheBackoffUpToFifteenMinutes()
    {
        host.Throttle.Seed(OwnerEmail, 29);
        var expected = new[] { 60, 120, 240, 480, 900, 900 };
        foreach (var seconds in expected)
        {
            await AssertErrorAsync(await host.LoginAsync(OwnerEmail, WrongPassword), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
            using var locked = await host.LoginAsync(OwnerEmail, WrongPassword);
            await AssertErrorAsync(locked, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
            Assert.Equal(seconds.ToString(), Assert.Single(locked.Headers.GetValues("Retry-After")));
            host.Clock.Advance(TimeSpan.FromSeconds(seconds));
        }
        Assert.Equal(35, host.Throttle.Failures(OwnerEmail));
    }

    [Fact]
    public async Task SuccessfulLoginResetsTheCount()
    {
        for (var i = 0; i < 29; i++) await host.LoginAsync(OwnerEmail, WrongPassword);
        using (var ok = await host.LoginAsync(OwnerEmail, OwnerPassword))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(0, host.Throttle.Failures(OwnerEmail));

        for (var i = 0; i < 29; i++)
            await AssertErrorAsync(await host.LoginAsync(OwnerEmail, WrongPassword), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Null(host.Throttle.LockedUntil(OwnerEmail));
    }

    [Fact]
    public async Task ManyRequestsFromOneIpAcrossAccountsOrRepeatedSuccessesAreNeverThrottled()
    {
        // 不设 IP 维度：同一 IP 对 200 个不同账号各失败一次，不触发 429。
        for (var i = 0; i < 200; i++)
            await AssertErrorAsync(await host.LoginAsync($"user{i}@example.test", WrongPassword), HttpStatusCode.Unauthorized,
                "INVALID_CREDENTIALS");
        // 成功不计数：同一账号反复成功登录 100 次。
        for (var i = 0; i < 100; i++)
        {
            using var ok = await host.LoginAsync(OwnerEmail, OwnerPassword);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        Assert.Equal(300, host.LoginCalls);
        Assert.Null(host.Throttle.LockedUntil(OwnerEmail));
    }

    [Fact]
    public async Task KnownAndUnknownEmailsGetTheSameErrorAndBothAreCounted()
    {
        using var known = await host.LoginAsync(OwnerEmail, WrongPassword);
        using var unknown = await host.LoginAsync("nobody@example.test", WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, known.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(1, host.Throttle.Failures(OwnerEmail));
        Assert.Equal(1, host.Throttle.Failures("nobody@example.test"));

        // 不存在的邮箱同样会被锁定，429 不泄露账号是否存在。
        host.Throttle.Seed("nobody@example.test", 29);
        await host.LoginAsync("nobody@example.test", WrongPassword);
        await AssertErrorAsync(await host.LoginAsync("nobody@example.test", WrongPassword), HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task InvalidEmailIsRejectedBeforeTheThrottleAndIdentityStore()
    {
        await AssertErrorAsync(await host.LoginAsync("not-an-email", WrongPassword), HttpStatusCode.BadRequest, "INVALID_EMAIL");
        Assert.Equal(0, host.LoginCalls);
        Assert.Equal(0, host.Throttle.Calls);
    }

    [Fact]
    public async Task MissingThrottleTableFailsClosedWith503()
    {
        host.Throttle.SchemaMissing = true;
        await AssertErrorAsync(await host.LoginAsync(OwnerEmail, OwnerPassword), HttpStatusCode.ServiceUnavailable,
            "AUTH_THROTTLE_SCHEMA_NOT_MIGRATED");
        Assert.Equal(0, host.LoginCalls);
    }

    [Fact]
    public async Task RecordFailureAfterASuccessfulPasswordCheckFailsClosedAndRevokesTheUndeliveredSession()
    {
        host.Throttle.RecordSchemaMissing = true;
        foreach (var delivery in new string?[] { null, "token" })
        {
            using var response = await host.LoginAsync(OwnerEmail, OwnerPassword, delivery: delivery);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(IssuedToken, text);
            await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "AUTH_THROTTLE_SCHEMA_NOT_MIGRATED");
        }
        Assert.Equal(2, host.LoginCalls);
        // 会话已由 LoginAsync 创建但未交付：每次都撤销，不留下无人持有的有效会话。
        Assert.Equal(2, host.Identity.Count(nameof(IIdentityStore.RevokeSessionAsync)));

        // 失败结果的记录出错同样 503，且没有会话需要撤销。
        await AssertErrorAsync(await host.LoginAsync(OwnerEmail, WrongPassword), HttpStatusCode.ServiceUnavailable,
            "AUTH_THROTTLE_SCHEMA_NOT_MIGRATED");
        Assert.Equal(2, host.Identity.Count(nameof(IIdentityStore.RevokeSessionAsync)));
    }

    [Fact]
    public async Task RejectionsAreAuditedWithoutEmailPasswordOrToken()
    {
        host.Throttle.Seed(OwnerEmail, 29);
        await host.LoginAsync(OwnerEmail, WrongPassword);
        await host.LoginAsync(OwnerEmail, OwnerPassword);
        using (var ok = await host.LoginAsync("member@example.test", OwnerPassword)) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var rejected = host.Logs.Entries.Where(entry => entry.EventName == "LoginRejected").ToArray();
        Assert.Equal(2, rejected.Length);
        var prefix = LoginThrottleRules.AccountSubjectHash(OwnerEmail)[..12];
        Assert.Contains(rejected, entry => entry.Message.Contains("reason=invalid_credentials") && entry.Message.Contains(prefix));
        Assert.Contains(rejected, entry => entry.Message.Contains("reason=account_locked") && entry.Message.Contains(prefix));
        foreach (var entry in host.Logs.Entries)
        {
            Assert.DoesNotContain("example.test", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(WrongPassword, entry.Message);
            Assert.DoesNotContain(OwnerPassword, entry.Message);
            Assert.DoesNotContain(IssuedToken, entry.Message);
            Assert.DoesNotContain(LoginThrottleRules.AccountSubjectHash(OwnerEmail), entry.Message);
        }
    }

    [Fact]
    public async Task DefaultDeliverySetsTheCookieAndOmitsTheTokenFromTheBody()
    {
        foreach (var header in new string?[] { null, "cookie", " Cookie " })
        {
            using var response = await host.LoginAsync(OwnerEmail, OwnerPassword, delivery: header);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.StartsWith($"{AuthController.CookieName}={IssuedToken}", cookie);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(IssuedToken, body);
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("sessionToken", out _));
            Assert.Equal(OwnerEmail, json.RootElement.GetProperty("user").GetProperty("email").GetString());
        }
    }

    [Fact]
    public async Task TokenDeliveryReturnsTheTokenWithoutACookieAndTheTokenWorksAsBearer()
    {
        using var response = await host.LoginAsync(OwnerEmail, OwnerPassword, delivery: "token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("sessionToken").GetString();
        Assert.Equal(IssuedToken, token);

        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/auth/me",
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Theory]
    [InlineData("tokens")]
    [InlineData("both")]
    [InlineData("cookie,token")]
    public async Task InvalidDeliveryIs400BeforeAnyStoreCall(string header)
    {
        await AssertErrorAsync(await host.LoginAsync(OwnerEmail, OwnerPassword, delivery: header), HttpStatusCode.BadRequest,
            "INVALID_SESSION_DELIVERY");
        await AssertErrorAsync(await host.RegisterAsync(delivery: header), HttpStatusCode.BadRequest, "INVALID_SESSION_DELIVERY");
        Assert.Equal(0, host.LoginCalls);
        Assert.Equal(0, host.Identity.Count(nameof(IIdentityStore.RegisterAsync)));
        Assert.Equal(0, host.Throttle.Calls);
    }

    [Fact]
    public async Task RegisterHonoursTheDeliveryHeader()
    {
        using (var cookie = await host.RegisterAsync(delivery: null))
        {
            Assert.Equal(HttpStatusCode.Created, cookie.StatusCode);
            Assert.Single(cookie.Headers.GetValues("Set-Cookie"));
            using var json = JsonDocument.Parse(await cookie.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.TryGetProperty("sessionToken", out _));
        }
        using (var token = await host.RegisterAsync(delivery: "token"))
        {
            Assert.Equal(HttpStatusCode.Created, token.StatusCode);
            Assert.False(token.Headers.Contains("Set-Cookie"));
            using var json = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
            Assert.Equal(IssuedToken, json.RootElement.GetProperty("sessionToken").GetString());
        }
    }

    [Fact]
    public async Task EveryIdentityResponseIsNoStore()
    {
        var responses = new List<(string Name, HttpResponseMessage Response)>
        {
            ("login 200", await host.LoginAsync(OwnerEmail, OwnerPassword)),
            ("login token 200", await host.LoginAsync(OwnerEmail, OwnerPassword, delivery: "token")),
            ("login 401", await host.LoginAsync(OwnerEmail, WrongPassword)),
            ("login 400", await host.LoginAsync(OwnerEmail, OwnerPassword, delivery: "bad")),
            ("register 201", await host.RegisterAsync(delivery: null)),
            ("send code 200", await host.SendAsync(HttpMethod.Post, "/api/v1/auth/register/email-code",
                request => request.Content = JsonContent.Create(new { email = "new@example.test" }))),
            ("me 200", await host.SendAsync(HttpMethod.Get, "/api/v1/auth/me",
                request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IssuedToken))),
            ("logout 204", await host.SendAsync(HttpMethod.Post, "/api/v1/auth/logout",
                request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IssuedToken))),
        };
        host.Throttle.Seed("locked@example.test", 30, host.Clock.GetUtcNow().AddMinutes(1));
        responses.Add(("login 429", await host.LoginAsync("locked@example.test", OwnerPassword)));
        host.Throttle.SchemaMissing = true;
        responses.Add(("login 503", await host.LoginAsync(OwnerEmail, OwnerPassword)));

        foreach (var (name, response) in responses)
        {
            using (response)
            {
                Assert.True(response.Headers.CacheControl?.NoStore == true, $"{name} ({(int)response.StatusCode}) 缺少 Cache-Control: no-store");
            }
        }
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"期望 {(int)status}，实际 {(int)response.StatusCode}：{text}");
            using var body = JsonDocument.Parse(text);
            Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    /// <summary>可手动推进的时钟。</summary>
    internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>按 <see cref="LoginThrottleRules"/> 实现的内存频控存储，可模拟缺表（503）。</summary>
    internal sealed class InMemoryLoginThrottleStore(LoginThrottleOptions options) : ILoginThrottleStore
    {
        private readonly ConcurrentDictionary<string, LoginThrottleState> rows = new();
        private int calls;

        public bool SchemaMissing { get; set; }

        /// <summary>只让记录（RecordResultAsync）抛出缺表异常，检查仍放行：模拟检查与记录之间表被删除。</summary>
        public bool RecordSchemaMissing { get; set; }

        public int Calls => Volatile.Read(ref calls);

        public int Failures(string email)
            => rows.TryGetValue(LoginThrottleRules.AccountSubjectHash(email), out var row) ? row.ConsecutiveFailures : 0;

        public DateTimeOffset? LockedUntil(string email)
            => rows.TryGetValue(LoginThrottleRules.AccountSubjectHash(email), out var row) ? row.LockedUntil : null;

        public void Seed(string email, int failures, DateTimeOffset? lockedUntil = null)
            => rows[LoginThrottleRules.AccountSubjectHash(email)] = new LoginThrottleState(failures, lockedUntil);

        public Task<LoginThrottleDecision> CheckAsync(string accountHash, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            ThrowIfMissing();
            rows.TryGetValue(accountHash, out var row);
            return Task.FromResult(LoginThrottleRules.Evaluate(row, now));
        }

        public Task RecordResultAsync(string accountHash, bool succeeded, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            ThrowIfMissing();
            if (RecordSchemaMissing)
                throw new CollectorStoreException(LoginThrottleRules.SchemaNotMigratedCode, "登录频控所需迁移 045 尚未部署。", 503);
            if (succeeded)
            {
                if (rows.TryGetValue(accountHash, out var row)) rows[accountHash] = LoginThrottleRules.RecordSuccess(row)!;
            }
            else
            {
                rows.AddOrUpdate(accountHash, _ => LoginThrottleRules.RecordFailure(null, now, options),
                    (_, row) => LoginThrottleRules.RecordFailure(row, now, options));
            }
            return Task.CompletedTask;
        }

        private void ThrowIfMissing()
        {
            if (SchemaMissing)
                throw new CollectorStoreException(LoginThrottleRules.SchemaNotMigratedCode, "登录频控所需迁移 045 尚未部署。", 503);
        }
    }

    /// <summary>收集格式化后的日志，用于断言审计日志不含敏感字段。</summary>
    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, string? EventName, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                // 结构化字段与格式化文本都纳入断言范围。
                var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
                owner.Entries.Enqueue((category, eventId.Name, formatter(state, exception) + " | " + fields));
            }
        }
    }

    /// <summary>进程内宿主：真实会话中间件 + 默认会话策略 + 全部 Controller；身份存储为桩，频控为内存实现。</summary>
    private sealed class Host : IAsyncDisposable
    {
        private const string TestIpHeader = "X-Test-Remote-Ip";

        private Host(WebApplication app, McpStoreProxy identity, InMemoryLoginThrottleStore throttle, ManualTimeProvider clock,
            CapturingLoggerProvider logs)
        {
            App = app;
            Identity = identity;
            Throttle = throttle;
            Clock = clock;
            Logs = logs;
            // 不使用 CookieContainer，便于直接断言 Set-Cookie 响应头。
            Client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        }

        internal WebApplication App { get; }
        internal HttpClient Client { get; }
        internal McpStoreProxy Identity { get; }
        internal InMemoryLoginThrottleStore Throttle { get; }
        internal ManualTimeProvider Clock { get; }
        internal CapturingLoggerProvider Logs { get; }
        internal int LoginCalls => Identity.Count(nameof(IIdentityStore.LoginAsync));

        internal static async Task<Host> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(HealthController).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var logs = new CapturingLoggerProvider();
            builder.Logging.AddProvider(logs);

            var tenant = Guid.NewGuid();
            AuthSessionView Session(string email) => new(IssuedToken, DateTimeOffset.UtcNow.AddDays(30),
                new AuthUserView(Guid.NewGuid(), email, null, "active", [new(tenant, "Team", "owner", "active")]));

            var (identityStore, identity) = McpStoreProxy.Create<IIdentityStore>();
            // 桩模拟 IdentityStore：只有密码正确的账号返回会话，其余（含不存在的邮箱）一律返回空。
            identity.Handlers[nameof(IIdentityStore.LoginAsync)] = args =>
            {
                var request = (LoginRequest)args[0]!;
                return Task.FromResult<AuthSessionView?>(request.Password == OwnerPassword
                    ? Session(request.Email.Trim().ToLowerInvariant())
                    : null);
            };
            identity.Handlers[nameof(IIdentityStore.RegisterAsync)] = args =>
                Task.FromResult(Session(((RegisterRequest)args[0]!).Email));
            identity.Handlers[nameof(IIdentityStore.SendRegistrationCodeAsync)] = _ =>
                Task.FromResult(new RegistrationCodeSentView(DateTimeOffset.UtcNow.AddMinutes(10), 60));
            identity.Handlers[nameof(IIdentityStore.RevokeSessionAsync)] = _ => Task.CompletedTask;
            identity.Handlers[nameof(IIdentityStore.ValidateSessionAsync)] = args => Task.FromResult(
                (string)args[0]! == IssuedToken
                    ? new AuthenticatedRequestContext(Session(OwnerEmail).User, Guid.NewGuid(), IssuedToken)
                    : null);

            var options = new LoginThrottleOptions();
            var throttle = new InMemoryLoginThrottleStore(options);
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 1, 0, 0, TimeSpan.Zero));

            builder.Services.AddSingleton(identityStore);
            builder.Services.AddSingleton<ILoginThrottleStore>(throttle);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddJogeoaiSessionAuthorization();
            builder.Services.AddControllers().AddApplicationPart(typeof(HealthController).Assembly);

            var app = builder.Build();
            // 测试专用：用请求头模拟不同来源 IP。
            app.Use((context, next) =>
            {
                if (System.Net.IPAddress.TryParse(context.Request.Headers[TestIpHeader].FirstOrDefault(), out var ip))
                    context.Connection.RemoteIpAddress = ip;
                return next(context);
            });
            app.UseMiddleware<SessionAuthenticationMiddleware>();
            app.UseAuthorization();
            app.MapControllers().RequireJogeoaiSession();
            await app.StartAsync();
            return new Host(app, identity, throttle, clock, logs);
        }

        internal Task<HttpResponseMessage> LoginAsync(string email, string password, string? delivery = null, string? ip = null)
            => SendAsync(HttpMethod.Post, "/api/v1/auth/login", request =>
            {
                request.Content = JsonContent.Create(new { email, password });
                if (delivery is not null) request.Headers.Add(AuthController.SessionDeliveryHeader, delivery);
                if (ip is not null) request.Headers.Add(TestIpHeader, ip);
            });

        internal Task<HttpResponseMessage> RegisterAsync(string? delivery)
            => SendAsync(HttpMethod.Post, "/api/v1/auth/register", request =>
            {
                request.Content = JsonContent.Create(new
                {
                    email = $"new-{Guid.NewGuid():N}@example.test", password = OwnerPassword, emailCode = "123456"
                });
                if (delivery is not null) request.Headers.Add(AuthController.SessionDeliveryHeader, delivery);
            });

        internal async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, path);
            configure?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
