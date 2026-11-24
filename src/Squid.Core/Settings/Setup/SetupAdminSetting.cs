namespace Squid.Core.Settings.Setup;

/// <summary>
/// The administrator chosen in the setup wizard, stored as a pair (name + password hash) in the
/// data directory's configuration file under its own section. Kept apart from
/// <see cref="BootstrapAdminSetting"/> on purpose: start parameters (<c>SQUID_ADMIN_USER</c>) rank
/// above the file, and must not be able to rename the account the wizard just created.
/// </summary>
public class SetupAdminSetting : IConfigurationSetting
{
    public const string SectionName = "Setup:Admin";

    public SetupAdminSetting() { }

    public SetupAdminSetting(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        UserName = section.GetValue<string>(nameof(UserName)) ?? string.Empty;
        PasswordHash = section.GetValue<string>(nameof(PasswordHash)) ?? string.Empty;
    }

    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(PasswordHash);
}
