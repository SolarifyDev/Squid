using Squid.Core.Setup.Embedded;
using Squid.Core.Settings.Setup;
using Squid.Message.Models.Setup;

namespace Squid.Core.Setup.Install;

/// <summary>
/// Turns a validated setup request into a working install: brings up (or checks) the database and
/// Redis, runs the schema migrations, then writes the resulting configuration into the data
/// directory. Writing the configuration is the LAST step, so a failure anywhere earlier leaves the
/// install still in setup mode and the wizard can simply be submitted again.
/// </summary>
public sealed partial class SetupInstaller
{
    public static readonly IReadOnlyList<(string Key, string Title)> Steps = new[]
    {
        (StepKeys.Database, "Prepare PostgreSQL"),
        (StepKeys.Redis, "Prepare Redis"),
        (StepKeys.Migrate, "Create database schema"),
        (StepKeys.Persist, "Save configuration")
    };

    private readonly EmbeddedServiceHost _embedded;
    private readonly IConfiguration _configuration;
    private readonly SetupProgressTracker _tracker;

    private SetupInstallContext _ctx;

    public SetupInstaller(EmbeddedServiceHost embedded, IConfiguration configuration, SetupProgressTracker tracker)
    {
        _embedded = embedded;
        _configuration = configuration;
        _tracker = tracker;
    }

    public async Task InstallAsync(SetupInstallRequest request, CancellationToken ct)
    {
        try
        {
            _ctx = BuildContext(request);

            await RunStepAsync(StepKeys.Database, PrepareDatabaseAsync, ct);
            await RunStepAsync(StepKeys.Redis, PrepareRedisAsync, ct);
            await RunStepAsync(StepKeys.Migrate, MigrateDatabaseAsync, ct);
            await RunStepAsync(StepKeys.Persist, PersistConfigurationAsync, ct);

            _tracker.Complete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "First-run setup failed");
            _tracker.Fail(ex.Message);
            throw;
        }
    }

    private async Task RunStepAsync(string key, Func<CancellationToken, Task<string>> step, CancellationToken ct)
    {
        _tracker.Mark(key, SetupStepStatus.Running);

        try
        {
            var message = await step(ct).ConfigureAwait(false);

            _tracker.Mark(key, SetupStepStatus.Succeeded, message);
        }
        catch (Exception ex)
        {
            _tracker.Mark(key, SetupStepStatus.Failed, ex.Message);
            throw;
        }
    }

    public static class StepKeys
    {
        public const string Database = "database";
        public const string Redis = "redis";
        public const string Migrate = "migrate";
        public const string Persist = "persist";
    }
}
