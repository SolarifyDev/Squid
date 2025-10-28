namespace Squid.Message.Models.Deployments.Process;

using System;
using System.Collections.Generic;

public class ActionSnapshotData
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string ActionType { get; set; }

    public int ActionOrder { get; set; }

    public int? WorkerPoolId { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsRequired { get; set; }

    public bool CanBeUsedForProjectVersioning { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();

    public List<int> Environments { get; set; } = new List<int>();

    public List<int> Channels { get; set; } = new List<int>();

    public List<string> MachineRoles { get; set; } = new List<string>();
}
