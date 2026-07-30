namespace Squid.Core.Services.DeploymentExecution;

/// <summary>
/// Action-property constants for the <c>Squid.DeployWindowsService</c> action type.
///
/// <para>
/// Mirrors Octopus-style Windows service deployment naming with the <c>Squid.</c>
/// prefix. Generic feed/package/version selection remains on
/// <see cref="Message.Constants.SpecialVariables.Action"/> so package acquisition can
/// use the existing selected-package flow.
/// </para>
/// </summary>
internal static class WindowsServiceDeployProperties
{
    internal const string CreateOrUpdateService = "Squid.Action.WindowsService.CreateOrUpdateService";
    internal const string ServiceName = "Squid.Action.WindowsService.ServiceName";
    internal const string DisplayName = "Squid.Action.WindowsService.DisplayName";
    internal const string Description = "Squid.Action.WindowsService.Description";
    internal const string ExecutablePath = "Squid.Action.WindowsService.ExecutablePath";
    internal const string Arguments = "Squid.Action.WindowsService.Arguments";
    internal const string ServiceAccount = "Squid.Action.WindowsService.ServiceAccount";
    internal const string CustomAccountName = "Squid.Action.WindowsService.CustomAccountName";
    internal const string CustomAccountPassword = "Squid.Action.WindowsService.CustomAccountPassword";
    internal const string StartMode = "Squid.Action.WindowsService.StartMode";
    internal const string DesiredStatus = "Squid.Action.WindowsService.DesiredStatus";
    internal const string Dependencies = "Squid.Action.WindowsService.Dependencies";

    internal const string PackageSourcePath = "Squid.Action.WindowsService.Package.SourcePath";
    internal const string PackageExtractTo = "Squid.Action.WindowsService.Package.ExtractTo";
    internal const string PackagePurgeBeforeExtract = "Squid.Action.WindowsService.Package.PurgeBeforeExtract";

    internal const string TentacleOS = "Squid.Tentacle.OS";
}
