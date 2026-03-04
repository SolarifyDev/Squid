using Squid.Calamari.Execution;
using Squid.Calamari.Scripting;
using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Tests.Calamari.Scripting;

public class ScriptEngineTests
{
    [Fact]
    public async Task ExecuteAsync_UsesMatchingExecutor()
    {
        var executor = new FakeExecutor(ScriptSyntax.Bash, new ScriptExecutionResult(0));
        var engine = new ScriptEngine([executor]);
        var request = CreateRequest();

        var result = await engine.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        executor.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_AppliesDecoratorsInOrder()
    {
        var calls = new List<string>();
        var executor = new FakeExecutor(ScriptSyntax.Bash, new ScriptExecutionResult(0), calls);
        var decoratorA = new RecordingDecorator(order: 10, "A", calls);
        var decoratorB = new RecordingDecorator(order: 20, "B", calls);
        var engine = new ScriptEngine([executor], [decoratorB, decoratorA]);

        _ = await engine.ExecuteAsync(CreateRequest(), CancellationToken.None);

        calls.ShouldBe(
        [
            "A:before",
            "B:before",
            "executor",
            "B:after",
            "A:after"
        ]);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenNoExecutorRegistered()
    {
        var engine = new ScriptEngine(Array.Empty<IScriptExecutor>());

        await Should.ThrowAsync<NotSupportedException>(() =>
            engine.ExecuteAsync(CreateRequest(), CancellationToken.None));
    }

    private static ScriptExecutionRequest CreateRequest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-script-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "test.sh");
        File.WriteAllText(scriptPath, "echo hi\n");

        return new ScriptExecutionRequest
        {
            ScriptPath = scriptPath,
            WorkingDirectory = tempDir,
            Syntax = ScriptSyntax.Bash,
            OutputProcessor = new ScriptOutputProcessor()
        };
    }

    private sealed class FakeExecutor : IScriptExecutor
    {
        private readonly ScriptSyntax _syntax;
        private readonly ScriptExecutionResult _result;
        private readonly List<string>? _calls;

        public FakeExecutor(ScriptSyntax syntax, ScriptExecutionResult result, List<string>? calls = null)
        {
            _syntax = syntax;
            _result = result;
            _calls = calls;
        }

        public int Calls { get; private set; }

        public bool CanExecute(ScriptSyntax syntax) => syntax == _syntax;

        public Task<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Calls++;
            _calls?.Add("executor");
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingDecorator : IScriptDecorator
    {
        private readonly string _name;
        private readonly List<string> _calls;

        public RecordingDecorator(int order, string name, List<string> calls)
        {
            Order = order;
            _name = name;
            _calls = calls;
        }

        public int Order { get; }

        public bool IsEnabled(ScriptExecutionRequest request) => true;

        public async Task<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            ScriptExecutionDelegate next,
            CancellationToken ct)
        {
            _calls.Add($"{_name}:before");
            var result = await next(request, ct);
            _calls.Add($"{_name}:after");
            return result;
        }
    }
}
