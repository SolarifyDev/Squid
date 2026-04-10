namespace Squid.Core.Services.DeploymentExecution.Transport;

[Flags]
public enum PackageStagingMode
{
    None = 0,
    UploadOnly = 1 << 0,
    CacheAware = 1 << 1,
    RemoteDownload = 1 << 2,
    Delta = 1 << 3
}
