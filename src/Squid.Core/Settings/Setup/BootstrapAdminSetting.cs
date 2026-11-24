namespace Squid.Core.Settings.Setup;

/// <summary>
/// The administrator an operator passes as start parameters (<c>SQUID_ADMIN_USER</c> /
/// <c>SQUID_ADMIN_PASSWORD</c>), used only to create the very first account of a fresh install.
/// It never changes or re-creates an existing account.
/// </summary>
public class BootstrapAdminSetting : IConfigurationSetting
{
    public const string SectionName = "Bootstrap:Admin";

    public BootstrapAdminSetting() { }

    public BootstrapAdminSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        UserName = section.GetValue<string>(nameof(UserName)) ?? string.Empty;
        Password = section.GetValue<string>(nameof(Password)) ?? string.Empty;
    }

    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
