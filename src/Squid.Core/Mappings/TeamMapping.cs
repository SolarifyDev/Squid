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

        CreateMap<UpdateTeamCommand, Team>()
            // P0-D.2: UpdateTeamCommand.SpaceId is now int? (ISpaceScoped contract).
            // Without this Condition, a null value (e.g. header not injected) would
            // overwrite Team.SpaceId with default(int) = 0 and silently move the team
            // to space 0. Only apply the update when the caller actually supplied a value.
            .ForMember(dest => dest.SpaceId, opt => opt.Condition(src => src.SpaceId.HasValue));

        CreateMap<TeamMember, TeamMemberDto>();
    }
}
