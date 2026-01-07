using Squid.Core.Attributes;
using Squid.Core.Persistence.Db.Postgres;

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
    public PostgresSetting? Postgres { get; set; }
}