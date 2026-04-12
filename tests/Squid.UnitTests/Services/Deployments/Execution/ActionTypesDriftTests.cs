using System.Linq;
using System.Reflection;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ActionTypesDriftTests
{
    private static readonly Type[] HandlerTypes = typeof(IActionHandler).Assembly
        .GetTypes()
        .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IActionHandler).IsAssignableFrom(t))
        .ToArray();

    [Fact]
    public void EveryHandler_ActionType_IsInActionTypesAll()
    {
        HandlerTypes.ShouldNotBeEmpty();

        var violations = new List<string>();

        foreach (var handlerType in HandlerTypes)
        {
            var handler = (IActionHandler)CreateHandlerWithNulls(handlerType);
            var actionType = handler.ActionType;

            if (!SpecialVariables.ActionTypes.All.Contains(actionType))
                violations.Add($"{handlerType.Name} => \"{actionType}\"");
        }

        violations.ShouldBeEmpty("Handlers declare ActionType values missing from SpecialVariables.ActionTypes.All: " + string.Join(", ", violations));
    }

    [Fact]
    public void ActionTypesAll_ContainsEveryDeclaredConstant()
    {
        var declaredConstants = typeof(SpecialVariables.ActionTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue())
            .ToArray();

        declaredConstants.ShouldNotBeEmpty();

        foreach (var constant in declaredConstants)
            SpecialVariables.ActionTypes.All.ShouldContain(constant);
    }

    private static object CreateHandlerWithNulls(Type handlerType)
    {
        var ctor = handlerType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();

        return ctor.Invoke(args);
    }
}
