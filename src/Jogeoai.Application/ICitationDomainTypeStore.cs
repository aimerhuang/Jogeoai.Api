using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>全局引用来源域名类型字典（表 <c>citation_domain_types</c>，迁移 034）。</summary>
public interface ICitationDomainTypeStore
{
    /// <summary>读取整个字典并建立索引；实现可在短时间内缓存。</summary>
    /// <param name="cancellationToken">取消请求令牌。</param>
    Task<CitationDomainTypeIndex> LoadAsync(CancellationToken cancellationToken = default);
}
