using TinyCosmos.Manager;

namespace TinyCosmos.Integration.Tests;

public sealed class ManagerPathTests
{
    [Fact]
    public void DefaultStatePathUsesHiddenTinyCosmosDirectory()
    {
        var path = ManagerPaths.DefaultStatePath();

        Assert.EndsWith(
            Path.Combine(ManagerPaths.StateDirectoryName, ManagerPaths.StateDatabaseFileName),
            path,
            StringComparison.Ordinal);
    }
}
