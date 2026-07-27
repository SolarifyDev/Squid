using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Squid.Calamari.Commands;
using Squid.Calamari.Commands.Configuration;
using Squid.Calamari.Tests.Calamari.Package;
using Squid.Calamari.Variables;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Commands.Configuration;

/// <summary>
/// Pipeline-level tests for <see cref="ConfigurationVariablesStep"/>.
/// Mirrors IIS/Octopus ConfigurationVariables semantics:
/// replace matching appSettings / connectionStrings / applicationSettings
/// entries from the deployment VariableSet when the feature is enabled.
/// </summary>
[Trait("Category", DeployPackageE2ECategories.Full)]
public sealed class ConfigurationVariablesStepTests : IDisposable
{
    private readonly string _workDir;

    public ConfigurationVariablesStepTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"cfgvars-step-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
    }

    // ── Enable-gating ───────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_True_WhenCanonicalEnabled()
    {
        var context = BuildContext(enabled: "True", extraVars: ("AppName", "Hello"));
        WriteMinimalWebConfig(appName: "old");

        new ConfigurationVariablesStep().IsEnabled(context).ShouldBeTrue();
    }

    [Fact]
    public void IsEnabled_True_WhenLegacyIISEnabled()
    {
        var vars = new VariableSet();
        vars.Set(ConfigurationVariablesVariableNames.Legacy.Enabled, "True");

        var context = new RunScriptCommandContext
        {
            ScriptPath = Path.Combine(_workDir, "script.sh"),
            VariablesPath = Path.Combine(_workDir, "variables.json"),
            WorkingDirectory = _workDir,
            Variables = vars
        };

        new ConfigurationVariablesStep().IsEnabled(context).ShouldBeTrue(
            customMessage: "Legacy IIS-prefixed Enabled MUST trigger the step (back-compat).");
    }

    [Theory]
    [InlineData("False")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-bool")]
    public void IsEnabled_NotTrue_SkipsStep(string? toggle)
    {
        var context = BuildContext(enabled: toggle);
        new ConfigurationVariablesStep().IsEnabled(context).ShouldBeFalse();
    }

    // ── Rewrites ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_ReplacesAppSettingsByVariableName()
    {
        WriteMinimalWebConfig(appName: "old");
        var context = BuildContext(enabled: "True", extraVars: ("AppName", "Hello"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "Web.config"))
            .ShouldContain("value=\"Hello\"");
    }

    [Fact]
    public async Task Execute_ReplacesConnectionStringsByVariableName()
    {
        File.WriteAllText(Path.Combine(_workDir, "Web.config"), """
            <?xml version="1.0"?>
            <configuration>
              <connectionStrings>
                <add name="Default" connectionString="Server=old" providerName="System.Data.SqlClient" />
              </connectionStrings>
            </configuration>
            """);

        var context = BuildContext(enabled: "True", extraVars: ("Default", "Server=prod"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "Web.config"))
            .ShouldContain("connectionString=\"Server=prod\"");
    }

    [Fact]
    public async Task Execute_ReplacesApplicationSettingsValueNode()
    {
        File.WriteAllText(Path.Combine(_workDir, "App.config"), """
            <?xml version="1.0"?>
            <configuration>
              <applicationSettings>
                <MyApp.Properties.Settings>
                  <setting name="ApiUrl" serializeAs="String">
                    <value>http://old</value>
                  </setting>
                </MyApp.Properties.Settings>
              </applicationSettings>
            </configuration>
            """);

        var context = BuildContext(enabled: "True", extraVars: ("ApiUrl", "https://prod"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "App.config"))
            .ShouldContain("<value>https://prod</value>");
    }

    [Fact]
    public async Task Execute_CreatesApplicationSettingsValueNodeWhenMissing()
    {
        File.WriteAllText(Path.Combine(_workDir, "App.config"), """
            <?xml version="1.0"?>
            <configuration>
              <applicationSettings>
                <MyApp.Properties.Settings>
                  <setting name="ApiUrl" serializeAs="String" />
                </MyApp.Properties.Settings>
              </applicationSettings>
            </configuration>
            """);

        var context = BuildContext(enabled: "True", extraVars: ("ApiUrl", "https://created"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "App.config"))
            .ShouldContain("https://created");
    }

    [Fact]
    public async Task Execute_VariableNameMatchIsCaseInsensitive()
    {
        WriteMinimalWebConfig(appName: "old");
        var context = BuildContext(enabled: "True", extraVars: ("appname", "HelloCase"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(_workDir, "Web.config"))
            .ShouldContain("value=\"HelloCase\"");
    }

    [Fact]
    public async Task Execute_DoesNotTouchUnmatchedEntries()
    {
        File.WriteAllText(Path.Combine(_workDir, "Web.config"), """
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="AppName" value="old" />
                <add key="KeepMe" value="stay" />
              </appSettings>
            </configuration>
            """);

        var context = BuildContext(enabled: "True", extraVars: ("AppName", "Hello"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        var content = File.ReadAllText(Path.Combine(_workDir, "Web.config"));
        content.ShouldContain("value=\"Hello\"");
        content.ShouldContain("value=\"stay\"");
    }

    [Fact]
    public async Task Execute_ScansNestedConfigFiles()
    {
        var nested = Path.Combine(_workDir, "config");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "app.config"), """
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="AppName" value="old" />
              </appSettings>
            </configuration>
            """);

        var context = BuildContext(enabled: "True", extraVars: ("AppName", "nested-hello"));

        await new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None);

        File.ReadAllText(Path.Combine(nested, "app.config"))
            .ShouldContain("value=\"nested-hello\"");
    }

    // ── XML parse failure policy ────────────────────────────────────────────

    [Fact]
    public async Task Execute_MalformedXml_ThrowsByDefault()
    {
        File.WriteAllText(Path.Combine(_workDir, "broken.config"), "<not-closed");
        var context = BuildContext(enabled: "True", extraVars: ("AppName", "Hello"));

        await Should.ThrowAsync<Exception>(() =>
            new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_MalformedXml_WarnsAndSkips_WhenIgnoreErrorsTrue()
    {
        File.WriteAllText(Path.Combine(_workDir, "broken.config"), "<not-closed");
        WriteMinimalWebConfig(appName: "old");

        var context = BuildContext(
            enabled: "True",
            ignoreVariableReplacementErrors: "True",
            extraVars: ("AppName", "Hello"));

        await Should.NotThrowAsync(() =>
            new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None));

        File.ReadAllText(Path.Combine(_workDir, "Web.config"))
            .ShouldContain("value=\"Hello\"",
                customMessage: "Valid sibling config MUST still be rewritten when ignore-errors is on.");
    }

    [Fact]
    public async Task Execute_WorkingDirNotSet_Throws()
    {
        var context = BuildContext(enabled: "True");
        context.WorkingDirectory = null;

        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ConfigurationVariablesStep().ExecuteAsync(context, CancellationToken.None));
    }

    // ── Wire-contract pinning ──────────────────────────────────────────────

    [Fact]
    public void EnabledVariableName_Canonical_PinnedHandlerAgnostic()
        => ConfigurationVariablesVariableNames.Enabled.ShouldBe("Squid.Action.ConfigurationVariables.Enabled");

    [Fact]
    public void LegacyVariableName_PinnedToIISHandlerContract()
        => ConfigurationVariablesVariableNames.Legacy.Enabled
            .ShouldBe("Squid.Action.IISWebSite.ConfigurationVariables.Enabled");

    [Fact]
    public void IgnoreVariableReplacementErrors_PinnedPackageContract()
        => ConfigurationVariablesVariableNames.IgnoreVariableReplacementErrors
            .ShouldBe("Squid.Action.Package.IgnoreVariableReplacementErrors");


    // ── Helpers ────────────────────────────────────────────────────────────

    private void WriteMinimalWebConfig(string appName)
    {
        File.WriteAllText(Path.Combine(_workDir, "Web.config"), $"""
            <?xml version="1.0"?>
            <configuration>
              <appSettings>
                <add key="AppName" value="{appName}" />
              </appSettings>
            </configuration>
            """);
    }

    private RunScriptCommandContext BuildContext(
        string? enabled,
        string? ignoreVariableReplacementErrors = null,
        params (string name, string value)[] extraVars)
    {
        var vars = new VariableSet();
        if (enabled != null) vars.Set(ConfigurationVariablesVariableNames.Enabled, enabled);
        if (ignoreVariableReplacementErrors != null)
            vars.Set(ConfigurationVariablesVariableNames.IgnoreVariableReplacementErrors, ignoreVariableReplacementErrors);

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
