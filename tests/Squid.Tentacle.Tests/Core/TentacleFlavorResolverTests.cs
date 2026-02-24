using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleFlavorResolverTests
{
    [Fact]
    public void Resolve_EmptyFlavorId_FallsBack_To_KubernetesAgent()
    {
        var kubernetes = new TestFlavor("KubernetesAgent");
        var resolver = new TentacleFlavorResolver(new[] { kubernetes, new TestFlavor("Linux") });

        var resolved = resolver.Resolve("");

        resolved.ShouldBeSameAs(kubernetes);
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

    private sealed class TestFlavor : ITentacleFlavor
    {
        public TestFlavor(string id) => Id = id;

        public string Id { get; }

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
