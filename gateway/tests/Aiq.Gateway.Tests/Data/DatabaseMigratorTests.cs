using Aiq.Gateway.Data;

namespace Aiq.Gateway.Tests.Data;

public sealed class DatabaseMigratorTests
{
    [Fact]
    public void ChecksumIsStableSha256LowercaseHex()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            DatabaseMigrator.ComputeChecksum(string.Empty));
        Assert.Equal(64, DatabaseMigrator.ComputeChecksum("SELECT 1;\n").Length);
        Assert.Matches("^[0-9a-f]{64}$", DatabaseMigrator.ComputeChecksum("SELECT 1;\n"));
    }

    [Fact]
    public void ChangingMigrationTextChangesChecksum()
    {
        Assert.NotEqual(
            DatabaseMigrator.ComputeChecksum("SELECT 1;\n"),
            DatabaseMigrator.ComputeChecksum("SELECT 2;\n"));
    }
}
