namespace Squid.Message.Models.ProjectTriggers;

public sealed record ProjectTriggerChangeDto(int Id, int Revision, int ExecutionUserId, bool IsDisabled, bool IsDeleted);
