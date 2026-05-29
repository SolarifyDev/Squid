using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Single source of truth for resolving the Kubernetes target namespace of an
/// action at render time. Reads <see cref="SpecialVariables.Kubernetes.Namespace"/>
/// from the render context's effective variables and expands any <c>#{...}</c>
/// templates (e.g. <c>#{Environment}-app</c> → <c>prod-app</c>) via the context's
/// variable dictionary.
///
/// <para>This lives in the Kubernetes transport so the <i>generic</i> render pipeline
/// (<c>ExecuteStepsPhase</c>) never has to know the namespace variable name — only the
/// transports that have a namespace concept read it. Returns the raw value (which may be
/// <c>null</c> or empty) untouched when the variable is absent; callers decide the default.</para>
/// </summary>
internal static class KubernetesTargetNamespaceResolver
{
    public static string? Resolve(IntentRenderContext context)
    {
        var raw = context.EffectiveVariables
            .FirstOrDefault(v => v.Name == SpecialVariables.Kubernetes.Namespace)?.Value;

        return string.IsNullOrEmpty(raw)
            ? raw
            : VariableExpander.ExpandString(raw, context.VariableDictionary);
    }
}
