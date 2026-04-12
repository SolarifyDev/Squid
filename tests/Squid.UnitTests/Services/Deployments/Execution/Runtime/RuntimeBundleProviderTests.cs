using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Runtime.Exceptions;

namespace Squid.UnitTests.Services.Deployments.Execution.Runtime;

public class RuntimeBundleProviderTests
{
    private sealed class StubBundle : IRuntimeBundle
    {
        public StubBundle(RuntimeBundleKind kind, string tag)
        {
            Kind = kind;
            Tag = tag;
        }

        public RuntimeBundleKind Kind { get; }
        public string Tag { get; }

        public string Wrap(RuntimeBundleWrapContext context) => $"stub:{Tag}:{context.UserScriptBody}";
    }

    private static RuntimeBundleWrapContext MakeContext() => new()
    {
        UserScriptBody = "noop",
        WorkDirectory = "/work",
        BaseDirectory = "/base",
        ServerTaskId = 1,
        Variables = Array.Empty<Squid.Message.Models.Deployments.Variable.VariableDto>()
    };

    [Fact]
    public void Constructor_NullBundles_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new RuntimeBundleProvider(null));
    }

    [Fact]
    public void GetBundle_Bash_ReturnsBashImplementation()
    {
        var provider = new RuntimeBundleProvider(new IRuntimeBundle[]
        {
            new StubBundle(RuntimeBundleKind.Bash, "bash"),
            new StubBundle(RuntimeBundleKind.PowerShell, "pwsh")
        });

        var bundle = provider.GetBundle(RuntimeBundleKind.Bash);

        bundle.ShouldBeOfType<StubBundle>().Tag.ShouldBe("bash");
    }

    [Fact]
    public void GetBundle_PowerShell_ReturnsPowerShellImplementation()
    {
        var provider = new RuntimeBundleProvider(new IRuntimeBundle[]
        {
            new StubBundle(RuntimeBundleKind.Bash, "bash"),
            new StubBundle(RuntimeBundleKind.PowerShell, "pwsh")
        });

        var bundle = provider.GetBundle(RuntimeBundleKind.PowerShell);

        bundle.ShouldBeOfType<StubBundle>().Tag.ShouldBe("pwsh");
    }

    [Fact]
    public void GetBundle_UnknownKind_ThrowsRuntimeBundleNotFoundException()
    {
        var provider = new RuntimeBundleProvider(new IRuntimeBundle[]
        {
            new StubBundle(RuntimeBundleKind.Bash, "bash")
        });

        var ex = Should.Throw<RuntimeBundleNotFoundException>(() =>
            provider.GetBundle(RuntimeBundleKind.Python));

        ex.Kind.ShouldBe(RuntimeBundleKind.Python);
    }

    [Fact]
    public void GetBundle_DuplicateKind_LastRegistrationWins()
    {
        var provider = new RuntimeBundleProvider(new IRuntimeBundle[]
        {
            new StubBundle(RuntimeBundleKind.Bash, "first"),
            new StubBundle(RuntimeBundleKind.Bash, "second")
        });

        var bundle = provider.GetBundle(RuntimeBundleKind.Bash);

        bundle.ShouldBeOfType<StubBundle>().Tag.ShouldBe("second");
    }

    [Fact]
    public void GetBundle_EmptyRegistration_ThrowsForAllKinds()
    {
        var provider = new RuntimeBundleProvider(Array.Empty<IRuntimeBundle>());

        Should.Throw<RuntimeBundleNotFoundException>(() => provider.GetBundle(RuntimeBundleKind.Bash));
    }

    [Fact]
    public void GetBundle_ReturnedBundleIsFunctional()
    {
        var provider = new RuntimeBundleProvider(new IRuntimeBundle[]
        {
            new StubBundle(RuntimeBundleKind.Bash, "bash")
        });

        var bundle = provider.GetBundle(RuntimeBundleKind.Bash);
        var wrapped = bundle.Wrap(MakeContext());

        wrapped.ShouldBe("stub:bash:noop");
    }
}
