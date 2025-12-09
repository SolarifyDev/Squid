using System;
using System.Collections.Generic;

namespace Squid.Message.Models.Deployments.Process;

public class DeploymentPlanDto
{
    public int DeploymentId { get; set; }

    public ProcessSnapshotData ProcessSnapshot { get; set; }

    // 变量快照、目标、步骤等后续补充
}
