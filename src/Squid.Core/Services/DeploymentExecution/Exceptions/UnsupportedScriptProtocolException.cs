namespace Squid.Core.Services.DeploymentExecution.Exceptions;

/// <summary>
/// Thrown when a script was dispatched with a script-service protocol version this server does not
/// implement (an in-flight record written by a newer server). The server must not fall back to another
/// dialect: re-attaching to a script through a protocol it was not started with can read "unknown ticket"
/// and run it a second time.
/// </summary>
public class UnsupportedScriptProtocolException : InvalidOperationException
{
    public string Protocol { get; }

    public UnsupportedScriptProtocolException(string protocol)
        : base($"The script was dispatched with script-service protocol '{protocol}', which this server does not support. Upgrade the server to resume it; it was not re-dispatched to avoid running it twice.")
    {
        Protocol = protocol;
    }
}
