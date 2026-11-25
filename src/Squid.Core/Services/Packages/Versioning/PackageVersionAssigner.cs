using System.Text;
using System.Text.RegularExpressions;
using Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning;

public sealed partial class PackageVersionAssigner(IEnumerable<IVersionTokenSource> tokenSources, TimeProvider time = null) : IPackageVersionAssigner
{
    // The longest version accepted: far below the 255-character package id columns and the index row limits it ends up in.
    public const int MaxVersionLength = 128;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<string> AssignAsync(string template, VersionTokenContext context, CancellationToken ct)
    {
        // One clock reading for the check and the real run, so a midnight in between cannot make them render different versions.
        var stamped = context with { Now = _time.GetUtcNow() };

        await RenderAsync(template, stamped with { DryRun = true }, ct).ConfigureAwait(false);

        return await RenderAsync(template, stamped with { DryRun = false }, ct).ConfigureAwait(false);
    }

    public Task<string> PreviewAsync(string template, VersionTokenContext sample, CancellationToken ct)
    {
        return RenderAsync(template, sample with { Now = _time.GetUtcNow(), DryRun = true }, ct);
    }

    private async Task<string> RenderAsync(string template, VersionTokenContext context, CancellationToken ct)
    {
        var parts = VersionTemplateParser.Parse(template);
        var sources = IndexSources();

        EnsureAllKnown(parts, sources);

        var version = await ResolveAllAsync(parts, sources, context, ct).ConfigureAwait(false);

        EnsureUsable(template, version);

        return version;
    }

    private Dictionary<string, IVersionTokenSource> IndexSources()
    {
        return tokenSources.ToDictionary(s => s.Token, StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureAllKnown(IReadOnlyList<VersionTemplatePart> parts, Dictionary<string, IVersionTokenSource> sources)
    {
        var unknown = parts.FirstOrDefault(p => p.IsToken && !sources.ContainsKey(p.Token));

        if (unknown != null)
            throw new UnknownVersionTokenException(unknown.Token, sources.Keys);
    }

    private static async Task<string> ResolveAllAsync(IReadOnlyList<VersionTemplatePart> parts, Dictionary<string, IVersionTokenSource> sources, VersionTokenContext context, CancellationToken ct)
    {
        var rendered = new StringBuilder();

        foreach (var part in parts)
        {
            if (!part.IsToken) { rendered.Append(part.Literal); continue; }

            var value = await sources[part.Token].ResolveAsync(context with { PrecedingText = rendered.ToString() }, part.Argument, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(value))
                throw new VersionTokenValueMissingException(part.Token, "it resolved to an empty value");

            rendered.Append(value);
        }

        return rendered.ToString();
    }

    // The version ends up in file names and URLs, so beyond sorting like every other feed version it may only hold characters a version needs.
    private static void EnsureUsable(string template, string version)
    {
        if (version.Length > MaxVersionLength)
            throw new NonSortablePackageVersionException(template, version, $"it is {version.Length} characters long, the limit is {MaxVersionLength}");

        if (!AllowedCharacters().IsMatch(version))
            throw new NonSortablePackageVersionException(template, version, "it may only contain letters, digits, '.', '-' and '+'");

        if (!PackageVersionFilter.IsSortable(version))
            throw new NonSortablePackageVersionException(template, version, "use the form major.minor[.patch[.revision]][-prerelease], for example 1.0.{counter}, so feed versions sort and channel version rules apply");
    }

    [GeneratedRegex(@"^[0-9A-Za-z.+\-]+$")]
    private static partial Regex AllowedCharacters();
}
