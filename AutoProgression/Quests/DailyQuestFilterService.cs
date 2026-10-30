using System;
using System.Collections.Generic;
using AutoProgression.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Quests;

internal sealed class DailyQuestFilterService
{
    private const int MaximumRerollsPerGeneration = 500;
    private const int MaximumTransientRecoveryAttempts = 120;
    private const float PostGenerationSettleSeconds = 5f;
    private const float PostRerollSettleSeconds = 0.2f;
    private const float FailedRerollRetrySeconds = 1f;
    private const float UnavailableLogIntervalSeconds = 10f;
    private const float ActivePassIntervalSeconds = 30f;

    private readonly HashSet<int> protectedQuestIds = new();
    private DailyQuest target;
    private bool processing;
    private bool completionVerificationPending;
    private int attempts;
    private int transientRecoveryAttempts;
    private int lastActiveQuestCount;
    private float rerollReadyAt;
    private float nextUnavailableLogAt;
    private float nextActivePassAt;

    internal bool IsProcessing => processing;

    internal bool Tick()
    {
        if (!processing && !TryBeginNextGeneration())
            return false;
        if (Time.unscaledTime < rerollReadyAt)
            return false;

        if (target == null && !PrepareNextTarget())
        {
            if (lastActiveQuestCount == 0)
            {
                return RetryTransient(
                    "The generated Daily Quest set is not visible yet.");
            }

            if (completionVerificationPending)
            {
                completionVerificationPending = false;
                rerollReadyAt = Time.unscaledTime + PostRerollSettleSeconds;
                return false;
            }

            return FinishSuccessfully();
        }

        completionVerificationPending = false;

        DailyQuestReroll reroll = ResolveReroll();
        if (reroll == null || target == null)
        {
            return RetryTransient(
                "Daily Quest reroll objects are temporarily unavailable.");
        }

        if (IsReadyToClaim(target))
        {
            int completedId = target.GetInstanceID();
            protectedQuestIds.Add(completedId);
            ProgressionLog.Debug(
                $"Generated Daily Quest filtering skipped a completed quest: " +
                $"Id={completedId}, Type={QuestCompat.Kind(target)}.");
            target = null;
            completionVerificationPending = true;
            rerollReadyAt = Time.unscaledTime + PostRerollSettleSeconds;
            return false;
        }

        Exception invocationException = null;
        try
        {
            reroll.rerollEnabled = true;
            if (!reroll.rerollEnabled)
            {
                return RetryTransient(
                    "The native Daily Quest reroll permission is not ready.");
            }

            int targetId = target.GetInstanceID();
            reroll.PrepareReroll(target);
            int boundId = reroll.dailyQuestToReroll?.GetInstanceID() ?? 0;
            if (boundId != targetId)
            {
                return RetryTransient(
                    $"The native Daily Quest target binding is not ready " +
                    $"(TargetId={targetId}, BoundAfter={boundId}).");
            }

            ProgressionLog.Debug(
                $"Rerolling generated Daily Quest: Id={targetId}, " +
                $"Type={QuestCompat.Kind(target)}, Name={target.name}.");
            reroll.RewardForShowing();
        }
        catch (Exception exception)
        {
            invocationException = exception;
        }

        bool targetStillActive;
        try
        {
            targetStillActive = target.active;
        }
        catch
        {
            targetStillActive = true;
        }

        if (targetStillActive)
        {
            if (invocationException != null)
            {
                ProgressionLog.Debug(
                    $"The native Daily Quest call returned before replacement " +
                    $"was observable ({invocationException.GetType().Name}); retrying.");
            }

            return RetryTransient(
                "The selected Daily Quest is still active after the native reroll.");
        }

        // Count only replacements whose authoritative source object became
        // inactive. Transient preparation and UI failures do not consume the
        // generated-set reroll limit.
        attempts++;
        transientRecoveryAttempts = 0;
        nextUnavailableLogAt = 0f;

        if (invocationException != null)
        {
            ProgressionLog.Debug(
                "The native Daily Quest reroll completed before a non-fatal UI update exception.");
        }

        if (attempts >= MaximumRerollsPerGeneration)
        {
            ProgressionLog.Warning(
                $"Generated Daily Quest filtering stopped after " +
                $"{MaximumRerollsPerGeneration} rerolls.");
            return Finish();
        }

        // Native quest replacement updates are not guaranteed to become fully
        // visible in the same frame as RewardForShowing(). Reacquire the whole
        // active set after a short delay instead of trusting a transient list.
        target = null;
        completionVerificationPending = true;
        rerollReadyAt = Time.unscaledTime + PostRerollSettleSeconds;

        return false;
    }

    private bool TryBeginNextGeneration()
    {
        while (DailyQuestGenerationBridge.TryDequeue(out _))
        {
            if (!Plugin.Config.FilterGeneratedDailyQuests.Value)
                continue;

            BeginPass(PostGenerationSettleSeconds);
            return true;
        }

        // A set only regenerates once every quest is done, so one slow quest
        // kept from an earlier set (or added later) blocks the next set.
        // Periodically run the same pass over the quests still active.
        if (Plugin.Config.FilterActiveDailyQuests.Value &&
            Time.unscaledTime >= nextActivePassAt)
        {
            nextActivePassAt = Time.unscaledTime + ActivePassIntervalSeconds;
            foreach (DailyQuest quest in FindActiveDailyQuests())
            {
                if (IsReadyToClaim(quest) || !ShouldReroll(quest)) continue;
                BeginPass(0f);
                return true;
            }
        }

        return false;
    }

    private void BeginPass(float settleSeconds)
    {
        protectedQuestIds.Clear();
        target = null;
        completionVerificationPending = false;
        attempts = 0;
        transientRecoveryAttempts = 0;
        lastActiveQuestCount = 0;
        rerollReadyAt = Time.unscaledTime + settleSeconds;
        nextUnavailableLogAt = 0f;
        processing = true;
    }

    private bool PrepareNextTarget()
    {
        List<DailyQuest> active = FindActiveDailyQuests();
        lastActiveQuestCount = active.Count;
        if (active.Count == 0) return false;

        foreach (DailyQuest quest in active)
        {
            if (protectedQuestIds.Contains(quest.GetInstanceID()))
                continue;
            if (IsReadyToClaim(quest))
            {
                protectedQuestIds.Add(quest.GetInstanceID());
                continue;
            }
            if (ShouldReroll(quest))
            {
                target = quest;
                return true;
            }

            protectedQuestIds.Add(quest.GetInstanceID());
        }

        return false;
    }

    private bool RetryTransient(string reason)
    {
        transientRecoveryAttempts++;
        if (transientRecoveryAttempts >= MaximumTransientRecoveryAttempts)
        {
            ProgressionLog.Warning(
                $"Generated Daily Quest filtering stopped after " +
                $"{MaximumTransientRecoveryAttempts} transient recovery " +
                $"attempts. Last state: {reason}");
            return Finish();
        }

        if (Time.unscaledTime >= nextUnavailableLogAt)
        {
            ProgressionLog.Debug(
                $"{reason} Waiting without discarding the generated Daily set.");
            nextUnavailableLogAt =
                Time.unscaledTime + UnavailableLogIntervalSeconds;
        }

        target = null;
        completionVerificationPending = false;
        rerollReadyAt = Time.unscaledTime + FailedRerollRetrySeconds;
        return false;
    }

    internal const string GoblinFilterType = "Goblin";

    internal const string DefaultFilterTypes =
        "Goblin, ChestHuntChests, HitRandomBoxes, HitRandomSilverBoxes, Boost, " +
        "UseRageMode, BonusStage, BonusStageSections, CraftTemporaryItems, " +
        "GetMaterials, CompleteAscendingHeights, CompleteGrappleRuns";

    private static string parsedFilterTypes;
    private static bool filterGoblins;
    private static readonly HashSet<LegacyQuestType> FilteredTypes = new();

    private static bool ShouldReroll(DailyQuest quest)
    {
        if (quest == null) return false;
        if (FarmMapBridge.ShouldReroll(quest)) return true;
        RefreshFilterTypes();
        if (filterGoblins && HasGoblinObjective(quest)) return true;
        return FilteredTypes.Contains(QuestCompat.Kind(quest));
    }

    private static void RefreshFilterTypes()
    {
        string configured = Plugin.Config.DailyFilterTypes.Value ?? string.Empty;
        if (configured == parsedFilterTypes) return;

        parsedFilterTypes = configured;
        filterGoblins = false;
        FilteredTypes.Clear();
        foreach (string entry in configured.Split(','))
        {
            string name = entry.Trim();
            if (name.Length == 0) continue;
            if (name.Equals(GoblinFilterType, StringComparison.OrdinalIgnoreCase))
                filterGoblins = true;
            else if (Enum.TryParse(name, true, out LegacyQuestType type))
                FilteredTypes.Add(type);
            else
                ProgressionLog.Warning($"Daily Filter Types: unknown type '{name}' ignored.");
        }
    }

    private static bool HasGoblinObjective(Quest quest)
    {
        if (ContainsText(quest.enemyType?.name, "goblin") ||
            ContainsText(quest.name, "goblin"))
            return true;

        HashSet<string> visited = new(StringComparer.Ordinal);
        Enemy enemy = quest.enemyToKill;
        for (int depth = 0; enemy != null && depth < 32; depth++)
        {
            string name = enemy.name ?? string.Empty;
            if (ContainsText(name, "goblin")) return true;
            if (!visited.Add(name)) break;
            enemy = enemy.evolutionBackward;
        }

        return false;
    }

    private static bool ContainsText(string value, string fragment) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(
            fragment, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsReadyToClaim(DailyQuest quest)
    {
        if (quest == null || quest.isClaimed)
            return true;

        try
        {
            return quest.CanBeClaimed();
        }
        catch
        {
            // A rebuilding native object is not safe to reroll this frame.
            return true;
        }
    }

    private static DailyQuestReroll ResolveReroll()
    {
        DailyQuestReroll instance = DailyQuestReroll.instance;
        if (instance != null) return instance;

        foreach (DailyQuestReroll candidate in
                 Resources.FindObjectsOfTypeAll<DailyQuestReroll>())
        {
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private bool FinishSuccessfully()
    {
        if (attempts > 0)
        {
            ProgressionLog.User(
                $"Generated Daily Quests filtered after {attempts} reroll(s).");
        }
        else
        {
            ProgressionLog.Debug(
                "Generated Daily Quests passed the automatic filter without rerolls.");
        }
        return Finish();
    }

    private bool Finish()
    {
        processing = false;
        target = null;
        completionVerificationPending = false;
        protectedQuestIds.Clear();
        attempts = 0;
        transientRecoveryAttempts = 0;
        lastActiveQuestCount = 0;
        rerollReadyAt = 0f;
        nextUnavailableLogAt = 0f;
        return true;
    }

    private static List<DailyQuest> FindActiveDailyQuests()
    {
        List<DailyQuest> result = new();
        foreach (DailyQuest quest in
                 Resources.FindObjectsOfTypeAll<DailyQuest>())
        {
            if (quest != null && quest.active && !quest.isClaimed)
                result.Add(quest);
        }

        return result;
    }

    internal void Reset()
    {
        processing = false;
        target = null;
        completionVerificationPending = false;
        protectedQuestIds.Clear();
        attempts = 0;
        transientRecoveryAttempts = 0;
        lastActiveQuestCount = 0;
        rerollReadyAt = 0f;
        nextUnavailableLogAt = 0f;
        DailyQuestGenerationBridge.DiscardPending();
    }
}
