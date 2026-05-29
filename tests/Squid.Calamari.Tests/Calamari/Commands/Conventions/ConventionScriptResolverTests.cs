using Shouldly;
using Squid.Calamari.Commands.Conventions;
using Squid.Calamari.Scripting;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Conventions;

/// <summary>
/// PR-7 — resolution tests for <see cref="ConventionScriptResolver"/>.
/// Pins the multi-shell probe behaviour: .sh / .ps1 detection, the
/// both-shipped tie-break (main script syntax wins), and the back-compat
/// .sh-only path.
/// </summary>
public sealed class ConventionScriptResolverTests : IDisposable
{
    private readonly string _workDir;

    public ConventionScriptResolverTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"conv-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void Resolve_NeitherExists_ReturnsNull()
    {
        ConventionScriptResolver.Resolve(_workDir, "PreDeploy", ScriptSyntax.Bash)
            .ShouldBeNull();
    }

    [Fact]
    public void Resolve_OnlyShExists_ReturnsBash_BackCompat()
    {
        // Every pre-PR-7 package ships only .sh. MUST resolve to bash
        // regardless of the main script's preferred syntax.
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "echo hi");

        var resolved = ConventionScriptResolver.Resolve(_workDir, "PreDeploy", ScriptSyntax.PowerShell);

        resolved.ShouldNotBeNull();
        resolved!.Value.Syntax.ShouldBe(ScriptSyntax.Bash,
            customMessage: ".sh-only package MUST resolve to bash even when the main script is PowerShell — " +
                           "the file that exists wins; preference only breaks ties.");
        resolved.Value.Path.ShouldEndWith("PreDeploy.sh");
    }

    [Fact]
    public void Resolve_OnlyPs1Exists_ReturnsPowerShell()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.ps1"), "Write-Output hi");

        var resolved = ConventionScriptResolver.Resolve(_workDir, "PreDeploy", ScriptSyntax.Bash);

        resolved.ShouldNotBeNull();
        resolved!.Value.Syntax.ShouldBe(ScriptSyntax.PowerShell);
        resolved.Value.Path.ShouldEndWith("PreDeploy.ps1");
    }

    [Fact]
    public void Resolve_BothExist_PrefersPowerShell_WhenMainIsPowerShell()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "echo hi");
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.ps1"), "Write-Output hi");

        var resolved = ConventionScriptResolver.Resolve(_workDir, "PreDeploy", ScriptSyntax.PowerShell);

        resolved!.Value.Syntax.ShouldBe(ScriptSyntax.PowerShell,
            customMessage: "When both convention variants ship, the one matching the MAIN script's syntax wins. " +
                           "PowerShell main → PowerShell convention.");
        resolved.Value.Path.ShouldEndWith("PreDeploy.ps1");
    }

    [Fact]
    public void Resolve_BothExist_PrefersBash_WhenMainIsBash()
    {
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.sh"), "echo hi");
        File.WriteAllText(Path.Combine(_workDir, "PreDeploy.ps1"), "Write-Output hi");

        var resolved = ConventionScriptResolver.Resolve(_workDir, "PreDeploy", ScriptSyntax.Bash);

        resolved!.Value.Syntax.ShouldBe(ScriptSyntax.Bash,
            customMessage: "Bash main → bash convention when both ship.");
        resolved.Value.Path.ShouldEndWith("PreDeploy.sh");
    }

    [Fact]
    public void Resolve_DifferentConventionName_DoesNotCrossMatch()
    {
        // PostDeploy.sh exists; asking for PreDeploy MUST return null.
        File.WriteAllText(Path.Combine(_workDir, "PostDeploy.sh"), "echo hi");

        ConventionScriptResolver.Resolve(_workDir, "PreDeploy", ScriptSyntax.Bash)
            .ShouldBeNull();
    }
}
