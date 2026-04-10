using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;

/// <summary>
/// Thrown by <see cref="IIntentRenderer"/> when an intent kind is not supported by
/// the resolved renderer for a given transport. Also thrown by
/// <see cref="IIntentRendererRegistry"/> when no renderer is registered for a
/// <see cref="CommunicationStyle"/> at all.
/// </summary>
public sealed class UnsupportedIntentException : InvalidOperationException
{
    public CommunicationStyle CommunicationStyle { get; }
    public string IntentName { get; }

    public UnsupportedIntentException(CommunicationStyle communicationStyle, ExecutionIntent intent)
        : base($"Transport '{communicationStyle}' does not support intent '{intent.Name}' (type {intent.GetType().Name}).")
    {
        CommunicationStyle = communicationStyle;
        IntentName = intent.Name;
    }

    public UnsupportedIntentException(CommunicationStyle communicationStyle, string message)
        : base(message)
    {
        CommunicationStyle = communicationStyle;
        IntentName = string.Empty;
    }
}
