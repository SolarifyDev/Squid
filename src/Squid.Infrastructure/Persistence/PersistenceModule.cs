namespace Squid.Infrastructure.Persistence;

public class PersistenceModule : Module
{
    private readonly SquidStoreSetting _squidStoreSetting;

    public PersistenceModule(SquidStoreSetting squidStoreSetting)
    {
        _squidStoreSetting = squidStoreSetting;
    }

    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(_ => new SquidDbContext(_squidStoreSetting))
            .AsSelf().As<ISquidDbContext>()
            .InstancePerLifetimeScope();
    }
}