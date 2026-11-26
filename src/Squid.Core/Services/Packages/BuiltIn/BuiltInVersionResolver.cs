using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.Versioning;
using Squid.Message.Commands.BuiltInPackages;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Gives a push its version: the one the caller typed (checked like any template's result), else the one the package's version rule yields.</summary>
internal interface IBuiltInVersionResolver : IScopedDependency
{
    Task<string> ResolveAsync(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, PackageArchiveTextReader archive, CancellationToken ct);
}

internal sealed class BuiltInVersionResolver(IPackageVersionAssigner assigner, IPackageVersionRuleService rules) : IBuiltInVersionResolver
{
    private const string InputTemplate = "{input}";

    public async Task<string> ResolveAsync(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, PackageArchiveTextReader archive, CancellationToken ct)
    {
        var template = await ChooseTemplateAsync(feed, packageId, command, ct).ConfigureAwait(false);

        return await assigner.AssignAsync(template, BuildContext(feed, packageId, command, archive), ct).ConfigureAwait(false);
    }

    private async Task<string> ChooseTemplateAsync(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.Version)) return InputTemplate;

        var rule = await rules.GetEffectiveRuleAsync(command.SpaceId, feed.Id, packageId, ct).ConfigureAwait(false);

        return rule.VersionTemplate;
    }

    private static VersionTokenContext BuildContext(ExternalFeed feed, string packageId, PushBuiltInPackageCommand command, PackageArchiveTextReader archive)
    {
        return new VersionTokenContext
        {
            FeedId = feed.Id,
            PackageId = packageId,
            FileName = command.FileName,
            InputVersion = command.Version,
            GitSha = command.GitSha,
            TryReadTextFileAsync = archive.TryReadTextFileAsync,
            ListRootFileNamesAsync = archive.ListRootFileNamesAsync
        };
    }
}
