using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

/// <summary>
/// Shared builder for <see cref="OpenClawInvokeIntent"/>. Each OpenClaw action handler
/// routes its <c>DescribeIntentAsync</c> override through this factory so that the
/// property-pass-through rules (copy all <c>Squid.Action.OpenClaw.*</c> properties, omit
/// missing/empty values) live in exactly one place.
/// </summary>
internal static class OpenClawIntentFactory
{
    private const string OpenClawPropertyPrefix = "Squid.Action.OpenClaw.";

    public static OpenClawInvokeIntent Build(ActionExecutionContext ctx, OpenClawInvocationKind kind, string semanticName)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return new OpenClawInvokeIntent
        {
            Name = semanticName,
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            Kind = kind,
            Parameters = ReadParameters(ctx.Action)
        };
    }

    private static IReadOnlyDictionary<string, string> ReadParameters(DeploymentActionDto action)
    {
        if (action?.Properties == null || action.Properties.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var prop in action.Properties)
        {
            if (prop?.PropertyName == null) continue;
            if (!prop.PropertyName.StartsWith(OpenClawPropertyPrefix, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(prop.PropertyValue)) continue;

            result[prop.PropertyName] = prop.PropertyValue;
        }

        return result;
    }
}
