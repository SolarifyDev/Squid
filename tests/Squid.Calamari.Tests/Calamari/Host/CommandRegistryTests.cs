using Squid.Calamari.Host;

namespace Squid.Calamari.Tests.Calamari.Host;

public class CommandRegistryTests
{
    [Fact]
    public void TryGet_ResolvesName_AndAlias_CaseInsensitive()
    {
        var handler = new StubCommandHandler(
            new CommandDescriptor("run-script", "run-script --script=<path>", "desc", ["rs"]));
        var registry = new CommandRegistry([handler]);

        registry.TryGet("RUN-SCRIPT", out var byName).ShouldBeTrue();
        registry.TryGet("Rs", out var byAlias).ShouldBeTrue();

        byName.ShouldBeSameAs(handler);
        byAlias.ShouldBeSameAs(handler);
    }

    [Fact]
    public void Constructor_Throws_OnDuplicateNames()
    {
        Should.Throw<InvalidOperationException>(() =>
            new CommandRegistry(
            [
                new StubCommandHandler(new CommandDescriptor("run-script", "a", "a")),
                new StubCommandHandler(new CommandDescriptor("run-script", "b", "b"))
            ]))
            .Message.ShouldContain("Duplicate command registration");
    }

    [Fact]
    public void Constructor_Throws_OnDuplicateAliasAndName()
    {
        Should.Throw<InvalidOperationException>(() =>
            new CommandRegistry(
            [
                new StubCommandHandler(new CommandDescriptor("run-script", "a", "a", ["rs"])),
                new StubCommandHandler(new CommandDescriptor("rs", "b", "b"))
            ]))
            .Message.ShouldContain("Duplicate command registration");
    }

    [Fact]
    public void UsagePrinter_PrintsRegisteredCommandUsages()
    {
        var registry = new CommandRegistry(
        [
            new StubCommandHandler(new CommandDescriptor("one", "one --a", "first")),
            new StubCommandHandler(new CommandDescriptor("two", "two --b", "second"))
        ]);

        using var writer = new StringWriter();
        UsagePrinter.Print(registry, writer);

        var text = writer.ToString();
        text.ShouldContain("squid-calamari <subcommand> [options]");
        text.ShouldContain("Subcommands:");
        text.ShouldContain("  one --a");
        text.ShouldContain("  two --b");
    }

    private sealed class StubCommandHandler(CommandDescriptor descriptor) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;

        public Task<int> ExecuteAsync(string[] args, CancellationToken ct) => Task.FromResult(0);
    }
}
