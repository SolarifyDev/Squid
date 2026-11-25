using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.Core.Services.Packages.BuiltIn;

internal enum BuiltInPackageFormat
{
    Zip,
    TarGz
}

/// <summary>The two formats the built-in feed accepts, told apart by the file's first bytes: a file's name says nothing about what it holds.</summary>
internal static class BuiltInPackageFormats
{
    public static BuiltInPackageFormat Detect(string path)
    {
        var header = new byte[4];

        using (var file = File.OpenRead(path))
            header = header[..file.Read(header, 0, header.Length)];

        if (PackageArchiveSafety.IsZip(header)) return BuiltInPackageFormat.Zip;

        return PackageArchiveSafety.IsTarGz(header) ? BuiltInPackageFormat.TarGz : throw new UnsupportedPackageFormatException();
    }

    public static string ExtensionOf(BuiltInPackageFormat format) => format == BuiltInPackageFormat.Zip ? ".zip" : ".tar.gz";
}
