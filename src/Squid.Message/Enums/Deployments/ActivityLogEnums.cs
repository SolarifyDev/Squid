namespace Squid.Message.Enums.Deployments;

public enum DeploymentActivityLogNodeType
{
    Task,
    Step,
    Action,
    LogEntry
}

public enum DeploymentActivityLogNodeStatus
{
    Pending,
    Running,
    Success,
    Failed
}

public enum DeploymentActivityLogCategory
{
    Info,
    Warning,
    Error
}

public enum ServerTaskLogCategory
{
    Info,
    Warning,
    Error
}
