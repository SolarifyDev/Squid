namespace Squid.Message.Models.Deployments.Deployment;

public class DeploymentPreviewResult
{
    public bool CanDeploy { get; set; }

    public List<string> BlockingReasons { get; set; } = [];

    public int AvailableMachineCount { get; set; }

    public int? LifecycleId { get; set; }

    public List<int> AllowedEnvironmentIds { get; set; } = [];

    public List<DeploymentPreviewTargetResult> CandidateTargets { get; set; } = [];

    /// <summary>
    /// Targets that matched the environment but were excluded by the project's Transient
    /// Deployment Targets policy (unavailable / unhealthy) — the same exclusion the real
    /// deployment applies. Surfaced so the UI can explain why the available count is lower
    /// than the number of machines in the environment.
    /// </summary>
    public List<DeploymentPreviewTargetResult> ExcludedTargets { get; set; } = [];

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

    /// <summary>Machine health at preview time (e.g. Healthy / Unavailable / Unhealthy / Unknown).</summary>
    public string HealthStatus { get; set; } = string.Empty;
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
