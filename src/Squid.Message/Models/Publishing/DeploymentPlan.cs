namespace Squid.Message.Models.Publishing;

/// <summary>
/// What a recipe wants a published package to be deployed with: an ordered list of steps made of existing action types, and the inputs the person
/// publishing has to give. It is data only: turning it into a project and a deployment process is the template applier's job.
/// </summary>
public class DeploymentPlan
{
    public List<PlanStep> Steps { get; set; } = new();

    public List<PlanInput> Inputs { get; set; } = new();
}

public class PlanStep
{
    public string Name { get; set; }

    public List<string> TargetRoles { get; set; } = new();

    public List<PlanAction> Actions { get; set; } = new();
}

public class PlanAction
{
    public string Name { get; set; }

    public string ActionType { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new();

    /// <summary>Set when the action deploys the published package: the applier adds the built-in feed and the release selects the version.</summary>
    public PlanPackageReference Package { get; set; }
}

public class PlanPackageReference
{
    public string PackageId { get; set; }
}

/// <summary>
/// A value the person publishing has to give for the environment they publish to. Publishing stores it as a project variable scoped to that environment
/// (<see cref="PlanInputVariables.Name"/>), which the plan's scripts read at deploy time, so one process serves every environment with its own value.
/// </summary>
public class PlanInput
{
    public string Name { get; set; }

    public string Description { get; set; }

    public PlanInputType Type { get; set; }
}

public enum PlanInputType
{
    TcpPort = 1
}

/// <summary>The names recipes declare their inputs under.</summary>
public static class PlanInputNames
{
    public const string HostPort = "hostPort";
}

/// <summary>The project variable an input is stored in.</summary>
public static class PlanInputVariables
{
    public const string Prefix = "Squid.Publish.Input.";

    public static string Name(string input) => Prefix + input;
}
