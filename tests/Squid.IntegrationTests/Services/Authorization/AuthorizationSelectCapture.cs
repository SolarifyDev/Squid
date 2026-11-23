using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Squid.Core.Services.Identity;

namespace Squid.IntegrationTests.Services.Authorization;

internal sealed class AuthorizationSelectCapture : DbCommandInterceptor
{
    public List<Select> Selects { get; } = new();

    public async Task<Action<ContainerBuilder>> CreateRegistrationAsync(ILifetimeScope parent)
    {
        await using var scope = parent.BeginLifetimeScope();
        var options = new DbContextOptionsBuilder<SquidDbContext>()
            .UseNpgsql(scope.Resolve<SquidDbContext>().Database.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(this)
            .Options;

        return builder => builder.Register(context => new SquidDbContext(options, context.Resolve<ICurrentUser>()))
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();
    }

    public override InterceptionResult DataReaderDisposing(
        DbCommand command,
        DataReaderDisposingEventData eventData,
        InterceptionResult result)
    {
        if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            // EF counts the final Read call that returns false for a fully consumed query.
            Selects.Add(new Select(command.CommandText, command.Transaction != null, eventData.ReadCount - 1));
        }

        return result;
    }

    public sealed record Select(string Sql, bool InTransaction, int RowCount);
}
