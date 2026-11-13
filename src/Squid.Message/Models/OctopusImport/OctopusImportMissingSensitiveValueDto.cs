using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>One secret an import wrote empty because the Octopus server withheld it. Carries names only, never a value.</summary>
public class OctopusImportMissingSensitiveValueDto
{
    public OctopusImportRequiredInputKind Kind { get; set; }

    /// <summary>Variable, feed, account or action name.</summary>
    public string Name { get; set; }

    /// <summary>The project or library variable set that owns it, when it has one.</summary>
    public string OwnerName { get; set; }

    /// <summary><c>Project</c> or <c>LibraryVariableSet</c> when <see cref="OwnerName"/> is set.</summary>
    public string OwnerKind { get; set; }

    /// <summary>The field that is empty, e.g. <c>Value</c>, <c>Password</c> or an action property name.</summary>
    public string Field { get; set; }

    /// <summary>Readable scope of a variable value, e.g. <c>Environment: Production</c>; empty when unscoped.</summary>
    public string ScopeSummary { get; set; }

    /// <summary>The Squid row that holds the empty secret (variable, feed, account or action).</summary>
    public int? DestinationId { get; set; }

    /// <summary>The Squid project or library variable set that owns that row.</summary>
    public int? DestinationOwnerId { get; set; }

    /// <summary>Importing the Octopus export file later fills this value.</summary>
    public bool Fillable { get; set; }
}
