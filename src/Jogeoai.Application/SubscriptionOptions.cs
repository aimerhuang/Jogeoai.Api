namespace Jogeoai.Application;

/// <summary>套餐周期滚动与卡密兑换的运行参数（配置节 Subscription）。</summary>
public sealed class SubscriptionOptions
{
    /// <summary>Worker 轮询到期周期与到期订阅的间隔，默认 60 秒。</summary>
    public TimeSpan RolloverInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>单轮最多处理的订阅数，默认 100。</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>每租户、每客户端 IP 每小时允许的卡密兑换失败次数，默认 10。</summary>
    public int RedeemFailuresPerHour { get; set; } = 10;
}
