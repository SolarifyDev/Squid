using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Substitution;

/// <summary>
/// G1.1 — pipeline-level tests for <see cref="SubstituteInFilesStep"/>.
///
/// <para>Operator-facing context: the IIS deploy handler advertises a
/// "Substitute variables in files" toggle backed by these two variables:
/// <list type="bullet">
///   <item><c>Squid.Action.IISWebSite.SubstituteInFiles.Enabled</c> ("True"/"False")</item>
///   <item><c>Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles</c>
///         (newline-separated file globs, relative to working dir)</item>
/// </list>
/// Pre-G1.1 those variables existed in the script preamble but no Calamari
/// step consumed them — the toggle was UI theatre. Pin the operator-
/// observable behaviours of the new step:</para>
/// </summary>
public sealed class SubstituteInFilesStepTests : IDisposable
{
    private readonly string _workDir;

    public SubstituteInFilesStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"sub-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── Enable-gating ───────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_TrueToggle_RunsStep()
    {
        var context = BuildContext(enabled: "True", targetFiles: "*.config");
        new SubstituteInFilesStep().IsEnabled(context).ShouldBeTrue();
    }

    [Theory]
    [InlineData("False")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-bool")]
    public void IsEnabled_NotTrue_SkipsStep(string? toggle)
    {
        var context = BuildContext(enabled: toggle, targetFiles: "*.config");
        new SubstituteInFilesStep().IsEnabled(context).ShouldBeFalse(
            customMessage: "Default-OFF semantic: anything other than 'True' must skip. Matches Octopus's bool-variable parsing.");
    }

    // ── Happy path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_SingleGlob_OneMatchingFile_TokensReplacedInPlace()
    {
        var file = Path.Combine(_workDir, "appsettings.json");
        File.WriteAllText(file, """{"ConnectionString":"#{Db.ConnectionString}","Env":"#{Squid.Environment.Name}"}""");

        var context = BuildContext(
            enabled: "True",
            targetFiles: "appsettings.json",
            ("Db.ConnectionString", "Server=prod-db;Database=app"),
            ("Squid.Environment.Name", "Production"));

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        var result = File.ReadAllText(file);
        result.ShouldBe("""{"ConnectionString":"Server=prod-db;Database=app","Env":"Production"}""");
    }

    [Fact]
    public async Task Execute_MultipleGlobs_NewlineSeparated_AllProcessed()
    {
        File.WriteAllText(Path.Combine(_workDir, "web.config"), "<env>#{Env}</env>");
        File.WriteAllText(Path.Combine(_workDir, "appsettings.json"), """{"env":"#{Env}"}""");
        File.WriteAllText(Path.Combine(_workDir, "ignored.txt"), "literal #{Env}");

        var context = BuildContext(
            enabled: "True",
            targetFiles: "web.config\nappsettings.json",  // wire shape: newline-separated, NOT comma
            ("Env", "Staging"));

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "web.config")).ShouldBe("<env>Staging</env>");
        File.ReadAllText(Path.Combine(_workDir, "appsettings.json")).ShouldBe("""{"env":"Staging"}""");
        File.ReadAllText(Path.Combine(_workDir, "ignored.txt"))
            .ShouldBe("literal #{Env}",
                customMessage: "Files NOT in target globs MUST stay untouched. Pin the no-collateral-edit invariant.");
    }

    [Fact]
    public async Task Execute_RecursiveGlob_FindsFilesInSubdirs()
    {
        Directory.CreateDirectory(Path.Combine(_workDir, "config"));
        Directory.CreateDirectory(Path.Combine(_workDir, "config", "envs"));
        File.WriteAllText(Path.Combine(_workDir, "config", "envs", "prod.json"), """{"k":"#{V}"}""");

        var context = BuildContext(enabled: "True", targetFiles: "**/*.json", ("V", "ok"));

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "config", "envs", "prod.json")).ShouldBe("""{"k":"ok"}""");
    }

    // ── Encoding preservation ──────────────────────────────────────────────

    [Fact]
    public async Task Execute_FileHasUtf8Bom_BomPreservedOnWriteBack()
    {
        // BOM round-trip: an operator's web.config with UTF-8 BOM MUST come
        // out the other side with the same BOM. Without this, IIS / .NET
        // Framework configuration parsers can choke on the same file after
        // we substitute.
        var file = Path.Combine(_workDir, "web.config");
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(file, "<env>#{Env}</env>", utf8WithBom);

        var context = BuildContext(enabled: "True", targetFiles: "*.config", ("Env", "QA"));

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        var bytes = File.ReadAllBytes(file);
        bytes.Take(3).ToArray().ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF },
            customMessage: "UTF-8 BOM MUST be preserved across substitution. Stripping it breaks IIS / .NET FX config parsers that detect encoding via BOM.");
    }

    [Fact]
    public async Task Execute_FileHasNoBom_NoBomAdded()
    {
        var file = Path.Combine(_workDir, "appsettings.json");
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(file, """{"env":"#{Env}"}""", utf8NoBom);

        var context = BuildContext(enabled: "True", targetFiles: "*.json", ("Env", "QA"));

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        var bytes = File.ReadAllBytes(file);
        bytes.Take(3).ToArray().ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF },
            customMessage: "File without BOM MUST stay without BOM — JSON parsers in node/python toolchains reject leading BOM.");
    }

    // ── Lenient vs strict missing-token handling ───────────────────────────

    [Fact]
    public async Task Execute_UnknownToken_DefaultLenient_LeavesPlaceholderAndContinues()
    {
        var file = Path.Combine(_workDir, "web.config");
        File.WriteAllText(file, "<env>#{NotDefined}</env>");

        var context = BuildContext(enabled: "True", targetFiles: "*.config");

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(file).ShouldBe("<env>#{NotDefined}</env>",
            customMessage: "Default-lenient: unresolved tokens stay as literal placeholders so the deployed file is still parseable. Operator can flip ShouldFailDeploymentOnSubstitutionFails=True to harden.");
    }

    [Fact]
    public async Task Execute_UnknownToken_StrictMode_ThrowsWithListOfUnresolved()
    {
        var file = Path.Combine(_workDir, "web.config");
        File.WriteAllText(file, "<a>#{Missing1}</a><b>#{Missing2}</b><c>#{Found}</c>");

        var context = BuildContext(
            enabled: "True",
            targetFiles: "*.config",
            ("Found", "yes"),
            ("Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails", "True"));

        var ex = await Should.ThrowAsync<SubstituteInFilesException>(() =>
            new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None));

        ex.Message.ShouldContain("Missing1");
        ex.Message.ShouldContain("Missing2");
        ex.Message.ShouldNotContain("Found",
            customMessage: "Strict-mode error MUST list ONLY unresolved tokens, not resolved ones (those got substituted fine).");
    }

    // ── Edge cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_EmptyTargetFiles_NoOp()
    {
        var file = Path.Combine(_workDir, "web.config");
        File.WriteAllText(file, "<env>#{Env}</env>");

        var context = BuildContext(enabled: "True", targetFiles: "", ("Env", "Production"));

        await new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(file).ShouldBe("<env>#{Env}</env>",
            customMessage: "Empty TargetFiles MUST be a no-op. Operator left the field blank intentionally; we don't second-guess.");
    }

    [Fact]
    public async Task Execute_GlobMatchesNoFiles_DoesNotCrash()
    {
        // Operator typo / file moved → glob matches nothing. We log a warning
        // and continue. Deploy doesn't fail on this alone (operator wants
        // strict mode for that — same variable as missing-token).
        var context = BuildContext(enabled: "True", targetFiles: "nonexistent.config");

        await Should.NotThrowAsync(() => new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_WorkingDirNotSet_Throws()
    {
        var context = BuildContext(enabled: "True", targetFiles: "*.config");
        context.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_VariablesNotLoaded_Throws()
    {
        var context = BuildContext(enabled: "True", targetFiles: "*.config");
        context.Variables = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new SubstituteInFilesStep().ExecuteAsync(context, CancellationToken.None));
    }

    // ── Wire-contract pinning ───────────────────────────────────────────────

    [Fact]
    public void EnabledVariableName_PinnedToIISHandlerContract()
    {
        // Drift detector (Rule 8): if either side renames this, the chain
        // silently no-ops. Pin the wire literal.
        SubstituteInFilesStep.EnabledVariableName
            .ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.Enabled");
    }

    [Fact]
    public void TargetFilesVariableName_PinnedToIISHandlerContract()
    {
        SubstituteInFilesStep.TargetFilesVariableName
            .ShouldBe("Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles");
    }

    [Fact]
    public void ShouldFailVariableName_PinnedToOctopusParity()
    {
        SubstituteInFilesStep.ShouldFailOnUnresolvedVariableName
            .ShouldBe("Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private RunScriptCommandContext BuildContext(string? enabled, string? targetFiles, params (string name, string value)[] extraVars)
    {
        var vars = new VariableSet();
        if (enabled != null)
            vars.Set(SubstituteInFilesStep.EnabledVariableName, enabled);
        if (targetFiles != null)
            vars.Set(SubstituteInFilesStep.TargetFilesVariableName, targetFiles);
        foreach (var (name, value) in extraVars)
            vars.Set(name, value);

        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };
    }
}
