using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>PackyAPI Worker 把租约上的 Provider 与回答模式原样交给 PackyAPI 客户端。</summary>
public sealed class PackyApiTaskWorkerTests
{
    [Theory]
    [InlineData("packy_qwen", "deep")]
    [InlineData("packy_glm", "fast")]
    [InlineData("packy_kimi", "fast")]
    [InlineData("packy_yuanbao", "deep")]
    public async Task LeaseAnswerModeReachesTheClientRequest(string provider, string mode)
    {
        var definition = AnswerProviderDefinition.Packy.Single(d => d.Provider == provider);
        var now = DateTimeOffset.UtcNow;
        var lease = new CloroTaskLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "工业管道内窥镜有哪些品牌？", definition.Engines[0], "zh-CN", "CN", CloroTaskStates.Leased, null,
            1, now.AddMinutes(10), 1, now.AddMinutes(-1), null, null, AnswerMode: mode);
        var store = new DeepSeekApiTaskWorkerTests.FakeStore(lease) { ExpectedLeaseSeconds = 600 };
        var client = new FakeClient();

        using (var worker = Create(provider, store, client))
        {
            await worker.StartAsync(CancellationToken.None);
            try { await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await worker.StopAsync(CancellationToken.None); }
        }

        var request = Assert.Single(client.Requests);
        Assert.Equal((provider, mode, lease.TaskId, "工业管道内窥镜有哪些品牌？"),
            (request.Provider, request.AnswerMode, request.ProviderTaskId, request.Prompt));
        Assert.Equal(provider, store.LeasedProvider);
        Assert.NotNull(store.Completed);
    }

    private static PackyApiTaskWorker Create(string provider, ICloroVisibilityStore store, IPackyAnswerClient client)
    {
        var options = Options.Create(new PackyWorkerOptions());
        return provider switch
        {
            "packy_qwen" => new PackyQwenTaskWorker(NullLogger<PackyQwenTaskWorker>.Instance, store, client, options),
            "packy_glm" => new PackyGlmTaskWorker(NullLogger<PackyGlmTaskWorker>.Instance, store, client, options),
            "packy_kimi" => new PackyKimiTaskWorker(NullLogger<PackyKimiTaskWorker>.Instance, store, client, options),
            "packy_yuanbao" => new PackyYuanbaoTaskWorker(NullLogger<PackyYuanbaoTaskWorker>.Instance, store, client, options),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }

    private sealed class FakeClient : IPackyAnswerClient
    {
        public List<PackyAnswerRequest> Requests { get; } = [];

        public Task<CloroAnswerSnapshot> AskAsync(PackyAnswerRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            var definition = AnswerProviderDefinition.Packy.Single(d => d.Provider == request.Provider);
            var raw = JsonSerializer.SerializeToElement(new { id = "resp_packy_fixture", model = "fixture", output = Array.Empty<object>() });
            return Task.FromResult(new CloroAnswerSnapshot(definition.Provider, definition.Engines[0], definition.CaptureMode,
                definition.EvidenceClass, "fixture", "回答", true, [], ["管道内窥镜 品牌"], raw, DateTimeOffset.UtcNow,
                ParserVersion: definition.ParserVersion, QueryAvailability: "available"));
        }
    }
}
