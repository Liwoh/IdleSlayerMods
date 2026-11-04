using System;
using AutoAdventurer.Diagnostics;
using Il2Cpp;

namespace AutoAdventurer.Gameplay;

// Silver Death adds saws to Bonus Stages; the Ascension upgrade Silver Spirit
// removes them in Spirit Boost. An Ultra Ascension resets the upgrade, so
// Silver Death is kept off until the tree buys it again, then restored. Only
// a Silver Death this service turned off is restored, and a manual change
// is respected until the next Ultra Ascension.
internal sealed class SilverDeathService
{
    private const float CheckIntervalSeconds = 5f;
    private float nextCheckTime;
    private double manualOverrideUltraAscensions = -1d;
    private bool waitLogged;
    private bool stateLogged;

    internal void Tick(float now, bool enabled)
    {
        if (!enabled || now < nextCheckTime) return;
        nextCheckTime = now + CheckIntervalSeconds;

        GameStates state = GameState.current;
        if (state != GameStates.RunnerMode && state != GameStates.RageMode) return;

        AscensionSkill upgrade = AscensionSkills.list != null ? AscensionSkills.list.SilverSpirit : null;
        Divinity silverDeath = Divinities.list != null ? Divinities.list.SilverDeath : null;
        DivinitiesManager manager = DivinitiesManager.instance;
        PlayerInventory inventory = PlayerInventory.instance;
        if (upgrade == null || silverDeath == null || manager == null || inventory == null) return;

        try
        {
            bool bought = upgrade.unlocked;
            bool active = silverDeath.unlocked;
            bool restorePending = Plugin.Config.SilverDeathRestorePending.Value;
            if (!stateLogged)
            {
                stateLogged = true;
                AdventurerLog.User(
                    $"Silver Death guard: saw upgrade bought={bought}, Silver Death active={active}, restore pending={restorePending}.");
            }

            if (manualOverrideUltraAscensions >= 0d &&
                inventory.ultraAscensions > manualOverrideUltraAscensions)
                manualOverrideUltraAscensions = -1d;

            if (!bought)
            {
                if (!active || manualOverrideUltraAscensions >= 0d) return;
                if (restorePending)
                {
                    // Turned back on by the player while the upgrade is missing.
                    manualOverrideUltraAscensions = inventory.ultraAscensions;
                    Plugin.Config.SilverDeathRestorePending.Value = false;
                    AdventurerLog.User("Silver Death turned on manually: left as is until the next Ultra Ascension.");
                    return;
                }
                if (!SetDivinityState(manager, silverDeath, false)) return;
                Plugin.Config.SilverDeathRestorePending.Value = true;
                AdventurerLog.User("Silver Death disabled: the Ascension upgrade that removes its saws in Spirit Boost is not bought yet.");
                return;
            }

            if (!restorePending) return;
            if (active)
            {
                Plugin.Config.SilverDeathRestorePending.Value = false;
                return;
            }
            double available = manager.CalculateDivinityPointsLeft();
            if (!silverDeath.IsAvailable() || available < silverDeath.cost)
            {
                if (!waitLogged)
                {
                    waitLogged = true;
                    AdventurerLog.User(
                        $"Silver Death restore waiting: available={silverDeath.IsAvailable()}, divinityPointsLeft={available:0}, cost={silverDeath.cost:0}.");
                }
                return;
            }
            if (!SetDivinityState(manager, silverDeath, true)) return;
            Plugin.Config.SilverDeathRestorePending.Value = false;
            waitLogged = false;
            AdventurerLog.User("Silver Death restored: the saw upgrade is bought again.");
        }
        catch (Exception exception)
        {
            AdventurerLog.Debug(
                $"Silver Death guard deferred safely; exception={exception.GetType().Name}.", "Gameplay");
        }
    }

    private static bool SetDivinityState(DivinitiesManager manager, Divinity divinity, bool enabled)
    {
        if (divinity.unlocked == enabled) return true;
        if (enabled) manager.ActivateDivinity(divinity.name);
        else manager.DeactivateDivinity(divinity.name);
        if (divinity.unlocked != enabled)
            divinity.unlocked = enabled;
        divinity.SaveData();
        manager.CheckAvailableDarkDivinities();
        if (divinity.divinityObjectComponent != null)
            divinity.divinityObjectComponent.RefreshColors();
        return divinity.unlocked == enabled;
    }
}
