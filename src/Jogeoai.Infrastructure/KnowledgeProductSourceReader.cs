using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>按识别任务类型读取来源并装配冻结的识别输入。</summary>
public sealed class KnowledgeProductSourceReader(
    IKnowledgeProductStore products,
    IObjectStorage objectStorage,
    IDocumentTextExtractor extractor,
    IKnowledgeRetrievalService retrieval,
    IManagementStore? management = null) : IKnowledgeProductSourceReader
{
    private const int MaxSourceCharacters = 60_000;
    private const int MaxBrandEvidence = 12;

    /// <inheritdoc />
    public async Task<ProductParseInput> ReadAsync(ProductParseJobLease job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var product = await products.GetAsync(job.TenantId, job.SiteId, job.ProductId, cancellationToken)
            ?? throw new ProductParseException("PRODUCT_NOT_FOUND", "产品信息不存在。", false);
        var brandName = string.Empty;
        if (management is not null)
        {
            var brand = await management.GetBrandAsync(job.TenantId, job.SiteId, cancellationToken);
            brandName = brand?.Name ?? string.Empty;
        }
        var current = new KnowledgeProductWrite(product.Name, product.Introduction, product.Advantages, product.Scenarios,
            product.Cases, product.Specs, product.ProductUrl, product.Tags);

        if (job.Kind == ProductParseJobKind.Completion)
        {
            var search = await retrieval.SearchAsync(job.TenantId, job.SiteId,
                new KnowledgeSearchRequest(product.Name, KnowledgeScopeMode.Default, TopK: MaxBrandEvidence), cancellationToken);
            return new ProductParseInput(job.Kind, product.Name, brandName, current, null, [],
                search.Items.Take(MaxBrandEvidence).ToArray(), false);
        }

        if (string.IsNullOrWhiteSpace(job.SourceObjectKey) || string.IsNullOrWhiteSpace(job.SourceExtension))
            throw new ProductParseException("PRODUCT_PARSE_SOURCE_UNREADABLE", "来源资料没有可读取的版本。", false);
        if (!extractor.CanExtract(job.SourceExtension, null))
            throw new ProductParseException("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "来源文件格式不支持识别。", false);

        ExtractedDocument extracted;
        try
        {
            await using var stream = await objectStorage.OpenReadAsync(job.SourceObjectKey, cancellationToken)
                ?? throw new ProductParseException("PRODUCT_PARSE_SOURCE_UNREADABLE", "来源文件对象不存在。", true);
            extracted = await extractor.ExtractAsync(stream, job.SourceExtension, null, cancellationToken);
        }
        catch (KnowledgeExtractionException exception)
        {
            var code = exception.Code == "KNOWLEDGE_UNSUPPORTED_FILE_TYPE" ? "PRODUCT_PARSE_SOURCE_UNSUPPORTED" : "PRODUCT_PARSE_SOURCE_UNREADABLE";
            throw new ProductParseException(code, exception.Message, exception.Retryable, exception);
        }

        var text = BuildSourceText(extracted, out var textTruncated);
        if (text.Length >= BasicDocumentTextExtractor.MinimumPdfTextCharacters || (extracted.ContentKind != "image" && text.Length > 0))
            return new ProductParseInput(job.Kind, product.Name, brandName, current, text, [], [], textTruncated || extracted.Truncated);
        var images = (extracted.Images ?? []).Take(BasicDocumentTextExtractor.MaxImages)
            .Select(image => new ProductParseImage(image.Ordinal, image.MediaType, image.Bytes, image.Location)).ToArray();
        if (images.Length == 0)
            throw new ProductParseException("PRODUCT_PARSE_SOURCE_UNREADABLE", "来源无法读取：既无文本也无可用页图。", false);
        return new ProductParseInput(job.Kind, product.Name, brandName, current,
            text.Length > 0 ? text : null, images, [], extracted.Truncated || (extracted.Images?.Count ?? 0) > images.Length);
    }

    /// <summary>把段落连同定位标记拼成文本，超过上限按段落截断。</summary>
    internal static string BuildSourceText(ExtractedDocument extracted, out bool truncated)
    {
        truncated = false;
        var builder = new StringBuilder();
        foreach (var segment in extracted.Segments)
        {
            var line = string.IsNullOrWhiteSpace(segment.Location) ? segment.Text : $"[{segment.Location}] {segment.Text}";
            if (builder.Length + line.Length + 1 > MaxSourceCharacters) { truncated = true; break; }
            builder.Append(line).Append('\n');
        }
        return builder.ToString().Trim();
    }
}
