namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// How many source resources fall into each preview outcome. <see cref="AlreadyImported"/> and <see cref="Reused"/> exist in Squid already;
/// <see cref="Create"/>, <see cref="Update"/>, <see cref="Fill"/> and <see cref="Reused"/> are what this import changes (<see cref="Incremental"/>).
/// </summary>
public class OctopusImportChangeCountsDto
{
    /// <summary>Imported by an earlier import: nothing is written.</summary>
    public int AlreadyImported { get; set; }

    /// <summary>A Squid resource with the same identity is linked as-is: nothing is written to it.</summary>
    public int Reused { get; set; }

    public int Create { get; set; }

    /// <summary>History an earlier import wrote that changed in Octopus (release notes, task state, task details).</summary>
    public int Update { get; set; }

    /// <summary>A secret an earlier import left missing, now filled.</summary>
    public int Fill { get; set; }

    public int Skip { get; set; }

    public int Unsupported { get; set; }

    /// <summary>Blocked or waiting for a rename: nothing is written until resolved.</summary>
    public int Blocked { get; set; }

    public int Total { get; set; }

    public int Incremental => Create + Update + Fill + Reused;
}
