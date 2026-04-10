using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Validates whether an <see cref="ExecutionIntent"/> can be satisfied by a target's
/// <see cref="ITransportCapabilities"/>. The validator is non-throwing — it accumulates
/// every failure into a list so the preview UI can display them all at once. The
/// executor (Phase 6 onwards) converts a non-empty list into a
/// <c>DeploymentPlanValidationException</c>.
///
/// <para>
/// Phase 5.5 introduces this service as a pure function with no pipeline integration.
/// Phase 6 wires it into <c>DeploymentPlanner</c> so both Preview and Execute share the
/// same validation surface.
/// </para>
/// </summary>
public interface ICapabilityValidator : IScopedDependency
{
    /// <summary>
    /// Returns every violation detected for <paramref name="intent"/> against
    /// <paramref name="capabilities"/>. An empty list means the intent is fully supported.
    /// </summary>
    /// <param name="intent">The semantic intent produced by a handler or the legacy adapter.</param>
    /// <param name="capabilities">The target transport's capability declaration.</param>
    /// <param name="communicationStyle">The transport the capabilities belong to, for violation tagging.</param>
    /// <param name="actionType">
    /// Optional legacy action type (e.g. <c>Squid.Script</c>). When supplied and the
    /// transport declares a non-empty <see cref="ITransportCapabilities.SupportedActionTypes"/>,
    /// the validator emits <see cref="ViolationCodes.UnsupportedActionType"/> if the action
    /// type is missing from that set. Pass <c>null</c> for intents that don't map to a
    /// legacy action type (Phase 9+ native intents).
    /// </param>
    IReadOnlyList<CapabilityViolation> Validate(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        string? actionType = null);
}
