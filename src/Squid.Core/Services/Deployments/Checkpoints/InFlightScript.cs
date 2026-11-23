using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// One recorded in-flight agent script: the dispatch unit, its ticket and the protocol version it was dispatched with.
/// <see cref="Protocol"/> is <see langword="null"/> when the record names a version this server does not know — the script lives in a dialect
/// this server cannot speak, so callers must neither guess V1 nor drop the entry silently.
/// </summary>
public sealed record InFlightScript(DispatchSlot Slot, string Ticket, ScriptProtocolVersion? Protocol);
