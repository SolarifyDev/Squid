using Squid.Calamari.Host;

namespace Squid.Calamari.Tests.Calamari.Host;

public class CalamariCommandRegistryFactoryTests
{
    [Fact]
    public void CreateDefault_IncludesCoreCommandsOnly()
    {
        var registry = CalamariCommandRegistryFactory.CreateDefault();

        registry.TryGet("run-script", out _).ShouldBeTrue();
        registry.TryGet("apply-yaml", out _).ShouldBeTrue();
    }

    [Fact]
    public void CreateFromModules_AllowsAddingCommandsWithoutChangingProgram()
    {
        var registry = CalamariCommandRegistryFactory.CreateFromModules(
        [
            new CoreCommandModule(),
            new CustomModule()
        ]);

        registry.TryGet("custom-cmd", out var handler).ShouldBeTrue();
        handler.ShouldNotBeNull();
        handler!.Descriptor.Name.ShouldBe("custom-cmd");
    }

    private sealed class CustomModule : ICommandModule
    {
        public IEnumerable<ICommandHandler> CreateHandlers()
        {
            yield return new CustomHandler();
        }
    }

    private sealed class CustomHandler : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } =
            new("custom-cmd", "custom-cmd", "custom");

        public Task<int> ExecuteAsync(string[] args, CancellationToken ct) => Task.FromResult(0);
    }
}
