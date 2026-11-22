namespace Squid.Message.Models.Deployments.ServerTask;

/// <summary>An active task whose lease went stale: the owner that last renewed it, and the state it was seen in.</summary>
public sealed record StaleServerTask(int Id, string State, string LeaseOwner);
