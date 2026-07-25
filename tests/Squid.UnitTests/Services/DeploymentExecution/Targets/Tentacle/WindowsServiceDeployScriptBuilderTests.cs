using System.Linq;
using System.Text;
using Shouldly;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class WindowsServiceDeployScriptBuilderTests
{
    [Fact]
    public void Build_EmitsSquidParametersHashtable_AtTopOfScript()
    {
        var action = BuildAction(
            (WindowsServiceDeployProperties.ServiceName, "OrderWorker"),
            (WindowsServiceDeployProperties.ExecutablePath, "Order.Worker.exe"));

        var script = WindowsServiceDeployScriptBuilder.Build(action);

        script.ShouldContain("# BEGIN GENERATED PREAMBLE");
        script.ShouldContain("$SquidParameters = @{}");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ServiceName'] = 'OrderWorker'");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ExecutablePath'] = 'Order.Worker.exe'");
    }

    [Fact]
    public void Build_AppendsEmbeddedScriptBody_AfterPreamble()
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction());

        var preambleIndex = script.IndexOf("# BEGIN GENERATED PREAMBLE", StringComparison.Ordinal);
        var bodyIndex = script.IndexOf("function Resolve-PackageRoot", StringComparison.Ordinal);

        preambleIndex.ShouldBeGreaterThanOrEqualTo(0);
        bodyIndex.ShouldBeGreaterThanOrEqualTo(0);
        preambleIndex.ShouldBeLessThan(bodyIndex);
        script.ShouldContain("function Resolve-AcquiredPackageSourcePath");
        script.ShouldContain("package-references.json");
        script.ShouldContain("Invoke-Sc create $serviceName");
        script.ShouldContain("Invoke-Sc config $serviceName");
    }

    [Fact]
    public void Build_EmbeddedScriptStopsExistingService_BeforePackageExtraction()
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction());

        var existingServiceIndex = script.IndexOf("$existingService = Get-Service", StringComparison.Ordinal);
        var stopExistingServiceIndex = script.IndexOf("Stop-ServiceIfRunning -Name $serviceName", existingServiceIndex, StringComparison.Ordinal);
        var packageRootIndex = script.IndexOf("$packageRoot = Resolve-PackageRoot", StringComparison.Ordinal);

        existingServiceIndex.ShouldBeGreaterThanOrEqualTo(0);
        stopExistingServiceIndex.ShouldBeGreaterThanOrEqualTo(0);
        packageRootIndex.ShouldBeGreaterThanOrEqualTo(0);
        stopExistingServiceIndex.ShouldBeLessThan(packageRootIndex,
            customMessage: "Existing services must be stopped before package purge/extract touches the install directory.");
    }

    [Fact]
    public void Build_EmbeddedScriptWaitsForStoppedServiceProcess_BeforePackageExtraction()
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction());

        var waitProcessDefinitionIndex = script.IndexOf("function Wait-ServiceProcessExit", StringComparison.Ordinal);
        var waitProcessCallIndex = script.IndexOf("Wait-ServiceProcessExit -ProcessId $processId", StringComparison.Ordinal);
        var packageRootIndex = script.IndexOf("$packageRoot = Resolve-PackageRoot", StringComparison.Ordinal);

        waitProcessDefinitionIndex.ShouldBeGreaterThanOrEqualTo(0);
        waitProcessCallIndex.ShouldBeGreaterThanOrEqualTo(0);
        packageRootIndex.ShouldBeGreaterThanOrEqualTo(0);
        waitProcessCallIndex.ShouldBeLessThan(packageRootIndex,
            customMessage: "Existing service processes must exit before package purge/extract can safely replace locked binaries.");
    }

    [Fact]
    public void Build_EmbeddedScriptRetriesPackageFileOperations()
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction());

        script.ShouldContain("function Invoke-WithRetry");
        script.ShouldContain("Invoke-WithRetry -Description \"Removing existing package extract directory '$extractTo'\"");
        script.ShouldContain("Invoke-WithRetry -Description \"Copying package content from '$($sourceItem.FullName)' to '$extractTo'\"");
        script.ShouldContain("Invoke-WithRetry -Description \"Expanding package '$($sourceItem.FullName)' to '$extractTo'\"");
    }

    [Fact]
    public void Build_EmbeddedScriptWaitsForCreatedService_BeforeStart()
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction());

        var createIndex = script.IndexOf("Invoke-Sc create $serviceName", StringComparison.Ordinal);
        var waitExistsIndex = script.IndexOf("Wait-ServiceExists -Name $serviceName", createIndex, StringComparison.Ordinal);
        var startIndex = script.IndexOf("Start-Service -Name $serviceName", StringComparison.Ordinal);

        createIndex.ShouldBeGreaterThanOrEqualTo(0);
        waitExistsIndex.ShouldBeGreaterThanOrEqualTo(0);
        startIndex.ShouldBeGreaterThanOrEqualTo(0);
        waitExistsIndex.ShouldBeLessThan(startIndex,
            customMessage: "Newly-created services should be visible to Get-Service before Start-Service runs.");
    }

    [Fact]
    public void Build_AllRecognisedProperties_HaveExplicitEntryInHashtable_EvenWhenUnset()
    {
        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction());

        foreach (var propertyName in WindowsServiceDeployScriptBuilder.RecognisedProperties)
            script.ShouldContain($"$SquidParameters['{propertyName}']");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("C:\\Services\\Order", "C:\\Services\\Order")]
    [InlineData("$env:TEMP", "$env:TEMP")]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData("'; Stop-Service X; '", "''; Stop-Service X; ''")]
    public void EscapeForPowerShellSingleQuote_FollowsPowerShellLiteralRules(string input, string expected)
    {
        WindowsServiceDeployScriptBuilder.EscapeForPowerShellSingleQuote(input).ShouldBe(expected);
    }

    [Fact]
    public void Build_MultilineDescription_PreservesNewlinesViaBase64()
    {
        var description = "line 1\nline 2";
        var action = BuildAction((WindowsServiceDeployProperties.Description, description));

        var script = WindowsServiceDeployScriptBuilder.Build(action);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(description));

        script.ShouldContain($"FromBase64String('{encoded}')");
        script.ShouldContain("Squid.Action.WindowsService.Description");
    }

    [Fact]
    public void Build_Dependencies_PreservesNewlineSeparatedListViaBase64()
    {
        var dependencies = "MSSQLSERVER\nEventLog";
        var action = BuildAction((WindowsServiceDeployProperties.Dependencies, dependencies));

        var script = WindowsServiceDeployScriptBuilder.Build(action);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(dependencies));

        script.ShouldContain($"FromBase64String('{encoded}')");
    }

    [Fact]
    public void Build_EmitsSquidVariablesHashtable()
    {
        var variables = new[]
        {
            new VariableDto { Name = "ConnString", Value = "Server='prod';" },
            new VariableDto { Name = "Empty", Value = null }
        };

        var script = WindowsServiceDeployScriptBuilder.Build(BuildAction(), variables, Array.Empty<SelectedPackageDto>());

        script.ShouldContain("$SquidVariables = @{}");
        script.ShouldContain("$SquidVariables['ConnString'] = 'Server=''prod'';'");
        script.ShouldContain("$SquidVariables['Empty'] = ''");
    }

    [Fact]
    public void Build_EmitsSelectedPackageMetadata_AndActionMatchedPrimaryPackage()
    {
        var action = BuildAction(actionName: "Deploy worker");
        var packages = new[]
        {
            new SelectedPackageDto { ActionName = "Other action", PackageReferenceName = "Other.Package", Version = "1.0.0" },
            new SelectedPackageDto { ActionName = "Deploy worker", PackageReferenceName = "Order.Worker", Version = "2.3.4" }
        };

        var script = WindowsServiceDeployScriptBuilder.Build(action, Array.Empty<VariableDto>(), packages);

        script.ShouldContain("$SquidSelectedPackages = @()");
        script.ShouldContain("PackageReferenceName = 'Order.Worker'");
        script.ShouldContain("Version = '2.3.4'");
        script.ShouldContain("if ($package['ActionName'] -ieq 'Deploy worker')");
        script.ShouldContain("$SquidSelectedPackage = $package");
    }

    [Fact]
    public void Build_ServiceAccountAndLifecycleProperties_FlowThroughPreamble()
    {
        var action = BuildAction(
            (WindowsServiceDeployProperties.ServiceAccount, "SpecificUser"),
            (WindowsServiceDeployProperties.CustomAccountName, "DOMAIN\\worker"),
            (WindowsServiceDeployProperties.CustomAccountPassword, "p@ss'word"),
            (WindowsServiceDeployProperties.StartMode, "Manual"),
            (WindowsServiceDeployProperties.DesiredStatus, "Stopped"),
            (WindowsServiceDeployProperties.Arguments, "--listen 9000"));

        var script = WindowsServiceDeployScriptBuilder.Build(action);

        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.ServiceAccount'] = 'SpecificUser'");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.CustomAccountName'] = 'DOMAIN\\worker'");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.CustomAccountPassword'] = 'p@ss''word'");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.StartMode'] = 'Manual'");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.DesiredStatus'] = 'Stopped'");
        script.ShouldContain("$SquidParameters['Squid.Action.WindowsService.Arguments'] = '--listen 9000'");
    }

    private static DeploymentActionDto BuildAction(params (string Name, string Value)[] properties)
        => BuildAction("Windows Service", properties);

    private static DeploymentActionDto BuildAction(string actionName, params (string Name, string Value)[] properties)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            Name = actionName,
            ActionType = "Squid.DeployWindowsService",
            Properties = properties
                .Select(p => new DeploymentActionPropertyDto { PropertyName = p.Name, PropertyValue = p.Value })
                .ToList()
        };
    }
}
