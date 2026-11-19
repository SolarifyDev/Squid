namespace Squid.Message.Enums.OctopusImport;

/// <summary>What the incoming source offers for a secret that is still missing in Squid.</summary>
public enum OctopusImportSecretFillSource
{
    /// <summary>A value (decrypted with the export password) that can fill the empty Squid value.</summary>
    Fillable = 0,

    /// <summary>The source holds no value either (empty in Octopus): nothing to write, the secret is settled.</summary>
    EmptyInOctopus = 1,

    /// <summary>No value can be read (an Octopus server source, or a value the export password cannot decrypt).</summary>
    StillMissing = 2,

    /// <summary>Two or more source variables have the same name, type and scope: which one is meant cannot be told.</summary>
    Ambiguous = 3
}
