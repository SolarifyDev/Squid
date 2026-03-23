using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines;

public class DefaultMachinePolicySeeder : IStartable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILifetimeScope _scope;

    public DefaultMachinePolicySeeder(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        try
        {
            SeedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Default machine policy seeding failed — will retry on next startup");
        }
    }

    private async Task SeedAsync()
    {
        await using var scope = _scope.BeginLifetimeScope();

        var repository = scope.Resolve<IRepository>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();

        var existing = await repository.FirstOrDefaultAsync<MachinePolicy>(p => p.IsDefault).ConfigureAwait(false);

        if (existing != null) return;

        try
        {
            var policy = BuildDefaultPolicy();

            await repository.InsertAsync(policy).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            Log.Information("Seeded default machine policy {PolicyName}", policy.Name);
        }
        catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            Log.Debug("Default machine policy was already created by another instance");
        }
    }

    internal static MachinePolicy BuildDefaultPolicy()
    {
        return new MachinePolicy
        {
            SpaceId = 1,
            Name = "Default Machine Policy",
            Description = "This policy is automatically applied to machines that are created when no policy is specified. Any other machine policies will inherit this policy's custom health check scripts unless they specify their own.",
            IsDefault = true,
            MachineHealthCheckPolicy = JsonSerializer.Serialize(new MachineHealthCheckPolicyDto(), JsonOptions),
            MachineConnectivityPolicy = JsonSerializer.Serialize(new MachineConnectivityPolicyDto(), JsonOptions),
            MachineCleanupPolicy = JsonSerializer.Serialize(new MachineCleanupPolicyDto(), JsonOptions),
            MachineUpdatePolicy = JsonSerializer.Serialize(new MachineUpdatePolicyDto(), JsonOptions),
            MachineRpcCallRetryPolicy = JsonSerializer.Serialize(new MachineRpcCallRetryPolicyDto(), JsonOptions),
        };
    }
}
