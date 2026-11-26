using Squid.Core.Services.Packages.BuiltIn.Exceptions;

namespace Squid.Core.Services.Packages.BuiltIn;

internal sealed partial class BuiltInPackagePushService
{
    /// <summary>How many versions a version rule with a counter may produce for one push, each already taken, before the push gives up.</summary>
    public const int MaxVersionAttempts = 10;

    // Replace is a request about a version the caller named. A version the package's rule assigned was never named by anyone, so it can never be overwritten.
    private bool ReplacesNamedVersion => _ctx.Command.Replace && !string.IsNullOrWhiteSpace(_ctx.Command.Version);

    private bool VersionIsAssigned => string.IsNullOrWhiteSpace(_ctx.Command.Version);

    // A version the rule assigned may already exist (pushed by hand, or by a push that won the number a moment ago). The rule is asked again, so a counter hands out the
    // next number; a rule without one yields the same version again, which cannot move on, so the first refusal stands. The unique index stays the final arbiter: a
    // push that loses it at insert time lands here too. Only an assigned version ever retries, a named one keeps failing.
    private async Task WriteUnderAnAvailableVersionAsync(CancellationToken ct)
    {
        PackageVersionAlreadyExistsException refused = null;

        for (var attempt = 1; attempt <= MaxVersionAttempts; attempt++)
        {
            var taken = _ctx.Version;

            await ResolveVersionAsync(ct);

            if (refused != null && _ctx.Version == taken)
                throw refused;

            try
            {
                await EnsureVersionWritableAsync(ct);
                await WriteBlobAsync(ct);
                await RecordVersionAsync(ct);

                return;
            }
            catch (PackageVersionAlreadyExistsException ex) when (VersionIsAssigned)
            {
                refused = ex;

                Log.Information("[BuiltInFeed] Version {Version} of {PackageId} already exists, taking the next one", _ctx.Version, _ctx.PackageId);
            }
        }

        throw PackageVersionAlreadyExistsException.NumbersTaken(_ctx.PackageId, _ctx.Version, MaxVersionAttempts);
    }

    private async Task ResolveVersionAsync(CancellationToken ct)
    {
        var archive = new PackageArchiveTextReader(_ctx.Staged.Path, _ctx.Format, _ctx.Inspection);

        _ctx.Version = await versions.ResolveAsync(_ctx.Feed, _ctx.PackageId, _ctx.Command, archive, ct).ConfigureAwait(false);
    }

    private async Task EnsureVersionWritableAsync(CancellationToken ct)
    {
        _ctx.Replacing = await recorder.EnsureWritableAsync(_ctx.Feed.Id, _ctx.PackageId, _ctx.Version, ReplacesNamedVersion, ct).ConfigureAwait(false);
    }
}
