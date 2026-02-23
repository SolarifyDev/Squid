namespace Squid.Calamari.Pipeline;

public sealed class CleanupTemporaryFilesStep<TContext> : AlwaysRunExecutionStep<TContext>
    where TContext : ITemporaryFileTrackingExecutionContext
{
    public override Task ExecuteAsync(TContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        foreach (var filePath in context.TemporaryFiles.ToArray())
        {
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            finally
            {
                context.TemporaryFiles.Remove(filePath);
            }
        }

        return Task.CompletedTask;
    }
}
