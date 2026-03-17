using Squid.Message.Response;

namespace Squid.Message.Commands.Teams;

public class DeleteTeamCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteTeamResponse : SquidResponse
{
}
