namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// One field an Update or Fill writes into a row an earlier import created: the value Squid holds now and the value the import writes.
/// A secret never carries values (<see cref="ValueHidden"/>): only the field is named.
/// </summary>
public class OctopusImportFieldChangeDto
{
    public string Field { get; set; }

    /// <summary>Squid's current value; null when Squid holds none or the value is hidden.</summary>
    public string OldValue { get; set; }

    /// <summary>The value the import writes; null when it writes none or the value is hidden.</summary>
    public string NewValue { get; set; }

    /// <summary>True for a secret: the values are never shown.</summary>
    public bool ValueHidden { get; set; }

    /// <summary>What the change means when the values alone do not say it (details re-read, a missing row re-written, a secret filled).</summary>
    public string Description { get; set; }
}
