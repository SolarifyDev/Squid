using Squid.Tentacle.Flavors.KubernetesAgent;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Flavors.KubernetesAgent;

[Trait("Category", TentacleTestCategories.Flavor)]
public class InitializationFlagStartupHookTests : TimedTestBase
{
    [Fact]
    public async Task RunAsync_Creates_Flag_File_When_Directory_Exists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"squid-tentacle-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var flagPath = Path.Combine(root, "initialized");

        try
        {
            var hook = new InitializationFlagStartupHook(flagPath);

            await hook.RunAsync(TestCancellationToken);

            File.Exists(flagPath).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Does_Not_Throw_When_Directory_Does_Not_Exist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"squid-tentacle-hook-{Guid.NewGuid():N}");
        var flagPath = Path.Combine(root, "initialized");
        var hook = new InitializationFlagStartupHook(flagPath);

        await hook.RunAsync(TestCancellationToken);

        File.Exists(flagPath).ShouldBeFalse();
    }
}
