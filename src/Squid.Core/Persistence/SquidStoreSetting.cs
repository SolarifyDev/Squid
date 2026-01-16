using Squid.Core.Attributes;

namespace Squid.Core.Persistence;

public class SquidStoreSetting: IConfigurationSetting
{
    public enum SquidStoreType
    {
        /// <summary>
        /// Non-persistent Squid store
        /// </summary>
        Volatile,

        /// <summary>
        /// Postgres based persistent Squid store.
        /// </summary>
        Postgres,

        /// <summary>
        /// MySql based persistent Squid store.
        /// </summary>
        MySql
    }

    public SquidStoreType Type { get; set; } = SquidStoreType.Volatile;

    [RequiredOnPropertyValue(nameof(Type), SquidStoreType.Postgres)]
    public ConnectionSetting? Postgres { get; set; }
    
    [RequiredOnPropertyValue(nameof(Type), SquidStoreType.MySql)]
    public ConnectionSetting? MySql { get; set; }
}