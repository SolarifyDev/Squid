using System.Linq;
using Mediator.Net.Contracts;
using Squid.Core.Extensions;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Core.Middlewares.FluentMessageValidator;

namespace Squid.UnitTests.Mediators;

public class MessageChecker
{
    [Fact]
    public void ShouldAllMessageHaveValidators()
    {
        var messageTypes = typeof(CreateLifeCycleCommand).Assembly.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(IRequest)) || t.IsAssignableTo(typeof(ICommand)))
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .ToList();

        var validatorTypes = typeof(IUserContext).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => t.IsAssignableToGenericType(typeof(FluentMessageValidator<>))).ToList();

        var noValidatorMessageTypes = (from messageType in messageTypes
            let hasValidator =
                validatorTypes.Any(x => x.BaseType != null && x.BaseType.GenericTypeArguments[0] == messageType)
            where !hasValidator
            select messageType).ToList();

        noValidatorMessageTypes.ShouldBeEmpty(
            $"[{string.Join(", ", noValidatorMessageTypes.Select(t => t.FullName))}] has no validators.");

        messageTypes.ShouldNotBeNull();
    }
}