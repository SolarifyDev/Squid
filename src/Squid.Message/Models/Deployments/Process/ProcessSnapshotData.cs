namespace Squid.Message.Models.Deployments.Process;

using System;
using System.Collections.Generic;

public class ProcessSnapshotData
{
    public int Id { get; set; }

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<ProcessDetailSnapshotData> Processes { get; set; } = new List<ProcessDetailSnapshotData>();

    public Dictionary<string, List<string>> ScopeDefinitions { get; set; } = new Dictionary<string, List<string>>();
}
