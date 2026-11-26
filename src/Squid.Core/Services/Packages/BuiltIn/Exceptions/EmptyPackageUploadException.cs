namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class EmptyPackageUploadException() : InvalidOperationException("The upload is empty; send a .zip or .tar.gz file");
