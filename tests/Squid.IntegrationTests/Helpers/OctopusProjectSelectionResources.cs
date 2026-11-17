namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// Two more Octopus projects beside the stub's "Server project" (Projects-1), in both source shapes (REST as the API serves them, and
/// export documents). "Api" (Projects-2) deploys through its own lifecycle to its own environment and includes a library only it uses,
/// and has one deployment; "Worker" (Projects-3) shares Projects-1's lifecycle. Choosing one project must import it and what it uses —
/// and nothing only the others use.
/// </summary>
public static class OctopusProjectSelectionResources
{
    public const string ApiProjectId = "Projects-2";
    public const string ApiProjectName = "Api";
    public const string WorkerProjectId = "Projects-3";
    public const string WorkerProjectName = "Worker";
    public const string ApiLibraryName = "Api library";
    public const string ApiEnvironmentName = "Server PROD";
    public const string ApiLifecycleName = "Api lifecycle";

    public static StubOctopusServer WithTwoMoreProjects(this StubOctopusServer octopus)
    {
        octopus.Resources["environments"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Environments-1", ["Name"] = "Server TEST", ["Slug"] = "server-test" }, new() { ["Id"] = "Environments-2", ["Name"] = ApiEnvironmentName, ["Slug"] = "server-prod" } };
        ((List<Dictionary<string, object>>)octopus.Resources["lifecycles"]).Add(new() { ["Id"] = "Lifecycles-2", ["Name"] = ApiLifecycleName, ["Slug"] = "api-lifecycle", ["Phases"] = new[] { new Dictionary<string, object> { ["Id"] = "Phases-2", ["Name"] = "PROD", ["AutomaticDeploymentTargets"] = new[] { "Environments-2" } } } });
        octopus.Resources["libraryvariablesets"] = new List<Dictionary<string, object>> { new() { ["Id"] = "LibraryVariableSets-2", ["Name"] = ApiLibraryName, ["VariableSetId"] = "variableset-LibraryVariableSets-2", ["ContentType"] = "Variables" } };
        octopus.Resources["variables/variableset-LibraryVariableSets-2"] = new Dictionary<string, object> { ["Id"] = "variableset-LibraryVariableSets-2", ["OwnerId"] = "LibraryVariableSets-2", ["Variables"] = new[] { new { Id = "5e7d1c20-0000-4000-8000-000000000002", Name = "Api.Url", Value = "https://api.example.com", Type = "String" } } };

        AddProject(octopus, ApiProjectId, ApiProjectName, "Lifecycles-2", ["LibraryVariableSets-2"]);
        AddProject(octopus, WorkerProjectId, WorkerProjectName, "Lifecycles-1", []);
        AddApiDeployment(octopus);

        return octopus;
    }

    private static void AddProject(StubOctopusServer octopus, string id, string name, string lifecycleId, string[] libraryIds)
    {
        ((List<Dictionary<string, object>>)octopus.Resources["projects"]).Add(new() { ["Id"] = id, ["Name"] = name, ["Slug"] = name.ToLowerInvariant(), ["ProjectGroupId"] = "ProjectGroups-1", ["LifecycleId"] = lifecycleId, ["VariableSetId"] = $"variableset-{id}", ["DeploymentProcessId"] = $"deploymentprocess-{id}", ["IncludedLibraryVariableSetIds"] = libraryIds });
        octopus.Resources[$"projects/{id}/channels"] = new List<Dictionary<string, object>> { new() { ["Id"] = ChannelOf(id), ["Name"] = "Default", ["Slug"] = "default", ["ProjectId"] = id, ["IsDefault"] = true } };
        octopus.Resources[$"projects/{id}/deploymentprocesses"] = new Dictionary<string, object> { ["Id"] = $"deploymentprocess-{id}", ["ProjectId"] = id, ["Steps"] = Array.Empty<object>() };
        octopus.Resources[$"projects/{id}/variables"] = new Dictionary<string, object> { ["Id"] = $"variableset-{id}", ["OwnerId"] = id, ["Variables"] = Array.Empty<object>() };
    }

    private static void AddApiDeployment(StubOctopusServer octopus)
    {
        octopus.Releases[ApiProjectId] = [new() { ["Id"] = "Releases-2", ["ProjectId"] = ApiProjectId, ["ChannelId"] = ChannelOf(ApiProjectId), ["Version"] = "2.0.0", ["ReleaseNotes"] = "api notes", ["SelectedPackages"] = Array.Empty<object>() }];
        octopus.Deployments[ApiProjectId] = [new() { ["Id"] = "Deployments-2", ["ProjectId"] = ApiProjectId, ["ChannelId"] = ChannelOf(ApiProjectId), ["EnvironmentId"] = "Environments-2", ["ReleaseId"] = "Releases-2", ["TaskId"] = "ServerTasks-2", ["Name"] = "Deploy Deployments-2", ["DeployedToMachineIds"] = new[] { "Machines-1" }, ["Created"] = "2024-02-02T03:04:05Z" }];
        octopus.Tasks["ServerTasks-2"] = new() { ["Id"] = "ServerTasks-2", ["Name"] = "Deploy", ["Description"] = "Deploy Deployments-2", ["State"] = "Success", ["QueueTime"] = "2024-02-02T03:04:05Z", ["StartTime"] = "2024-02-02T03:04:06Z", ["CompletedTime"] = "2024-02-02T03:05:06Z", ["HasWarningsOrErrors"] = false };
    }

    /// <summary>Two token accounts named by value (a variable's value, as an account-bound step stores it): "Server token" by Projects-1, "Api token" by Api.</summary>
    public static StubOctopusServer WithAccountsNamedByValue(this StubOctopusServer octopus)
    {
        octopus.Resources["accounts"] = new List<Dictionary<string, object>> { Account("Accounts-1", ServerAccountName), Account("Accounts-2", ApiAccountName) };
        octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = new[] { AccountVariable("5e7d1c20-0000-4000-8000-0000000000a1", "Accounts-1") } };
        octopus.Resources[$"projects/{ApiProjectId}/variables"] = new Dictionary<string, object> { ["Id"] = $"variableset-{ApiProjectId}", ["OwnerId"] = ApiProjectId, ["Variables"] = new[] { AccountVariable("5e7d1c20-0000-4000-8000-0000000000a2", "Accounts-2") } };

        return octopus;
    }

    public const string ServerAccountName = "Server token";
    public const string ApiAccountName = "Api token";

    private static Dictionary<string, object> Account(string id, string name)
        => new() { ["Id"] = id, ["Name"] = name, ["Slug"] = name.ToLowerInvariant().Replace(' ', '-'), ["AccountType"] = "Token", ["EnvironmentIds"] = Array.Empty<string>(), ["Token"] = new { HasValue = false } };

    private static object AccountVariable(string id, string accountId) => new { Id = id, Name = "Cluster.Account", Value = accountId, Type = "String" };

    public static string ChannelOf(string projectId) => $"Channels-{projectId["Projects-".Length..]}";

    /// <summary>The same two projects as an export file carries them (beside the base export's Projects-1).</summary>
    public static IEnumerable<(string Id, string DocumentType, string Json)> TwoMoreProjectDocuments() =>
    [
        ("Environments-2", "StaticDeploymentEnvironment", """{"Id":"Environments-2","Name":"Server PROD","Slug":"server-prod"}"""),
        ("Lifecycles-2", "Lifecycle", """{"Id":"Lifecycles-2","Name":"Api lifecycle","Slug":"api-lifecycle","Phases":[{"Id":"Phases-2","Name":"PROD","AutomaticDeploymentTargets":["Environments-2"]}]}"""),
        ("LibraryVariableSets-2", "LibraryVariableSet", """{"Id":"LibraryVariableSets-2","Name":"Api library","VariableSetId":"variableset-LibraryVariableSets-2","ContentType":"Variables"}"""),
        ("variableset-LibraryVariableSets-2", "LibraryVariables", """{"Id":"variableset-LibraryVariableSets-2","OwnerId":"LibraryVariableSets-2","OwnerType":"LibraryVariableSet","Variables":[{"Id":"5e7d1c20-0000-4000-8000-000000000002","Name":"Api.Url","Value":"https://api.example.com","Type":"String"}]}"""),
        .. ProjectDocuments(ApiProjectId, ApiProjectName, "Lifecycles-2", """["LibraryVariableSets-2"]"""),
        .. ProjectDocuments(WorkerProjectId, WorkerProjectName, "Lifecycles-1", "[]")
    ];

    private static IEnumerable<(string Id, string DocumentType, string Json)> ProjectDocuments(string id, string name, string lifecycleId, string libraryIds) =>
    [
        (id, "Project", $$"""{"Id":"{{id}}","Name":"{{name}}","Slug":"{{name.ToLowerInvariant()}}","ProjectGroupId":"ProjectGroups-1","LifecycleId":"{{lifecycleId}}","VariableSetId":"variableset-{{id}}","DeploymentProcessId":"deploymentprocess-{{id}}","IncludedLibraryVariableSetIds":{{libraryIds}}}"""),
        ($"variableset-{id}", "ProjectVariables", $$"""{"Id":"variableset-{{id}}","OwnerId":"{{id}}","OwnerType":"Project","Variables":[]}"""),
        ($"deploymentprocess-{id}", "DeploymentProcess", $$"""{"Id":"deploymentprocess-{{id}}","OwnerId":"{{id}}","Steps":[]}"""),
        (ChannelOf(id), "Channel", $$"""{"Id":"{{ChannelOf(id)}}","Name":"Default","Slug":"default","ProjectId":"{{id}}","IsDefault":true}""")
    ];
}
