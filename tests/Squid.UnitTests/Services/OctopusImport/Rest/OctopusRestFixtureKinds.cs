using System.Linq;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>Which Octopus resource kind each <see cref="OctopusServer2026Fixtures"/> file holds, and the project it was fetched under.</summary>
internal static class OctopusRestFixtureKinds
{
    public static IReadOnlyList<(string Fixture, OctopusResourceKind Kind)> All { get; } =
    [
        ("projectgroups-all", OctopusResourceKind.ProjectGroup),
        ("environments-all", OctopusResourceKind.Environment),
        ("lifecycles-all", OctopusResourceKind.Lifecycle),
        ("feeds-all", OctopusResourceKind.Feed),
        ("accounts-all", OctopusResourceKind.Account),
        ("teams-all", OctopusResourceKind.Team),
        ("libraryvariablesets-all", OctopusResourceKind.LibraryVariableSet),
        ("variables-variableset-LibraryVariableSets-1", OctopusResourceKind.VariableSet),
        ("variables-variableset-LibraryVariableSets-2", OctopusResourceKind.VariableSet),
        ("actiontemplates-all", OctopusResourceKind.ActionTemplate),
        ("machines-all", OctopusResourceKind.Machine),
        ("certificates-all", OctopusResourceKind.Certificate),
        ("workerpools-all", OctopusResourceKind.WorkerPool),
        ("tenants-all", OctopusResourceKind.Tenant),
        ("projects-all", OctopusResourceKind.Project),
        ("projects-Projects-1-channels", OctopusResourceKind.Channel),
        ("projects-Projects-2-channels", OctopusResourceKind.Channel),
        ("projects-Projects-1-deploymentprocesses", OctopusResourceKind.DeploymentProcess),
        ("projects-Projects-2-deploymentprocesses", OctopusResourceKind.DeploymentProcess),
        ("projects-Projects-1-deploymentsettings", OctopusResourceKind.DeploymentSettings),
        ("projects-Projects-2-deploymentsettings", OctopusResourceKind.DeploymentSettings),
        ("projects-Projects-1-runbooks", OctopusResourceKind.Runbook),
        ("projects-Projects-1-triggers", OctopusResourceKind.Trigger),
        ("variables-variableset-Projects-1", OctopusResourceKind.VariableSet),
        ("variables-variableset-Projects-2", OctopusResourceKind.VariableSet),
        ("projects-Projects-1-releases", OctopusResourceKind.Release),
        ("deployments-projects-Projects-1", OctopusResourceKind.Deployment),
        ("tasks-ServerTasks-1", OctopusResourceKind.ServerTask),
        ("deploymentprocesses-deploymentprocess-Projects-1-s-4-ABCDE", OctopusResourceKind.DeploymentProcessSnapshot)
    ];

    /// <summary>The project a per-project fixture was fetched under (<c>projects-Projects-1-…</c>), else null.</summary>
    public static string OwnerOf(string fixture)
        => fixture.StartsWith("projects-Projects-", StringComparison.Ordinal) ? string.Join("-", fixture.Split('-').Skip(1).Take(2)) : null;
}
