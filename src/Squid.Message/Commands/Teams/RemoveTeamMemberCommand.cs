using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

public class RemoveTeamMemberCommand : ICommand
{
    public int TeamId { get; set; }
    public int UserId { get; set; }
}

public class RemoveTeamMemberResponse : SquidResponse
{
}
