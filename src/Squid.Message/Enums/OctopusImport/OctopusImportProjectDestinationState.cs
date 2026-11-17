namespace Squid.Message.Enums.OctopusImport;

/// <summary>Where an Octopus project's changes land in Squid. Append-only: the SquidWeb mirror is ordinal.</summary>
public enum OctopusImportProjectDestinationState
{
    /// <summary>This import creates the Squid project.</summary>
    New = 0,

    /// <summary>An earlier import created (or linked) the Squid project; this import only adds to it.</summary>
    Existing = 1,

    /// <summary>The project is not imported by this plan (skipped, unsupported or blocked); see its diagnostics.</summary>
    NotImported = 2
}
