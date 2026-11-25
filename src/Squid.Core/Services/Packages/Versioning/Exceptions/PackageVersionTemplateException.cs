namespace Squid.Core.Services.Packages.Versioning.Exceptions;

/// <summary>A version template that cannot produce a version. The user can fix it, so the API reports it as a bad request.</summary>
public abstract class PackageVersionTemplateException(string message) : InvalidOperationException(message);
