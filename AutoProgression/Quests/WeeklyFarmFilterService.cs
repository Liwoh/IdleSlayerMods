using System;
using AutoProgression.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Quests;

// Rerolls active Weekly Quests that cannot be done on the farm map (see
// FarmMapBridge), one at a time, every 30 seconds.
internal sealed class WeeklyFarmFilterService
{
    private const int MaximumRerollsPerPass = 50;
    private const float PassIntervalSeconds = 30f;
    private const float PostRerollSettleSeconds = 0.5f;
    private const float RetrySeconds = 1f;
    private const int MaximumRetries = 60;

    private bool processing;
    private int attempts;
    private int retries;
    private float readyAt;
    private float nextPassAt;

    internal bool IsProcessing => processing;

    internal bool Tick()
    {
        if (!Plugin.Config.RerollQuestsOutsideFarmMap.Value) return false;
        float now = Time.unscaledTime;
        if (now < readyAt) return false;
        if (!processing)
        {
            if (now < nextPassAt) return false;
            nextPassAt = now + PassIntervalSeconds;
            if (FindTarget() == null) return false;
            processing = true;
            attempts = 0;
            retries = 0;
        }

        WeeklyQuest target = FindTarget();
        if (target == null) return Finish();
        if (attempts >= MaximumRerollsPerPass || retries >= MaximumRetries)
        {
            ProgressionLog.Warning(
                $"Weekly farm-map filter stopped after {attempts} reroll(s) and {retries} retries.");
            return Finish();
        }

        WeeklyQuestReroll reroll = WeeklyQuestReroll.instance;
        if (reroll == null)
        {
            foreach (WeeklyQuestReroll candidate in
                     Resources.FindObjectsOfTypeAll<WeeklyQuestReroll>())
            {
                if (candidate != null) { reroll = candidate; break; }
            }
        }
        if (reroll == null) return Retry(now);

        try
        {
            reroll.rerollEnabled = true;
            reroll.PrepareReroll(target);
            if (reroll.weeklyQuestToReroll?.GetInstanceID() != target.GetInstanceID())
                return Retry(now);
            ProgressionLog.Debug(
                $"Rerolling Weekly Quest outside the farm map: Id={target.GetInstanceID()}, " +
                $"Type={QuestCompat.Kind(target)}.");
            reroll.RewardForShowing();
        }
        catch (Exception exception)
        {
            // The native call may replace the quest before a UI update throws.
            ProgressionLog.Debug(
                $"Weekly farm-map reroll returned {exception.GetType().Name}; checking the quest.");
        }

        bool stillActive;
        try { stillActive = target.active; }
        catch { stillActive = true; }
        if (stillActive) return Retry(now);

        attempts++;
        retries = 0;
        readyAt = now + PostRerollSettleSeconds;
        return false;
    }

    private static WeeklyQuest FindTarget()
    {
        foreach (WeeklyQuest quest in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
        {
            if (quest == null || !quest.active || quest.isClaimed) continue;
            bool claimable;
            try { claimable = quest.CanBeClaimed(); }
            catch { continue; }
            if (!claimable && FarmMapBridge.ShouldReroll(quest)) return quest;
        }
        return null;
    }

    private bool Retry(float now)
    {
        retries++;
        readyAt = now + RetrySeconds;
        return false;
    }

    private bool Finish()
    {
        if (attempts > 0)
            ProgressionLog.User($"Weekly Quests filtered for the farm map after {attempts} reroll(s).");
        processing = false;
        attempts = 0;
        retries = 0;
        readyAt = 0f;
        return true;
    }

    internal void Reset()
    {
        processing = false;
        attempts = 0;
        retries = 0;
        readyAt = 0f;
    }
}
