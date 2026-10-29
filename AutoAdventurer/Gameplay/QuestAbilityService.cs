using System;
using AutoAdventurer.Diagnostics;
using Il2Cpp;

namespace AutoAdventurer.Gameplay;

// Wind Dash kill quests only progress while Wind Dash is the selected ability,
// and the Boost/Wind Dash automation always uses the selected one. Select Wind
// Dash for such a quest and restore the player's previous ability afterwards.
internal sealed class QuestAbilityService
{
    private const float CheckIntervalSeconds = 1f;
    private float nextCheckTime;
    private string restoreAbility = string.Empty;

    internal void Tick(float now, bool needWindDash)
    {
        if (now < nextCheckTime) return;
        nextCheckTime = now + CheckIntervalSeconds;
        if (!needWindDash && restoreAbility.Length == 0) return;

        AbilitiesManager manager = AutomaticBoostService.ResolveAbilitiesManager();
        if (manager == null || manager.selectedAbility == null) return;
        string selected = AutomaticBoostService.GetAbilityName(manager.selectedAbility);

        if (needWindDash)
        {
            if (manager.windDash == null || !manager.windDash.Unlocked() ||
                selected == nameof(WindDash)) return;
            if (restoreAbility.Length == 0) restoreAbility = selected;
            if (SwitchTo(manager, nameof(WindDash)))
                AdventurerLog.User(
                    $"Wind Dash selected for a Wind Dash kill quest; {restoreAbility} will be restored afterwards.");
            return;
        }

        string target = restoreAbility;
        restoreAbility = string.Empty;
        if (selected != target && SwitchTo(manager, target))
            AdventurerLog.User($"{target} restored after the Wind Dash kill quest.");
    }

    internal void Reset() => restoreAbility = string.Empty;

    private static bool SwitchTo(AbilitiesManager manager, string abilityName)
    {
        int attempts = manager.abilities != null ? manager.abilities.Length : 2;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                manager.SwitchToNextAbility();
            }
            catch (Exception exception)
            {
                AdventurerLog.Exception("Ability switch", exception);
                return false;
            }

            if (AutomaticBoostService.GetAbilityName(manager.selectedAbility) == abilityName)
                return true;
        }
        return false;
    }
}
