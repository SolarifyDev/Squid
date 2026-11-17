using Squid.Message.Models.Deployments.LifeCycle;

namespace Squid.Message.Models.OctopusImport;

/// <summary>One Octopus lifecycle phase mapped to the Squid phase model, with the diagnostics of that phase only.</summary>
public sealed record OctopusImportLifecyclePhaseMappingResult(LifecyclePhaseModel Phase, IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics);
