namespace Jogeoai.Worker;

/// <summary>同步 API 回答 Provider（豆包、DeepSeek）任务 Worker 的共用运行参数；各 Provider 绑定自己的配置节。</summary>
public abstract class ApiAnswerWorkerOptions
{
    /// <summary>是否启动任务循环；为 false 时不注册 Worker，已创建的任务保持 pending。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>每次领取的任务数（1–10）。</summary>
    public int BatchSize { get; set; } = 2;
    /// <summary>同时进行的外部调用上限。</summary>
    public int MaxConcurrency { get; set; } = 2;
    /// <summary>租约秒数（10–900）；应不小于设置中的 timeout_seconds。</summary>
    public int LeaseSeconds { get; set; } = 300;
    /// <summary>没有任务时的空转等待。</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>*_REQUEST_FAILED / *_TIMEOUT 及未预期异常的总尝试次数上限（含首次）。</summary>
    public int MaxRetryAttempts { get; set; } = 3;
    /// <summary>*_RATE_LIMITED 的总尝试次数上限（含首次）。</summary>
    public int RateLimitMaxAttempts { get; set; } = 5;
    /// <summary>限流退避序列；超出序列长度沿用最后一项。</summary>
    public TimeSpan[] RateLimitBackoff { get; set; } = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];
    /// <summary>配额、欠费或余额不足（DeferMinutes）延后的最长总等待；超过后终态失败并释放积分预留。</summary>
    public TimeSpan MaxDeferWait { get; set; } = TimeSpan.FromHours(2);
}
