using System;
using System.Collections.Generic;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.LifeCycle;

namespace Squid.UnitTests.Services.Deployments;

public class LifecycleEntityTests
{
    // ─── Lifecycle Inline Retention Defaults ───

    [Fact]
    public void Lifecycle_DefaultsToKeepForever()
    {
        var lifecycle = new Lifecycle();

        lifecycle.ReleaseRetentionKeepForever.ShouldBeTrue();
        lifecycle.TentacleRetentionKeepForever.ShouldBeTrue();
        lifecycle.ReleaseRetentionQuantity.ShouldBe(0);
        lifecycle.TentacleRetentionQuantity.ShouldBe(0);
    }

    [Fact]
    public void Lifecycle_InlineRetention_AllFieldsSettable()
    {
        var lifecycle = new Lifecycle
        {
            ReleaseRetentionUnit = RetentionPolicyUnit.Days,
            ReleaseRetentionQuantity = 30,
            ReleaseRetentionKeepForever = false,
            TentacleRetentionUnit = RetentionPolicyUnit.Weeks,
            TentacleRetentionQuantity = 4,
            TentacleRetentionKeepForever = false
        };

        lifecycle.ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Days);
        lifecycle.ReleaseRetentionQuantity.ShouldBe(30);
        lifecycle.ReleaseRetentionKeepForever.ShouldBeFalse();
        lifecycle.TentacleRetentionUnit.ShouldBe(RetentionPolicyUnit.Weeks);
        lifecycle.TentacleRetentionQuantity.ShouldBe(4);
        lifecycle.TentacleRetentionKeepForever.ShouldBeFalse();
    }

    // ─── Phase Nullable Retention (Inherit from Lifecycle) ───

    [Fact]
    public void Phase_RetentionDefaultsToNull_InheritsFromLifecycle()
    {
        var phase = new LifecyclePhase();

        phase.ReleaseRetentionUnit.ShouldBeNull();
        phase.ReleaseRetentionQuantity.ShouldBeNull();
        phase.ReleaseRetentionKeepForever.ShouldBeNull();
        phase.TentacleRetentionUnit.ShouldBeNull();
        phase.TentacleRetentionQuantity.ShouldBeNull();
        phase.TentacleRetentionKeepForever.ShouldBeNull();
    }

    [Theory]
    [InlineData(true, null, true)]     // phase null → use lifecycle (KeepForever=true)
    [InlineData(true, false, false)]   // phase overrides lifecycle
    [InlineData(false, null, false)]   // phase null → use lifecycle (KeepForever=false)
    [InlineData(false, true, true)]    // phase overrides lifecycle
    public void Phase_EffectiveRetention_NullCoalescing(
        bool lifecycleKeepForever, bool? phaseKeepForever, bool expected)
    {
        var lifecycle = new Lifecycle { ReleaseRetentionKeepForever = lifecycleKeepForever };
        var phase = new LifecyclePhase { ReleaseRetentionKeepForever = phaseKeepForever };

        var effective = phase.ReleaseRetentionKeepForever ?? lifecycle.ReleaseRetentionKeepForever;

        effective.ShouldBe(expected);
    }

    // ─── Channel Nullable LifecycleId ───

    [Fact]
    public void Channel_LifecycleId_IsNullable()
    {
        var channel = new Channel { LifecycleId = null };
        channel.LifecycleId.ShouldBeNull();

        channel.LifecycleId = 42;
        channel.LifecycleId.ShouldBe(42);
    }

    // ─── PhaseEnvironment Entity ───

    [Fact]
    public void PhaseEnvironment_CompositeKey()
    {
        var pe = new LifecyclePhaseEnvironment
        {
            PhaseId = 1,
            EnvironmentId = 10,
            TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
        };

        pe.PhaseId.ShouldBe(1);
        pe.EnvironmentId.ShouldBe(10);
        pe.TargetType.ShouldBe(LifecyclePhaseEnvironmentTargetType.Automatic);
    }

    // ─── PhaseDto Target IDs ───

    [Fact]
    public void PhaseDto_TargetIds_AreIntLists()
    {
        var dto = new LifecyclePhaseDto
        {
            AutomaticDeploymentTargetIds = new List<int> { 1, 2, 3 },
            OptionalDeploymentTargetIds = new List<int> { 4, 5 }
        };

        dto.AutomaticDeploymentTargetIds.Count.ShouldBe(3);
        dto.OptionalDeploymentTargetIds.Count.ShouldBe(2);
    }

    // ─── LifeCycleDto Inline Retention ───

    [Fact]
    public void LifeCycleDto_InlineRetention_NoForeignKeys()
    {
        var dto = new LifeCycleDto
        {
            ReleaseRetentionUnit = RetentionPolicyUnit.Months,
            ReleaseRetentionQuantity = 6,
            ReleaseRetentionKeepForever = false,
            TentacleRetentionUnit = RetentionPolicyUnit.Days,
            TentacleRetentionQuantity = 90,
            TentacleRetentionKeepForever = false
        };

        dto.ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Months);
        dto.ReleaseRetentionQuantity.ShouldBe(6);
        dto.TentacleRetentionKeepForever.ShouldBeFalse();
    }

    // ─── RetentionPolicyUnit Enum Typo Fix ───

    [Fact]
    public void RetentionPolicyUnit_HasMonths_NotMouths()
    {
        var months = RetentionPolicyUnit.Months;
        months.ShouldBe((RetentionPolicyUnit)2);

        Enum.IsDefined(typeof(RetentionPolicyUnit), "Months").ShouldBeTrue();
    }

    // ─── Phase SortOrder ───

    [Fact]
    public void Phase_HasSortOrder()
    {
        var phase = new LifecyclePhase { SortOrder = 5 };
        phase.SortOrder.ShouldBe(5);
    }

    // ─── LifecycleMapping ───

    [Fact]
    public void LifecycleMapping_MapsEntityToDto_WithInlineRetention()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<LifecycleMapping>());
        var mapper = config.CreateMapper();

        var lifecycle = new Lifecycle
        {
            Id = 1,
            Name = "Default",
            ReleaseRetentionUnit = RetentionPolicyUnit.Days,
            ReleaseRetentionQuantity = 30,
            ReleaseRetentionKeepForever = false,
            TentacleRetentionKeepForever = true
        };

        var dto = mapper.Map<LifeCycleDto>(lifecycle);

        dto.Name.ShouldBe("Default");
        dto.ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Days);
        dto.ReleaseRetentionQuantity.ShouldBe(30);
        dto.ReleaseRetentionKeepForever.ShouldBeFalse();
        dto.TentacleRetentionKeepForever.ShouldBeTrue();
    }

    [Fact]
    public void LifecycleMapping_MapsPhaseEntity_IgnoresTargetIds()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<LifecycleMapping>());
        var mapper = config.CreateMapper();

        var phase = new LifecyclePhase
        {
            Id = 1,
            Name = "Dev",
            SortOrder = 0,
            ReleaseRetentionUnit = RetentionPolicyUnit.Months,
            ReleaseRetentionQuantity = 3
        };

        var dto = mapper.Map<LifecyclePhaseDto>(phase);

        dto.Name.ShouldBe("Dev");
        dto.SortOrder.ShouldBe(0);
        dto.AutomaticDeploymentTargetIds.ShouldBeEmpty();
        dto.OptionalDeploymentTargetIds.ShouldBeEmpty();
        dto.ReleaseRetentionUnit.ShouldBe(RetentionPolicyUnit.Months);
    }
}
