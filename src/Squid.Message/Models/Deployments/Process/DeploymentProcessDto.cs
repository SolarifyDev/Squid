using System;
using System.Collections.Generic;

namespace Squid.Message.Models.Deployments.Process;

public class DeploymentProcessDto
{
    public int Id { get; set; }

    public int Version { get; set; }

    public int SpaceId { get; set; }

    public DateTimeOffset LastModified { get; set; }

    public string LastModifiedBy { get; set; }

    public List<DeploymentStepDto> Steps { get; set; } = new List<DeploymentStepDto>();
}
