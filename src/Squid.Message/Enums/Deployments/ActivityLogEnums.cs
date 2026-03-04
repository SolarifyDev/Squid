namespace Squid.Message.Enums.Deployments;

public enum DeploymentActivityLogNodeType
{
    Task = 0,
    Step = 1,
    Action = 2,
    LogEntry = 3
}

public enum DeploymentActivityLogNodeStatus
{
    Pending = 0,
    Running = 1,
    Success = 2,
    Failed = 3
}

public enum DeploymentActivityLogCategory
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public enum ServerTaskLogCategory
{
    Info = 0,
    Warning = 1,
    Error = 2
}
