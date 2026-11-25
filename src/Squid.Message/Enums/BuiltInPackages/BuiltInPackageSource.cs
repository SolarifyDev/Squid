namespace Squid.Message.Enums.BuiltInPackages;

/// <summary>How a package reached the built-in feed. Stored in lower case (upload, api, cli).</summary>
public enum BuiltInPackageSource
{
    Api,
    Upload,
    Cli
}
