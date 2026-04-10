using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;

/// <summary>
/// Thrown when an <see cref="IIntentRenderer"/> fails to translate a valid, supported
/// intent into a <c>ScriptExecutionRequest</c> due to an error (e.g. missing required
/// context, invalid asset, etc.). This wraps the original exception with intent metadata.
/// </summary>
public sealed class IntentRenderingException : InvalidOperationException
{
    public CommunicationStyle CommunicationStyle { get; }
    public string IntentName { get; }

    public IntentRenderingException(
        CommunicationStyle communicationStyle,
        ExecutionIntent intent,
        string message,
        Exception? innerException = null)
        : base($"Failed to render intent '{intent.Name}' for transport '{communicationStyle}': {message}", innerException)
    {
        CommunicationStyle = communicationStyle;
        IntentName = intent.Name;
    }
}
