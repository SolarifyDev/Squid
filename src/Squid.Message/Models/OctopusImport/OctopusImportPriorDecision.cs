using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// What an earlier confirmed import decides for one resource: the preview action, the prior destination
/// it maps to (null when the resource has no row of its own) and an optional diagnostic to surface. A decision with a
/// <see cref="Choice"/> is the user's to make: <see cref="Action"/> is what happens when the resource is selected.
/// </summary>
public sealed record OctopusImportPriorDecision(OctopusImportPreviewAction Action, int? DestinationId, OctopusImportDiagnosticDto Diagnostic = null, OctopusImportChoice Choice = null);
