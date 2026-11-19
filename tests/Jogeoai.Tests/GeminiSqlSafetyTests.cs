namespace Jogeoai.Tests;

public sealed class GeminiSqlSafetyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Server=127.0.0.1;Port=3306;Database=jogeoai")]
    [InlineData("Server=127.0.0.1;Port=33318;Database=jogeoai")]
    [InlineData("Server=remote.example;Port=33318;Database=jogeoai_gemini_test_20261008")]
    public void PersistenceFixturesRefuseUnspecifiedSharedOrRemoteDatabases(string? connection)
        => Assert.Throws<InvalidOperationException>(() => GeminiAnswerPersistenceSqlTests.RequireIsolatedConnection(connection));

    [Fact]
    public void DedicatedIdentityPassesWithoutOpeningAnyConnection()
    {
        const string connection = "Server=127.0.0.1;Port=33318;Database=jogeoai_gemini_test_20261008";
        Assert.Equal(connection, GeminiAnswerPersistenceSqlTests.RequireIsolatedConnection(connection));
    }
}
