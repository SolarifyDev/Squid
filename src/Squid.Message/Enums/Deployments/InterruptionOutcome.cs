namespace Squid.Message.Enums.Deployments;

public enum InterruptionOutcome
{
    Pending = 0,
    Retry = 1,
    Skip = 2,
    Abort = 3,
    Proceed = 4
}
