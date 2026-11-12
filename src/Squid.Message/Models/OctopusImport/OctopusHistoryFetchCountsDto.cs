namespace Squid.Message.Models.OctopusImport;

public class OctopusHistoryFetchCountsDto
{
    public int Releases { get; set; }

    public int Deployments { get; set; }

    public int ServerTasks { get; set; }

    public int DetailsFetched { get; set; }

    public int DetailsSkipped { get; set; }

    /// <summary>Tasks whose details were over the per-response cap; they are imported without details.</summary>
    public int DetailsTooLarge { get; set; }

    /// <summary>Scoped projects the Octopus space does not have (deleted there, or another space was chosen).</summary>
    public int ProjectsNotFound { get; set; }
}
