using System.Collections;
using System.Reflection;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class DomainlessBrandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingDomainIsNullAndCreatesOnlyNameAndAliasTerms(string? value)
    {
        var normalize = typeof(ManagementStore).GetMethod("NormalizeOptionalDomain", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(normalize.Invoke(null, [value]));
        var terms = (IEnumerable)typeof(ManagementStore).GetMethod("BrandTerms", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["优联", value, new[] { "Ulinktek" }, Array.Empty<string>()])!;
        var rows = JsonSerializer.SerializeToElement(terms).EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.DoesNotContain(rows, t => t.GetProperty("Namespace").GetString() == "domain");
        Assert.Contains(rows, t => t.GetProperty("Key").GetString() == "ulinktek");
    }

    [Fact]
    public void DomainlessSnapshotRemainsCompleteAndNameMatchingWorksWhileOwnershipIsUnknown()
    {
        var id = Guid.NewGuid();
        var configuration = SubjectConfiguration.Parse(id, 1, JsonSerializer.Serialize(new
        {
            brandId = id, name = "优联", primaryDomain = (string?)null, brandStatus = "active",
            terms = new[] { new { subjectId = id, subjectType = "brand", termType = "name", key = "优联" },
                new { subjectId = id, subjectType = "brand", termType = "alias", key = "ulinktek" } },
        }));
        Assert.True(configuration.IsComplete);
        Assert.Equal(2, MentionMatcher.Match("优联与 Ulinktek 提供设备。", configuration).Count);
        var ownership = CitationDomainRules.Ownership("unrelated.test", configuration);
        Assert.Equal(CitationOwnership.Unknown, ownership.Ownership);
        Assert.Equal("OWNERSHIP_BASIS_UNAVAILABLE", ownership.Reason);
        var brand = new BrandView(id, "优联", null, [], [], "CN", "zh-CN", "UTC", "active", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Null(typeof(ContentStore).GetMethod("ComputeOwnership", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["https://unrelated.test/", brand]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NewBrandRequiresDescriptionBeforeOpeningDatabase(string? description)
    {
        var store = new ManagementStore(new MySqlDatabase("Server=invalid;Database=unused"));
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CreateBrandAsync(Guid.NewGuid(), new("品牌", Description: description)));
        Assert.Equal("VALIDATION_FAILED", error.Code);
    }

    [Fact]
    public void NullablePublicContractsAndPatchKeepVersusClearAreDistinct()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Null(JsonSerializer.Deserialize<BrandWrite>("{\"name\":\"品牌\",\"description\":\"业务介绍\"}", options)!.PrimaryDomain);
        Assert.Null(JsonSerializer.Deserialize<BrandPatch>("{}", options)!.PrimaryDomain);
        Assert.Null(JsonSerializer.Deserialize<BrandPatch>("{\"primaryDomain\":null}", options)!.PrimaryDomain);
        Assert.Equal("", JsonSerializer.Deserialize<BrandPatch>("{\"primaryDomain\":\"\"}", options)!.PrimaryDomain);
        Assert.False(new BrandPatch(PrimaryDomain: "", Description: "介绍").IsDisplayOnly);
        Assert.True(new BrandPatch(Description: "介绍").IsDisplayOnly);
    }

    [Fact]
    public void ActualOnboardingCompletionGateAcceptsDomainlessActiveBrandButRejectsInactiveOrNameless()
    {
        var guard = typeof(ManagementStore).GetMethod("EnsureBrandReadyForOnboarding", BindingFlags.Static | BindingFlags.NonPublic)!;
        guard.Invoke(null, [new[] { "品牌", "", "active" }]);
        guard.Invoke(null, [new[] { "品牌", "brand.test", "active" }]);
        foreach (var row in new[] { new[] { "品牌", "", "archived" }, new[] { " ", "brand.test", "active" } })
        {
            var error = Assert.Throws<TargetInvocationException>(() => guard.Invoke(null, [row]));
            Assert.Equal("BRAND_REQUIRED", Assert.IsType<CollectorStoreException>(error.InnerException).Code);
        }
    }

    [Fact]
    public void ResearchNormalizesNoWebsiteOnlyWithBusinessDescription()
    {
        var method = typeof(ResearchStore).GetMethod("NormalizeRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        var normalized = (StartBrandResearchRequest)method.Invoke(null, [new StartBrandResearchRequest(" ", BusinessDescription: "  工业检测设备  ")])!;
        Assert.Null(normalized.Website);
        Assert.Equal("工业检测设备", normalized.BusinessDescription);
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [new StartBrandResearchRequest()]));
        Assert.IsType<ArgumentException>(exception.InnerException);
        var context = ResearchContextRules.Create(normalized, null);
        Assert.Null(context.Website);
        Assert.Equal("user", Assert.Single(context.Evidence).Id);
    }

    [Fact]
    public void MigrationAddsNullableHostAndTenantScopedRetryIdentityWithoutRewritingData()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations/032_domainless_brands.sql"));
        var sql = File.ReadAllText(path);
        Assert.Contains("MODIFY COLUMN host VARCHAR(253) COLLATE utf8mb4_0900_bin NULL DEFAULT NULL", sql);
        Assert.Contains("is_nullable = 'NO'", sql);
        Assert.Contains("creation_request_id BINARY(16) NULL", sql);
        Assert.Contains("creation_request_hash CHAR(64)", sql);
        Assert.Contains("(tenant_id, creation_request_id)", sql);
        Assert.Contains("information_schema.statistics", sql);
        Assert.Contains("SIGNAL SQLSTATE", sql);
        Assert.DoesNotContain("UPDATE sites", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP INDEX", sql, StringComparison.OrdinalIgnoreCase);
    }
}
