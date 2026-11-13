namespace Squid.IntegrationTests.Helpers;

/// <summary>
/// A space whose API responses carry every kind of secret the Octopus API withholds on resources the import writes: a
/// project and a library sensitive variable, a feed password (with username), an account token and a step property.
/// </summary>
public static class OctopusWithheldSecretResources
{
    public static StubOctopusServer WithWithheldSecrets(this StubOctopusServer octopus)
    {
        var withheld = new { HasValue = true, NewValue = (string)null, Hint = (string)null };

        octopus.Resources["feeds"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Feeds-7", ["Name"] = "Private registry", ["Slug"] = "private-registry", ["FeedType"] = "Docker", ["FeedUri"] = "https://registry.example.com", ["Username"] = "robot", ["Password"] = withheld } };
        octopus.Resources["accounts"] = new List<Dictionary<string, object>> { new() { ["Id"] = "Accounts-7", ["Name"] = "Cluster token", ["Slug"] = "cluster-token", ["AccountType"] = "Token", ["EnvironmentIds"] = Array.Empty<string>(), ["Token"] = withheld } };
        octopus.Resources["libraryvariablesets"] = new List<Dictionary<string, object>> { new() { ["Id"] = "LibraryVariableSets-7", ["Name"] = "Shared mail", ["VariableSetId"] = "variableset-LibraryVariableSets-7", ["ContentType"] = "Variables" } };
        octopus.Resources["variables/variableset-LibraryVariableSets-7"] = new Dictionary<string, object> { ["Id"] = "variableset-LibraryVariableSets-7", ["OwnerId"] = "LibraryVariableSets-7", ["Variables"] = new[] { Sensitive("5b0c7d21-0000-4000-8000-000000000102", "Smtp.Password", []) } };
        octopus.Resources["projects/Projects-1/variables"] = new Dictionary<string, object> { ["Id"] = "variableset-Projects-1", ["OwnerId"] = "Projects-1", ["Variables"] = new[] { Sensitive("5b0c7d21-0000-4000-8000-000000000101", "Db.Password", ["Environments-1"]) } };
        octopus.Resources["projects/Projects-1/deploymentprocesses"] = ProcessWithWithheldProperty();

        return octopus;
    }

    private static object Sensitive(string id, string name, string[] environments)
        => new { Id = id, Name = name, Value = (string)null, Type = "Sensitive", IsSensitive = true, IsEditable = true, Prompt = (object)null, Scope = environments.Length == 0 ? new Dictionary<string, string[]>() : new Dictionary<string, string[]> { ["Environment"] = environments } };

    private static Dictionary<string, object> ProcessWithWithheldProperty()
        => new()
        {
            ["Id"] = "deploymentprocess-Projects-1",
            ["ProjectId"] = "Projects-1",
            ["Steps"] = new[]
            {
                new
                {
                    Id = "8d3c1b20-0000-4000-8000-000000000101", Name = "Notify", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, object>(),
                    Actions = new[] { new { Id = "8d3c1b20-0000-4000-8000-000000000102", Name = "Notify", ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, object> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = "echo hi", ["Octopus.Action.RunOnServer"] = "true", ["Notify.ApiKey"] = new { HasValue = true, NewValue = (string)null } } } }
                }
            }
        };
}
