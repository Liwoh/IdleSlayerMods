using System;
using System.Collections.Generic;
using AutoAdventurer.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoAdventurer.Quests;

/// <summary>
/// Temporarily disables Silver Bank while a normal Silver Random Box quest
/// is active, then restores the exact state owned by this service.
/// </summary>
internal sealed class SilverBoxQuestService
{
    private const double RequiredAvailableDivinityPoints = 30d;
    private const float CheckIntervalSeconds = 5f;
    private bool restoreSilverBank;
    private bool restoreFailureLogged;
    private bool detectionLogged;
    private float nextObservationTime;
    private QuestsList resolvedQuestList;
    private string lastThresholdState = string.Empty;

    internal void Tick(
        float now, bool controlEnabled, bool taskReleaseEnabled,
        double permanentReleaseThreshold)
    {
        if (!controlEnabled)
        {
            // The master switch means strictly manual control. Forget any
            // temporary ownership without changing the player's current
            // Silver Bank state.
            ForgetOwnership();
            return;
        }

        if (now < nextObservationTime) return;
        nextObservationTime = now + CheckIntervalSeconds;

        bool activeTask = HasActiveNormalSilverBoxQuest();
        if (taskReleaseEnabled && activeTask)
        {
            // Task release always wins and is never constrained by the point
            // threshold or the P hotkey.
            MaintainOverride(true);
            return;
        }

        detectionLogged = false;
        if (permanentReleaseThreshold > 0d)
        {
            MaintainThreshold(permanentReleaseThreshold);
            return;
        }

        RestoreIfOwned(taskReleaseEnabled
            ? "the Silver Random Box quest ended"
            : "automatic Silver Box task release was disabled");
    }

    private void MaintainThreshold(double threshold)
    {
        Divinity silverBank = Divinities.list?.SilverBank;
        DivinitiesManager manager = DivinitiesManager.instance;
        if (silverBank == null || manager == null) return;

        try
        {
            double available = manager.CalculateDivinityPointsLeft();
            // Disabling Silver Bank refunds its own cost. Subtract that refund
            // while it is disabled so the threshold has the same meaning in
            // both states and cannot oscillate every five seconds.
            double normalizedAvailable = Math.Max(0d, available -
                (silverBank.unlocked ? 0d : silverBank.cost));
            bool shouldEnableSilverBank = normalizedAvailable <= threshold;

            if (shouldEnableSilverBank && !silverBank.unlocked)
            {
                if (!silverBank.IsAvailable())
                {
                    LogThresholdStateOnce("unavailable",
                        "Silver Bank threshold wants the lock enabled, but the Dark Divinity is not currently available.");
                    return;
                }
                if (available < silverBank.cost)
                {
                    LogThresholdStateOnce("insufficient",
                        $"Silver Bank threshold wants the lock enabled, but availableDivinityPoints={available:0.##} is not greater than cost={silverBank.cost:0.##}.");
                    return;
                }
                if (!SetDivinityState(manager, silverBank, true)) return;
                LogThresholdStateOnce("locked",
                    $"Silver Bank enabled by threshold control; normalizedAvailableDivinityPoints={normalizedAvailable:0.##}, threshold={threshold:0.##}.");
            }
            else if (!shouldEnableSilverBank && silverBank.unlocked)
            {
                if (!SetDivinityState(manager, silverBank, false)) return;
                LogThresholdStateOnce("released",
                    $"Silver Bank disabled by threshold control; normalizedAvailableDivinityPoints={normalizedAvailable:0.##}, threshold={threshold:0.##}.");
            }

            // Threshold control owns the current decision, not restoration to
            // a state captured by a previous task.
            restoreSilverBank = false;
            restoreFailureLogged = false;
        }
        catch (Exception exception)
        {
            AdventurerLog.SilverBoxDebug(
                $"Silver Bank threshold check deferred safely; exception={exception.GetType().Name}.");
        }
    }

    private void LogThresholdStateOnce(string state, string message)
    {
        if (string.Equals(lastThresholdState, state,
                StringComparison.Ordinal)) return;
        lastThresholdState = state;
        AdventurerLog.SilverBoxDebug(message);
    }

    private bool HasActiveNormalSilverBoxQuest()
    {
        bool needsSilverBoxes = false;
        QuestsList list = QuestsList.instance ?? resolvedQuestList;
        if (list == null)
        {
            foreach (QuestsList candidate in
                     Resources.FindObjectsOfTypeAll<QuestsList>())
            {
                if (candidate == null) continue;
                list = candidate;
                resolvedQuestList = candidate;
                break;
            }
        }

        TryRefreshHiddenQuestCache(list);

        // allQuests is the game's full definition catalogue and contains
        // future locked quests. Only the live UI/runtime cache identifies
        // quests that have actually been added to the current save.
        var cached = QuestCompat.ListedQuests(list);
        if (!needsSilverBoxes && cached != null)
        {
            for (int index = 0; index < cached.Count; index++)
            {
                Quest quest = cached[index];
                if (IsActiveNormalSilverBoxQuest(quest))
                {
                    needsSilverBoxes = true;
                    break;
                }
            }
        }

        return needsSilverBoxes;
    }

    private static void TryRefreshHiddenQuestCache(QuestsList questsList)
    {
        if (questsList == null) return;
        GameStates state = GameState.current;
        if (state != GameStates.RunnerMode && state != GameStates.RageMode)
            return;

        MapController maps = MapController.instance;
        if (maps == null || !maps.initialized || maps.changingMap) return;

        bool listVisible = questsList.questsScrollRect != null
            ? questsList.questsScrollRect.gameObject.activeInHierarchy
            : questsList.gameObject.activeInHierarchy;
        if (listVisible) return;

        try
        {
            questsList.RefreshList();
        }
        catch (Exception exception)
        {
            AdventurerLog.SilverBoxDebug(
                $"Global Silver Box task scan could not refresh the hidden quest cache safely; exception={exception.GetType().Name}.");
        }
    }

    private static bool IsActiveNormalSilverBoxQuest(Quest quest)
    {
        if (quest == null || quest is DailyQuest || quest is WeeklyQuest ||
            quest.isClaimed ||
            QuestCompat.Kind(quest) != LegacyQuestType.HitRandomSilverBoxes) return false;
        try
        {
            return !quest.IsCompleted();
        }
        catch
        {
            return false;
        }
    }

    private void MaintainOverride(bool needsSilverBoxes)
    {
        if (!needsSilverBoxes)
        {
            detectionLogged = false;
            RestoreIfOwned("the Silver Random Box quest ended");
            return;
        }

        Divinity silverBank = Divinities.list?.SilverBank;
        DivinitiesManager manager = DivinitiesManager.instance;
        if (silverBank == null || manager == null) return;

        if (!detectionLogged)
        {
            detectionLogged = true;
            AdventurerLog.SilverBoxDebug(
                $"Normal Silver Random Box quest detected; silverBankActive={silverBank.unlocked}; temporaryOverrideOwned={restoreSilverBank}.");
        }

        if (restoreSilverBank)
        {
            // Keep the temporary override in force if the setting was
            // manually re-enabled before the quest finished.
            if (silverBank.unlocked)
            {
                try
                {
                    SetDivinityState(manager, silverBank, false);
                }
                catch (Exception exception)
                {
                    AdventurerLog.SilverBoxDebug(
                        $"Silver Bank temporary disable deferred safely; exception={exception.GetType().Name}.");
                }
            }
            return;
        }

        // If Silver Bank was already off, this service never takes ownership
        // and therefore must not turn it on when the quest ends.
        if (!silverBank.unlocked) return;

        try
        {
            if (!SetDivinityState(manager, silverBank, false)) return;
            restoreSilverBank = true;
            restoreFailureLogged = false;
            AdventurerLog.SilverBoxDebug(
                "Silver Bank temporarily disabled for a normal Silver Random Box quest; its 30 Divinity Points were refunded.");
        }
        catch (Exception exception)
        {
            AdventurerLog.SilverBoxDebug(
                $"Silver Bank temporary disable deferred safely; exception={exception.GetType().Name}.");
        }
    }

    private void RestoreIfOwned(string reason)
    {
        if (!restoreSilverBank) return;
        Divinity silverBank = Divinities.list?.SilverBank;
        DivinitiesManager manager = DivinitiesManager.instance;
        if (silverBank == null || manager == null) return;

        try
        {
            double available = manager.CalculateDivinityPointsLeft();
            if (available <= RequiredAvailableDivinityPoints)
            {
                if (!restoreFailureLogged)
                {
                    restoreFailureLogged = true;
                    AdventurerLog.SilverBoxDebug(
                        $"Silver Bank was not restored after {reason}; " +
                        $"availableDivinityPoints={available:0.##}, requiredGreaterThan={RequiredAvailableDivinityPoints:0.##}. " +
                        "The refunded points were used while the quest was active.");
                }
                // Do not keep a delayed claim on the player's future points.
                restoreSilverBank = false;
                return;
            }

            if (!silverBank.IsAvailable())
            {
                restoreSilverBank = false;
                restoreFailureLogged = false;
                AdventurerLog.SilverBoxDebug(
                    $"Silver Bank restoration ownership discarded after {reason}; " +
                    "the Dark Divinity is no longer available, consistent with a progression reset.");
                return;
            }

            if (!SetDivinityState(manager, silverBank, true)) return;
            restoreSilverBank = false;
            restoreFailureLogged = false;
            AdventurerLog.SilverBoxDebug(
                $"Silver Bank restored after {reason}.");
        }
        catch (Exception exception)
        {
            AdventurerLog.SilverBoxDebug(
                $"Silver Bank restoration deferred safely; exception={exception.GetType().Name}.");
        }
    }

    private static bool SetDivinityState(
        DivinitiesManager manager, Divinity divinity, bool enabled)
    {
        if (divinity.unlocked == enabled) return true;

        if (enabled) manager.ActivateDivinity(divinity.name);
        else manager.DeactivateDivinity(divinity.name);

        // Some game states accept the manager call but defer updating the
        // runtime object. Synchronize the saved state first, then run the same
        // availability/color refresh APIs used by the Divinity UI.
        if (divinity.unlocked != enabled)
            divinity.unlocked = enabled;

        divinity.SaveData();
        manager.CheckAvailableDarkDivinities();
        divinity.divinityObjectComponent?.RefreshColors();
        return divinity.unlocked == enabled;
    }

    internal void Reset()
    {
        RestoreIfOwned("Quest Automation stopped");
        restoreFailureLogged = false;
        detectionLogged = false;
        nextObservationTime = 0f;
        resolvedQuestList = null;
        lastThresholdState = string.Empty;
    }

    private void ForgetOwnership()
    {
        restoreSilverBank = false;
        restoreFailureLogged = false;
        detectionLogged = false;
        nextObservationTime = 0f;
        resolvedQuestList = null;
        lastThresholdState = string.Empty;
    }

    internal void DiscardForProgressionReset()
    {
        if (restoreSilverBank)
            AdventurerLog.SilverBoxDebug(
                "Silver Bank restoration ownership discarded because Ultra Ascension reset progression.");
        restoreSilverBank = false;
        restoreFailureLogged = false;
        detectionLogged = false;
        nextObservationTime = 0f;
        resolvedQuestList = null;
        lastThresholdState = string.Empty;
    }
}
