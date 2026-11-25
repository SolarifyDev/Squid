using System.Linq;
using Squid.Core.Services.Publishing;
using Squid.Message.Models.Publishing;

namespace Squid.UnitTests.Services.Publishing;

public class PublishedProjectMarkerTests
{
    [Fact]
    public void RoundTrips()
    {
        var marker = new PublishedProjectMarker("Acme.Web", "static-site", "abc123");

        PublishedProjectMarker.TryParse(marker.ToJson()).ShouldBe(marker);
    }

    [Fact]
    public void JsonKey_IsPinned_BecauseItIsStoredInEveryPublishedProject()
    {
        PublishedProjectMarker.JsonKey.ShouldBe("squidPublish");
        new PublishedProjectMarker("p", "r", "h").ToJson().ShouldBe("{\"squidPublish\":{\"packageId\":\"p\",\"recipe\":\"r\",\"planHash\":\"h\"}}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"SourceId\":\"Projects-1\",\"Description\":\"imported\"}")]
    [InlineData("{\"squidPublish\":\"text\"}")]
    [InlineData("{\"squidPublish\":{\"recipe\":\"r\"}}")]
    public void AnythingElse_HasNoMarker(string json)
    {
        PublishedProjectMarker.TryParse(json).ShouldBeNull();
    }

    private static DeploymentPlan Plan(string script, params string[] roles)
    {
        var action = new PlanAction { Name = "Run", ActionType = "Squid.Script", Properties = { ["a"] = "1", ["b"] = script } };

        return new DeploymentPlan { Steps = [new PlanStep { Name = "Run", TargetRoles = roles.ToList(), Actions = [action] }] };
    }

    [Fact]
    public void Fingerprint_IsStable_AndIgnoresTheOrderOfRoles()
    {
        DeploymentPlanFingerprint.Of(Plan("x", "web", "edge")).ShouldBe(DeploymentPlanFingerprint.Of(Plan("x", "edge", "web")));
    }

    [Fact]
    public void Fingerprint_IgnoresThePropertyOrder()
    {
        var one = Plan("x");
        var other = Plan("x");

        other.Steps[0].Actions[0].Properties = new Dictionary<string, string> { ["b"] = "x", ["a"] = "1" };

        DeploymentPlanFingerprint.Of(one).ShouldBe(DeploymentPlanFingerprint.Of(other));
    }

    [Fact]
    public void Fingerprint_ChangesWhenAnythingThatEndsUpInTheProcessChanges()
    {
        var baseline = DeploymentPlanFingerprint.Of(Plan("x", "web"));

        DeploymentPlanFingerprint.Of(Plan("y", "web")).ShouldNotBe(baseline);
        DeploymentPlanFingerprint.Of(Plan("x", "web", "edge")).ShouldNotBe(baseline);

        var renamed = Plan("x", "web");
        renamed.Steps[0].Name = "Other";

        DeploymentPlanFingerprint.Of(renamed).ShouldNotBe(baseline);

        var packaged = Plan("x", "web");
        packaged.Steps[0].Actions[0].Package = new PlanPackageReference { PackageId = "p" };

        DeploymentPlanFingerprint.Of(packaged).ShouldNotBe(baseline);
    }
}
