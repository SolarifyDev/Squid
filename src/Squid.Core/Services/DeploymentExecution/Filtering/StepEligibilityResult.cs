namespace Squid.Core.Services.DeploymentExecution.Filtering;

public enum StepSkipReason
{
    None,
    Disabled,
    SuccessConditionNotMet,
    FailureConditionNotMet,
    VariableConditionFalse,
    RoleMismatch
}

public enum ActionSkipReason
{
    None,
    Disabled,
    EnvironmentMismatch,
    ChannelMismatch,
    ManuallySkipped
}

public readonly record struct StepEligibilityResult(bool ShouldExecute, StepSkipReason SkipReason, string Message);

public readonly record struct ActionEligibilityResult(bool ShouldExecute, ActionSkipReason SkipReason, string Message);
