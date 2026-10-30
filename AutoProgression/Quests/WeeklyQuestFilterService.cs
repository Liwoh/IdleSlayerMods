using System.Collections.Generic;
using System;
using AutoProgression.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Quests;

// Rerolls active Weekly Quests towards the preferred types of Weekly Keep
// Types (ordered list), or, without that list, away from quests that cannot
// be done on the farm map (see FarmMapBridge). Checked every 30 seconds.
//
// The reroll pool can run out of a type (seen on 2026-10-30: no
// CompleteDailyQuests drawn in 900 rerolls after three were done), so every
// LevelRerolls failed rerolls the next type of the list is accepted too. A
// weekly accepted that way is left alone until the next native generation.
internal sealed class WeeklyQuestFilterService
{
    private static string parsedKeepTypes;
    private static readonly List<LegacyQuestType> KeepOrder = new();

    private const int LevelRerolls = 15;
    private const int MaximumRerollsPerPass = 100;
    private const float PassIntervalSeconds = 30f;
    private const float PostRerollSettleSeconds = 0.5f;
    private const float RetrySeconds = 1f;
    private const int MaximumRetries = 60;

    private readonly HashSet<int> settledIds = new();
    private int seenGeneration = -1;
    private bool processing;
    private int attempts;
    private int retries;
    private int acceptedLevel = 1;
    private float readyAt;
    private float nextPassAt;

    internal bool IsProcessing => processing;

    internal bool Tick()
    {
        RefreshKeepTypes();
        if (!Plugin.Config.RerollQuestsOutsideFarmMap.Value && KeepOrder.Count == 0)
            return false;
        if (seenGeneration != WeeklyQuestGenerationBridge.GenerationCount)
        {
            seenGeneration = WeeklyQuestGenerationBridge.GenerationCount;
            settledIds.Clear();
        }

        float now = Time.unscaledTime;
        if (now < readyAt) return false;
        if (!processing)
        {
            if (now < nextPassAt) return false;
            nextPassAt = now + PassIntervalSeconds;
            acceptedLevel = 1;
            if (FindTarget() == null) return false;
            processing = true;
            attempts = 0;
            retries = 0;
        }

        WeeklyQuest target = FindTarget();
        if (target == null) return Finish(settle: true);
        if (attempts >= MaximumRerollsPerPass || retries >= MaximumRetries)
        {
            ProgressionLog.Warning(
                $"Weekly Quest filter stopped after {attempts} reroll(s) and {retries} retries.");
            return Finish(settle: false);
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
                $"Rerolling Weekly Quest: Id={target.GetInstanceID()}, " +
                $"Type={QuestCompat.Kind(target)}, acceptedLevel={acceptedLevel}.");
            reroll.RewardForShowing();
        }
        catch (Exception exception)
        {
            // The native call may replace the quest before a UI update throws.
            ProgressionLog.Debug(
                $"Weekly Quest reroll returned {exception.GetType().Name}; checking the quest.");
        }

        bool stillActive;
        try { stillActive = target.active; }
        catch { stillActive = true; }
        if (stillActive) return Retry(now);

        attempts++;
        retries = 0;
        if (KeepOrder.Count > 0 && attempts % LevelRerolls == 0 &&
            acceptedLevel < KeepOrder.Count)
        {
            acceptedLevel++;
            ProgressionLog.User(
                $"Weekly Quest filter: {KeepOrder[acceptedLevel - 2]} not drawn in {LevelRerolls} rerolls; " +
                $"now also accepting {KeepOrder[acceptedLevel - 1]}.");
        }
        readyAt = now + PostRerollSettleSeconds;
        return false;
    }

    private WeeklyQuest FindTarget()
    {
        foreach (WeeklyQuest quest in ActiveUnfinishedWeeklies())
        {
            if (settledIds.Contains(quest.GetInstanceID())) continue;
            bool reroll = KeepOrder.Count > 0
                ? Rank(quest) >= acceptedLevel
                : FarmMapBridge.ShouldReroll(quest);
            if (reroll) return quest;
        }
        return null;
    }

    private static int Rank(Quest quest)
    {
        int index = KeepOrder.IndexOf(QuestCompat.Kind(quest));
        return index < 0 ? KeepOrder.Count : index;
    }

    private static List<WeeklyQuest> ActiveUnfinishedWeeklies()
    {
        var result = new List<WeeklyQuest>();
        foreach (WeeklyQuest quest in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
        {
            if (quest == null || !quest.active || quest.isClaimed) continue;
            bool claimable;
            try { claimable = quest.CanBeClaimed(); }
            catch { continue; }
            if (!claimable) result.Add(quest);
        }
        return result;
    }

    private static void RefreshKeepTypes()
    {
        string configured = Plugin.Config.WeeklyKeepTypes.Value ?? string.Empty;
        if (configured == parsedKeepTypes) return;
        parsedKeepTypes = configured;
        KeepOrder.Clear();
        foreach (string entry in configured.Split(','))
        {
            string name = entry.Trim();
            if (name.Length == 0) continue;
            if (Enum.TryParse(name, true, out LegacyQuestType type))
            {
                if (!KeepOrder.Contains(type)) KeepOrder.Add(type);
            }
            else
                ProgressionLog.Warning($"Weekly Keep Types: unknown type '{name}' ignored.");
        }
    }

    private bool Retry(float now)
    {
        retries++;
        readyAt = now + RetrySeconds;
        return false;
    }

    private bool Finish(bool settle)
    {
        if (settle && KeepOrder.Count > 0 && attempts > 0)
        {
            var kept = new List<string>();
            foreach (WeeklyQuest quest in ActiveUnfinishedWeeklies())
            {
                if (Rank(quest) > 0) settledIds.Add(quest.GetInstanceID());
                kept.Add(QuestCompat.Kind(quest).ToString());
            }
            ProgressionLog.User(
                $"Weekly Quests filtered after {attempts} reroll(s); kept: {string.Join(", ", kept)}.");
        }
        else if (attempts > 0)
            ProgressionLog.User($"Weekly Quests filtered after {attempts} reroll(s).");
        processing = false;
        attempts = 0;
        retries = 0;
        acceptedLevel = 1;
        readyAt = 0f;
        return true;
    }

    internal void Reset()
    {
        processing = false;
        attempts = 0;
        retries = 0;
        acceptedLevel = 1;
        readyAt = 0f;
    }
}
