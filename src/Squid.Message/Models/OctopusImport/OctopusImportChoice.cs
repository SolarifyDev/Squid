using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>The policy's verdict that the user decides whether a resource is imported: why, and what happens when nobody decides.</summary>
public sealed record OctopusImportChoice(OctopusImportChoiceReason Reason, bool DefaultSelected);
