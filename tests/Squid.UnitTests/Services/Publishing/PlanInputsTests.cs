using Squid.Core.Services.Publishing;
using Squid.Core.Services.Publishing.Exceptions;
using Squid.Message.Models.Publishing;

namespace Squid.UnitTests.Services.Publishing;

public class PlanInputsTests
{
    private static DeploymentPlan PlanWith(PlanInputType type, string name = "hostPort") => new() { Inputs = [new PlanInput { Name = name, Description = "the port", Type = type }] };

    [Theory]
    [InlineData("8080", "8080")]
    [InlineData(" 80 ", "80")]
    [InlineData("65535", "65535")]
    [InlineData("1", "1")]
    public void ATcpPort_IsAcceptedAndNormalised(string given, string expected)
    {
        PlanInputs.Validate(PlanWith(PlanInputType.TcpPort), new Dictionary<string, string> { ["hostPort"] = given })["hostPort"].ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("80; touch x")]
    [InlineData("08x")]
    public void ABadTcpPort_NamesTheInput(string given)
    {
        var ex = Should.Throw<RecipeInputInvalidException>(() => PlanInputs.Validate(PlanWith(PlanInputType.TcpPort), new Dictionary<string, string> { ["hostPort"] = given }));

        ex.Input.ShouldBe("hostPort");
    }

    [Fact]
    public void NoInputsGiven_ForADeclaredOne_IsRefused()
    {
        Should.Throw<RecipeInputInvalidException>(() => PlanInputs.Validate(PlanWith(PlanInputType.TcpPort), null));
    }

    [Fact]
    public void TheNameIsMatchedIgnoringCase()
    {
        PlanInputs.Validate(PlanWith(PlanInputType.TcpPort), new Dictionary<string, string> { ["HOSTPORT"] = "81" })["hostPort"].ShouldBe("81");
    }

    [Fact]
    public void ARecipeThatDeclaresNoInputs_NeedsNone_AndAnyoneCanAddOneWithoutTouchingThisClass()
    {
        PlanInputs.Validate(new DeploymentPlan(), null).ShouldBeEmpty();
        PlanInputs.Validate(PlanWith(PlanInputType.TcpPort, "adminPort"), new Dictionary<string, string> { ["adminPort"] = "9000" })["adminPort"].ShouldBe("9000");
    }

    [Fact]
    public void AnUnknownType_IsAProgrammingError_NotAUserError()
    {
        Should.Throw<InvalidOperationException>(() => PlanInputs.Validate(PlanWith((PlanInputType)99), new Dictionary<string, string> { ["hostPort"] = "1" }));
    }
}
