using System.Net;
using System.Net.Sockets;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jogeoai.Tests;

// The dev-only fake-ip allowance for web page reads (ArticleSource:AllowFakeIpRange) only
// widens ArticleSourceReader's DNS pre-check and the research-web connect callback by
// 198.18.0.0/15, and only in Development. Nothing here touches DNS or external networks.
public sealed class ArticleSourceFakeIpTests
{
    private static readonly ArticleSourceOptions Enabled = new() { AllowFakeIpRange = true };

    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("::ffff:198.18.0.1")]
    public void DevelopmentWithFlagAllowsFakeIpRange(string address)
    {
        var logger = new CapturingLogger();
        var policy = ArticleSourceAddressPolicy.Create(Enabled, new FakeEnvironment(Environments.Development), logger);

        Assert.True(policy.AllowFakeIpRange);
        Assert.True(policy.IsAllowed(IPAddress.Parse(address)));
        Assert.Equal([LogLevel.Information], logger.Levels);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Local")]
    public void FlagOutsideDevelopmentIsIgnoredWithWarning(string environmentName)
    {
        var logger = new CapturingLogger();
        var policy = ArticleSourceAddressPolicy.Create(Enabled, new FakeEnvironment(environmentName), logger);

        Assert.False(policy.AllowFakeIpRange);
        Assert.False(policy.IsAllowed(IPAddress.Parse("198.18.0.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("::ffff:198.18.0.1")));
        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    [Fact]
    public void DevelopmentWithoutFlagRejectsFakeIpRangeSilently()
    {
        Assert.False(new ArticleSourceOptions().AllowFakeIpRange);
        var logger = new CapturingLogger();
        var policy = ArticleSourceAddressPolicy.Create(new ArticleSourceOptions(), new FakeEnvironment(Environments.Development), logger);

        Assert.False(policy.AllowFakeIpRange);
        Assert.False(policy.IsAllowed(IPAddress.Parse("198.18.0.1")));
        Assert.False(policy.IsAllowed(IPAddress.Parse("::ffff:198.18.0.1")));
        Assert.Empty(logger.Levels);
        Assert.False(ArticleSourceAddressPolicy.PublicOnly.AllowFakeIpRange);
        Assert.False(ArticleSourceAddressPolicy.PublicOnly.IsAllowed(IPAddress.Parse("198.18.0.1")));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void PrivateLoopbackAndLinkLocalStayRejectedEvenWithFlag(string address)
    {
        var policy = ArticleSourceAddressPolicy.Create(Enabled, new FakeEnvironment(Environments.Development), NullLogger.Instance);
        Assert.False(policy.IsAllowed(IPAddress.Parse(address)));
        Assert.False(ArticleSourceAddressPolicy.PublicOnly.IsAllowed(IPAddress.Parse(address)));
    }

    // Outside 198.18.0.0/15 the flag must not change anything in either direction.
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("2001:db8::c612:1")]
    public void AddressesOutsideTheFakeIpRangeFollowPublicRulesOnly(string address)
    {
        var parsed = IPAddress.Parse(address);
        Assert.Equal(ArticleSourceReader.IsPublicAddress(parsed), new ArticleSourceAddressPolicy(true).IsAllowed(parsed));
        Assert.Equal(ArticleSourceReader.IsPublicAddress(parsed), ArticleSourceAddressPolicy.PublicOnly.IsAllowed(parsed));
    }

    [Fact]
    public async Task ReaderWithoutPolicyStillRejectsFakeIpLiteralBeforeAnyRequest()
    {
        var factory = new RecordingFactory();
        var result = await new ArticleSourceReader(factory).ReadAsync("http://198.18.0.1/about");

        Assert.Equal("SOURCE_PRIVATE_HOST", result.ErrorCode);
        Assert.NotEqual("ready", result.ReadStatus);
        Assert.Empty(factory.Requested);
    }

    [Fact]
    public async Task ReaderWithPublicOnlyPolicyRejectsFakeIpLiteral()
    {
        var factory = new RecordingFactory();
        var result = await new ArticleSourceReader(factory, ArticleSourceAddressPolicy.PublicOnly).ReadAsync("http://198.18.0.1/about");

        Assert.Equal("SOURCE_PRIVATE_HOST", result.ErrorCode);
        Assert.Empty(factory.Requested);
    }

    [Fact]
    public async Task ReaderWithFakeIpPolicyPassesThePreCheckForFakeIpLiteral()
    {
        var factory = new RecordingFactory();
        var result = await new ArticleSourceReader(factory, new ArticleSourceAddressPolicy(true)).ReadAsync("http://198.18.0.1/about");

        Assert.Equal("ready", result.ReadStatus);
        Assert.Null(result.ErrorCode);
        Assert.Equal(["http://198.18.0.1/about"], factory.Requested);
        Assert.Contains("正文", result.BodyMarkdown);
    }

    [Theory]
    [InlineData("http://10.0.0.1/about")]
    [InlineData("http://127.0.0.1/about")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost/about")]
    [InlineData("http://metadata.google.internal/")]
    public async Task ReaderWithFakeIpPolicyStillRejectsPrivateHosts(string url)
    {
        var factory = new RecordingFactory();
        var result = await new ArticleSourceReader(factory, new ArticleSourceAddressPolicy(true)).ReadAsync(url);

        Assert.Equal("SOURCE_PRIVATE_HOST", result.ErrorCode);
        Assert.Empty(factory.Requested);
    }

    [Fact]
    public async Task ReaderAppliesThePolicyToEveryRedirectHop()
    {
        var factory = new RecordingFactory(redirectTo: "http://10.0.0.1/internal");
        var result = await new ArticleSourceReader(factory, new ArticleSourceAddressPolicy(true)).ReadAsync("http://198.18.0.1/about");

        Assert.Equal("SOURCE_PRIVATE_HOST", result.ErrorCode);
        Assert.Equal(["http://198.18.0.1/about"], factory.Requested);
    }

    [Fact]
    public async Task PinnedHandlerWithFakeIpPolicyStillRefusesLoopback()
    {
        // Local listener only: the fake-ip policy must not let the connect callback reach loopback.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new HttpClient(ArticleSourceReader.CreatePinnedHandler(new ArticleSourceAddressPolicy(true).IsAllowed));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{port}/"));
        Assert.Contains("private address", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingFactory(string? redirectTo = null) : IHttpClientFactory
    {
        public List<string> Requested { get; } = [];
        public string? RedirectTo { get; } = redirectTo;

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("research-web", name);
            return new HttpClient(new Handler(this));
        }

        private sealed class Handler(RecordingFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requested.Add(request.RequestUri!.AbsoluteUri);
                if (owner.RedirectTo is not null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(owner.RedirectTo) } });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><title>t</title><body><p>这是正文内容。</p></body></html>", Encoding.UTF8, "text/html")
                });
            }
        }
    }

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Jogeoai.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
