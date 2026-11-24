namespace Squid.Message.Models.ProjectTriggers;

public sealed record ProjectTriggerPage<T>(int Count, IReadOnlyList<T> Items);
