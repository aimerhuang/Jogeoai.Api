using System.Diagnostics;
using Jogeoai.Domain;

namespace Jogeoai.Api.Mcp;

/// <summary>
/// MCP 工具调用审计：每次调用写一条结构化日志（TokenId、TenantId、Tool、SiteId、Outcome、ElapsedMs）。
/// 不记录令牌、参数中的自由文本或回答正文。业务异常在这里统一转换为对客户端安全的 <see cref="McpToolException"/>。
/// </summary>
public static class McpToolAudit
{
    /// <summary>审计日志的消息模板。</summary>
    public const string MessageTemplate = "MCP tool call {TokenId} {TenantId} {Tool} {SiteId} {Outcome} {ElapsedMs}";

    /// <summary>执行工具并记录审计。</summary>
    /// <param name="logger">审计日志。</param>
    /// <param name="caller">当前调用方。</param>
    /// <param name="tool">工具名。</param>
    /// <param name="siteId">请求的站点；list_sites 为空。</param>
    /// <param name="action">工具主体。</param>
    public static async Task<T> RunAsync<T>(ILogger logger, McpCallerContext caller, string tool, Guid? siteId, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcome = "ok";
        try
        {
            return await action();
        }
        catch (McpToolException exception)
        {
            outcome = exception.Outcome;
            throw;
        }
        catch (CollectorStoreException exception)
        {
            var translated = Translate(exception);
            outcome = translated.Outcome;
            throw translated;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception)
        {
            outcome = "error";
            throw;
        }
        finally
        {
            logger.LogInformation(MessageTemplate, caller.TokenId, caller.TenantId, tool, siteId, outcome,
                stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>把 Store 的稳定业务错误转换为工具错误；404 / 401 / 403 统一为「不存在或无权访问」。</summary>
    /// <param name="exception">Store 抛出的业务异常。</param>
    public static McpToolException Translate(CollectorStoreException exception) => exception.StatusCode switch
    {
        401 or 403 or 404 => new McpToolException("denied", McpScope.SiteDenied),
        400 => new McpToolException("invalid_argument", exception.Message),
        409 => new McpToolException("conflict", exception.Message),
        503 => new McpToolException("unavailable", exception.Message),
        _ => new McpToolException("error", "查询失败，请稍后重试。"),
    };
}
