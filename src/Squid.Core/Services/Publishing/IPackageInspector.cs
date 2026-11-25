namespace Squid.Core.Services.Publishing;

/// <summary>
/// Reads one kind of fact out of a pushed package and adds it to the context. Inspectors are independent of each other and of their order. One that throws
/// does not fail the push: the inspection service records the failure as a fact and carries on.
/// </summary>
public interface IPackageInspector : IScopedDependency
{
    Task InspectAsync(PackageInspectionContext context, CancellationToken ct);
}
