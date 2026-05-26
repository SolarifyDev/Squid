using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Configuration;

/// <summary>
/// G1.2 — pipeline-level tests for <see cref="ConfigurationTransformsStep"/>.
///
/// <para>Operator-facing variables (mirrors the IIS deploy script preamble):
/// <list type="bullet">
///   <item><c>Squid.Action.IISWebSite.ConfigurationTransforms.Enabled</c> ("True"/"False")</item>
///   <item><c>Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName</c>
///         (e.g. "Production" — drives auto-pairing of <c>web.{Env}.config</c>)</item>
///   <item><c>Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms</c>
///         (newline-separated "transform.config => base.config" explicit pairs)</item>
/// </list></para>
///
/// <para>Auto-pairing rule (Octopus parity): for each <c>*.config</c> in the
/// working dir, look for a sibling <c>*.{EnvironmentName}.config</c> and
/// apply it. <c>*.Release.config</c> is also applied unconditionally
/// (matches .NET FX build-time XDT defaults).</para>
/// </summary>
public sealed class ConfigurationTransformsStepTests : IDisposable
{
    private readonly string _workDir;

    public ConfigurationTransformsStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"xdt-step-{Guid.NewGuid():N}");
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
        var context = BuildContext(enabled: "True", environmentName: "Production");
        new ConfigurationTransformsStep().IsEnabled(context).ShouldBeTrue();
    }

    [Theory]
    [InlineData("False")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-bool")]
    public void IsEnabled_NotTrue_SkipsStep(string? toggle)
    {
        var context = BuildContext(enabled: toggle, environmentName: "Production");
        new ConfigurationTransformsStep().IsEnabled(context).ShouldBeFalse();
    }

    // ── Auto-pairing by EnvironmentName ─────────────────────────────────────

    [Fact]
    public async Task Execute_EnvironmentSpecificTransform_AppliedToMatchingBase()
    {
        File.WriteAllText(Path.Combine(_workDir, "web.config"), """
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="Env" value="Dev" />
              </appSettings>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(_workDir, "web.Production.config"), """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="Env" value="Prod" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
              </appSettings>
            </configuration>
            """);

        var context = BuildContext(enabled: "True", environmentName: "Production");

        await new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "web.config")).ShouldContain("value=\"Prod\"");
    }

    [Fact]
    public async Task Execute_NoMatchingEnvironmentTransform_NoOp_DoesNotCrash()
    {
        File.WriteAllText(Path.Combine(_workDir, "web.config"), """
            <?xml version="1.0"?>
            <configuration><appSettings><add key="A" value="1" /></appSettings></configuration>
            """);
        // intentionally no web.Staging.config exists

        var context = BuildContext(enabled: "True", environmentName: "Staging");

        await Should.NotThrowAsync(() =>
            new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None));

        File.ReadAllText(Path.Combine(_workDir, "web.config")).ShouldContain("value=\"1\"",
            customMessage: "No transform for this environment = no change; base file untouched.");
    }

    [Fact]
    public async Task Execute_TransformsAlsoNonWebConfig_AppSettings_Etc()
    {
        // The auto-pair rule applies to ANY *.config, not just web.config.
        // appsettings.config, custom.config, etc. all get the {Env}.config
        // pair treatment.
        File.WriteAllText(Path.Combine(_workDir, "appsettings.config"), """
            <?xml version="1.0"?>
            <configuration><add key="K" value="v0" /></configuration>
            """);
        File.WriteAllText(Path.Combine(_workDir, "appsettings.Production.config"), """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <add key="K" value="v1" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
            </configuration>
            """);

        var context = BuildContext(enabled: "True", environmentName: "Production");

        await new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "appsettings.config")).ShouldContain("value=\"v1\"");
    }

    // ── AdditionalTransforms — operator-supplied explicit pairs ──────────────

    [Fact]
    public async Task Execute_AdditionalTransforms_OperatorPairs_Applied()
    {
        File.WriteAllText(Path.Combine(_workDir, "app.config"), """
            <?xml version="1.0"?>
            <configuration><appSettings><add key="A" value="x" /></appSettings></configuration>
            """);
        File.WriteAllText(Path.Combine(_workDir, "custom-override.config"), """
            <?xml version="1.0"?>
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <appSettings>
                <add key="A" value="y" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" />
              </appSettings>
            </configuration>
            """);

        var context = BuildContext(
            enabled: "True",
            environmentName: null,
            additionalTransforms: "custom-override.config => app.config");

        await new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "app.config")).ShouldContain("value=\"y\"",
            customMessage: "Operator-supplied 'transform => target' pair MUST be applied even without an EnvironmentName auto-pair match.");
    }

    [Fact]
    public async Task Execute_AdditionalTransforms_MultiplePairs_NewlineSeparated()
    {
        File.WriteAllText(Path.Combine(_workDir, "a.config"), """<?xml version="1.0"?><c><add key="K" value="0" /></c>""");
        File.WriteAllText(Path.Combine(_workDir, "b.config"), """<?xml version="1.0"?><c><add key="K" value="0" /></c>""");
        File.WriteAllText(Path.Combine(_workDir, "a-prod.config"), """<?xml version="1.0"?><c xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform"><add key="K" value="1" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" /></c>""");
        File.WriteAllText(Path.Combine(_workDir, "b-prod.config"), """<?xml version="1.0"?><c xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform"><add key="K" value="2" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" /></c>""");

        var context = BuildContext(
            enabled: "True",
            environmentName: null,
            additionalTransforms: "a-prod.config => a.config\nb-prod.config => b.config");

        await new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "a.config")).ShouldContain("value=\"1\"");
        File.ReadAllText(Path.Combine(_workDir, "b.config")).ShouldContain("value=\"2\"");
    }

    [Fact]
    public async Task Execute_AdditionalTransforms_MalformedLine_SkippedWithWarning()
    {
        // Line without `=>` separator is operator typo — skip with warning,
        // continue with other pairs. Don't fail the whole deploy on a
        // formatting mistake.
        File.WriteAllText(Path.Combine(_workDir, "a.config"), """<?xml version="1.0"?><c><add key="K" value="0" /></c>""");
        File.WriteAllText(Path.Combine(_workDir, "a-prod.config"), """<?xml version="1.0"?><c xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform"><add key="K" value="1" xdt:Transform="SetAttributes" xdt:Locator="Match(key)" /></c>""");

        var context = BuildContext(
            enabled: "True",
            environmentName: null,
            additionalTransforms: "malformed line no arrow\na-prod.config => a.config");

        await Should.NotThrowAsync(() =>
            new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None));

        File.ReadAllText(Path.Combine(_workDir, "a.config")).ShouldContain("value=\"1\"",
            customMessage: "Valid pair after malformed line MUST still be applied — one typo doesn't break the entire transform list.");
    }

    // ── Edge cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_BaseFileMissing_WarningContinue_NoCrash()
    {
        // AdditionalTransforms references a base file that doesn't exist —
        // probably extracted to a different location. Warn + continue.
        File.WriteAllText(Path.Combine(_workDir, "transform.config"), """<?xml version="1.0"?><c />""");

        var context = BuildContext(
            enabled: "True",
            environmentName: null,
            additionalTransforms: "transform.config => missing-base.config");

        await Should.NotThrowAsync(() =>
            new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_EnabledButNoTransformsAtAll_NoOp()
    {
        // Operator enabled the toggle but provided no environment-name AND
        // no additional transforms. No-op cleanly.
        File.WriteAllText(Path.Combine(_workDir, "web.config"), """<?xml version="1.0"?><c />""");

        var context = BuildContext(enabled: "True", environmentName: null, additionalTransforms: null);

        await Should.NotThrowAsync(() =>
            new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None));

        File.ReadAllText(Path.Combine(_workDir, "web.config")).ShouldContain("<c />",
            customMessage: "Enabled but no transform pairs = base files untouched.");
    }

    [Fact]
    public async Task Execute_WorkingDirNotSet_Throws()
    {
        var context = BuildContext(enabled: "True", environmentName: "Production");
        context.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ConfigurationTransformsStep().ExecuteAsync(context, CancellationToken.None));
    }

    // ── Wire-contract pinning ──────────────────────────────────────────────

    [Fact]
    public void EnabledVariableName_PinnedToIISHandlerContract()
        => ConfigurationTransformsVariableNames.Enabled.ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.Enabled");

    [Fact]
    public void EnvironmentNameVariableName_PinnedToIISHandlerContract()
        => ConfigurationTransformsVariableNames.EnvironmentName.ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName");

    [Fact]
    public void AdditionalTransformsVariableName_PinnedToIISHandlerContract()
        => ConfigurationTransformsVariableNames.AdditionalTransforms.ShouldBe("Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms");

    // ── Helpers ────────────────────────────────────────────────────────────

    private RunScriptCommandContext BuildContext(string? enabled, string? environmentName, string? additionalTransforms = null)
    {
        var vars = new VariableSet();
        if (enabled != null) vars.Set(ConfigurationTransformsVariableNames.Enabled, enabled);
        if (environmentName != null) vars.Set(ConfigurationTransformsVariableNames.EnvironmentName, environmentName);
        if (additionalTransforms != null) vars.Set(ConfigurationTransformsVariableNames.AdditionalTransforms, additionalTransforms);

        return new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };
    }
}
