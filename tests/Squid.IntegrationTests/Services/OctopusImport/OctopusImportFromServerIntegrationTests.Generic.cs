using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Data shapes real Octopus servers produce that the minimal fixtures never do: random GUID phase ids, variables scoped to the
/// deployment process the 2019.11+ way, and many releases of one project.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    // Octopus phase ids are random GUIDs. Each phase must be recorded against the Squid phase made from it, whatever its id sorts as.
    [Fact]
    public async Task LifecyclePhasesWithGuidIds_OutOfLexicalOrder_EachRecordedAgainstItsOwnSquidPhase()
    {
        await using var octopus = OneDeployment();
        var phases = new[] { ("f3a1c2d4-0000-4000-8000-000000000001", "TEST"), ("0b7e9a12-0000-4000-8000-000000000002", "STAGING"), ("7c4d5e6f-0000-4000-8000-000000000003", "PROD") };
        var lifecycle = ((List<Dictionary<string, object>>)octopus.Resources["lifecycles"]).Single();
        lifecycle["Phases"] = phases.Select((p, i) => new Dictionary<string, object> { ["Id"] = p.Item1, ["Name"] = p.Item2, ["AutomaticDeploymentTargets"] = i == 0 ? new[] { "Environments-1" } : Array.Empty<string>() }).ToArray();

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        var squidPhases = await Run<SquidDbContext, Dictionary<int, string>>(db => db.Set<LifecyclePhase>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name));
        var recorded = (await SourceMapAsync()).Where(r => r.SourceType == "LifecyclePhase").ToDictionary(r => r.SourceId, r => squidPhases[r.DestinationId], StringComparer.OrdinalIgnoreCase);

        recorded.Count.ShouldBe(3);

        foreach (var (id, name) in phases)
            recorded[id].ShouldBe(name, $"Octopus phase {name} ({id}) is recorded against the wrong Squid phase");
    }

    // "ProcessOwner": ["Projects-1"] is how Octopus 2019.11+ scopes a variable to the project's deployment process. It imports as the
    // Squid Process scope on the imported process, instead of blocking the whole import.
    [Fact]
    public async Task VariableScopedToTheDeploymentProcess_ProcessOwnerKey_ImportsAsTheProcessScope()
    {
        await using var octopus = OneDeployment();
        octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = new[] { new Dictionary<string, object> { ["Id"] = "5e0c1f2a-0000-4000-8000-000000000001", ["Name"] = "OnlyInDeployments", ["Value"] = "yes", ["Type"] = "String", ["IsSensitive"] = false, ["Scope"] = new Dictionary<string, object> { ["ProcessOwner"] = new[] { "Projects-1" } } } } };

        var result = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(result);
        var project = await SingleAsync<Project>();
        var scope = await Run<SquidDbContext, VariableScope>(db => db.Set<VariableScope>().AsNoTracking().Join(db.Set<Variable>().Where(v => v.Name == "OnlyInDeployments"), s => s.VariableId, v => v.Id, (s, _) => s).SingleAsync());
        (scope.ScopeType, scope.ScopeValue).ShouldBe((VariableScopeType.Process, project.DeploymentProcessId.ToString()));
    }

    // Every release of a project imported in one confirmation is created against the project's current snapshots, the same pair.
    [Fact]
    public async Task ManyReleasesOfOneProject_AllCreatedAgainstTheSameExistingSnapshots()
    {
        await using var octopus = OneDeployment();

        for (var i = 2; i <= 6; i++) octopus.Release("Projects-1", $"Releases-{i}", $"1.0.{i}");

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        var releases = await Run<SquidDbContext, List<Release>>(db => db.Set<Release>().AsNoTracking().ToListAsync());
        releases.Count.ShouldBe(6);
        releases.Select(r => (r.ProjectVariableSetSnapshotId, r.ProjectDeploymentProcessSnapshotId)).Distinct().ShouldHaveSingleItem();
        (await CountAsync<VariableSetSnapshot>()).ShouldBe(1);
        (await CountAsync<DeploymentProcessSnapshot>()).ShouldBe(1);
        (await Run<SquidDbContext, bool>(db => db.Set<VariableSetSnapshot>().AnyAsync(s => s.Id == releases[0].ProjectVariableSetSnapshotId))).ShouldBeTrue();
        (await Run<SquidDbContext, bool>(db => db.Set<DeploymentProcessSnapshot>().AnyAsync(s => s.Id == releases[0].ProjectDeploymentProcessSnapshotId))).ShouldBeTrue();
    }
}
