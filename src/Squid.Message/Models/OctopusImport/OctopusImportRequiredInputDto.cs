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

    public Dictionary<string, List<string>> SourceScopes { get; set; } = [];
}
