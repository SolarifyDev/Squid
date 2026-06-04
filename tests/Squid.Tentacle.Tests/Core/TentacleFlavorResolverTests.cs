using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleFlavorResolverTests
{
    [Fact]
    public void Resolve_EmptyFlavorId_Throws_With_Configuration_Guidance()
    {
        var resolver = new TentacleFlavorResolver(new[]
        {
            new TestFlavor("KubernetesAgent"),
            new TestFlavor("Linux")
        });

        var ex = Should.Throw<InvalidOperationException>(() => resolver.Resolve(""));

        ex.Message.ShouldContain("Tentacle flavor not configured");
        ex.Message.ShouldContain("Tentacle:Flavor");
        ex.Message.ShouldContain("KubernetesAgent");
        ex.Message.ShouldContain("Linux");
    }

    [Fact]
    public void Resolve_UnknownFlavor_Throws_With_Available_List()
    {
        var resolver = new TentacleFlavorResolver(new[]
        {
            new TestFlavor("KubernetesAgent"),
            new TestFlavor("Linux")
        });

        var ex = Should.Throw<InvalidOperationException>(() => resolver.Resolve("Windows"));

        ex.Message.ShouldContain("Unknown Tentacle flavor 'Windows'");
        ex.Message.ShouldContain("KubernetesAgent");
        ex.Message.ShouldContain("Linux");
    }

    [Fact]
    public void Resolve_LegacyAliasId_ReturnsSameFlavorAsPrimaryId()
    {
        // A renamed flavor (primary id "Tentacle") must keep resolving under its legacy alias
        // ("LinuxTentacle") so already-deployed agents / old install snippets don't break.
        var tentacle = new TestFlavor("Tentacle", "LinuxTentacle");
        var resolver = new TentacleFlavorResolver(new ITentacleFlavor[] { tentacle, new TestFlavor("KubernetesAgent") });

        resolver.Resolve("Tentacle").ShouldBeSameAs(tentacle);
        resolver.Resolve("LinuxTentacle").ShouldBeSameAs(tentacle);
        resolver.Resolve("linuxtentacle").ShouldBeSameAs(tentacle);  // case-insensitive
    }

    [Fact]
    public void Resolve_RealTentacleFlavor_ResolvesUnderNewIdAndLegacyAlias()
    {
        // Pins the production flavor's rename: "Tentacle" is canonical, "LinuxTentacle" the alias.
        var resolver = new TentacleFlavorResolver(new ITentacleFlavor[] { new Squid.Tentacle.Flavors.Tentacle.TentacleFlavor() });

        resolver.Resolve("Tentacle").ShouldBeOfType<Squid.Tentacle.Flavors.Tentacle.TentacleFlavor>();
        resolver.Resolve("LinuxTentacle").ShouldBeOfType<Squid.Tentacle.Flavors.Tentacle.TentacleFlavor>();
    }

    private sealed class TestFlavor : ITentacleFlavor
    {
        public TestFlavor(string id, params string[] aliases)
        {
            Id = id;
            Aliases = aliases;
        }

        public string Id { get; }

        public IReadOnlyCollection<string> Aliases { get; }

        public TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context)
        {
            return new TentacleFlavorRuntime
            {
                Registrar = new FakeRegistrar(),
                ScriptBackend = new FakeScriptBackend()
            };
        }
    }
}
