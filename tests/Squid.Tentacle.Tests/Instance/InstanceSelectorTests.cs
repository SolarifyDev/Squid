using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Tests.Instance;

public class InstanceSelectorTests
{
    [Fact]
    public void ExtractInstanceArg_NoInstanceFlag_ReturnsNullAndOriginalArgs()
    {
        var (name, remaining) = InstanceSelector.ExtractInstanceArg(["--server", "https://x", "--api-key", "KEY"]);

        name.ShouldBeNull();
        remaining.ShouldBe(["--server", "https://x", "--api-key", "KEY"]);
    }

    [Fact]
    public void ExtractInstanceArg_SpaceSeparatedForm_ExtractsAndStrips()
    {
        var (name, remaining) = InstanceSelector.ExtractInstanceArg(
            ["--instance", "production", "--server", "https://x"]);

        name.ShouldBe("production");
        remaining.ShouldBe(["--server", "https://x"]);
    }

    [Fact]
    public void ExtractInstanceArg_EqualsForm_ExtractsAndStrips()
    {
        var (name, remaining) = InstanceSelector.ExtractInstanceArg(
            ["--instance=production", "--server", "https://x"]);

        name.ShouldBe("production");
        remaining.ShouldBe(["--server", "https://x"]);
    }

    [Fact]
    public void ExtractInstanceArg_InMiddleOfArgs_StillExtracted()
    {
        var (name, remaining) = InstanceSelector.ExtractInstanceArg(
            ["--server", "https://x", "--instance", "staging", "--api-key", "KEY"]);

        name.ShouldBe("staging");
        remaining.ShouldBe(["--server", "https://x", "--api-key", "KEY"]);
    }

    [Theory]
    [InlineData("--INSTANCE")]
    [InlineData("--Instance")]
    [InlineData("--instance")]
    public void ExtractInstanceArg_CaseInsensitive(string flagCase)
    {
        var (name, _) = InstanceSelector.ExtractInstanceArg([flagCase, "X"]);

        name.ShouldBe("X");
    }

    [Fact]
    public void Resolve_NullName_ReturnsDefault_EvenWithoutRegistry()
    {
        // Regression guard: commands invoked as the very first install step (register)
        // must be able to resolve "Default" without pre-registering anything.
        var record = InstanceSelector.Resolve(null);

        record.Name.ShouldBe(InstanceRecord.DefaultName);
        record.ConfigPath.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_EmptyName_TreatedAsDefault()
    {
        InstanceSelector.Resolve("").Name.ShouldBe(InstanceRecord.DefaultName);
        InstanceSelector.Resolve("   ").Name.ShouldBe(InstanceRecord.DefaultName);
    }
}
