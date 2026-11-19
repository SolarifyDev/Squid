using System.Net;

namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// "Api" (Projects-2 of <see cref="OctopusProjectSelectionResources"/>) converted to Octopus Config as Code: its project document is
/// version-controlled (git url and credentials included, as Octopus serves them) and, like a real server, everything below
/// <c>projects/Projects-2/</c> answers 400. Its history stays on the stub, so any request for it would show in the request log.
/// </summary>
public static class OctopusConfigAsCodeResources
{
    public const string GitUrl = "https://git.example.com/acme/api.git";
    public const string GitUsername = "git-user";

    private static readonly string[] ProjectChildren = ["deploymentprocesses", "deploymentsettings", "variables", "channels", "runbooks", "triggers", "releases"];

    public static StubOctopusServer WithApiAsConfigAsCode(this StubOctopusServer octopus)
    {
        var api = ((List<Dictionary<string, object>>)octopus.Resources["projects"]).Single(p => (string)p["Id"] == OctopusProjectSelectionResources.ApiProjectId);

        api["IsVersionControlled"] = true;
        api["PersistenceSettings"] = new Dictionary<string, object> { ["Type"] = "VersionControlled", ["Url"] = GitUrl, ["DefaultBranch"] = "main", ["BasePath"] = ".octopus", ["Credentials"] = new Dictionary<string, object> { ["Type"] = "UsernamePassword", ["Username"] = GitUsername, ["Password"] = new Dictionary<string, object> { ["HasValue"] = true, ["NewValue"] = null, ["Hint"] = null } } };

        foreach (var child in ProjectChildren) octopus.FailNext($"/projects/{OctopusProjectSelectionResources.ApiProjectId}/{child}", 1000, HttpStatusCode.BadRequest);

        return octopus;
    }

    /// <summary>The same project as an export file carries it: the project and its variables, no process (it lives in git).</summary>
    public static IEnumerable<(string Id, string DocumentType, string Json)> ConfigAsCodeApiDocuments() =>
    [
        .. OctopusProjectSelectionResources.TwoMoreProjectDocuments().Where(d => d.Id != OctopusProjectSelectionResources.ApiProjectId && d.Id != $"deploymentprocess-{OctopusProjectSelectionResources.ApiProjectId}"),
        (OctopusProjectSelectionResources.ApiProjectId, "Project", """{"Id":"Projects-2","Name":"Api","Slug":"api","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-2","VariableSetId":"variableset-Projects-2","DeploymentProcessId":"deploymentprocess-Projects-2","IncludedLibraryVariableSetIds":["LibraryVariableSets-2"],"IsVersionControlled":true,"PersistenceSettings":{"Type":"VersionControlled","Url":"https://git.example.com/acme/api.git","DefaultBranch":"main","BasePath":".octopus","Credentials":{"Type":"Anonymous"}}}""")
    ];
}
