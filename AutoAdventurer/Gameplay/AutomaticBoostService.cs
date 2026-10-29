using System;
using AutoAdventurer.Diagnostics;
using AutoAdventurer.Runtime;
using AutoAdventurer.World;
using Il2Cpp;
using UnityEngine;

namespace AutoAdventurer.Gameplay;

internal sealed class AutomaticBoostService
{
    private readonly WorldInterruptionService interruptions = new();
    private const float CheckIntervalSeconds = 0.1f;
    private const float PostAscensionDelaySeconds = 5f;
    // MainScreenGuard already owns the post-transition stabilization delay.
    // Keeping a second delay here made Auto Boost wait again after the screen
    // had already been declared safe.
    private const float RunnerStableSeconds = 0f;
    private const float RepeatedActionLogIntervalSeconds = 30f;
    private const float StationaryGroundFallbackSeconds = 0.25f;
    private const float StationaryVerticalTolerance = 0.0025f;
    private const int RequiredStableAbilityChecks = 3;

    private float nextCheckTime;
    private float suspendedUntil;
    private float runnerValidSince = -1f;
    private float cooldownReadySince = -1f;
    private float nextTriggerLogTime;
    private float nextUnavailableLogTime;
    private bool hasFirstSkillState;
    private bool previousFirstSkillBought;
    private string stableAbilityType = string.Empty;
    private int stableAbilityChecks;
    private bool managerMissingLogged;
    private bool immediateActivationRequested;
    private float lastPlayerY;
    private float verticallyStationarySince = -1f;
    private bool hasPlayerY;

    internal void RequestImmediateActivation(float now)
    {
        immediateActivationRequested = true;
        runnerValidSince = now - RunnerStableSeconds;
    }

    internal void Tick(float now, bool enabled, bool suppressMovementForBox)
    {
        if (!enabled)
        {
            immediateActivationRequested = false;
            runnerValidSince = -1f;
            ResetAbilityStability();
            return;
        }

        if (!IsGameplayStateStable(now)) return;
        ObserveAscensionReset(now);
        if (now < nextCheckTime) return;
        nextCheckTime = now + CheckIntervalSeconds;

        try
        {
            // Resolve the authoritative singleton every pass. Ascension can
            // rebuild ability objects without forcing GameState out of Runner.
            AbilitiesManager manager = ResolveAbilitiesManager();
            if (manager == null)
            {
                ResetAbilityStability();
                if (!managerMissingLogged)
                {
                    managerMissingLogged = true;
                    AdventurerLog.MovementDebug(
                        "AbilitiesManager is not available yet.");
                }
                return;
            }

            managerMissingLogged = false;
            Ability selected = manager.selectedAbility;
            bool immediate = immediateActivationRequested;
            if (immediate)
            {
                if (!IsSupportedAbility(selected))
                {
                    LogUnavailableAbility(now, selected);
                    immediateActivationRequested = false;
                    ResetAbilityStability();
                    return;
                }
            }
            else if (!TrackStableSupportedAbility(selected))
            {
                if (!IsSupportedAbility(selected)) LogUnavailableAbility(now, selected);
                return;
            }

            if (now < suspendedUntil) return;
            if (!selected.Unlocked())
            {
                immediateActivationRequested = false;
                ResetAbilityStability();
                return;
            }
            if (selected.GetCurrentCooldown() > 0d)
            {
                immediateActivationRequested = false;
                cooldownReadySince = -1f;
                return;
            }

            if (!immediate && cooldownReadySince < 0f)
            {
                cooldownReadySince = now;
                return;
            }

            double activationDelay = Math.Max(0d,
                Plugin.Config.AutoBoostActivationDelaySecondsValue);
            if (!immediate && now - cooldownReadySince < activationDelay) return;

            // Without the corresponding horizontal box magnet, either
            // movement ability can change the approach speed while the jump
            // helper is timing an intercept. Preserve the ready cooldown and
            // retry after the box clears. Chest Hunt Keys still suppress only
            // Wind Dash; normal Boost does not outrun that encounter.
            if (suppressMovementForBox ||
                (IsWindDash(selected) && interruptions.HasChestHuntKey))
            {
                nextCheckTime = now;
                return;
            }

            // Wind Dash can pass above portals and elite enemies when it is
            // activated during a jump. Ground contact is map-independent and
            // is therefore safer than comparing an absolute world Y value.
            // Once the ability is ready, poll every frame until the player is
            // back at the safe height; no additional activation delay is used.
            if (IsWindDash(selected) &&
                Plugin.Config.WindDashRequireGrounded.Value &&
                !IsWindDashHeightSafe(now))
            {
                nextCheckTime = now;
                return;
            }

            // Ability.Activate() does not reliably publish its cooldown before
            // the next managed poll. Mirror the proven game-mod path and write
            // the authoritative cooldown immediately.
            selected.Activate();
            selected.currentCd = selected.GetCooldown();
            immediateActivationRequested = false;
            cooldownReadySince = -1f;
            if (now >= nextTriggerLogTime)
            {
                nextTriggerLogTime = now + RepeatedActionLogIntervalSeconds;
                AdventurerLog.MovementDebug(
                    $"Ability triggered: selected={GetAbilityName(selected)}; cooldown={selected.currentCd:0.###}s.");
            }
        }
        catch (Exception exception)
        {
            AdventurerLog.Exception(
                "Movement ability automation", exception);
            SuspendAndClear(now, PostAscensionDelaySeconds);
        }
    }

    private bool IsGameplayStateStable(float now)
    {
        GameStates state = GameState.current;
        if (state != GameStates.RunnerMode &&
            state != GameStates.RageMode)
        {
            runnerValidSince = -1f;
            ResetAbilityStability();
            return false;
        }

        if (runnerValidSince < 0f)
        {
            runnerValidSince = now;
            ResetAbilityStability();
            return false;
        }

        return now - runnerValidSince >= RunnerStableSeconds;
    }

    private void ObserveAscensionReset(float now)
    {
        Upgrade firstSkill = Upgrades.list?.RandomBox;
        if (firstSkill == null)
        {
            hasFirstSkillState = false;
            return;
        }

        bool bought = firstSkill.bought;
        if (!hasFirstSkillState)
        {
            hasFirstSkillState = true;
            previousFirstSkillBought = bought;
            return;
        }

        if (previousFirstSkillBought && !bought)
        {
            SuspendAndClear(now, PostAscensionDelaySeconds);
            AdventurerLog.MovementDebug(
                $"Ascension detected; ability automation suspended for {PostAscensionDelaySeconds:0.#} seconds.");
        }

        previousFirstSkillBought = bought;
    }

    private bool TrackStableSupportedAbility(Ability selected)
    {
        if (!IsSupportedAbility(selected))
        {
            ResetAbilityStability();
            return false;
        }

        string typeName = GetAbilityName(selected);
        if (!string.Equals(typeName, stableAbilityType, StringComparison.Ordinal))
        {
            stableAbilityType = typeName;
            stableAbilityChecks = 1;
            AdventurerLog.MovementDebug(
                $"Selected ability observed: {typeName}.");
            return false;
        }

        if (stableAbilityChecks < RequiredStableAbilityChecks)
            stableAbilityChecks++;

        return stableAbilityChecks >= RequiredStableAbilityChecks;
    }

    private void SuspendAndClear(float now, float seconds)
    {
        suspendedUntil = Math.Max(suspendedUntil, now + Math.Max(0f, seconds));
        ResetAbilityStability();
        managerMissingLogged = false;
    }

    private void ResetAbilityStability()
    {
        stableAbilityType = string.Empty;
        stableAbilityChecks = 0;
        cooldownReadySince = -1f;
    }

    internal static string GetAbilityName(Ability ability) =>
        ability?.GetIl2CppType()?.Name ?? "Unknown";

    private static bool IsWindDash(Ability ability) =>
        string.Equals(GetAbilityName(ability), nameof(WindDash), StringComparison.Ordinal);

    private static bool IsSupportedAbility(Ability ability)
    {
        string name = GetAbilityName(ability);
        return string.Equals(name, nameof(Boost), StringComparison.Ordinal) ||
               string.Equals(name, nameof(WindDash), StringComparison.Ordinal);
    }

    internal static AbilitiesManager ResolveAbilitiesManager()
    {
        AbilitiesManager singleton = AbilitiesManager.instance;
        if (singleton != null && IsSupportedAbility(singleton.selectedAbility))
            return singleton;

        // Scene reloads can leave the static IL2CPP singleton pointing at an
        // obsolete manager while the ability button is already bound to the
        // replacement. Prefer the loaded manager that exposes the supported
        // ability the player can currently use.
        AbilitiesManager[] managers = Resources.FindObjectsOfTypeAll<AbilitiesManager>();
        foreach (AbilitiesManager candidate in managers)
        {
            if (candidate != null && IsSupportedAbility(candidate.selectedAbility))
                return candidate;
        }

        return singleton;
    }

    private void LogUnavailableAbility(float now, Ability selected)
    {
        if (now < nextUnavailableLogTime) return;
        nextUnavailableLogTime = now + 30f;
        AdventurerLog.MovementDebug(
            $"No supported selected ability resolved; selected={GetAbilityName(selected)}.");
    }

    private bool IsWindDashHeightSafe(float now)
    {
        PlayerMovement player = PlayerMovement.instance;
        if (player == null)
        {
            ResetGroundObservation();
            return false;
        }

        float currentY = player.transform.position.y;
        if (player.IsGrounded())
        {
            lastPlayerY = currentY;
            hasPlayerY = true;
            verticallyStationarySince = now;
            return true;
        }

        // IsGrounded can remain false while the character stands continuously
        // on the floor; jumping causes the game to refresh that contact flag,
        // which previously made AutoJump appear to fix Auto Boost. Treat a
        // sustained, virtually unchanged Y position as grounded too. The
        // duration prevents the brief apex of a jump from passing this check.
        if (!hasPlayerY || Math.Abs(currentY - lastPlayerY) > StationaryVerticalTolerance)
        {
            lastPlayerY = currentY;
            hasPlayerY = true;
            verticallyStationarySince = now;
            return false;
        }

        return verticallyStationarySince >= 0f &&
               now - verticallyStationarySince >= StationaryGroundFallbackSeconds;
    }

    private void ResetGroundObservation()
    {
        hasPlayerY = false;
        verticallyStationarySince = -1f;
    }

    internal void Reset()
    {
        nextCheckTime = 0f;
        suspendedUntil = 0f;
        nextTriggerLogTime = 0f;
        nextUnavailableLogTime = 0f;
        runnerValidSince = -1f;
        hasFirstSkillState = false;
        previousFirstSkillBought = false;
        managerMissingLogged = false;
        immediateActivationRequested = false;
        ResetGroundObservation();
        ResetAbilityStability();
    }
}
