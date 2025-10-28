namespace Squid.Message.Models.Deployments.Process;

using System;
using System.Collections.Generic;

public class ProcessDetailSnapshotData
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string StepType { get; set; }

    public int StepOrder { get; set; }

    public string Condition { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

    public DateTimeOffset CreatedAt { get; set; }

    public List<ActionSnapshotData> Actions { get; set; } = new List<ActionSnapshotData>();
}
