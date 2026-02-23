using Squid.Calamari.Execution;
using Squid.Calamari.Scripting;

namespace Squid.Calamari.Tests.Calamari.Scripting;

public class EnsureScriptFileExistsDecoratorTests
{
    [Fact]
    public async Task ExecuteAsync_ThrowsWhenScriptMissing()
    {
        var decorator = new EnsureScriptFileExistsDecorator();
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-missing-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var request = new ScriptExecutionRequest
        {
            ScriptPath = Path.Combine(tempDir, "missing.sh"),
            WorkingDirectory = tempDir,
            Syntax = ScriptSyntax.Bash,
            OutputProcessor = new ScriptOutputProcessor()
        };

        await Should.ThrowAsync<FileNotFoundException>(() =>
            decorator.ExecuteAsync(
                request,
                (r, ct) => Task.FromResult(new ScriptExecutionResult(0)),
                CancellationToken.None));
    }
}
