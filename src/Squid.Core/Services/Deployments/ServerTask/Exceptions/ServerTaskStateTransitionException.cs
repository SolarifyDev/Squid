namespace Squid.Core.Services.Deployments.ServerTask.Exceptions;

public class ServerTaskStateTransitionException : InvalidOperationException
{
    public string FromState { get; }

    public string ToState { get; }

    public ServerTaskStateTransitionException(string from, string to)
        : base($"Invalid server task state transition from '{from}' to '{to}'")
    {
        FromState = from;
        ToState = to;
    }
}
