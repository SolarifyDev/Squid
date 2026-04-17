using Shouldly;
using Squid.Tentacle.Security.Admission;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Security.Admission;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class AdmissionPolicyTests
{
    [Fact]
    public void Empty_Policy_AllowsEverything()
    {
        var policy = AdmissionPolicy.Empty();

        var decision = policy.Evaluate(new AdmissionContext("rm -rf /", "NoIsolation", null));

        decision.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void DenyRegex_MatchingScript_IsDenied()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule
                {
                    Id = "no-destructive",
                    DenyScriptBodyRegex = { @"rm\s+-rf\s+/" },
                    Message = "Destructive disk operations prohibited"
                }
            }
        };

        var decision = policy.Evaluate(new AdmissionContext("echo warmup\nrm -rf /\nmore", "NoIsolation", null));

        decision.Allowed.ShouldBeFalse();
        decision.RuleId.ShouldBe("no-destructive");
        decision.Reason.ShouldContain("Destructive");
    }

    [Fact]
    public void DenyRegex_NonMatching_IsAllowed()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule { Id = "destructive", DenyScriptBodyRegex = { @"rm\s+-rf\s+/" } }
            }
        };

        var decision = policy.Evaluate(new AdmissionContext("echo safe", "NoIsolation", null));

        decision.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void RequireIsolation_ScopedToMutex_DeniesWrongLevel()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule
                {
                    Id = "prod-db-full-iso",
                    WhenIsolationMutexName = { "production-db" },
                    RequireIsolationLevel = "FullIsolation",
                    Message = "production-db requires FullIsolation"
                }
            }
        };

        var deny = policy.Evaluate(new AdmissionContext("echo migration", "NoIsolation", "production-db"));
        deny.Allowed.ShouldBeFalse();
        deny.RuleId.ShouldBe("prod-db-full-iso");

        var allow = policy.Evaluate(new AdmissionContext("echo migration", "FullIsolation", "production-db"));
        allow.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void RequireIsolation_OnlyScopedToMatchingMutex()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule
                {
                    Id = "prod-only",
                    WhenIsolationMutexName = { "production-db" },
                    RequireIsolationLevel = "FullIsolation"
                }
            }
        };

        var decision = policy.Evaluate(new AdmissionContext("echo ok", "NoIsolation", "test-db"));

        decision.Allowed.ShouldBeTrue("the rule's WhenIsolationMutexName does not match, so the rule must not fire");
    }

    [Fact]
    public void FirstMatchingRule_Wins()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule { Id = "first", DenyScriptBodyRegex = { @"dangerous" }, Message = "first hit" },
                new AdmissionRule { Id = "second", DenyScriptBodyRegex = { @"dangerous" }, Message = "second hit" }
            }
        };

        var decision = policy.Evaluate(new AdmissionContext("run dangerous script", "NoIsolation", null));

        decision.RuleId.ShouldBe("first");
    }

    [Fact]
    public void BadRegex_IsSurfacedAsException_NotSilentlyAllowed()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule { Id = "bad", DenyScriptBodyRegex = { @"[unclosed" } }
            }
        };

        // Compile happens lazily inside Evaluate; a bad regex throws at match time.
        Should.Throw<Exception>(() => policy.Evaluate(new AdmissionContext("anything", "NoIsolation", null)));
    }
}
