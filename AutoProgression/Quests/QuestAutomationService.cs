using System;
using System.Collections.Generic;
using AutoProgression.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Quests;

internal sealed class QuestAutomationService
{
    private const float CheckIntervalSeconds = 2f;
    private const float RegenerationSettleSeconds = 5f;
    private const float ClaimSummaryIntervalSeconds = 30f;

    private QuestsList questsList;
    private DailyQuestsManager dailyManager;
    private WeeklyQuestsManager weeklyManager;
    private PortalButton portalButton;
    private float nextCheckTime;
    private bool missingLogged;
    private bool refreshRequired = true;
    private int claimsSinceSummary;
    private float nextClaimSummaryTime;

    internal void TickPersistent()
    {
        if (!Plugin.Config.UnlimitedQuestRerolls.Value) return;

        try
        {
            // These UI objects can be rebuilt by scene changes, Ascension,
            // generation, and rerolling. Resolve the current singletons on
            // every frame instead of retaining IL2CPP objects across those
            // lifecycle boundaries.
            DailyQuestReroll daily = DailyQuestReroll.instance;
            WeeklyQuestReroll weekly = WeeklyQuestReroll.instance;
            if (daily != null) daily.rerollEnabled = true;
            if (weekly != null) weekly.rerollEnabled = true;
        }
        catch
        {
            // A native object may be replaced during this exact frame. The
            // next frame resolves the replacement, so no cached recovery or
            // repeated error log is necessary.
        }
    }

    internal void StopPersistentAutomation()
    {
        if (!Plugin.Config.UnlimitedQuestRerolls.Value) return;

        try
        {
            DailyQuestReroll daily = DailyQuestReroll.instance;
            WeeklyQuestReroll weekly = WeeklyQuestReroll.instance;
            if (daily != null) daily.rerollEnabled = false;
            if (weekly != null) weekly.rerollEnabled = false;
        }
        catch
        {
            // Native reroll objects may be replaced during this exact frame.
            // Runtime automation is already stopped, so no retry is needed.
        }
    }

    internal void Tick(float now)
    {
        FlushClaimSummary(now);
        if (now < nextCheckTime) return;
        nextCheckTime = now + CheckIntervalSeconds;

        try
        {
            ResolveObjects();
            ApplyPortalCooldownReset();
        }
        catch (Exception exception)
        {
            ProgressionLog.Exception("Quest automation setup", exception);
            return;
        }

        if (questsList == null)
        {
            LogMissing();
            return;
        }

        if (refreshRequired)
        {
            try
            {
                questsList.RefreshList();
                refreshRequired = false;
            }
            catch (Exception exception)
            {
                ProgressionLog.Exception("Quest-list refresh", exception);
                return;
            }
        }

        var quests = QuestCompat.ListedQuests(questsList);
        if (quests == null)
        {
            LogMissing();
            return;
        }

        missingLogged = false;
        List<Quest> snapshot = new(quests.Count);
        for (int index = 0; index < quests.Count; index++)
        {
            Quest quest = quests[index];
            if (quest != null) snapshot.Add(quest);
        }

        if (Plugin.Config.AutoClaimCompletedQuests.Value)
        {
            List<Quest> claimCandidates = BuildClaimCandidates(snapshot);
            ClaimResult claimResult = TryClaimOneCompleted(claimCandidates);
            if (claimResult != ClaimResult.None)
            {
                if (claimResult == ClaimResult.Claimed)
                {
                    claimsSinceSummary++;
                    if (nextClaimSummaryTime <= 0f)
                        nextClaimSummaryTime = now + ClaimSummaryIntervalSeconds;
                }

                // Claim only one object from each fresh snapshot. A successful
                // claim is followed by a new snapshot on the next frame.
                refreshRequired = true;
                nextCheckTime = claimResult == ClaimResult.Claimed
                    ? now
                    : now + RegenerationSettleSeconds;
                return;
            }
        }

        RegenerateMissingQuestTypes(now);
    }

    private static List<Quest> BuildClaimCandidates(List<Quest> uiSnapshot)
    {
        List<Quest> result = new();
        HashSet<int> seen = new();

        // The UI cache can retain inactive Daily and Weekly entries after
        // regeneration or rerolling. Only ordinary quests are trusted here.
        foreach (Quest quest in uiSnapshot)
        {
            if (quest == null || quest.isClaimed ||
                quest is DailyQuest || quest is WeeklyQuest)
                continue;

            if (seen.Add(quest.GetInstanceID()))
                result.Add(quest);
        }

        // Daily and Weekly candidates come from their authoritative live
        // ScriptableObjects instead of QuestCompat.ListedQuests(QuestsList).
        foreach (DailyQuest daily in Resources.FindObjectsOfTypeAll<DailyQuest>())
        {
            if (daily == null || !daily.active || daily.isClaimed)
                continue;

            try
            {
                if (daily.CheckIfIsValid() && seen.Add(daily.GetInstanceID()))
                    result.Add(daily);
            }
            catch
            {
                // An object being rebuilt is not a safe claim candidate yet.
            }
        }

        foreach (WeeklyQuest weekly in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
        {
            if (weekly == null || !weekly.active || weekly.isClaimed)
                continue;

            if (seen.Add(weekly.GetInstanceID()))
                result.Add(weekly);
        }

        return result;
    }

    private ClaimResult TryClaimOneCompleted(List<Quest> quests)
    {
        foreach (Quest quest in quests)
        {
            try
            {
                if (!quest.CanBeClaimed()) continue;
                quest.Claim();
                return ClaimResult.Claimed;
            }
            catch (Exception exception)
            {
                try
                {
                    // Weekly Quest claiming can finish its data update before
                    // an unavailable UI element throws. Treat the operation as
                    // successful when the authoritative claimed flag changed.
                    if (quest != null && quest.isClaimed)
                    {
                        ProgressionLog.Debug(
                            $"Quest claim completed before a non-fatal native UI exception: " +
                            $"{DescribeQuest(quest)}.");
                        return ClaimResult.Claimed;
                    }
                }
                catch
                {
                    // Fall through to the original error when the quest state
                    // itself can no longer be read safely.
                }

                ProgressionLog.Exception(
                    $"Quest claim ({DescribeQuest(quest)})",
                    exception);
                return ClaimResult.Failed;
            }
        }

        return ClaimResult.None;
    }

    private static string DescribeQuest(Quest quest)
    {
        if (quest == null) return "Quest=null";

        try
        {
            return $"Type={quest.GetIl2CppType()?.FullName ?? "Unknown"}, " +
                   $"Id={quest.GetInstanceID()}, Name={quest.name ?? "Unknown"}, " +
                   $"LocalizedName={quest.localizedName ?? "Unknown"}, " +
                   $"IsClaimed={quest.isClaimed}";
        }
        catch
        {
            return "Quest metadata unavailable";
        }
    }

    private enum ClaimResult
    {
        None,
        Claimed,
        Failed
    }

    private void RegenerateMissingQuestTypes(float now)
    {
        // Count from the live ScriptableObjects, like the claim candidates.
        // Since the October 2026 update the quest-list sections stay empty
        // until the quest panel is shown, which made every set look missing
        // and regenerated active Daily and Weekly Quests in a loop.
        int dailyCount = 0;
        int weeklyCount = 0;
        foreach (DailyQuest daily in Resources.FindObjectsOfTypeAll<DailyQuest>())
            if (daily != null && daily.active && !daily.isClaimed) dailyCount++;
        foreach (WeeklyQuest weekly in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
            if (weekly != null && weekly.active && !weekly.isClaimed) weeklyCount++;

        bool regenerated = false;
        CraftableItems craftables = CraftableItems.list;

        if (dailyCount == 0 &&
            Plugin.Config.RegenerateDailyQuests.Value &&
            dailyManager != null &&
            craftables?.DailyQuests?.IsActive() == true)
        {
            try
            {
                dailyManager.RegenerateDailys();
                ProgressionLog.Debug(
                    "Generated a new set of Daily Quests.",
                    "Quests");
                regenerated = true;
            }
            catch (Exception exception)
            {
                ProgressionLog.Exception("Daily Quest regeneration", exception);
            }
        }

        if (weeklyCount == 0 &&
            Plugin.Config.RegenerateWeeklyQuests.Value &&
            weeklyManager != null &&
            craftables?.WeeklyQuests?.IsActive() == true)
        {
            try
            {
                weeklyManager.RegenerateWeeklies();
                ProgressionLog.Debug(
                    "Generated a new set of Weekly Quests.",
                    "Quests");
                regenerated = true;
            }
            catch (Exception exception)
            {
                ProgressionLog.Exception("Weekly Quest regeneration", exception);
            }
        }

        if (regenerated)
        {
            refreshRequired = true;
            nextCheckTime = now + RegenerationSettleSeconds;
        }
    }

    private void ApplyPortalCooldownReset()
    {
        if (!Plugin.Config.ResetPortalCooldown.Value || portalButton == null) return;
        if (portalButton.currentCd > 0d) portalButton.currentCd = 0d;
    }

    private void ResolveObjects()
    {
        questsList ??= FindLoadedSceneComponent<QuestsList>();
        dailyManager ??= DailyQuestsManager.instance;
        weeklyManager ??= WeeklyQuestsManager.instance;
        portalButton ??= PortalButton.instance;
    }

    private void LogMissing()
    {
        if (missingLogged) return;

        ProgressionLog.Debug(
            $"Quest automation objects unavailable. QuestList={questsList != null}, " +
            $"DailyManager={dailyManager != null}, WeeklyManager={weeklyManager != null}.");
        missingLogged = true;
    }

    private void FlushClaimSummary(float now)
    {
        if (claimsSinceSummary <= 0 || now < nextClaimSummaryTime)
            return;

        ProgressionLog.Debug(
            $"Claimed {claimsSinceSummary} completed quest(s) in the last " +
            $"{ClaimSummaryIntervalSeconds:0} seconds.",
            "Quests");
        claimsSinceSummary = 0;
        nextClaimSummaryTime = 0f;
    }

    private static T FindLoadedSceneComponent<T>() where T : Component
    {
        foreach (T component in Resources.FindObjectsOfTypeAll<T>())
        {
            if (component == null || component.gameObject == null) continue;
            var scene = component.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded) return component;
        }

        return null;
    }

    internal void Reset()
    {
        questsList = null;
        dailyManager = null;
        weeklyManager = null;
        portalButton = null;
        nextCheckTime = 0f;
        missingLogged = false;
        refreshRequired = true;
        claimsSinceSummary = 0;
        nextClaimSummaryTime = 0f;
    }
}
