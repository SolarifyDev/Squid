using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for the imported "Deploy Kubernetes containers" step: a minimal export goes through
/// the REAL planning pipeline + confirmation orchestrator (real DI, real Postgres) and the persisted
/// action properties and confirmation diagnostics are asserted. Covers the legacy DeploymentWait taking
/// precedence over ResourceStatusCheck, an enabled custom resource with Octostache expressions, unmapped
/// Octopus built-ins being reported, and a disabled ConfigMap still referenced by the pod being reported.
/// </summary>
public class OctopusImportKubernetesContainersIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string P = "Octopus.Action.KubernetesContainers.";

    private const string CustomResourceYaml = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: #{AppName}-extra
          namespace: monitoring
          labels:
            release: "#{Octopus.Release.Number}"
            owner: "#{Octopus.Action.KubernetesContainers.DeploymentName}"
        data:
        #{each port in Ports}
          port-#{port}: "#{port}"
        #{/each}
        """;

    public OctopusImportKubernetesContainersIntegrationTests()
        : base("OctopusImportKubernetesContainers", "squid_it_octopus_import_k8s_containers")
    {
    }

    [Fact]
    public async Task ContainersStep_WithWaitCustomResourceAndDisabledConfigMap_PersistsOctopusSemanticsAndReportsEveryGap()
    {
        var archivePath = WriteArchive(Documents(new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.KubernetesService,Octopus.Features.KubernetesCustomResource",
            [P + "DeploymentResourceType"] = "StatefulSet",
            [P + "DeploymentName"] = "web",
            [P + "Namespace"] = "apps",
            [P + "DeploymentWait"] = "Wait",
            ["Octopus.Action.Kubernetes.ResourceStatusCheck"] = "False",
            ["Octopus.Action.Kubernetes.DeploymentTimeout"] = "240",
            [P + "Containers"] = """[{"Name":"web","Image":"nginx:1.25","Ports":[{"key":"http","value":"80","option":"TCP"}]}]""",
            [P + "ConfigMapName"] = "web-config",
            [P + "ConfigMapValues"] = """{"mode":"prod"}""",
            [P + "CombinedVolumes"] = """[{"Name":"cfg","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""",
            [P + "CustomResourceYaml"] = CustomResourceYaml
        }));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var result = await ImportAsync(scope, db, archivePath);

                result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: string.Join("; ", result.Result.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.Message}")));

                var properties = await ActionPropertiesAsync(db, "Deploy web");

                properties["Squid.Action.KubernetesContainers.ObjectStatusCheck"].ShouldBe("True", customMessage: "Calamari runs the legacy wait when DeploymentWait=Wait, whatever ResourceStatusCheck says");
                properties["Squid.Action.KubernetesContainers.ObjectStatusCheckTimeout"].ShouldBe("240");
                properties[SpecialVariables.Action.KubernetesYaml].ShouldBe(CustomResourceYaml.Replace("#{Octopus.Release.Number}", "#{Squid.Release.Number}"));
                properties.ShouldNotContainKey("Squid.Action.KubernetesContainers.ConfigMapName");
                properties["Squid.Action.KubernetesContainers.DeploymentResourceType"].ShouldBe("StatefulSet");

                // Resource-level diagnostics are also rolled up into the session diagnostics, so compare distinct messages.
                var diagnostics = result.Result.Diagnostics.Concat(result.Result.Resources.SelectMany(r => r.Diagnostics)).ToList();
                diagnostics.Where(d => d.Code == OctopusImportVariableReferenceDiagnosticCodes.UnsupportedOctopusSystemVariable).Select(d => d.Message).Distinct().Single().ShouldContain("Octopus.Action.KubernetesContainers.DeploymentName that Squid does not provide");
                diagnostics.Where(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DisabledFeatureResourceReferenced).Select(d => d.Message).Distinct().Single().ShouldContain("volume cfg");
                diagnostics.ShouldContain(d => d.Code == OctopusImportActionMappingDiagnosticCodes.DeploymentWaitMappedToStatusCheck);
                diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task ContainersStep_WithCaseVariantPropertyKeys_ImportsInsteadOfCrashing()
    {
        var archivePath = WriteArchive(Documents(new Dictionary<string, string>
        {
            ["Octopus.Action.EnabledFeatures"] = "Octopus.Features.KubernetesService",
            [P + "DeploymentName"] = "web",
            [P + "Namespace"] = "apps",
            ["octopus.action.kubernetescontainers.namespace"] = "ignored",
            [P + "IngressName"] = "web-ingress",
            [P + "Containers"] = """[{"Name":"web","Image":"nginx:1.25"}]"""
        }));

        try
        {
            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                var result = await ImportAsync(scope, db, archivePath);

                result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: string.Join("; ", result.Result.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.Message}")));

                var properties = await ActionPropertiesAsync(db, "Deploy web");
                properties["Squid.Action.KubernetesContainers.Namespace"].ShouldBe("apps");
                properties.ShouldNotContainKey("Squid.Action.KubernetesContainers.IngressName");
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static async Task<OctopusImportSessionDto> ImportAsync(ILifetimeScope scope, SquidDbContext db, string archivePath)
    {
        var sessionId = Guid.NewGuid();

        db.Set<OctopusImportSession>().Add(new OctopusImportSession
        {
            SessionId = sessionId,
            DestinationSpaceId = SpaceId,
            OwnerUserId = CurrentUsers.InternalUser.Id,
            State = OctopusImportSessionState.Validated.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        scope.Resolve<IOctopusExportSecretsAccessor>().Secrets = OctopusExportSecrets.None;

        var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
        snapshot.PreviewPlan.HasBlockers.ShouldBeFalse(customMessage: string.Join("; ", snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics).Concat(snapshot.PreviewPlan.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}")));

        return await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));
    }

    private static async Task<Dictionary<string, string>> ActionPropertiesAsync(SquidDbContext db, string actionName)
    {
        var action = await db.Set<DeploymentAction>().OrderByDescending(a => a.Id).FirstAsync(a => a.Name == actionName);

        return await db.Set<DeploymentActionProperty>().Where(p => p.ActionId == action.Id).ToDictionaryAsync(p => p.PropertyName, p => p.PropertyValue);
    }

    // ── Minimal export ───────────────────────────────────────────────────────────────────────────

    private static List<(string Id, string DocumentType, string DocumentSource, string Json)> Documents(Dictionary<string, string> containerProperties)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Id(string prefix) => $"{prefix}-{suffix}";

        var action = new { Id = Id("Actions-k8s"), Name = "Deploy web", ActionType = "Octopus.KubernetesDeployContainers", IsRequired = false, IsDisabled = false, Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = containerProperties };
        var step = new { Id = Id("Steps-k8s"), Name = "Deploy web", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string> { ["Octopus.Action.TargetRoles"] = "k8s" }, Actions = new[] { action } };

        return
        [
            Doc(Id("ProjectGroups-1"), "ProjectGroup", new { Id = Id("ProjectGroups-1"), Name = $"K8s {suffix}", Slug = $"k8s-{suffix}" }),
            Doc(Id("Environments-1"), "StaticDeploymentEnvironment", new { Id = Id("Environments-1"), Name = $"TEST-{suffix}", Slug = $"test-{suffix}" }),
            Doc(Id("Lifecycles-1"), "Lifecycle", new { Id = Id("Lifecycles-1"), Name = $"K8s {suffix}", Slug = $"k8s-lc-{suffix}", Phases = new[] { new { Id = Id("Phases-1"), Name = "Test", AutomaticDeploymentTargets = new[] { Id("Environments-1") }, OptionalDeploymentTargets = Array.Empty<string>() } } }),
            Doc(Id("Projects-1"), "Project", new { Id = Id("Projects-1"), Name = $"K8s.Api.{suffix}", Slug = $"k8s-api-{suffix}", ProjectGroupId = Id("ProjectGroups-1"), LifecycleId = Id("Lifecycles-1"), VariableSetId = Id("variableset-Projects-1"), DeploymentProcessId = Id("deploymentprocess-Projects-1"), IncludedLibraryVariableSetIds = Array.Empty<string>() }),
            Doc(Id("Channels-1"), "Channel", new { Id = Id("Channels-1"), Name = "Default", ProjectId = Id("Projects-1"), LifecycleId = (string)null, IsDefault = true, Slug = "default" }),
            Doc(Id("variableset-Projects-1"), "ProjectVariables", new { Id = Id("variableset-Projects-1"), OwnerId = Id("Projects-1"), OwnerType = "Project", Variables = new object[] { new { Id = Guid.NewGuid().ToString(), Name = "AppName", Value = "web", Scope = new Dictionary<string, string[]>() } } }),
            Doc(Id("deploymentprocess-Projects-1"), "DeploymentProcess", new { Id = Id("deploymentprocess-Projects-1"), OwnerId = Id("Projects-1"), Steps = new[] { step } })
        ];
    }

    private static (string Id, string DocumentType, string DocumentSource, string Json) Doc(string id, string documentType, object document)
        => (id, documentType, $"{id}.json", JsonSerializer.Serialize(document));

    private static string WriteArchive(IReadOnlyList<(string Id, string DocumentType, string DocumentSource, string Json)> documents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-k8s-{Guid.NewGuid():N}.zip");
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", d.DocumentSource, ParentId = (string)null, Hash = Sha1(d.Json) })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        WriteEntry(archive, "manifest.json", manifest);

        foreach (var document in documents)
            WriteEntry(archive, document.DocumentSource, document.Json);

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
