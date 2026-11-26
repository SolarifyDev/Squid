using Squid.Core.Services.Publishing.Recipes;

namespace Squid.UnitTests.Services.Publishing.Recipes;

public class ContainerNameTests
{
    [Theory]
    [InlineData("Acme.Web", "squid-acme-web")]
    [InlineData("acme.web", "squid-acme-web")]
    [InlineData("My_App v2!", "squid-my-app-v2")]
    [InlineData("###", "squid-app")]
    public void Short_IdsAreSluggedToLowercaseLettersDigitsAndHyphens(string packageId, string expected)
    {
        ContainerAppPlan.ContainerNameBase(packageId).ShouldBe(expected);
    }

    [Fact]
    public void LongIds_AreCapped_WithAHashOfTheWholeId_SoSharedPrefixesDoNotCollide()
    {
        var one = ContainerAppPlan.ContainerNameBase("Acme.Company.Platform.Services.Billing.Web");
        var two = ContainerAppPlan.ContainerNameBase("Acme.Company.Platform.Services.Billing.Api");

        one.Length.ShouldBeLessThanOrEqualTo(ContainerAppPlan.ContainerNamePrefix.Length + 28);
        two.Length.ShouldBeLessThanOrEqualTo(ContainerAppPlan.ContainerNamePrefix.Length + 28);
        one.ShouldNotBe(two);
        one.ShouldMatch("^[a-z0-9-]+$");
        one.ShouldNotEndWith("-");
    }

    [Fact]
    public void IsDeterministic_AndIgnoresCaseOfTheIdEvenWhenCapped()
    {
        var id = "Acme.Company.Platform.Services.Billing.Web";

        ContainerAppPlan.ContainerNameBase(id).ShouldBe(ContainerAppPlan.ContainerNameBase(id));
        ContainerAppPlan.ContainerNameBase(id).ShouldBe(ContainerAppPlan.ContainerNameBase(id.ToUpperInvariant()));
    }

    [Fact]
    public void AnIdJustAtTheCap_IsNotHashed()
    {
        var id = new string('a', 28);

        ContainerAppPlan.ContainerNameBase(id).ShouldBe("squid-" + id);
    }
}
