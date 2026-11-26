namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class PackageVersionAlreadyExistsException : InvalidOperationException
{
    public PackageVersionAlreadyExistsException(string packageId, string version) : base($"Version {version} of package '{packageId}' already exists in the built-in feed; push it again with replace=true to overwrite it, or choose another version")
    {
        PackageId = packageId;
        Version = version;
    }

    private PackageVersionAlreadyExistsException(string packageId, string version, string message) : base(message)
    {
        PackageId = packageId;
        Version = version;
    }

    public string PackageId { get; }

    public string Version { get; }

    /// <summary>A version rule with a counter that kept landing on versions the package already has, until the push gave up.</summary>
    public static PackageVersionAlreadyExistsException NumbersTaken(string packageId, string lastVersion, int attempts)
    {
        return new PackageVersionAlreadyExistsException(packageId, lastVersion, $"The version rule of package '{packageId}' produced {attempts} versions in a row that already exist in the built-in feed (the last was {lastVersion}); push with an explicit version, or change the package's version rule");
    }
}
