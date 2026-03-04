using Squid.Calamari.Pipeline;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Pipeline;

public class ExecutionPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_RunsStepsInOrder_AndSkipsDisabledSteps()
    {
        var calls = new List<string>();
        var context = new PipelineProbeContext { InputPath = "/tmp/file.txt", VariablesPath = "/tmp/vars.json" };
        var pipeline = new ExecutionPipeline<PipelineProbeContext>(
        [
            new RecordingStep("A", calls),
            new DisabledRecordingStep("B", calls),
            new RecordingStep("C", calls)
        ]);

        await pipeline.ExecuteAsync(context, CancellationToken.None);

        calls.ShouldBe(["A", "C"]);
    }

    [Fact]
    public async Task ExecuteAsync_RunsAlwaysRunSteps_AfterFailure()
    {
        var calls = new List<string>();
        var context = new PipelineProbeContext { InputPath = "/tmp/file.txt", VariablesPath = "/tmp/vars.json" };
        var pipeline = new ExecutionPipeline<PipelineProbeContext>(
        [
            new RecordingStep("A", calls),
            new ThrowingStep("Boom", calls),
            new AlwaysRunRecordingStep("Cleanup", calls)
        ]);

        await Should.ThrowAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(context, CancellationToken.None));

        calls.ShouldBe(["A", "Boom", "Cleanup"]);
    }

    [Fact]
    public async Task ResolveWorkingDirectoryStep_SetsDirectoryFromInputPath()
    {
        var context = new PipelineProbeContext
        {
            InputPath = Path.Combine(Path.GetTempPath(), "x", "file.txt"),
            VariablesPath = "/tmp/vars.json"
        };

        var step = new ResolveWorkingDirectoryStep<PipelineProbeContext>();
        await step.ExecuteAsync(context, CancellationToken.None);

        context.WorkingDirectory.ShouldBe(Path.GetDirectoryName(Path.GetFullPath(context.InputPath)));
    }

    [Fact]
    public async Task LoadVariablesFromFilesStep_LoadsVariableSet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var varsPath = Path.Combine(tempDir, "variables.json");
            File.WriteAllText(varsPath, "{\"A\":\"1\"}");

            var context = new PipelineProbeContext
            {
                InputPath = Path.Combine(tempDir, "dummy.txt"),
                VariablesPath = varsPath
            };

            var step = new LoadVariablesFromFilesStep<PipelineProbeContext>();
            await step.ExecuteAsync(context, CancellationToken.None);

            context.Variables.ShouldNotBeNull();
            context.Variables.Get("A").ShouldBe("1");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupTemporaryFilesStep_DeletesTrackedFiles_AndClearsList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tempFile = Path.Combine(tempDir, "temp.txt");
            File.WriteAllText(tempFile, "x");

            var context = new PipelineProbeContext
            {
                InputPath = Path.Combine(tempDir, "dummy.txt"),
                VariablesPath = Path.Combine(tempDir, "vars.json")
            };
            context.TemporaryFiles.Add(tempFile);

            var step = new CleanupTemporaryFilesStep<PipelineProbeContext>();
            await step.ExecuteAsync(context, CancellationToken.None);

            File.Exists(tempFile).ShouldBeFalse();
            context.TemporaryFiles.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class PipelineProbeContext : IPathBasedExecutionContext, IVariableLoadingExecutionContext, ITemporaryFileTrackingExecutionContext
    {
        public required string InputPath { get; init; }
        public string? WorkingDirectory { get; set; }

        public required string VariablesPath { get; init; }
        public string? SensitivePath { get; init; }
        public string? Password { get; init; }
        public VariableSet? Variables { get; set; }
        public ICollection<string> TemporaryFiles { get; } = new List<string>();
    }

    private class RecordingStep : ExecutionStep<PipelineProbeContext>
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public RecordingStep(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public override Task ExecuteAsync(PipelineProbeContext context, CancellationToken ct)
        {
            _calls.Add(_name);
            return Task.CompletedTask;
        }
    }

    private sealed class DisabledRecordingStep : RecordingStep
    {
        public DisabledRecordingStep(string name, List<string> calls) : base(name, calls)
        {
        }

        public override bool IsEnabled(PipelineProbeContext context) => false;
    }

    private sealed class ThrowingStep : ExecutionStep<PipelineProbeContext>
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public ThrowingStep(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public override Task ExecuteAsync(PipelineProbeContext context, CancellationToken ct)
        {
            _calls.Add(_name);
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class AlwaysRunRecordingStep : AlwaysRunExecutionStep<PipelineProbeContext>
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public AlwaysRunRecordingStep(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public override Task ExecuteAsync(PipelineProbeContext context, CancellationToken ct)
        {
            _calls.Add(_name);
            return Task.CompletedTask;
        }
    }
}
