using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.Versioning;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>
/// Gives a push its version. In order of precedence: (1) the version the caller typed, checked like any template's result; (2) the package's own version rule, when it
/// has one; (3) the version in the upload's file name (<c>Contoso.Api.2.5.0.zip</c> gives 2.5.0, as in Octopus), when the package has no rule of its own, the file name
/// has a version and the id in it is the package's, so an unrelated name such as <c>build.20261126.zip</c> pushed under an explicit package id is never read as a version;
/// (4) the built-in default rule's template.
/// </summary>
internal interface IBuiltInVersionResolver : IScopedDependency
{
    Task<string> ResolveAsync(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, PackageArchiveTextReader archive, CancellationToken ct);
}

internal sealed class BuiltInVersionResolver(IPackageVersionAssigner assigner, IPackageVersionRuleService rules) : IBuiltInVersionResolver
{
    private const string InputTemplate = "{input}";

    private sealed record VersionChoice(string Template, string InputVersion);

    public async Task<string> ResolveAsync(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, PackageArchiveTextReader archive, CancellationToken ct)
    {
        var choice = await ChooseAsync(feed, packageId, command, ct).ConfigureAwait(false);

        return await assigner.AssignAsync(choice.Template, BuildContext(feed, packageId, command, choice.InputVersion, archive), ct).ConfigureAwait(false);
    }

    private async Task<VersionChoice> ChooseAsync(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.Version)) return new VersionChoice(InputTemplate, command.Version);

        var rule = await rules.GetEffectiveRuleAsync(command.SpaceId, feed.Id, packageId, ct).ConfigureAwait(false);

        if (rule.IsDefault && TryReadFileNameVersion(packageId, command.FileName, out var fileVersion))
            return new VersionChoice(InputTemplate, fileVersion);

        return new VersionChoice(rule.VersionTemplate, null);
    }

    private static bool TryReadFileNameVersion(string packageId, string fileName, out string version)
    {
        return PackageFileName.TryParseFileName(fileName, out var idInName, out version) && string.Equals(idInName, packageId, StringComparison.OrdinalIgnoreCase);
    }

    private static VersionTokenContext BuildContext(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, string inputVersion, PackageArchiveTextReader archive)
    {
        return new VersionTokenContext
        {
            FeedId = feed.Id,
            PackageId = packageId,
            FileName = command.FileName,
            InputVersion = inputVersion,
            GitSha = command.GitSha,
            TryReadTextFileAsync = archive.TryReadTextFileAsync,
            ListRootFileNamesAsync = archive.ListRootFileNamesAsync
        };
    }
}
