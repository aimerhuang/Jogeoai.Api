namespace Jogeoai.Domain;

/// <summary>创建品牌前的资料识别请求；不保存品牌或消耗品牌名额。</summary>
/// <param name="Description">品牌介绍或资料正文，最多 4000 字；名称、介绍与网址至少提供一项。</param>
/// <param name="Url">可选公开 HTTP(S) 资料网址，最多 2000 字；资料来源不一定是品牌官网。</param>
/// <param name="Name">可选品牌名称，最多 200 字；与网址一起作为 Luna 联网搜索的检索依据。</param>
/// <param name="Aliases">可选品牌别名，最多 20 个、每个最多 200 字；用于辅助联网搜索。</param>
public sealed record BrandRecognitionRequest(string? Description = null, string? Url = null,
    string? Name = null, IReadOnlyList<string>? Aliases = null);

/// <summary>Luna 联网搜索引用的公开网页，供用户核对识别结果。</summary>
/// <param name="Title">网页标题；缺失时为网页域名。</param>
/// <param name="Url">公开 HTTP(S) 网址，已去除 utm 跟踪参数。</param>
public sealed record BrandRecognitionSource(string Title, string Url);

/// <summary>Luna 提取的可编辑品牌建议；用户核对后再提交创建。</summary>
/// <param name="Name">有资料依据的品牌名称；未识别时为 null。</param>
/// <param name="Description">有资料依据的品牌介绍；未识别时为 null。</param>
/// <param name="PrimaryDomain">资料明确声明的官网域名；没有依据时为 null，不使用资料网页域名兜底。</param>
/// <param name="Aliases">资料明确出现的别名建议。</param>
/// <param name="Warnings">资料缺失、截断或需人工确认的安全提示。</param>
/// <param name="Provider">实际识别服务标识。</param>
/// <param name="Model">实际识别模型。</param>
/// <param name="Sources">联网搜索引用的网页，最多 10 条；未搜索或搜索无引用时为空。</param>
public sealed record BrandRecognitionView(string? Name, string? Description, string? PrimaryDomain,
    IReadOnlyList<string> Aliases, IReadOnlyList<string> Warnings, string Provider, string Model,
    IReadOnlyList<BrandRecognitionSource> Sources);
