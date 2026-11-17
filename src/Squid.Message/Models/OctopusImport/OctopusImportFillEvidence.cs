using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>The installation evidence of one import: its state, how many structure ids matched, and what proved it (for messages).</summary>
public sealed record OctopusImportFillEvidence(OctopusImportFillEvidenceState State, int MatchingStructureIds, string Basis);
