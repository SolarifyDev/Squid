namespace Squid.Message.Enums.OctopusImport;

/// <summary>What an import session reads. Append-only: the web reads the numbers.</summary>
public enum OctopusImportSourceKind
{
    ExportFile = 0,

    OctopusServer = 1
}
