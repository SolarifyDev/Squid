namespace Squid.Message.Models.Deployments.Deployment;

public class DeploymentPreviewResult
{
    public bool CanDeploy { get; set; }

    public List<string> BlockingReasons { get; set; } = [];

    public int AvailableMachineCount { get; set; }

    public int? LifecycleId { get; set; }

    public List<int> AllowedEnvironmentIds { get; set; } = [];

    public List<DeploymentPreviewTargetResult> CandidateTargets { get; set; } = [];

    public List<DeploymentPreviewStepResult> Steps { get; set; } = [];

    public string Message => CanDeploy
        ? "Deployment preview passed."
        : string.Join("; ", BlockingReasons);
}

public class DeploymentPreviewTargetResult
{
    public int MachineId { get; set; }

    public string MachineName { get; set; }

    public List<string> Roles { get; set; } = [];
}

public class DeploymentPreviewStepResult
{
    public int StepId { get; set; }

    public int StepOrder { get; set; }

    public string StepName { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsApplicable { get; set; }

    public bool IsStepLevelOnly { get; set; }

    public bool IsRunOnServer { get; set; }

    public string Reason { get; set; }

    public List<int> RunnableActionIds { get; set; } = [];

    public List<string> RequiredRoles { get; set; } = [];

    public List<DeploymentPreviewTargetResult> MatchedTargets { get; set; } = [];
}
