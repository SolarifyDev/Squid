namespace Squid.Calamari.Scripting;

public sealed class ScriptEngine : IScriptEngine
{
    private readonly IReadOnlyList<IScriptExecutor> _executors;
    private readonly IReadOnlyList<IScriptDecorator> _decorators;

    public ScriptEngine()
        : this(
            [new BashScriptExecutorAdapter()],
            [new EnsureScriptFileExistsDecorator()])
    {
    }

    public ScriptEngine(IEnumerable<IScriptExecutor> executors, IEnumerable<IScriptDecorator>? decorators = null)
    {
        _executors = executors?.ToArray() ?? Array.Empty<IScriptExecutor>();
        _decorators = decorators?.OrderBy(d => d.Order).ToArray() ?? Array.Empty<IScriptDecorator>();
    }

    public Task<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var executor = _executors.FirstOrDefault(e => e.CanExecute(request.Syntax))
                       ?? throw new NotSupportedException($"No script executor registered for syntax '{request.Syntax}'.");

        ScriptExecutionDelegate pipeline = (req, token) => executor.ExecuteAsync(req, token);

        foreach (var decorator in _decorators.Reverse())
        {
            var next = pipeline;
            pipeline = (req, token) =>
            {
                if (!decorator.IsEnabled(req))
                    return next(req, token);

                return decorator.ExecuteAsync(req, next, token);
            };
        }

        return pipeline(request, ct);
    }
}
