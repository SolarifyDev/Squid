namespace Squid.Calamari.Scripting;

public sealed class EnsureScriptFileExistsDecorator : IScriptDecorator
{
    public int Order => 0;

    public bool IsEnabled(ScriptExecutionRequest request) => true;

    public Task<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        ScriptExecutionDelegate next,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ScriptPath))
            throw new ArgumentException("Script path is required.", nameof(request));

        if (!File.Exists(request.ScriptPath))
            throw new FileNotFoundException($"Script file not found: {request.ScriptPath}", request.ScriptPath);

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory))
            throw new ArgumentException("Working directory is required.", nameof(request));

        if (!Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory not found: {request.WorkingDirectory}");

        return next(request, ct);
    }
}
