using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jogeoai.Providers.Cloro;

/// <summary>Cloro 外部请求和诊断轮询的时间边界。</summary>
public sealed class CloroAdapterOptions
{
    /// <summary>首次轮询等待时间。</summary>
    public TimeSpan InitialPollDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>最大轮询等待时间。</summary>
    public TimeSpan MaximumPollDelay { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>生成阶段最长等待时间。</summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>单任务诊断最长等待时间。</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromHours(1);
    /// <summary>单次 HTTP 请求（含响应体读取）的最长时间。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Cloro 异步任务适配器，提交、轮询和标准化可分别恢复；提交时从 key 池选 key，仅在明确拒绝时切换账号。</summary>
public sealed class CloroAdapter(
    HttpClient httpClient,
    ICloroSettingsStore settingsStore,
    ICloroKeyPool keyPool,
    IOptions<CloroAdapterOptions> options,
    IOptions<CloroKeyPoolOptions> poolOptions,
    ILogger<CloroAdapter> logger) : ICloroAdapter, ICloroTaskClient, ICloroCreditsClient
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 16 * 1024;
    private const int MaximumCreditsBytes = 64 * 1024;
    private const string InsufficientCredits = "INSUFFICIENT_CREDITS";
    private const string ModelUnavailable = "MODEL_UNAVAILABLE";
    private const string AiOverviewNotShown = "ai_overview_not_shown";
    // Only these values are retained from an error body; anything else falls back to the HTTP status.
    private static readonly HashSet<string> AllowedErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        InsufficientCredits, "CONCURRENCY_LIMIT_EXCEEDED", "RATE_LIMIT_EXCEEDED", "QUEUE_LIMIT_EXCEEDED",
        "UNAUTHORIZED", "INVALID_API_KEY", "INVALID_API_KEY_FORMAT", "API_KEY_REVOKED", "MISSING_API_KEY", "FORBIDDEN",
        ModelUnavailable,
    };
    private static long lastPoolWarningTicks;
    private const int MaximumCitations = 100;
    private const int MaximumQueries = 100;
    private static readonly IReadOnlyDictionary<string, CloroTaskDefinition> TaskDefinitions =
        new Dictionary<string, CloroTaskDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["chatgpt"] = new("CHATGPT", "prompt", new { markdown = true, searchQueries = true }),
            ["perplexity"] = new("PERPLEXITY", "prompt", new { markdown = true }),
            ["copilot"] = new("COPILOT", "prompt", new { markdown = true }),
            ["gemini"] = new("GEMINI", "prompt", new { markdown = true }),
            ["google-ai-mode"] = new("AIMODE", "prompt", new { markdown = true }),
            ["google-ai-overview"] = new("GOOGLE", "query", new { aioverview = new { markdown = true } }, SendsInterfaceLanguage: true),
        };
    private static readonly HashSet<string> QueuedStatuses = ["QUEUED", "PENDING", "CREATED", "SCHEDULED"];
    private static readonly HashSet<string> RunningStatuses = ["RUNNING", "PROCESSING", "IN_PROGRESS"];
    private static readonly HashSet<string> CompletedStatuses = ["COMPLETED", "SUCCESS"];
    private static readonly HashSet<string> FailedStatuses = ["FAILED", "ERROR", "CANCELLED", "CANCELED"];
    private static readonly Regex UnsafeText = new("<(?:!doctype|html|head|body|script)\\b|data:image/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BearerValue = new(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> SensitiveFields = [
        "authorization", "proxyauthorization", "apikey", "key", "token", "accesstoken", "refreshtoken", "idtoken", "authtoken", "auth", "signature", "sig",
        "cookie", "cookies", "setcookie", "password", "secret", "clientsecret", "credentials", "credential",
        "profile", "profilepath", "browserprofile", "userdatadir", "storagestate", "headers", "requestheaders", "responseheaders",
        "html", "rawhtml", "pagehtml", "innerhtml", "outerhtml", "screenshot", "screenshots", "screenshoturl", "screenshotbase64",
        "error", "errormessage", "stack", "stacktrace"
    ];

    /// <inheritdoc />
    public async Task<CloroSubmittedTask> SubmitAsync(CloroTaskRequest request, CancellationToken cancellationToken = default)
        => (await SubmitWithPoolAsync(request, cancellationToken)).Task;

    private async Task<(CloroSubmittedTask Task, CloroKeyLease Key)> SubmitWithPoolAsync(CloroTaskRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TaskDefinitions.TryGetValue(request.Engine, out var definition))
            throw new ArgumentException("Cloro engine is not supported.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) || Encoding.UTF8.GetByteCount(request.Prompt) > 65_535)
            throw new ArgumentException("Cloro prompt is empty or exceeds the size limit.", nameof(request));
        if (request.Country is not { Length: 2 } || request.Country.Any(c => !char.IsAsciiLetter(c)))
            throw new ArgumentException("Country must be a two-letter ISO code.", nameof(request));

        var endpoint = await GetEndpointAsync(cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            [definition.Field] = request.Prompt,
            ["country"] = request.Country.ToUpperInvariant(),
            ["include"] = definition.Include,
        };
        // Google surfaces accept an interface language independent of country; the locale is sent unchanged (e.g. zh-CN).
        if (definition.SendsInterfaceLanguage && !string.IsNullOrWhiteSpace(request.Locale))
            payload["hl"] = request.Locale;
        var requestJson = JsonSerializer.Serialize(new { taskType = definition.TaskType, payload });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))).ToLowerInvariant();
        var excluded = new List<Guid>();
        var attempts = Math.Clamp(poolOptions.Value.MaxSubmitAttemptsPerLease, 1, 10);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            // A stopping Worker must not start another account's submission after a rejection.
            cancellationToken.ThrowIfCancellationRequested();
            var key = await PickKeyAsync(excluded, cancellationToken)
                ?? throw await PoolUnavailableAsync(cancellationToken);
            if (!UsableKey(key.ApiKey))
            {
                await TryMarkRejectedAsync(key, request.ProviderTaskId, CloroKeyRejection.Unauthorized, "INVALID_KEY_FORMAT");
                excluded.Add(key.KeyId);
                continue;
            }
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.ApiKey);
            JsonElement submitted;
            try { submitted = await SendAsync(requestMessage, submitting: true, cancellationToken); }
            catch (KeyRejectedException rejected)
            {
                // 401/403/429 mean Cloro created no task, so trying another account cannot double-bill.
                // Timeouts, 5xx, network and unparseable bodies stay SubmissionUnknown and never reach here.
                await TryMarkRejectedAsync(key, request.ProviderTaskId, rejected.Rejection, rejected.ErrorCode);
                excluded.Add(key.KeyId);
                continue;
            }
            var taskId = ReadString(submitted, "task", "id") ?? ReadString(submitted, "id");
            if (!ValidTaskId(taskId) || taskId!.Contains(key.ApiKey, StringComparison.Ordinal))
                throw SubmissionUnknown();
            await TryMarkUsedAsync(key, request.ProviderTaskId, taskId);
            return (new CloroSubmittedTask(taskId, DateTimeOffset.UtcNow, hash, key.KeyId), key);
        }
        throw new CloroProviderException("CLORO_KEY_SWITCH_LIMIT",
            "Cloro rejected the submission for every account tried in this lease.", true);
    }

    /// <inheritdoc />
    public async Task<CloroTaskStatusSnapshot> GetAsync(string externalTaskId, Guid? keyId = null, CancellationToken cancellationToken = default)
    {
        if (!ValidTaskId(externalTaskId)) throw new ArgumentException("Cloro task ID is invalid.", nameof(externalTaskId));
        var endpoint = await GetEndpointAsync(cancellationToken);
        // Tasks belong to the submitting organization: always poll with that key, even if it was disabled since.
        var key = await keyPool.GetAsync(keyId, cancellationToken);
        if (key is null || !UsableKey(key.ApiKey)) throw NotConfigured();
        var uri = new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(externalTaskId)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.ApiKey);
        JsonElement payload;
        try { payload = await SendAsync(request, submitting: false, cancellationToken); }
        catch (KeyRejectedException rejected)
        {
            await TryMarkRejectedAsync(key, null, CloroKeyRejection.Unauthorized, rejected.ErrorCode);
            throw new CloroProviderException("CLORO_KEY_INVALID", "Cloro rejected the API key used for this task.", false);
        }
        var status = ReadStatus(payload);
        // Never retain Provider error text, even when a failed response carries a response object.
        if (FailedStatuses.Contains(status))
            return new CloroTaskStatusSnapshot(externalTaskId, "FAILED", JsonSerializer.SerializeToElement(new { status = "FAILED" }),
                "CLORO_TASK_FAILED", "Cloro task failed.", ProviderFailureCode: ReadFailureCode(payload));
        var safe = Sanitize(payload, key.ApiKey);
        return new CloroTaskStatusSnapshot(externalTaskId, status, safe,
            AnswerRedacted: AnswerFieldsChanged(payload, safe), SourcesRedacted: SourceFieldsChanged(payload, safe));
    }

    /// <inheritdoc />
    public async Task<CloroCreditsSnapshot> GetCreditsAsync(CloroKeyLease key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!UsableKey(key.ApiKey)) throw new CloroProviderException("CLORO_KEY_INVALID", "Cloro API key format is invalid.", false);
        var uri = await GetCreditsUriAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.ApiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = Positive(options.Value.RequestTimeout, TimeSpan.FromSeconds(30));
        deadline.CancelAfter(timeout < TimeSpan.FromSeconds(10) ? timeout : TimeSpan.FromSeconds(10));
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new CloroProviderException("CLORO_KEY_INVALID", "Cloro rejected the API key.", false);
            if (!response.IsSuccessStatusCode)
                throw new CloroProviderException(response.StatusCode == HttpStatusCode.TooManyRequests ? "CLORO_RATE_LIMITED" : "CLORO_CREDITS_FAILED",
                    "Cloro credits query failed.", true);
            return ParseCredits(await ReadJsonAsync(response, MaximumCreditsBytes, deadline.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloroProviderException("CLORO_CREDITS_FAILED", "Cloro credits query timed out.", true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new CloroProviderException("CLORO_CREDITS_FAILED", "Cloro credits query is temporarily unavailable.", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw InvalidResponse();
        }
    }

    /// <inheritdoc />
    public CloroAnswerSnapshot Normalize(CloroTaskStatusSnapshot completed, string requestedEngine)
    {
        ArgumentNullException.ThrowIfNull(completed);
        requestedEngine = requestedEngine.Trim().ToLowerInvariant();
        if (!TaskDefinitions.ContainsKey(requestedEngine)) throw new ArgumentException("Cloro engine is not supported.", nameof(requestedEngine));
        if (!CompletedStatuses.Contains(completed.Status) || completed.Payload.ValueKind != JsonValueKind.Object)
            throw InvalidResponse();
        // Re-validate the envelope for callers supplying a snapshot directly.
        if (ReadStatus(completed.Payload) != completed.Status) throw InvalidResponse();
        var safe = Sanitize(completed.Payload, null);
        if (!safe.TryGetProperty("response", out var output) || output.ValueKind != JsonValueKind.Object)
            throw InvalidResponse();
        var answer = output;
        string? answerAbsence = null;
        if (requestedEngine == "google-ai-overview")
        {
            // Google showed no AI Overview: the key is absent or null. An empty overview object stays a plain empty answer.
            if (!output.TryGetProperty("aioverview", out answer) || answer.ValueKind == JsonValueKind.Null)
            {
                answer = JsonSerializer.SerializeToElement(new { });
                answerAbsence = AiOverviewNotShown;
            }
            else if (answer.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        }
        var text = FirstString(answer, "markdown", "text") ?? string.Empty;
        var providedModel = FirstString(answer, "model", "modelVersion") ?? FirstString(output, "model", "modelVersion");
        var model = BoundString(providedModel ?? requestedEngine, 128);
        var (queries, availability) = ExtractQueries(answer);
        var (citations, quality) = ExtractCitations(answer,
            completed.SourcesRedacted || SourceFieldsChanged(completed.Payload, safe));
        return new CloroAnswerSnapshot("cloro", requestedEngine, "scraped", "observed_surface", model, text,
            !string.IsNullOrWhiteSpace(text), citations, queries, output.Clone(), DateTimeOffset.UtcNow,
            ParserVersion: "cloro-v2", QueryAvailability: availability,
            ResultQuality: new(completed.AnswerRedacted || AnswerFieldsChanged(completed.Payload, safe),
                providedModel is not null && !string.Equals(model, requestedEngine, StringComparison.OrdinalIgnoreCase)
                    && !model.Contains("redacted", StringComparison.OrdinalIgnoreCase), quality, answerAbsence));
    }

    /// <inheritdoc />
    public async Task<CloroTestResult> RunAsync(CloroTestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var engine = request.Model.ToProviderModelName();
        using var totalDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalDeadline.CancelAfter(Positive(options.Value.TotalTimeout, TimeSpan.FromHours(1)));
        try
        {
            var (submitted, key) = await SubmitWithPoolAsync(new CloroTaskRequest(engine, request.Prompt, "en-US", request.Country), totalDeadline.Token);
            DateTimeOffset? generationStarted = null;
            var attempt = 0;
            while (true)
            {
                await Task.Delay(PollDelay(attempt++), totalDeadline.Token);
                if (generationStarted is { } started && DateTimeOffset.UtcNow - started >= Positive(options.Value.GenerationTimeout, TimeSpan.FromMinutes(10)))
                    throw new CloroProviderException("CLORO_GENERATION_TIMEOUT", "Cloro generation timed out.", false);
                CloroTaskStatusSnapshot snapshot;
                try { snapshot = await GetAsync(submitted.ExternalTaskId, key.KeyId, totalDeadline.Token); }
                catch (CloroProviderException exception) when (exception.Retryable)
                {
                    logger.LogDebug("Cloro diagnostic poll will retry with code {ErrorCode}.", exception.Code);
                    continue;
                }
                if (snapshot.Status == "FAILED") throw new CloroProviderException("CLORO_TASK_FAILED", "Cloro task failed.", false);
                if (CompletedStatuses.Contains(snapshot.Status))
                {
                    var answer = Normalize(snapshot, engine);
                    return new CloroTestResult(submitted.ExternalTaskId, answer.ModelVersion, answer.AnswerText,
                        answer.Citations, answer.WebQueries, new { redacted = true }, answer.CapturedAt, key.AccountLabel, key.KeyHint);
                }
                if (RunningStatuses.Contains(snapshot.Status)) generationStarted ??= DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloroProviderException("CLORO_POLL_TIMEOUT", "Cloro diagnostic task timed out.", false);
        }
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, bool submitting, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Positive(options.Value.RequestTimeout, TimeSpan.FromSeconds(30)));
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (submitting)
                {
                    // Definite rejections: Cloro did not create a task. Only the allowlisted code is read from the body.
                    if (status is 401 or 403 or 429)
                    {
                        var code = await ReadErrorCodeAsync(response, deadline.Token, cancellationToken);
                        throw new KeyRejectedException(Classify(status, code), code ?? $"HTTP_{status}");
                    }
                    if (response.StatusCode == HttpStatusCode.RequestTimeout || status >= 500)
                        throw SubmissionUnknown();
                    // The provider disabled the model for everyone: terminal, and switching accounts cannot help.
                    if (await ReadErrorCodeAsync(response, deadline.Token, cancellationToken) == ModelUnavailable)
                        throw new CloroProviderException("CLORO_MODEL_UNAVAILABLE", "Cloro cannot access this model right now.", false);
                    throw new CloroProviderException("CLORO_SUBMIT_FAILED", "Cloro rejected the task submission.", false);
                }
                if (status == 401) throw new KeyRejectedException(CloroKeyRejection.Unauthorized, "HTTP_401");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new CloroProviderException("CLORO_RATE_LIMITED", "Cloro polling was rate limited.", true);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new CloroProviderException("CLORO_TASK_NOT_FOUND", "Cloro task was not found.", false);
                if (response.StatusCode == HttpStatusCode.RequestTimeout || status >= 500)
                    throw new CloroProviderException("CLORO_POLL_FAILED", "Cloro polling is temporarily unavailable.", true);
                throw new CloroProviderException("CLORO_POLL_FAILED", "Cloro polling was rejected.", false);
            }
            if (!submitting && response.StatusCode == HttpStatusCode.NoContent)
                return JsonSerializer.SerializeToElement(new { status = "QUEUED" });
            return await ReadJsonAsync(response, MaximumResponseBytes, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (submitting) throw SubmissionUnknown();
            throw new CloroProviderException("CLORO_POLL_TIMEOUT", "Cloro polling request timed out.", true);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            if (submitting) throw SubmissionUnknown();
            throw new CloroProviderException("CLORO_POLL_FAILED", "Cloro polling is temporarily unavailable.", true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            if (submitting) throw SubmissionUnknown();
            throw InvalidResponse();
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        int length;
        while ((length = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + length > maximumBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, length);
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        return document.RootElement.Clone();
    }

    // Reads only error.code / code and keeps it only when it is on the allowlist; the HTTP status
    // already proves the rejection, so any read or parse failure simply falls back to it.
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadJsonAsync(response, MaximumErrorBytes, deadline);
            var code = ReadString(body, "error", "code") ?? ReadString(body, "code");
            return code is not null && AllowedErrorCodes.TryGetValue(code, out var known) ? known : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException
            or HttpRequestException or InvalidOperationException) { return null; }
    }

    private static CloroKeyRejection Classify(int status, string? code) => status switch
    {
        401 => CloroKeyRejection.Unauthorized,
        403 => code == InsufficientCredits ? CloroKeyRejection.InsufficientCredits : CloroKeyRejection.Forbidden,
        _ => code switch
        {
            "CONCURRENCY_LIMIT_EXCEEDED" => CloroKeyRejection.ConcurrencyLimited,
            "QUEUE_LIMIT_EXCEEDED" => CloroKeyRejection.QueueFull,
            _ => CloroKeyRejection.RateLimited,
        },
    };

    // A FAILED task may say why; only the insufficient-credits code is recognised and nothing else is kept.
    private static string? ReadFailureCode(JsonElement payload)
    {
        string?[] candidates =
        [
            ReadString(payload, "error", "code"), ReadString(payload, "task", "error", "code"),
            ReadString(payload, "code"), ReadString(payload, "task", "code"),
            ReadString(payload, "error"), ReadString(payload, "task", "error"),
        ];
        return candidates.Any(value => string.Equals(value, InsufficientCredits, StringComparison.OrdinalIgnoreCase))
            ? InsufficientCredits : null;
    }

    private static CloroCreditsSnapshot ParseCredits(JsonElement payload)
    {
        foreach (var candidate in new[] { payload, Child(payload, "data"), Child(payload, "credits") })
        {
            if (candidate is not { ValueKind: JsonValueKind.Object } source || ReadCredits(source, "remaining") is not { } remaining)
                continue;
            return new CloroCreditsSnapshot(remaining, ReadCredits(source, "perCycle"), ReadTime(source, "cycleResetsAt"),
                DateTimeOffset.UtcNow);
        }
        throw InvalidResponse();
    }

    private static JsonElement? Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : null;

    private static decimal? ReadCredits(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return null;
        decimal number;
        if (value.ValueKind == JsonValueKind.Number) { if (!value.TryGetDecimal(out number)) throw new InvalidDataException(); }
        else if (value.ValueKind == JsonValueKind.String)
        {
            if (!decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out number)) throw new InvalidDataException();
        }
        else if (value.ValueKind == JsonValueKind.Null) return null;
        else throw new InvalidDataException();
        // DECIMAL(18,3) bound.
        if (Math.Abs(number) >= 1_000_000_000_000_000m) throw new InvalidDataException();
        return Math.Round(number, 3, MidpointRounding.ToZero);
    }

    private static DateTimeOffset? ReadTime(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            && parsed.Year is >= 2000 and <= 9000)
            return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch) && epoch > 0)
        {
            var time = epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (time.Year is >= 2000 and <= 9000) return time;
        }
        throw new InvalidDataException();
    }

    private async Task<Uri> GetEndpointAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (settings is null || !settings.Enabled || !TryEndpoint(settings.ApiUrl, out var endpoint)) throw NotConfigured();
        return endpoint;
    }

    private async Task<Uri> GetCreditsUriAsync(CancellationToken cancellationToken)
    {
        var configured = poolOptions.Value.CreditsUrl;
        if (!string.IsNullOrWhiteSpace(configured)) return TryEndpoint(configured, out var uri) ? uri : throw NotConfigured();
        // Validating a key must work while Cloro is globally disabled, so only the URL is required here.
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (settings is null || !TryEndpoint(settings.ApiUrl, out var endpoint)) throw NotConfigured();
        return new Uri($"{endpoint.GetLeftPart(UriPartial.Authority)}/v1/credits");
    }

    private static bool TryEndpoint(string? value, out Uri endpoint)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint!) || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            return false;
        return true;
    }

    private async Task<CloroKeyLease?> PickKeyAsync(IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken)
    {
        try { return await keyPool.PickAsync(excluded, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nothing has been sent for this attempt, so a later retry cannot duplicate a task.
            logger.LogWarning("Cloro key pool could not be read ({ExceptionType}).", exception.GetType().Name);
            throw PoolReadFailed();
        }
    }

    private async Task<CloroProviderException> PoolUnavailableAsync(CancellationToken cancellationToken)
    {
        CloroKeyPoolAvailability availability;
        try { availability = await keyPool.GetAvailabilityAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Cloro key pool availability could not be read ({ExceptionType}).", exception.GetType().Name);
            return PoolReadFailed();
        }
        if (availability.EnabledKeys == 0) return NotConfigured();
        // Accounts rejected in this lease may still look available until their state write lands; retry soon.
        var next = availability.AvailableKeys > 0 ? DateTimeOffset.UtcNow.AddSeconds(10) : availability.NextAvailableAt;
        // Only accounts that are out of credits (or invalid) exhaust the pool; a 429 cooldown merely keeps it busy.
        if (!availability.CreditsExhausted)
            return new CloroProviderException("CLORO_KEY_POOL_BUSY", "Every Cloro account is briefly at capacity.", true,
                nextAttemptAt: next);
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastPoolWarningTicks);
        if ((last == 0 || now - last >= 60_000) && Interlocked.CompareExchange(ref lastPoolWarningTicks, now, last) == last)
            logger.LogWarning("Cloro key pool has no available account among {EnabledKeys} enabled; next recovery at {NextAvailableAt}.",
                availability.EnabledKeys, next);
        return new CloroProviderException("CLORO_KEY_POOL_EXHAUSTED", "No Cloro account currently has credits.", true,
            nextAttemptAt: next);
    }

    private async Task TryMarkRejectedAsync(CloroKeyLease key, Guid? providerTaskId, CloroKeyRejection rejection, string errorCode)
    {
        if (rejection == CloroKeyRejection.Unauthorized)
            logger.LogWarning("Cloro account {AccountLabel} (key ending {KeyHint}) was rejected as invalid.", key.AccountLabel, key.KeyHint);
        // Short bookkeeping write that must not be cut by a stopping Worker; a lost write only costs one extra rejection later.
        try { await keyPool.MarkRejectedAsync(key.KeyId, providerTaskId, rejection, errorCode, CancellationToken.None); }
        catch (Exception exception)
        {
            logger.LogWarning("Cloro rejection {ErrorCode} for account {AccountLabel} could not be recorded ({ExceptionType}).",
                errorCode, key.AccountLabel, exception.GetType().Name);
        }
    }

    private async Task TryMarkUsedAsync(CloroKeyLease key, Guid? providerTaskId, string externalTaskId)
    {
        // The external task exists now; a bookkeeping failure must never lose its ID.
        try { await keyPool.MarkUsedAsync(key.KeyId, providerTaskId, externalTaskId, CancellationToken.None); }
        catch (Exception exception)
        {
            logger.LogWarning("Cloro usage for account {AccountLabel} could not be recorded ({ExceptionType}).",
                key.AccountLabel, exception.GetType().Name);
        }
    }

    private static bool UsableKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) && !apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    private TimeSpan PollDelay(int attempt)
    {
        var initial = Positive(options.Value.InitialPollDelay, TimeSpan.FromMilliseconds(100));
        var maximum = options.Value.MaximumPollDelay < initial ? initial : options.Value.MaximumPollDelay;
        return TimeSpan.FromMilliseconds(Math.Min(initial.TotalMilliseconds * Math.Pow(2, Math.Min(20, attempt / 5)), maximum.TotalMilliseconds));
    }

    private static string ReadStatus(JsonElement payload)
    {
        var status = (ReadString(payload, "task", "status") ?? ReadString(payload, "status"))?.ToUpperInvariant();
        if (status is null || !(QueuedStatuses.Contains(status) || RunningStatuses.Contains(status) || CompletedStatuses.Contains(status) || FailedStatuses.Contains(status)))
            throw InvalidResponse();
        return status;
    }

    private static JsonElement Sanitize(JsonElement payload, string? apiKey)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteSafeJson(writer, payload, apiKey);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteSafeJson(Utf8JsonWriter writer, JsonElement element, string? apiKey)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.Concat(property.Name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
                    if (SensitiveFields.Contains(key) || key.Contains("apikey", StringComparison.Ordinal) || key.Contains("screenshot", StringComparison.Ordinal)
                        || key.Contains("cookie", StringComparison.Ordinal) || key.Contains("authorization", StringComparison.Ordinal)
                        || key.Contains("password", StringComparison.Ordinal) || key.Contains("secret", StringComparison.Ordinal)) continue;
                    writer.WritePropertyName(Redact(property.Name, apiKey));
                    WriteSafeJson(writer, property.Value, apiKey);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteSafeJson(writer, item, apiKey);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(Redact(element.GetString()!, apiKey));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string Redact(string value, string? apiKey)
    {
        if (!string.IsNullOrEmpty(apiKey)) value = value.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        try
        {
            if (UnsafeText.IsMatch(value)) return "[redacted]";
            return BearerValue.Replace(value, "Bearer [redacted]");
        }
        catch (RegexMatchTimeoutException) { return "[redacted]"; }
    }

    private static (IReadOnlyList<CloroCitation>, CloroCitationQuality) ExtractCitations(JsonElement answer, bool redacted)
    {
        var citations = new List<CloroCitation>();
        var sourcePosition = 0;
        var exposed = false;
        var malformedField = false;
        var filtered = 0;
        var invalid = 0;
        var truncated = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (redacted) reasons.Add("SOURCE_REDACTED");
        foreach (var field in new[] { "sources", "citationPills" })
        {
            if (!answer.TryGetProperty(field, out var values)) continue;
            exposed = true;
            if (values.ValueKind != JsonValueKind.Array) { malformedField = true; reasons.Add("INVALID_SOURCE_SHAPE"); continue; }
            foreach (var item in values.EnumerateArray())
            {
                sourcePosition++;
                var url = FirstString(item, "url", "link");
                if (url?.Contains("redacted", StringComparison.OrdinalIgnoreCase) == true)
                { filtered++; reasons.Add("UNSAFE_SOURCE"); continue; }
                if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                { invalid++; reasons.Add("INVALID_SOURCE_SHAPE"); continue; }
                if (url.Length > 750 || parsed.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(parsed.UserInfo))
                { filtered++; reasons.Add("UNSAFE_SOURCE"); continue; }
                var host = parsed.IdnHost.TrimEnd('.').ToLowerInvariant();
                if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.') || IPAddress.TryParse(host, out _)
                    || host.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                    || new[] { "localhost", "local", "internal", "intranet", "invalid", "test", "home", "lan", "onion" }.Any(suffix => host == suffix || host.EndsWith('.' + suffix, StringComparison.Ordinal)))
                { filtered++; reasons.Add("UNSAFE_SOURCE"); continue; }
                if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
                var builder = new UriBuilder(parsed) { Host = host, Fragment = string.Empty };
                var normalized = builder.Uri.AbsoluteUri;
                if (normalized.Length > 750 || normalized.Contains("redacted", StringComparison.OrdinalIgnoreCase) || HasCredentialQuery(parsed))
                { filtered++; reasons.Add("UNSAFE_SOURCE"); continue; }
                if (citations.Count == MaximumCitations) { truncated++; reasons.Add("SOURCE_LIMIT"); continue; }
                var title = FirstString(item, "label", "title");
                var boundedTitle = title is null ? null : BoundString(title, 512);
                if (boundedTitle != title) reasons.Add("SOURCE_FIELD_LIMIT");
                citations.Add(new CloroCitation(normalized, host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host,
                    boundedTitle, sourcePosition));
            }
        }
        var availability = !exposed ? "not_exposed" : malformedField || invalid > 0 ? "unknown"
            : sourcePosition == 0 ? "empty" : "available";
        var completeness = !exposed ? "unknown" : reasons.Count == 0 ? "complete"
            : malformedField && sourcePosition == 0 ? "unknown" : "partial";
        return (citations, new(availability, completeness, !exposed || malformedField ? null : sourcePosition,
            citations.Count, filtered, invalid, truncated, reasons.Order(StringComparer.Ordinal).ToArray()));
    }

    // Carry only boolean evidence across the security boundary, never the original content.
    private static bool AnswerFieldsChanged(JsonElement original, JsonElement safe)
        => FieldsChanged(original, safe, ["markdown", "text"]);

    private static bool SourceFieldsChanged(JsonElement original, JsonElement safe)
        => FieldsChanged(original, safe, ["sources", "citationPills"]);

    private static bool FieldsChanged(JsonElement original, JsonElement safe, string[] fields)
    {
        if (original.ValueKind != JsonValueKind.Object || safe.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in fields)
            if (original.TryGetProperty(field, out var before)
                && (!safe.TryGetProperty(field, out var after) || before.GetRawText() != after.GetRawText()))
            {
                // JSON whitespace/escaping alone is not a redaction.
                if (!safe.TryGetProperty(field, out after) || !JsonElement.DeepEquals(before, after)) return true;
            }
        foreach (var nested in new[] { "response", "aioverview" })
            if (original.TryGetProperty(nested, out var before) && safe.TryGetProperty(nested, out var after)
                && FieldsChanged(before, after, fields)) return true;
        return false;
    }

    private static bool HasCredentialQuery(Uri uri)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var field = Uri.UnescapeDataString(part.Split('=', 2)[0]);
            var key = string.Concat(field.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            if (SensitiveFields.Contains(key) || key is "signature" or "sig" or "auth") return true;
        }
        return false;
    }

    private static (IReadOnlyList<string> Queries, string Availability) ExtractQueries(JsonElement answer)
    {
        var exposed = false;
        var malformed = false;
        IReadOnlyList<string>? firstExposed = null;
        foreach (var name in new[] { "searchQueries", "search_model_queries", "mapSearchQueries" })
        {
            if (!answer.TryGetProperty(name, out var values)) continue;
            if (values.ValueKind != JsonValueKind.Array) { malformed = true; continue; }
            exposed = true;
            var result = new List<string>();
            var totalBytes = 2;
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String) { malformed = true; continue; }
                var text = BoundString(value.GetString()!, 2048);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(text).Length + 1;
                if (result.Count == MaximumQueries || totalBytes + bytes > 60_000) break;
                result.Add(text); totalBytes += bytes;
            }
            firstExposed ??= result;
            if (result.Any(value => !string.IsNullOrWhiteSpace(value))) return (result, "available");
        }
        return (firstExposed ?? [], exposed ? malformed && firstExposed?.Count == 0 ? "unknown" : "empty"
            : malformed ? "unknown" : "not_exposed");
    }

    private static string BoundString(string value, int maximumScalars)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumScalars));
        var count = 0;
        foreach (var rune in value.EnumerateRunes()) { if (count++ == maximumScalars) break; builder.Append(rune.ToString()); }
        return builder.ToString();
    }
    private static string? FirstString(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())) return field.GetString();
        return null;
    }
    private static string? ReadString(JsonElement value, params string[] path)
    {
        foreach (var key in path) if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static bool ValidTaskId(string? value) => value is { Length: > 0 and <= 255 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static TimeSpan Positive(TimeSpan value, TimeSpan fallback) => value > TimeSpan.Zero ? value : fallback;
    private static CloroProviderException SubmissionUnknown() => new("CLORO_SUBMISSION_UNKNOWN", "Cloro submission outcome is unknown; automatic resubmission is disabled.", false, true);
    private static CloroProviderException InvalidResponse() => new("INVALID_PROVIDER_RESPONSE", "Cloro returned an invalid task response.", false);
    private static CloroProviderException NotConfigured() => new("CLORO_NOT_CONFIGURED", "Cloro connection is not configured or is invalid.", false);
    private static CloroProviderException PoolReadFailed() => new("CLORO_KEY_POOL_UNAVAILABLE", "Cloro key pool is temporarily unavailable.", true);
    private sealed record CloroTaskDefinition(string TaskType, string Field, object Include, bool SendsInterfaceLanguage = false);

    // Internal signal for a definite 401/403/429 rejection; never leaves the adapter.
    private sealed class KeyRejectedException(CloroKeyRejection rejection, string errorCode) : Exception("Cloro rejected the request for this key.")
    {
        public CloroKeyRejection Rejection { get; } = rejection;
        public string ErrorCode { get; } = errorCode;
    }
}

/// <summary>Cloro 外部调用的安全错误，不包含 Provider 响应正文。</summary>
public sealed class CloroProviderException(string code, string message, bool retryable, bool submissionUnknown = false,
    DateTimeOffset? nextAttemptAt = null) : InvalidOperationException(message)
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; } = code;
    /// <summary>是否明确允许重试当前调用。</summary>
    public bool Retryable { get; } = retryable;
    /// <summary>提交是否可能已经产生外部任务，禁止自动重提。</summary>
    public bool SubmissionUnknown { get; } = submissionUnknown;
    /// <summary>建议的最早重试时间（key 池耗尽时为最早恢复时间）。</summary>
    public DateTimeOffset? NextAttemptAt { get; } = nextAttemptAt;
}

internal static class CloroModelExtensions
{
    public static string ToProviderModelName(this CloroModel model) => model switch
    {
        CloroModel.ChatGpt => "chatgpt",
        CloroModel.Perplexity => "perplexity",
        CloroModel.Copilot => "copilot",
        CloroModel.Gemini => "gemini",
        CloroModel.GoogleAiMode => "google-ai-mode",
        CloroModel.GoogleAiOverview => "google-ai-overview",
        _ => throw new ArgumentException("Cloro engine is not supported.", nameof(model)),
    };
}
