namespace Squid.Core.Services.Deployments.Validation;

public enum DeploymentValidationIssueCode
{
    Unknown = 0,
    InvalidEnvironmentSelection = 1,
    MachineSelectionOverlap = 2,
    QueueTimeTooSoon = 3,
    QueueTimeTooFar = 4,
    QueueTimeExpiryBeforeQueueTime = 5,
    SkipActionNotFound = 6,
    NoRunnableActions = 7,
    AllRunnableActionsSkipped = 8,
    ProjectDisabled = 9,
    FeedNotFound = 10
}
