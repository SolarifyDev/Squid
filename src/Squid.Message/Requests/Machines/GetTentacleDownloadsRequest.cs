using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

/// <summary>
/// Returns the canonical Tentacle binary download URLs for the FE's
/// "Add Tentacle → choose download" UX. Mirrors Octopus's MSI dropdown menu
/// — the FE renders a per-architecture list the operator picks from before
/// running the install script (see <c>WindowsPowerShellScriptBuilder</c> /
/// <c>LinuxBinaryScriptBuilder</c>).
///
/// <para><b>What this returns:</b> a flat list of <see cref="TentacleDownloadDto"/>
/// — one entry per (OS, architecture, libc-variant) tuple — each carrying
/// the archive URL, its <c>.sha256</c> companion URL (Phase-12.E.9 release
/// pipeline publishes these), filename, and a label for display.</para>
///
/// <para><b>Filtering:</b>
/// <list type="bullet">
///   <item><c>OperatingSystem = "Windows"</c> → 2 downloads (win-x64, win-arm64).</item>
///   <item><c>OperatingSystem = "Linux"</c> → 4 downloads (linux-x64, linux-arm64,
///         linux-musl-x64, linux-musl-arm64). Musl variants serve Alpine.</item>
///   <item><c>OperatingSystem = null</c> → all 6 downloads.</item>
/// </list>
/// </para>
///
/// <para><b>Version resolution:</b>
/// <list type="bullet">
///   <item><c>Version = "latest"</c> or null → resolves via
///         <see cref="Squid.Core.Services.Machines.Upgrade.ITentacleVersionRegistry"/>:
///         GitHub Releases for Windows, Docker Hub tag for Linux.
///         Both are typically released on the same git tag, so the resolved
///         version is identical across OSes — but each is queried independently
///         to handle drift gracefully.</item>
///   <item><c>Version = "1.6.0"</c> (specific) → URLs use the literal version
///         verbatim, skipping registry lookup. Operators on air-gapped mirrors
///         pin the exact version they've staged locally.</item>
/// </list>
/// </para>
///
/// <para><b>Why this endpoint exists:</b> the existing
/// <c>POST /api/machines/generate-tentacle-install-script</c> emits embedded
/// install-and-register one-liners. That UX is great for "I just want to
/// paste this into a terminal" but doesn't map to Octopus's familiar
/// "download installer first, then run setup wizard" flow that Windows
/// operators expect. This endpoint surfaces the raw download URLs so the FE
/// can render the same dropdown UX as Octopus, complementing the script
/// generator rather than replacing it.</para>
/// </summary>
[RequiresPermission(Permission.MachineView)]
public class GetTentacleDownloadsRequest : IRequest
{
    /// <summary>
    /// Optional OS filter. Case-insensitive (matches the OS filter shape on
    /// <see cref="Squid.Message.Commands.Machine.GenerateTentacleInstallScriptCommand"/>
    /// for consistency). Accepted values: <c>"Windows"</c>, <c>"Linux"</c>, null.
    /// </summary>
    public string OperatingSystem { get; set; }

    /// <summary>
    /// Optional version pin. <c>"latest"</c>, null, or empty → registry resolves.
    /// Specific version (e.g. <c>"1.6.0"</c>) is used verbatim — operator
    /// responsibility to ensure that version actually has artefacts published.
    /// </summary>
    public string Version { get; set; }
}

public class GetTentacleDownloadsResponse : SquidResponse<GetTentacleDownloadsResponseData>
{
}

public class GetTentacleDownloadsResponseData
{
    /// <summary>
    /// The version embedded in every <see cref="TentacleDownloadDto.DownloadUrl"/>.
    /// When the request used <c>Version = "latest"</c> this is the version
    /// the registry resolved to (ideally identical across Linux + Windows
    /// because both ship on the same git tag, but exposed as a flat scalar
    /// here for the common case where the FE just wants "what version is
    /// the user about to install").
    /// </summary>
    public string LatestVersion { get; set; }

    public List<TentacleDownloadDto> Downloads { get; set; } = new();
}

/// <summary>
/// One downloadable archive variant. Flat list (not OS-grouped) so the FE
/// can group-by client-side without coupling our schema to a specific UX
/// shape (Octopus groups by OS in the dropdown; future Squid UI may group
/// by architecture or libc variant — flat data is the most flexible).
/// </summary>
public class TentacleDownloadDto
{
    /// <summary>Either <c>"Windows"</c> or <c>"Linux"</c>.</summary>
    public string OperatingSystem { get; set; }

    /// <summary>CPU architecture: <c>"x64"</c> or <c>"arm64"</c>.</summary>
    public string Architecture { get; set; }

    /// <summary>
    /// Linux libc variant: <c>"glibc"</c> (default — Debian/Ubuntu/RHEL) or
    /// <c>"musl"</c> (Alpine). Null on Windows. Operators on Alpine MUST
    /// pick the musl variant — a glibc-linked self-contained binary crashes
    /// at startup on musl with cryptic GLIBC version errors.
    /// </summary>
    public string LibcVariant { get; set; }

    /// <summary>
    /// .NET RID — the canonical machine-readable variant key:
    /// <c>"win-x64"</c>, <c>"win-arm64"</c>, <c>"linux-x64"</c>,
    /// <c>"linux-arm64"</c>, <c>"linux-musl-x64"</c>, <c>"linux-musl-arm64"</c>.
    /// </summary>
    public string Rid { get; set; }

    /// <summary>Archive download URL — the operator's "Save As" target.</summary>
    public string DownloadUrl { get; set; }

    /// <summary>
    /// SHA256 companion URL (<c>{DownloadUrl}.sha256</c>). Phase-12.E.9 release
    /// workflows publish these alongside every archive; the install scripts
    /// opportunistically fetch + verify against this. Operators wanting
    /// integrity verification on manual downloads use this URL too.
    /// </summary>
    public string Sha256Url { get; set; }

    /// <summary>
    /// Just the filename portion (e.g. <c>squid-tentacle-1.6.0-win-x64.zip</c>),
    /// for FE "Save As" hints + display labels.
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// Operator-facing label, e.g. <c>"Windows x64"</c> / <c>"Linux x64 (glibc)"</c>
    /// / <c>"Alpine Linux x64 (musl)"</c>. The FE renders this verbatim in
    /// the download dropdown.
    /// </summary>
    public string Label { get; set; }

    /// <summary>
    /// True for the most-common variant per OS — the one the FE pre-selects.
    /// Currently <c>win-x64</c> for Windows and <c>linux-x64</c> for Linux.
    /// Mirrors <c>ITentacleInstallScriptBuilder.IsRecommended</c> semantics.
    /// </summary>
    public bool IsRecommended { get; set; }
}
