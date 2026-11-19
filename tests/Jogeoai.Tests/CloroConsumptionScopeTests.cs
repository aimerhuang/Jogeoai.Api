using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class CloroConsumptionScopeTests
{
    [Fact]
    public void ScopedConsumerRequiresTenantBeforeOpeningDatabase()
        => Assert.Throws<ArgumentException>(() => new CloroVisibilityStore(new MySqlDatabase(""), Guid.Empty, Guid.NewGuid()));

    [Fact]
    public void ScopedConsumerRequiresRunBeforeOpeningDatabase()
        => Assert.Throws<ArgumentException>(() => new CloroVisibilityStore(new MySqlDatabase(""), Guid.NewGuid(), Guid.Empty));

    [Fact]
    public void ExplicitNonemptyScopeAndExistingUnscopedConstructionRemainAvailable()
    {
        var database = new MySqlDatabase("");
        Assert.NotNull(new CloroVisibilityStore(database, Guid.NewGuid(), Guid.NewGuid()));
        Assert.NotNull(new CloroVisibilityStore(database));
    }
}
