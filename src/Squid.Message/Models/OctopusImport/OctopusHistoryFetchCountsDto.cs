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

    /// <summary>Requests the Octopus server refused; each item is skipped and listed in the cursor's skipped items.</summary>
    public int ItemsRejected { get; set; }

    /// <summary>Items skipped after outliving the chunk deadline too many times.</summary>
    public int ItemsAbandoned { get; set; }

    /// <summary>Configuration documents staged (everything but releases, deployments and tasks).</summary>
    public int ConfigurationDocuments { get; set; }

    /// <summary>Secret values the server did not return (sensitive variables, feed passwords, account keys, ...); imported as "value missing".</summary>
    public int SecretsWithheld { get; set; }
}
