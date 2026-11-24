using System.Text.Json;

namespace Squid.Message.Models.ProjectTriggers;

public sealed class ProjectTriggerDto : ProjectTriggerSummaryDto
{
    /// <summary>Present when the installed handler recognizes and validates the stored configuration.</summary>
    public JsonElement? Configuration { get; set; }
}
