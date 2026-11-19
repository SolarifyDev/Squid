namespace Squid.Message.Enums.OctopusImport;

/// <summary>Where a reopened import session continues. Append-only: the web reads the numbers.</summary>
public enum OctopusImportResumeStep
{
    /// <summary>The read from Octopus stopped before it finished: the API key is entered again and the read resumes where it stopped.</summary>
    ResumeRead = 0,

    /// <summary>The source is read: it is extracted (or, for an export file, its projects chosen), then previewed.</summary>
    ContinueAfterRead = 1,

    /// <summary>The session was previewed (or validated, with the choices it was validated with): the preview is shown again.</summary>
    Preview = 2,

    /// <summary>The import is running: the page follows it until it ends.</summary>
    FollowImport = 3,

    /// <summary>The last import attempt imported nothing: the same plan can be imported again.</summary>
    RetryImport = 4
}
