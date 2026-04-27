using Halibut;

namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// P1-Phase9b.3 (audit OctopusTentacle gap #1) — separate file upload/download
/// wire contract, distinct from <see cref="IScriptService"/>.
///
/// <para><b>Why a separate contract</b>: pre-Phase-9b.3, files crossed the wire
/// only as <c>StartScriptCommand.Files</c> embedded in a script-execution
/// command. That works for the deployment fast-path but precluded:
/// <list type="bullet">
///   <item>Out-of-band fetch of <c>~/.squid/Logs/agent.log</c> from a hung agent
///         for diagnostic — operators couldn't pull arbitrary remote paths.</item>
///   <item>Pre-staging large packages without bundling them into a script call.</item>
///   <item>Incremental file sync (delta uploads) for repeat deploys.</item>
/// </list>
/// Separating the contract opens these workflows without touching the script
/// service at all.</para>
///
/// <para>Mirrors OctopusTentacle's <c>Octopus.Tentacle.Contracts.IFileTransferService</c>.
/// The async surface is in <see cref="IFileTransferServiceAsync"/>.</para>
/// </summary>
public interface IFileTransferService
{
    /// <summary>
    /// Uploads <paramref name="upload"/> bytes to the agent at the absolute
    /// <paramref name="remotePath"/>. Returns the actual stored path (which
    /// may differ from the requested path if the agent rewrites it for
    /// security or workspace policy), the SHA-256 hash for integrity
    /// verification, and the byte length.
    /// </summary>
    UploadResult UploadFile(string remotePath, DataStream upload);

    /// <summary>
    /// Reads the file at <paramref name="remotePath"/> and streams it back as
    /// a <see cref="DataStream"/>. Throws if the path doesn't exist or isn't
    /// readable by the agent process.
    /// </summary>
    DataStream DownloadFile(string remotePath);
}
