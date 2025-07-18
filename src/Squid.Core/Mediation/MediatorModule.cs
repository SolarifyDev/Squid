using Squid.Core.Middlewares.UnitOfWork;

namespace Squid.Core.Mediation;

public class MediatorModule : Module
{
    private readonly Assembly[] _assemblies;

    public MediatorModule(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        _assemblies = assemblies;
    }

    protected override void Load(ContainerBuilder builder)
    {
        var mediatorBuilder = new MediatorBuilder();

        mediatorBuilder.RegisterHandlers(_assemblies);
        mediatorBuilder.ConfigureGlobalReceivePipe(c =>
        {
            c.UseLogger();
            c.UseUnitOfWork();
            c.UseMessageValidator();
        });

        builder.RegisterMediator(mediatorBuilder);
        
        builder.RegisterAssemblyTypes(_assemblies)
            .Where(t => t.IsClass && typeof(IFluentMessageValidator).IsAssignableFrom(t))
            .AsSelf().AsImplementedInterfaces();
    }
}