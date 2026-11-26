namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Optional capability of an <see cref="IMachineUpgradeStrategy"/> whose upgrade script bounds its own run time: the
/// longest the script can take from dispatch until it writes its terminal status, INCLUDING a full rollback. The
/// upgrade-progress polling keeps refreshing the agent's upgrade events until that terminal status is seen, and uses
/// this as its ceiling. A strategy without it gets <see cref="UpgradeProgressPoller.DefaultCeiling"/>.
/// </summary>
public interface IUpgradeDurationBudget
{
    TimeSpan WorstCaseDuration { get; }
}
