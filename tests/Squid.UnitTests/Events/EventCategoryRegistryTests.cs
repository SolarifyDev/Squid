using System;
using Squid.Message.Constants.Events;
using Squid.Message.Enums.Events;

namespace Squid.UnitTests.Events;

public class EventCategoryRegistryTests
{
    [Fact]
    public void EveryEventCategory_HasANonEmptyDescriptor()
    {
        // Drift guard: a new EventCategory MUST come with a display name + message
        // template or the history UI can't render it.
        foreach (var category in Enum.GetValues<EventCategory>())
        {
            EventCategoryRegistry.Has(category).ShouldBeTrue(
                $"EventCategory.{category} has no EventCategoryRegistry descriptor — add its display name + message template.");

            var descriptor = EventCategoryRegistry.Describe(category);
            descriptor.DisplayName.ShouldNotBeNullOrWhiteSpace();
            descriptor.MessageTemplate.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData(EventCategory.DocumentCreated, "Document created")]
    [InlineData(EventCategory.DeploymentQueued, "Deployment queued")]
    [InlineData(EventCategory.DeploymentStarted, "Deployment started")]
    [InlineData(EventCategory.ManualInterventionRaised, "Manual intervention interruption raised")]
    [InlineData(EventCategory.DeploymentSucceeded, "Deployment succeeded")]
    [InlineData(EventCategory.DeploymentFailed, "Deployment failed")]
    [InlineData(EventCategory.DeploymentTimedOut, "Deployment timed out")]
    public void Describe_ReturnsExpectedDisplayName(EventCategory category, string expectedDisplayName)
    {
        EventCategoryRegistry.Describe(category).DisplayName.ShouldBe(expectedDisplayName);
    }

    [Fact]
    public void EnumValues_ArePinned_AndNotRenumbered()
    {
        // These are persisted as smallint — renumbering silently corrupts history.
        ((short)EventCategory.DocumentCreated).ShouldBe((short)1);
        ((short)EventCategory.DeploymentQueued).ShouldBe((short)10);
        ((short)EventCategory.DeploymentSucceeded).ShouldBe((short)16);
        ((short)EventCategory.DeploymentFailed).ShouldBe((short)17);
        ((short)EventCategory.DeploymentCanceled).ShouldBe((short)18);
        ((short)EventCategory.DeploymentTimedOut).ShouldBe((short)19);
    }
}
