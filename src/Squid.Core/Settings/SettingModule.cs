namespace Squid.Core.Settings;

public class SettingModule : Module
{
    private readonly IConfiguration _configuration;
    private readonly Assembly[] _assemblies;

    public SettingModule(IConfiguration configuration, params Assembly[] assemblies)
    {
        _configuration = configuration;
        _assemblies = assemblies;
    }

    protected override void Load(ContainerBuilder builder)
    {
        // 注册 IConfiguration，本来就有
        builder.RegisterInstance(_configuration)
            .As<IConfiguration>()
            .SingleInstance();

        var settingTypes = _assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && typeof(IConfigurationSetting).IsAssignableFrom(t))
            .ToArray();

        foreach (var type in settingTypes)
        {
            builder.Register(ctx =>
                {
                    var cfg = ctx.Resolve<IConfiguration>();

                    // 约定：类名去掉末尾的 "Setting" 就是 Section 名
                    var sectionName = type.Name.EndsWith("Setting")
                        ? type.Name[..^"Setting".Length]  // C# 8 range syntax
                        : type.Name;

                    var instance = Activator.CreateInstance(type)!;

                    // 自动绑定整个对象，包括嵌套
                    cfg.GetSection(sectionName).Bind(instance);

                    return instance;
                })
                .As(type)          // AsSelf
                .SingleInstance();
        }
    }
}
