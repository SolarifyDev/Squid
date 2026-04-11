namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent for an OpenClaw gateway invocation. The gateway offers several semantically
/// distinct operations (Wake, Assert, InvokeTool, RunAgent, etc.); a single intent type
/// with a <see cref="Kind"/> discriminator keeps all variants under one renderer surface
/// without proliferating parallel intent classes.
/// <para>
/// Renderers consume <see cref="Kind"/> to pick the HTTP route and
/// <see cref="Parameters"/> (keyed by the raw <c>Squid.Action.OpenClaw.*</c> property
/// names) for the request body. Parameters are empty for kinds that carry no input
/// (<see cref="OpenClawInvocationKind.Assert"/>, <see cref="OpenClawInvocationKind.FetchResult"/>).
/// </para>
/// </summary>
public sealed record OpenClawInvokeIntent : ExecutionIntent
{
    /// <summary>Which OpenClaw invocation flavour this intent represents.</summary>
    public required OpenClawInvocationKind Kind { get; init; }

    /// <summary>
    /// Raw OpenClaw action parameters the renderer needs to construct the gateway call.
    /// Keys are the legacy <c>Squid.Action.OpenClaw.*</c> property names; values are the
    /// already-expanded property values. Missing / empty properties are omitted so
    /// renderers can distinguish "not set" from "explicitly empty".
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>Discriminator for <see cref="OpenClawInvokeIntent"/>.</summary>
public enum OpenClawInvocationKind
{
    /// <summary>Wake an OpenClaw session with a text prompt.</summary>
    Wake,

    /// <summary>Assert a condition against the gateway's last result.</summary>
    Assert,

    /// <summary>Invoke a chat completion against the gateway.</summary>
    ChatCompletion,

    /// <summary>Fetch the latest session result from the gateway.</summary>
    FetchResult,

    /// <summary>Invoke a specific tool on the gateway.</summary>
    InvokeTool,

    /// <summary>Run a named agent on the gateway.</summary>
    RunAgent,

    /// <summary>Wait for a gateway session to reach a terminal state.</summary>
    WaitSession
}
