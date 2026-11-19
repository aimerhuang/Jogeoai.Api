using System.Net;
using System.Text;
using System.Text.Json;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

internal static class CloroAdapterTestFixture
{
    internal static CloroAdapter Create(FakeHandler handler) => Create(handler, DefaultPool());

    internal static CloroAdapter Create(FakeHandler handler, ICloroKeyPool pool, CloroKeyPoolOptions? poolOptions = null,
        CloroConnectionSettings? settings = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://cloro.test") },
        new FakeSettings(settings ?? new("", "https://cloro.test/v1/async/task", true)), pool,
        Options.Create(new CloroAdapterOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(2),
            InitialPollDelay = TimeSpan.FromMilliseconds(1),
            MaximumPollDelay = TimeSpan.FromMilliseconds(1),
        }), Options.Create(poolOptions ?? new CloroKeyPoolOptions()), NullLogger<CloroAdapter>.Instance);

    internal static InMemoryCloroKeyPool DefaultPool()
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("default", "test-api-key");
        return pool;
    }

    internal static async Task<CloroAnswerSnapshot> NormalizeAsync(string response, string engine)
    {
        var envelope = JsonSerializer.Serialize(new { task = new { status = "COMPLETED" },
            response = JsonSerializer.Deserialize<JsonElement>(response) });
        var adapter = Create(new FakeHandler(_ => Json(envelope)));
        return adapter.Normalize(await adapter.GetAsync("constructed-schema-fixture"), engine);
    }

    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    internal static HttpResponseMessage Status(HttpStatusCode status, string body = "") => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakeSettings(CloroConnectionSettings settings) : ICloroSettingsStore
    {
        public Task<CloroConnectionSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloroConnectionSettings?>(settings);
        public Task SaveAsync(CloroConnectionSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        internal List<string> RequestBodies { get; } = [];
        internal List<string?> Authorizations { get; } = [];
        internal List<Uri?> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.Parameter);
            Uris.Add(request.RequestUri);
            if (request.Content is not null) RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request);
        }
    }
}
