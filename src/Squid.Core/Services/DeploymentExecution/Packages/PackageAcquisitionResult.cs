namespace Squid.Core.Services.DeploymentExecution.Packages;

public record PackageAcquisitionResult(string LocalPath, string PackageId, string Version, long SizeBytes, string Hash);
