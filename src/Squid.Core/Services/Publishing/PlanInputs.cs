using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Commands.Publishing;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>Checks that the publish request gave what the plan declared it needs.</summary>
public static class PlanInputs
{
    public static void EnsureProvided(DeploymentPlan plan, PublishPackageCommand command)
    {
        foreach (var input in plan.Inputs) EnsureProvided(input, command);
    }

    private static void EnsureProvided(PlanInput input, PublishPackageCommand command)
    {
        switch (input.Name)
        {
            case PlanInputNames.HostPort:
                if (command.HostPort is < 1 or > 65535) throw new RecipeInputInvalidException(input.Name, "give a TCP port between 1 and 65535 to publish the app on");
                break;
            default:
                throw new InvalidOperationException($"The plan declares the input '{input.Name}', which publishing does not know how to read from the request.");
        }
    }
}
