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

// R8 / AC10: the dev-only fake-ip allowance only widens site-icon outbound checks
// by 198.18.0.0/15; every other private range and ArticleSourceReader stay unchanged.
public sealed class SiteIconFakeIpTests
{
    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("::ffff:198.18.0.1")]
    public void FakeIpRangeIsRejectedWhenDisabled(string address)
    {
        Assert.False(new SiteIconAddressPolicy(false).IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.254")]
    [InlineData("198.18.0.0")]
    [InlineData("198.19.255.255")]
    [InlineData("::ffff:198.18.0.1")]
    [InlineData("::ffff:198.19.255.254")]
    public void FakeIpRangeIsAllowedWhenEnabled(string address)
    {
        Assert.True(new SiteIconAddressPolicy(true).IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    public void OtherPrivateAddressesStayRejectedWhenEnabled(string address)
    {
        Assert.False(new SiteIconAddressPolicy(true).IsAllowed(IPAddress.Parse(address)));
    }

    // 198.17.x / 198.20.x sit just outside the /15 and are ordinary public
    // addresses: the switch must not affect them in either direction.
    [Theory]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("::ffff:198.20.0.1")]
    [InlineData("2001:db8::c612:1")]
    public void AddressesOutsideTheFakeIpRangeFollowPublicRulesOnly(string address)
    {
        var parsed = IPAddress.Parse(address);
        Assert.False(SiteIconAddressPolicy.IsFakeIpAddress(parsed));
        Assert.Equal(ArticleSourceReader.IsPublicAddress(parsed), new SiteIconAddressPolicy(true).IsAllowed(parsed));
        Assert.Equal(ArticleSourceReader.IsPublicAddress(parsed), new SiteIconAddressPolicy(false).IsAllowed(parsed));
    }

    [Fact]
    public void PublicAddressesAreAllowedEitherWay()
    {
        Assert.True(new SiteIconAddressPolicy(false).IsAllowed(IPAddress.Parse("8.8.8.8")));
        Assert.True(new SiteIconAddressPolicy(true).IsAllowed(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void ArticleSourceReaderStillTreatsFakeIpRangeAsPrivate()
    {
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("198.18.0.1")));
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("198.19.255.254")));
        Assert.False(ArticleSourceReader.IsPublicAddress(IPAddress.Parse("::ffff:198.18.0.1")));
    }

    [Fact]
    public async Task ArticleSourceReaderHostValidationIsUnchanged()
    {
        Assert.Equal("SOURCE_PRIVATE_HOST", await ArticleSourceReader.ValidatePublicHostAsync("http://198.18.0.1/", CancellationToken.None));
        Assert.Null(await ArticleSourceReader.ValidatePublicHostAsync("http://8.8.8.8/", CancellationToken.None));
    }

    [Theory]
    [InlineData("http://198.18.0.1/favicon.ico", false, SiteIconHostCheck.Private)]
    [InlineData("http://198.18.0.1/favicon.ico", true, SiteIconHostCheck.Public)]
    [InlineData("https://198.19.255.254/a.png", true, SiteIconHostCheck.Public)]
    [InlineData("http://[::ffff:198.18.0.1]/favicon.ico", true, SiteIconHostCheck.Public)]
    [InlineData("http://10.0.0.1/favicon.ico", true, SiteIconHostCheck.Private)]
    [InlineData("http://127.0.0.1/favicon.ico", true, SiteIconHostCheck.Private)]
    [InlineData("http://169.254.169.254/latest/meta-data", true, SiteIconHostCheck.Private)]
    [InlineData("http://localhost/favicon.ico", true, SiteIconHostCheck.Private)]
    [InlineData("http://metadata.google.internal/", true, SiteIconHostCheck.Private)]
    public async Task HostGuardAppliesPolicyToIpLiterals(string url, bool allowFakeIp, SiteIconHostCheck expected)
    {
        var guard = new PublicSiteIconHostGuard(new SiteIconAddressPolicy(allowFakeIp));
        Assert.Equal(expected, await guard.CheckAsync(new Uri(url), CancellationToken.None));
    }

    [Fact]
    public void ConfiguredTrueOutsideDevelopmentIsIgnoredWithWarning()
    {
        foreach (var environmentName in new[] { Environments.Production, Environments.Staging, "Local" })
        {
            var logger = new CapturingLogger();
            var policy = SiteIconAddressPolicy.Create(new SiteIconOptions { AllowFakeIpRange = true }, new FakeEnvironment(environmentName), logger);
            Assert.False(policy.AllowFakeIpRange);
            Assert.False(policy.IsAllowed(IPAddress.Parse("198.18.0.1")));
            Assert.Contains(LogLevel.Warning, logger.Levels);
        }
    }

    [Fact]
    public void ConfiguredTrueInDevelopmentIsEffective()
    {
        var logger = new CapturingLogger();
        var policy = SiteIconAddressPolicy.Create(new SiteIconOptions { AllowFakeIpRange = true }, new FakeEnvironment(Environments.Development), logger);
        Assert.True(policy.AllowFakeIpRange);
        Assert.True(policy.IsAllowed(IPAddress.Parse("198.18.0.1")));
        Assert.DoesNotContain(LogLevel.Warning, logger.Levels);
    }

    [Fact]
    public void UnconfiguredIsDisabledEvenInDevelopment()
    {
        Assert.False(new SiteIconOptions().AllowFakeIpRange);
        var logger = new CapturingLogger();
        var policy = SiteIconAddressPolicy.Create(new SiteIconOptions(), new FakeEnvironment(Environments.Development), logger);
        Assert.False(policy.AllowFakeIpRange);
        Assert.Empty(logger.Levels);
        Assert.False(SiteIconAddressPolicy.Create(new SiteIconOptions(), new FakeEnvironment(Environments.Production), NullLogger.Instance).AllowFakeIpRange);
    }

    [Fact]
    public async Task PinnedHandlerUsesSuppliedPredicateWhileDefaultStaysPublicOnly()
    {
        // Loopback stands in for an address only the custom predicate allows; the
        // listener is local so no external network is touched.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/";
        var server = ServeOnceAsync(listener);

        using (var fallback = new HttpClient(ArticleSourceReader.CreatePinnedHandler()))
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => fallback.GetAsync(url));
            Assert.Contains("private address", exception.Message, StringComparison.Ordinal);
        }
        using (var denied = new HttpClient(ArticleSourceReader.CreatePinnedHandler(new SiteIconAddressPolicy(true).IsAllowed)))
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => denied.GetAsync(url));
            Assert.Contains("private address", exception.Message, StringComparison.Ordinal);
        }
        using (var allowed = new HttpClient(ArticleSourceReader.CreatePinnedHandler(IPAddress.IsLoopback)))
        {
            using var response = await allowed.GetAsync(url);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        await server;
    }

    private static async Task ServeOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var buffer = new byte[4096];
        var received = new StringBuilder();
        while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
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
