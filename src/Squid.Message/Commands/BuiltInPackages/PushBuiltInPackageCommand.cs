using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Enums.BuiltInPackages;
using Squid.Message.Models.BuiltInPackages;
using Squid.Message.Response;

namespace Squid.Message.Commands.BuiltInPackages;

/// <summary>Pushes a .zip or .tar.gz to the space's built-in feed. Needs <see cref="Permission.FeedEdit"/>: Squid has no separate push permission.</summary>
[RequiresPermission(Permission.FeedEdit)]
public class PushBuiltInPackageCommand : ICommand, ISpaceScoped
{
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;

    /// <summary>Optional: read from <see cref="FileName"/> (<c>PackageId.Version.ext</c>) when absent.</summary>
    public string PackageId { get; set; }

    /// <summary>Optional: taken from the package's version rule when absent.</summary>
    public string Version { get; set; }

    /// <summary>Allows overwriting an existing version that no release uses.</summary>
    public bool Replace { get; set; }

    public string FileName { get; set; }

    /// <summary>The commit the package was built from; read by the <c>{git.sha}</c> version token.</summary>
    public string GitSha { get; set; }

    public BuiltInPackageSource Source { get; set; }

    /// <summary>The size the client announced, when it did; a push announcing more than the limit is refused before any byte is read.</summary>
    public long? DeclaredSizeBytes { get; set; }

    [JsonIgnore]
    public Stream Content { get; set; }
}

public class PushBuiltInPackageResponse : SquidResponse<PushBuiltInPackageResponseData>
{
}

public class PushBuiltInPackageResponseData
{
    public BuiltInPackageVersionDto Package { get; set; }

    /// <summary>True when the latest version already held these exact bytes: nothing was stored and no version number was used.</summary>
    public bool Deduplicated { get; set; }

    /// <summary>True when an existing version was overwritten.</summary>
    public bool Replaced { get; set; }
}
