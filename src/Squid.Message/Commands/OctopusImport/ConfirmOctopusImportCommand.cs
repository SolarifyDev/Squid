using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

[RequiresPermission(Permission.ProjectCreate)]
public class ConfirmOctopusImportCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>
    /// Queue the import as a background job and return the still-Validated session at once; the caller polls the
    /// session status. Off by default, so existing clients keep the synchronous confirmation.
    /// </summary>
    public bool RunInBackground { get; set; }
}

public class ConfirmOctopusImportResponse : SquidResponse<ConfirmOctopusImportResponseData>
{
}

public class ConfirmOctopusImportResponseData
{
    public OctopusImportSessionDto Session { get; set; }

    public OctopusImportBlockerSummaryDto BlockerSummary { get; set; }
}
