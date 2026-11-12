namespace Squid.Message.Models.OctopusImport;

public class OctopusHistoryFetchCountsDto
{
    public int Releases { get; set; }

    public int Deployments { get; set; }

    public int ServerTasks { get; set; }

    public int DetailsFetched { get; set; }

    public int DetailsSkipped { get; set; }
}
