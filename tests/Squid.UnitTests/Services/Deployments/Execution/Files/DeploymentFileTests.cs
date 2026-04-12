using Squid.Core.Services.DeploymentExecution.Script.Files;

namespace Squid.UnitTests.Services.Deployments.Execution.Files;

public class DeploymentFileTests
{
    private static readonly byte[] SampleContent = { 0x01, 0x02, 0x03 };

    // ========== Path validation — accept ==========

    [Theory]
    [InlineData("script.sh")]
    [InlineData("deploy.yaml")]
    [InlineData("content/values.yaml")]
    [InlineData("bin/helpers/set_var.sh")]
    [InlineData("a/b/c/d/e/file.txt")]
    [InlineData(".hidden")]
    [InlineData("content/.hidden")]
    public void EnsureValid_AcceptsValidRelativePaths(string relativePath)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        Should.NotThrow(() => file.EnsureValid());
    }

    // ========== Path validation — reject ==========

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void EnsureValid_RejectsEmptyOrWhitespace(string relativePath)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        var ex = Should.Throw<ArgumentException>(() => file.EnsureValid());
        ex.Message.ShouldContain("cannot be null or empty");
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:/Users/foo")]
    public void EnsureValid_RejectsRootedPaths(string relativePath)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        var ex = Should.Throw<ArgumentException>(() => file.EnsureValid());
        ex.Message.ShouldContain(relativePath);
    }

    [Theory]
    [InlineData("foo\\bar")]
    [InlineData("content\\values.yaml")]
    public void EnsureValid_RejectsBackslash(string relativePath)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        var ex = Should.Throw<ArgumentException>(() => file.EnsureValid());
        ex.Message.ShouldContain("forward slashes");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("content/../secrets")]
    [InlineData("a/b/../../c")]
    public void EnsureValid_RejectsPathTraversal(string relativePath)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        var ex = Should.Throw<ArgumentException>(() => file.EnsureValid());
        ex.Message.ShouldContain("'..'");
    }

    [Theory]
    [InlineData("foo//bar")]
    [InlineData("content//values.yaml")]
    public void EnsureValid_RejectsEmptySegments(string relativePath)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        var ex = Should.Throw<ArgumentException>(() => file.EnsureValid());
        ex.Message.ShouldContain("empty segments");
    }

    [Fact]
    public void EnsureValid_RejectsNullContent()
    {
        var file = new DeploymentFile("script.sh", null!, DeploymentFileKind.Asset);

        var ex = Should.Throw<ArgumentException>(() => file.EnsureValid());
        ex.Message.ShouldContain("Content");
        ex.Message.ShouldContain("script.sh");
    }

    [Fact]
    public void EnsureValid_AcceptsEmptyContent()
    {
        var file = DeploymentFile.Asset("empty.txt", Array.Empty<byte>());

        Should.NotThrow(() => file.EnsureValid());
    }

    // ========== Factory methods ==========

    [Fact]
    public void Script_DefaultsToExecutable()
    {
        var file = DeploymentFile.Script("deploy.sh", SampleContent);

        file.Kind.ShouldBe(DeploymentFileKind.Script);
        file.IsExecutable.ShouldBeTrue();
    }

    [Fact]
    public void Asset_IsNotExecutableByDefault()
    {
        var file = DeploymentFile.Asset("deploy.yaml", SampleContent);

        file.Kind.ShouldBe(DeploymentFileKind.Asset);
        file.IsExecutable.ShouldBeFalse();
    }

    [Fact]
    public void Package_MarkedAsPackageKind()
    {
        var file = DeploymentFile.Package("app.1.0.0.nupkg", SampleContent);

        file.Kind.ShouldBe(DeploymentFileKind.Package);
        file.IsExecutable.ShouldBeFalse();
    }

    [Fact]
    public void Bootstrap_DefaultsToExecutable()
    {
        var file = DeploymentFile.Bootstrap("bootstrap.sh", SampleContent);

        file.Kind.ShouldBe(DeploymentFileKind.Bootstrap);
        file.IsExecutable.ShouldBeTrue();
    }

    [Fact]
    public void RuntimeBundle_DefaultsToExecutable()
    {
        var file = DeploymentFile.RuntimeBundle("squid-runtime.sh", SampleContent);

        file.Kind.ShouldBe(DeploymentFileKind.RuntimeBundle);
        file.IsExecutable.ShouldBeTrue();
    }

    // ========== IsNested ==========

    [Theory]
    [InlineData("script.sh", false)]
    [InlineData("deploy.yaml", false)]
    [InlineData("content/values.yaml", true)]
    [InlineData("a/b/c.txt", true)]
    public void IsNested_ReflectsPathDepth(string relativePath, bool expectedNested)
    {
        var file = DeploymentFile.Asset(relativePath, SampleContent);

        file.IsNested.ShouldBe(expectedNested);
    }

    // ========== Record equality / with ==========

    [Fact]
    public void With_ProducesNewInstanceWithMutation()
    {
        var original = DeploymentFile.Asset("deploy.yaml", SampleContent);
        var mutated = original with { IsExecutable = true };

        mutated.RelativePath.ShouldBe("deploy.yaml");
        mutated.IsExecutable.ShouldBeTrue();
        original.IsExecutable.ShouldBeFalse();
    }
}
