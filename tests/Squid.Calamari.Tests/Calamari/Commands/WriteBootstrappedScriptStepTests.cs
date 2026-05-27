using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Scripting;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands;

/// <summary>
/// PR-4 — syntax-aware bootstrap shape. .ps1 scripts get the PowerShell
/// preamble + .ps1 temp file; everything else gets the bash preamble +
/// .sh temp file. Pinned by extension dispatch via
/// <see cref="ScriptSyntaxDetector"/>.
/// </summary>
public sealed class WriteBootstrappedScriptStepTests : IDisposable
{
    private readonly string _workDir;

    public WriteBootstrappedScriptStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"wbs-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task BashScript_GeneratesBashPreambleAndShExtension()
    {
        var script = Path.Combine(_workDir, "deploy.sh");
        File.WriteAllText(script, "echo hi");

        var ctx = BuildContext(script);
        ctx.Variables!.Set("MyVar", "v");

        await new WriteBootstrappedScriptStep().ExecuteAsync(ctx, CancellationToken.None);

        ctx.BootstrappedScriptPath.ShouldNotBeNull();
        ctx.BootstrappedScriptPath!.ShouldEndWith(".sh");
        ctx.DetectedScriptSyntax.ShouldBe(ScriptSyntax.Bash);

        var bootstrapped = File.ReadAllText(ctx.BootstrappedScriptPath);
        bootstrapped.ShouldContain("export MyVar=",
            customMessage: "Bash bootstrap MUST use `export VAR=` syntax.");
        bootstrapped.ShouldContain("echo hi");
    }

    [Fact]
    public async Task PowerShellScript_GeneratesPsPreambleAndPs1Extension()
    {
        var script = Path.Combine(_workDir, "deploy.ps1");
        File.WriteAllText(script, "Write-Output 'hi'");

        var ctx = BuildContext(script);
        ctx.Variables!.Set("MyVar", "v");

        await new WriteBootstrappedScriptStep().ExecuteAsync(ctx, CancellationToken.None);

        ctx.BootstrappedScriptPath.ShouldNotBeNull();
        ctx.BootstrappedScriptPath!.ShouldEndWith(".ps1");
        ctx.DetectedScriptSyntax.ShouldBe(ScriptSyntax.PowerShell);

        var bootstrapped = File.ReadAllText(ctx.BootstrappedScriptPath);
        bootstrapped.ShouldContain("$env:MyVar = 'v'",
            customMessage: "PowerShell bootstrap MUST use $env:VAR = 'value' syntax. " +
                           "If you see `export VAR=` here, the syntax dispatch regressed.");
        bootstrapped.ShouldContain("Write-Output 'hi'");
        bootstrapped.ShouldContain("OutputEncoding",
            customMessage: "PS bootstrap MUST include the UTF-8 stdout pin so non-ASCII chars round-trip.");
    }

    [Fact]
    public async Task ScriptWithoutExtension_DefaultsToBash_BackCompat()
    {
        // Existing operators ship script paths like `script-{guid}` (no
        // extension). Default MUST stay bash so nothing breaks.
        var script = Path.Combine(_workDir, "no-ext-script");
        File.WriteAllText(script, "echo hi");

        var ctx = BuildContext(script);

        await new WriteBootstrappedScriptStep().ExecuteAsync(ctx, CancellationToken.None);

        ctx.DetectedScriptSyntax.ShouldBe(ScriptSyntax.Bash);
        ctx.BootstrappedScriptPath!.ShouldEndWith(".sh");
    }

    [Fact]
    public async Task BootstrappedFile_TrackedAsTempForCleanup()
    {
        var script = Path.Combine(_workDir, "x.sh");
        File.WriteAllText(script, "");
        var ctx = BuildContext(script);

        await new WriteBootstrappedScriptStep().ExecuteAsync(ctx, CancellationToken.None);

        ctx.TemporaryFiles.ShouldContain(ctx.BootstrappedScriptPath!,
            customMessage: "Bootstrapped temp file MUST be tracked so CleanupTemporaryFilesStep removes it.");
    }

    [Fact]
    public async Task WorkingDirNull_ThrowsClearly()
    {
        var ctx = BuildContext(Path.Combine(_workDir, "x.sh"));
        ctx.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new WriteBootstrappedScriptStep().ExecuteAsync(ctx, CancellationToken.None));
    }

    private RunScriptCommandContext BuildContext(string scriptPath)
    {
        File.WriteAllText(scriptPath, File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : "");
        return new RunScriptCommandContext
        {
            ScriptPath = scriptPath,
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = new VariableSet()
        };
    }
}
