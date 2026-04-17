using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Squid.Tentacle.Security.Admission;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class LocalScriptServiceAdmissionTests
{
    [Fact]
    public void StartScript_PolicyDenies_Returns403_NoProcessSpawned()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule
                {
                    Id = "no-rm-rf",
                    DenyScriptBodyRegex = { @"rm\s+-rf\s+/" },
                    Message = "Destructive disk op prohibited"
                }
            }
        };
        var source = new FakeAdmissionSource(policy);
        var service = new LocalScriptService(new ScriptStateStoreFactory(), source);

        var command = MakeCommand(@"rm -rf /");
        var response = service.StartScript(command);

        response.State.ShouldBe(ProcessState.Complete);
        response.ExitCode.ShouldBe(-403);
        response.Logs.ShouldContain(l => l.Text.Contains("no-rm-rf"));

        var workDir = Path.Combine(Path.GetTempPath(), $"squid-tentacle-{command.ScriptTicket.TaskId}");
        Directory.Exists(workDir).ShouldBeFalse("admission denial must not create the workspace");
    }

    [Fact]
    public void StartScript_PolicyAllows_ProceedsNormally()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule { Id = "no-rm-rf", DenyScriptBodyRegex = { @"rm\s+-rf\s+/" } }
            }
        };
        var source = new FakeAdmissionSource(policy);
        var service = new LocalScriptService(new ScriptStateStoreFactory(), source);

        var command = MakeCommand("echo safe");
        var response = service.StartScript(command);

        response.ExitCode.ShouldNotBe(-403);
        response.State.ShouldBeOneOf(ProcessState.Running, ProcessState.Complete);

        service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));
    }

    [Fact]
    public void StartScript_NoAdmissionSource_AllowsEverything()
    {
        var service = new LocalScriptService();   // no source injected

        var command = MakeCommand("echo uncensored");
        var response = service.StartScript(command);

        response.ExitCode.ShouldNotBe(-403);
        service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));
    }

    [Fact]
    public void StartScript_EmptyPolicy_AllowsEverything()
    {
        var source = new FakeAdmissionSource(AdmissionPolicy.Empty());
        var service = new LocalScriptService(new ScriptStateStoreFactory(), source);

        var command = MakeCommand("echo ok");
        var response = service.StartScript(command);

        response.ExitCode.ShouldNotBe(-403);
        service.CancelScript(new CancelScriptCommand(command.ScriptTicket, 0));
    }

    [Fact]
    public void StartScript_PolicyEvaluationThrows_FailsClosed()
    {
        var policy = new AdmissionPolicy
        {
            Rules =
            {
                new AdmissionRule { Id = "bad-regex", DenyScriptBodyRegex = { "[unclosed" } }
            }
        };
        var source = new FakeAdmissionSource(policy);
        var service = new LocalScriptService(new ScriptStateStoreFactory(), source);

        var command = MakeCommand("echo x");
        var response = service.StartScript(command);

        response.State.ShouldBe(ProcessState.Complete);
        response.ExitCode.ShouldBe(-403, "broken policy must fail closed, not silently allow");
    }

    private static StartScriptCommand MakeCommand(string scriptBody)
    {
        return new StartScriptCommand(
            new ScriptTicket(Guid.NewGuid().ToString("N")),
            scriptBody,
            ScriptIsolationLevel.NoIsolation,
            TimeSpan.FromMinutes(5),
            null,
            Array.Empty<string>(),
            null,
            TimeSpan.Zero);
    }

    private sealed class FakeAdmissionSource : IAdmissionPolicySource
    {
        public AdmissionPolicy Current { get; }
        public event Action<AdmissionPolicy>? Updated;

        public FakeAdmissionSource(AdmissionPolicy policy)
        {
            Current = policy;
        }
    }
}
