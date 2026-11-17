using System.Text.Json;

namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// The stub's "Server project" (Projects-1) and a clone of it (Projects-2), in both source shapes (REST as the API serves them, and export
/// documents). Octopus copies step, action and variable ids verbatim when it clones a project, so both projects carry the same ids; only the
/// project, its channel, process and variable set get new ones. Each project has one script step with one action, a plain variable and a
/// variable scoped to its action, which the action's script prints.
/// </summary>
public static class OctopusClonedProjectResources
{
    public const string CloneProjectId = "Projects-2";
    public const string CloneProjectName = "Server project clone";
    public const string StepId = "c10e5e00-0000-4000-8000-000000000001";
    public const string ActionId = "c10e5e00-0000-4000-8000-000000000002";
    public const string VariableId = "c10e5e00-0000-4000-8000-000000000003";
    public const string ActionScopedVariableId = "c10e5e00-0000-4000-8000-000000000004";
    public const string ActionScopedVariableName = "Action.Only";

    public static readonly string[] ProjectIds = ["Projects-1", CloneProjectId];

    public static StubOctopusServer WithProjectAndClone(this StubOctopusServer octopus)
    {
        ((List<Dictionary<string, object>>)octopus.Resources["projects"]).Add(new() { ["Id"] = CloneProjectId, ["Name"] = CloneProjectName, ["Slug"] = "server-project-clone", ["ProjectGroupId"] = "ProjectGroups-1", ["LifecycleId"] = "Lifecycles-1", ["VariableSetId"] = $"variableset-{CloneProjectId}", ["DeploymentProcessId"] = $"deploymentprocess-{CloneProjectId}" });
        octopus.Resources[$"projects/{CloneProjectId}/channels"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Channels-2", ["Name"] = "Default", ["Slug"] = "default", ["ProjectId"] = CloneProjectId, ["IsDefault"] = true } };

        foreach (var projectId in ProjectIds)
        {
            octopus.Resources[$"projects/{projectId}/deploymentprocesses"] = new Dictionary<string, object> { ["Id"] = $"deploymentprocess-{projectId}", ["ProjectId"] = projectId, ["Steps"] = new[] { Step() } };
            octopus.Resources[$"projects/{projectId}/variables"] = new Dictionary<string, object> { ["Id"] = $"variableset-{projectId}", ["OwnerId"] = projectId, ["Variables"] = Variables(projectId) };
        }

        return octopus;
    }

    /// <summary>The same two projects as an export file carries them; Projects-1's process and variable set replace the base export's empty ones.</summary>
    public static IEnumerable<(string Id, string DocumentType, string Json)> ProjectAndCloneDocuments() =>
    [
        (CloneProjectId, "Project", $$"""{"Id":"{{CloneProjectId}}","Name":"{{CloneProjectName}}","Slug":"server-project-clone","ProjectGroupId":"ProjectGroups-1","LifecycleId":"Lifecycles-1","VariableSetId":"variableset-{{CloneProjectId}}","DeploymentProcessId":"deploymentprocess-{{CloneProjectId}}"}"""),
        ("Channels-2", "Channel", $$"""{"Id":"Channels-2","Name":"Default","Slug":"default","ProjectId":"{{CloneProjectId}}","IsDefault":true}"""),
        .. ProjectIds.SelectMany(ProcessAndVariableDocuments)
    ];

    private static IEnumerable<(string Id, string DocumentType, string Json)> ProcessAndVariableDocuments(string projectId) =>
    [
        ($"deploymentprocess-{projectId}", "DeploymentProcess", JsonSerializer.Serialize(new { Id = $"deploymentprocess-{projectId}", OwnerId = projectId, Steps = new[] { Step() } })),
        ($"variableset-{projectId}", "ProjectVariables", JsonSerializer.Serialize(new { Id = $"variableset-{projectId}", OwnerId = projectId, OwnerType = "Project", Variables = Variables(projectId) }))
    ];

    public const string ActionSecretVariableId = "c10e5e00-0000-4000-8000-000000000005";
    public const string ActionSecretName = "Action.Password";
    public const string ActionSecretValue = "action-secret";

    /// <summary>Projects-1 alone (no clone) with the cloned project's step and a sensitive variable scoped to its action, withheld by the API.</summary>
    public static StubOctopusServer WithActionScopedSecret(this StubOctopusServer octopus)
    {
        octopus.Resources["projects/Projects-1/deploymentprocesses"] = new Dictionary<string, object> { ["Id"] = "deploymentprocess-Projects-1", ["ProjectId"] = "Projects-1", ["Steps"] = new[] { Step() } };
        octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = new[] { new { Id = ActionSecretVariableId, Name = ActionSecretName, Value = (string)null, Type = "Sensitive", IsSensitive = true, IsEditable = true, Prompt = (object)null, Scope = new Dictionary<string, string[]> { ["Action"] = [ActionId] } } } };

        return octopus;
    }

    /// <summary>The same as the export file of that installation carries it: the value encrypted with the export password.</summary>
    public static IEnumerable<(string Id, string DocumentType, string Json)> ActionScopedSecretDocuments(string exportPassword) =>
    [
        ("deploymentprocess-Projects-1", "DeploymentProcess", JsonSerializer.Serialize(new { Id = "deploymentprocess-Projects-1", OwnerId = "Projects-1", Steps = new[] { Step() } })),
        ("variableset-Projects-1", "ProjectVariables", JsonSerializer.Serialize(new { Id = "variableset-Projects-1", OwnerId = "Projects-1", OwnerType = "Project", Variables = new[] { new { Id = ActionSecretVariableId, Name = ActionSecretName, Type = "Sensitive", IsSensitive = true, Value = OctopusWithheldSecretResources.EncryptForExport(ActionSecretValue, exportPassword), Scope = new Dictionary<string, string[]> { ["Action"] = [ActionId] } } } }))
    ];

    /// <summary>Projects-1 alone with the cloned project's step, action and variables (an import from before the clone existed).</summary>
    public static StubOctopusServer WithOriginalOnly(this StubOctopusServer octopus)
    {
        octopus.Resources["projects/Projects-1/deploymentprocesses"] = new Dictionary<string, object> { ["Id"] = "deploymentprocess-Projects-1", ["ProjectId"] = "Projects-1", ["Steps"] = new[] { Step() } };
        octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = Variables("Projects-1") };

        return octopus;
    }

    /// <summary><see cref="WithOriginalOnly"/> as an export file carries it.</summary>
    public static IEnumerable<(string Id, string DocumentType, string Json)> OriginalOnlyDocuments() => ProcessAndVariableDocuments("Projects-1");

    private static object Step()
        => new
        {
            Id = StepId, Name = "Deploy", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(),
            Actions = new[] { new { Id = ActionId, Name = "Run", ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = $"echo #{{{ActionScopedVariableName}}}", ["Octopus.Action.RunOnServer"] = "true" } } }
        };

    // The clone's values differ from the original's, so a test can tell which project's row a value landed in.
    private static object[] Variables(string projectId)
        =>
        [
            new { Id = VariableId, Name = "Clone.Url", Value = $"https://{projectId}.example.com", Type = "String", Scope = new Dictionary<string, string[]>() },
            new { Id = ActionScopedVariableId, Name = ActionScopedVariableName, Value = $"only-in-{projectId}", Type = "String", Scope = new Dictionary<string, string[]> { ["Action"] = [ActionId] } }
        ];
}
