namespace Squid.Core.Services.Publishing;

/// <summary>
/// Reads one kind of fact out of a pushed package and adds it to the context. Inspectors are independent of each other and of their order. One that throws
/// does not fail the push: the inspection service records the failure as a fact and carries on.
/// </summary>
public interface IPackageInspector : IScopedDependency
{
    /// <summary>Relative paths whose existence the inspector will ask about with <c>HasFile</c>. Found in the same single pass over the archive as everything else.</summary>
    IReadOnlyCollection<string> FilesToProbe => Array.Empty<string>();

    /// <summary>Relative paths of small files the inspector will read with <c>TryReadTextAsync</c>. Read in the same single pass; a file not declared here costs a pass of its own.</summary>
    IReadOnlyCollection<string> FilesToRead => Array.Empty<string>();

    Task InspectAsync(PackageInspectionContext context, CancellationToken ct);
}
