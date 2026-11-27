using Jogeoai.Application;
using Microsoft.Extensions.Configuration;

namespace Jogeoai.Tests;

/// <summary>
/// Collector:AppTenantIds 的解析：数组各项、以及一项里用逗号 / 分号 / 空白分隔的多个 GUID（部署变量 COLLECTOR_APP_TENANT_IDS）。
/// 绑定方式与 API、Worker 的 Program.cs 相同。
/// </summary>
public sealed class AppChannelOptionsTests
{
    private static readonly Guid A = Guid.Parse("38685331-621e-4dc6-bcc3-d5e35703ef90");
    private static readonly Guid B = Guid.Parse("d74e0cf2-7095-41ac-978b-870bcb4b80a5");
    private static readonly Guid C = Guid.Parse("16a7e7fd-b225-457d-9632-46f072ac0532");

    // Same binding as src/Jogeoai.Api/Program.cs and src/Jogeoai.Worker/Program.cs.
    private static AppChannelOptions Bind(Dictionary<string, string?> values)
        => new() { AppTenantIds = new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? [] };

    [Fact]
    public void TheComposeListVariableAddsCommaSeparatedTenantsToTheFirstOne()
    {
        // compose: Collector__AppTenantIds__0 = COLLECTOR_APP_TENANT_ID_0, Collector__AppTenantIds__1 = COLLECTOR_APP_TENANT_IDS.
        var options = Bind(new() { ["Collector:AppTenantIds:0"] = A.ToString(), ["Collector:AppTenantIds:1"] = $" {B}, {C};{A} " });
        Assert.Equal([A, B, C], options.OpenTenantIds());
        Assert.True(options.IsOpen(B) && options.IsOpen(C) && options.IsOpen(A));
        Assert.False(options.IsOpen(Guid.NewGuid()));
    }

    [Fact]
    public void EmptyVariablesAndJunkOpenNothing()
    {
        var empty = Bind(new() { ["Collector:AppTenantIds:0"] = "", ["Collector:AppTenantIds:1"] = "" });
        Assert.Empty(empty.OpenTenantIds());
        Assert.False(empty.IsOpen(A));
        var junk = Bind(new() { ["Collector:AppTenantIds:0"] = "not-a-guid", ["Collector:AppTenantIds:1"] = $",,{Guid.Empty} {B}\nnope" });
        Assert.Equal([B], junk.OpenTenantIds());
        Assert.False(junk.IsOpen(Guid.Empty));
        Assert.Empty(new AppChannelOptions { AppTenantIds = null! }.OpenTenantIds());
    }
}
