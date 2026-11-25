namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class PackageIdRequiredException(string fileName) : InvalidOperationException($"No package id was given and none can be read from the file name '{fileName}'; pass packageId, or name the file PackageId.Version.ext, for example Acme.Web.1.2.3.zip")
{
    public string FileName { get; } = fileName;
}
