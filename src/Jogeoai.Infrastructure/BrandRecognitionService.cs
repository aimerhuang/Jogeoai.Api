using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>联网搜索并读取品牌资料后执行一次有界识别；没有任何品牌、任务或积分写入。</summary>
public sealed class BrandRecognitionService(IResearchStore settings, IArticleSourceReader reader,
    IBrandSearchProvider search, IBrandRecognitionProvider provider) : IBrandRecognitionService
{
    internal const int MaxSourceCharacters = 60_000;

    /// <inheritdoc />
    public async Task<BrandRecognitionView> RecognizeAsync(BrandRecognitionRequest request,
        CancellationToken cancellationToken = default)
    {
        var description = request.Description?.Trim();
        var url = request.Url?.Trim();
        var name = request.Name?.Trim();
        var aliases = (request.Aliases ?? []).Select(alias => alias?.Trim()).OfType<string>()
            .Where(alias => alias.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (description?.Length > 4000 || url?.Length > 2000 || name?.Length > 200
            || request.Aliases?.Count > 20 || aliases.Any(alias => alias.Length > 200)
            || string.IsNullOrEmpty(description) && string.IsNullOrEmpty(url) && string.IsNullOrEmpty(name))
            throw new CollectorStoreException("INVALID_BRAND_RECOGNITION_REQUEST", "请填写品牌名称（最多 200 字）、品牌介绍（最多 4000 字）或公开网页网址（最多 2000 字）。", 400);
        if (!string.IsNullOrEmpty(url))
        {
            try { url = ArticleSourceReader.NormalizeFetchUrl(url); }
            catch (CollectorStoreException)
            { throw new CollectorStoreException("INVALID_BRAND_RECOGNITION_REQUEST", "资料网址必须是有效的公开 HTTP(S) 地址。", 400); }
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var configuration = await settings.GetProviderConfigurationAsync(LunaBrandRecognitionProvider.ProviderName, budget.Token);
            if (configuration is null)
                throw new CollectorStoreException("BRAND_RECOGNITION_NOT_CONFIGURED", "Luna 品牌识别尚未配置，可手动填写品牌资料。", 503);
            var searching = string.IsNullOrEmpty(name) && string.IsNullOrEmpty(url)
                ? Task.FromResult<BrandSearchResult?>(null)
                : SearchAsync(new(NullIfEmpty(name), aliases, NullIfEmpty(url)), configuration, budget.Token);
            var reading = string.IsNullOrEmpty(url) ? Task.FromResult<string?>(null) : ReadAsync(url, budget.Token);
            await Task.WhenAll(searching, reading);
            var found = await searching;
            var page = await reading;
            var warnings = new List<string>();
            if (found?.Warning is { } searchWarning) warnings.Add(searchWarning);
            // URL 本身只是抓取位置，不加入可作为官网依据的资料正文。
            if (!string.IsNullOrEmpty(url) && page is null) warnings.Add("资料网页未能读取，已使用其他资料识别。");
            if (string.IsNullOrEmpty(description) && found?.Summary is null && page is null)
                throw new CollectorStoreException("BRAND_SOURCE_UNAVAILABLE", "未能搜索或读取到品牌资料，请粘贴介绍文字或手动填写。", 422);
            var source = Assemble(name, aliases, description, found?.Summary, page);
            var truncated = source.Length > MaxSourceCharacters;
            var view = await provider.RecognizeAsync(truncated ? source[..MaxSourceCharacters] : source,
                truncated, configuration, budget.Token);
            return view with
            {
                Warnings = warnings.Concat(view.Warnings).Distinct(StringComparer.Ordinal).ToArray(),
                Sources = found?.Sources ?? [],
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CollectorStoreException("BRAND_RECOGNITION_TIMEOUT", "品牌识别超时，请重试或手动填写。", 504); }
    }

    private async Task<BrandSearchResult?> SearchAsync(BrandSearchQuery query, ResearchProviderConfiguration configuration,
        CancellationToken cancellationToken) => await search.SearchAsync(query, configuration, cancellationToken);

    private async Task<string?> ReadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var page = await reader.ReadAsync(url, cancellationToken);
            return page.ReadStatus == "ready" && !string.IsNullOrWhiteSpace(page.BodyMarkdown) ? page.BodyMarkdown : null;
        }
        catch (Exception error) when (error is CollectorStoreException or HttpRequestException or IOException)
        { return null; }
    }

    /// <summary>只有用户介绍时原样识别；有搜索或网页资料时按来源分段，段标题不含「官网」，避免成为官网摘录。</summary>
    private static string Assemble(string? name, IReadOnlyList<string> aliases, string? description, string? summary, string? page)
    {
        var user = string.Join('\n', new[]
        {
            string.IsNullOrEmpty(name) ? null : $"品牌名称：{name}",
            aliases.Count == 0 ? null : $"品牌别名：{string.Join('、', aliases)}",
            NullIfEmpty(description),
        }.OfType<string>());
        if (summary is null && page is null) return user;
        return string.Join("\n\n", new[]
        {
            user.Length == 0 ? null : "【用户填写】\n" + user,
            summary is null ? null : "【联网搜索摘要】\n" + summary,
            page is null ? null : "【网页正文】\n" + page,
        }.OfType<string>());
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
