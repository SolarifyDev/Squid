namespace Squid.Message.Models.PackageVersioning;

/// <summary>What a version token may read while a package version is being assigned. Anything a caller cannot supply stays null and the token that needs it says so.</summary>
public sealed record VersionTokenContext
{
    public int FeedId { get; init; }

    public string PackageId { get; init; }

    /// <summary>Name of the uploaded file, e.g. <c>Acme.Web.1.2.3.zip</c>.</summary>
    public string FileName { get; init; }

    /// <summary>The version the user typed.</summary>
    public string InputVersion { get; init; }

    public string GitSha { get; init; }

    /// <summary>Reads a text file of the package by relative path; yields null when the package has no such file. Null when the package content is not available.</summary>
    public Func<string, CancellationToken, Task<string>> TryReadTextFileAsync { get; init; }

    /// <summary>Names of the files at the package root. Null when the package content is not available.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<string>>> ListRootFileNamesAsync { get; init; }

    /// <summary>True while a template is only being checked: tokens must not consume anything.</summary>
    public bool DryRun { get; init; }

    /// <summary>The text rendered before the token being resolved. Set by the assigner for each token.</summary>
    public string PrecedingText { get; init; } = string.Empty;
}
