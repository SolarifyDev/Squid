namespace Squid.Message.Commands.Deployments.Release;

public class UpdateReleaseVariableCommand : ICommand
{
    public int ReleaseId { get; set; }
}