using IConfigurationProvider = AutoMapper.IConfigurationProvider;

namespace Squid.Core.Mappings;

public static class AutoMapperConfiguration
{
    public static IMapper Mapper { get; private set; }
    public static IConfigurationProvider MapperConfiguration { get; private set; }

    public static void Init(IConfigurationProvider configurationProvider)
    {
        Mapper = new Mapper(configurationProvider);
        MapperConfiguration = configurationProvider;
        Mapper.ConfigurationProvider.AssertConfigurationIsValid();
    }

    public static void RegisterMappings(IMapperConfigurationExpression cfg)
    {
        cfg.AddProfile<ChannelMapping>();
        cfg.AddProfile<LifecycleMapping>();
        cfg.AddProfile<MachineMapping>();
    }
}