namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class UnsupportedPackageFormatException() : InvalidOperationException("The upload is neither a zip archive nor a gzip-compressed tar (.tar.gz); the built-in feed accepts only those two formats, judged by the file's content, not its name");
