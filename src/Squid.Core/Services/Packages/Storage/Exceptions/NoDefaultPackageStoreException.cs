namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class NoDefaultPackageStoreException() : InvalidOperationException("No package store is marked as the default; make one the default first")
{
}
