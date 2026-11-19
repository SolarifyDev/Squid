using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Single source of truth for whether an Octopus feed's credentials can be imported faithfully —
/// shared by the preview planner (so it does not warn "credentials omitted" for a feed that will
/// in fact receive them) and the feed mapper (which writes them). Credentials are imported only as
/// a complete pair: a username with no password, or a password that decrypts with the export
/// password. An undecryptable password drops both — a half-configured feed (username, no
/// password) would fail at deploy time far from the import that caused it. A password the Octopus server withheld is
/// unresolved too, so a REST feed never gets its username without its password.
/// </summary>
public sealed record OctopusImportFeedCredentials(string Username, string Password, bool IsResolved)
{
    public static OctopusImportFeedCredentials Unresolved { get; } = new(null, null, false);

    public static OctopusImportFeedCredentials Resolve(OctopusFeedDto feed, OctopusExportSecrets secrets)
    {
        if (feed == null) return new OctopusImportFeedCredentials(null, null, true);

        if (IsPasswordWithheld(feed)) return Unresolved;

        var username = string.IsNullOrWhiteSpace(feed.Username) ? null : feed.Username;

        if (string.IsNullOrEmpty(feed.Password)) return new OctopusImportFeedCredentials(username, null, true);

        return secrets != null && secrets.TryDecrypt(feed.Password, out var password)
            ? new OctopusImportFeedCredentials(username, password, true)
            : Unresolved;
    }

    public static bool IsResolvable(OctopusFeedDto feed, OctopusExportSecrets secrets)
    {
        if (feed == null) return true;

        if (IsPasswordWithheld(feed)) return false;

        if (string.IsNullOrEmpty(feed.Password)) return true;

        return secrets != null && secrets.CanDecrypt(feed.Password);
    }

    public static bool IsPasswordWithheld(OctopusFeedDto feed) => feed != null && OctopusImportWithheldSecrets.IsWithheld(feed.ExtensionData, OctopusImportWithheldSecretInputs.FeedPasswordField);

    public override string ToString() => $"OctopusImportFeedCredentials(IsResolved = {IsResolved})";
}
