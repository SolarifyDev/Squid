using Squid.Core.Persistence.Entities.Account;
using Squid.Message.Commands.Teams;
using Squid.Message.Models.Teams;

namespace Squid.Core.Mappings;

public class TeamMapping : Profile
{
    public TeamMapping()
    {
        CreateMap<Team, TeamDto>();
        CreateMap<CreateTeamCommand, Team>();
        CreateMap<UpdateTeamCommand, Team>();
        CreateMap<TeamMember, TeamMemberDto>();
    }
}
