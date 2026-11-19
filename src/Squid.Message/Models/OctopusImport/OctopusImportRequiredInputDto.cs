using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportRequiredInputDto
{
    public string InputKey { get; set; }

    public OctopusImportRequiredInputKind Kind { get; set; }

    public string SourceId { get; set; }

    public string SourceType { get; set; }

    public string Name { get; set; }

    public string FieldName { get; set; }

    public string ValueType { get; set; }

    public bool HasSourceValue { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>A later import of the Octopus export file can supply this value (sensitive variable values and feed passwords only).</summary>
    public bool Fillable { get; set; }

    /// <summary>The Octopus server API never returns this secret; it is imported empty and listed as missing.</summary>
    public bool WithheldByServer { get; set; }

    public Dictionary<string, List<string>> SourceScopes { get; set; } = [];
}
