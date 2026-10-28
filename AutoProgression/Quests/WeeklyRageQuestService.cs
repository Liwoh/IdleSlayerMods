using System;
using System.Collections.Generic;
using AutoProgression.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Quests;

internal sealed class WeeklyRageQuestService
{
    private const int PreferredGoal = 180000;
    private const int MaximumRerollsPerGeneration = 200;
    private const int MaximumTransientRecoveryAttempts = 120;
    private const float PostGenerationSettleSeconds = 5f;
    private const float PostRerollSettleSeconds = 0.2f;
    private const float TransientRetrySeconds = 0.5f;
    private const float UnavailableLogIntervalSeconds = 10f;

    private readonly HashSet<int> protectedQuestIds = new();
    private WeeklyQuest target;
    private int[] generatedQuestIds = Array.Empty<int>();
    private bool processing;
    private bool targetSelectionInitialized;
    private int attempts;
    private int transientRecoveryAttempts;
    private float rerollReadyAt;
    private float nextUnavailableLogAt;

    internal bool IsProcessing => processing;

    internal bool Tick()
    {
        if (!processing && !TryBeginNextGeneration())
            return false;

        float now = Time.unscaledTime;
        if (now < rerollReadyAt)
            return false;

        List<WeeklyQuest> active = FindActiveWeeklyQuests();
        if (HasPreferredQuest(active))
            return FinishSuccessfully();

        if (attempts >= MaximumRerollsPerGeneration)
        {
            ProgressionLog.Warning(
                $"Automatic Weekly Quest reroll stopped after " +
                $"{MaximumRerollsPerGeneration} confirmed rerolls without " +
                $"finding the 180,000 Rage Mode kill quest.");
            return Finish();
        }

        if (target == null && !PrepareTarget(active))
            return RetryTransient(
                "The generated Weekly Quest replacement is not visible yet.");

        WeeklyQuestReroll reroll = ResolveReroll();
        if (reroll == null || target == null)
            return RetryTransient(
                "Weekly Quest reroll objects are temporarily unavailable.");

        int targetId;
        try
        {
            if (!target.active || target.isClaimed)
            {
                target = null;
                rerollReadyAt = now + PostRerollSettleSeconds;
                return false;
            }

            targetId = target.GetInstanceID();
            reroll.rerollEnabled = true;
            if (!reroll.rerollEnabled)
                return RetryTransient(
                    "The native Weekly Quest reroll permission is not ready.");

            int boundBefore = reroll.weeklyQuestToReroll?.GetInstanceID() ?? 0;
            ProgressionLog.Debug(
                $"Preparing native Weekly Quest reroll: TargetId={targetId}, " +
                $"BoundBefore={boundBefore}, RerollEnabled={reroll.rerollEnabled}.");

            reroll.PrepareReroll(target);
            int boundAfter = reroll.weeklyQuestToReroll?.GetInstanceID() ?? 0;
            if (boundAfter != targetId)
                return RetryTransient(
                    $"The native Weekly Quest target binding is not ready " +
                    $"(TargetId={targetId}, BoundAfter={boundAfter}).");
        }
        catch (Exception exception)
        {
            ProgressionLog.Debug(
                $"Weekly Quest reroll preparation encountered a transient " +
                $"native exception and will retry: {exception.GetType().Name}.");
            return RetryTransient(
                "Weekly Quest reroll preparation is temporarily unavailable.");
        }

        Exception invocationException = null;
        try
        {
            reroll.RewardForShowing();
        }
        catch (Exception exception)
        {
            // Some game versions replace the authoritative quest before an
            // optional UI update throws. Validate the target below.
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
                    $"The native Weekly Quest call returned before replacement " +
                    $"was observable ({invocationException.GetType().Name}); retrying.");
            }

            target = null;
            return RetryTransient(
                "The selected Weekly Quest is still active after the native reroll.");
        }

        // Count only replacements whose authoritative source object became
        // inactive. Preparation failures and delayed UI frames are not real
        // rerolls and therefore do not consume the generation limit.
        attempts++;
        transientRecoveryAttempts = 0;
        nextUnavailableLogAt = 0f;

        if (invocationException != null)
        {
            ProgressionLog.Debug(
                "The native Weekly Quest reroll completed before a non-fatal UI update exception.");
        }

        target = null;
        rerollReadyAt = now + PostRerollSettleSeconds;
        return false;
    }

    private bool TryBeginNextGeneration()
    {
        while (WeeklyQuestGenerationBridge.TryDequeue(out int[] newlyActiveIds))
        {
            if (!Plugin.Config.PreferMinimumRageWeeklyQuest.Value)
                continue;

            target = null;
            generatedQuestIds = newlyActiveIds ?? Array.Empty<int>();
            protectedQuestIds.Clear();
            attempts = 0;
            transientRecoveryAttempts = 0;
            targetSelectionInitialized = false;
            rerollReadyAt = Time.unscaledTime + PostGenerationSettleSeconds;
            nextUnavailableLogAt = 0f;
            processing = true;
            return true;
        }

        return false;
    }

    private bool PrepareTarget(List<WeeklyQuest> active)
    {
        if (active.Count == 0)
            return false;

        if (!targetSelectionInitialized)
        {
            target = FindRageQuest(active) ??
                     FindByInstanceId(active, generatedQuestIds) ??
                     active[^1];

            protectedQuestIds.Clear();
            foreach (WeeklyQuest quest in active)
            {
                if (quest != target)
                    protectedQuestIds.Add(quest.GetInstanceID());
            }

            targetSelectionInitialized = true;
            return target != null;
        }

        target = FindUnprotectedQuest(active, protectedQuestIds);
        return target != null;
    }

    private bool RetryTransient(string reason)
    {
        transientRecoveryAttempts++;
        if (transientRecoveryAttempts >= MaximumTransientRecoveryAttempts)
        {
            ProgressionLog.Warning(
                $"Automatic Weekly Quest reroll stopped after " +
                $"{MaximumTransientRecoveryAttempts} transient recovery " +
                $"attempts. Last state: {reason}");
            return Finish();
        }

        if (Time.unscaledTime >= nextUnavailableLogAt)
        {
            ProgressionLog.Debug(
                $"{reason} Waiting without discarding the generated Weekly set.");
            nextUnavailableLogAt =
                Time.unscaledTime + UnavailableLogIntervalSeconds;
        }

        target = null;
        rerollReadyAt = Time.unscaledTime + TransientRetrySeconds;
        return false;
    }

    private bool FinishSuccessfully()
    {
        ProgressionLog.User(
            $"Weekly Quest selected: 180,000 Rage Mode kills after {attempts} reroll(s).");
        return Finish();
    }

    private bool Finish()
    {
        processing = false;
        target = null;
        generatedQuestIds = Array.Empty<int>();
        protectedQuestIds.Clear();
        attempts = 0;
        transientRecoveryAttempts = 0;
        targetSelectionInitialized = false;
        rerollReadyAt = 0f;
        nextUnavailableLogAt = 0f;
        return true;
    }

    private static bool HasPreferredQuest(IEnumerable<WeeklyQuest> quests)
    {
        foreach (WeeklyQuest quest in quests)
        {
            if (QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesWithRageMode &&
                quest.questGoal == PreferredGoal)
                return true;
        }

        return false;
    }

    private static WeeklyQuest FindRageQuest(IEnumerable<WeeklyQuest> quests)
    {
        foreach (WeeklyQuest quest in quests)
        {
            if (QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesWithRageMode)
                return quest;
        }

        return null;
    }

    private static WeeklyQuest FindByInstanceId(
        IEnumerable<WeeklyQuest> quests,
        IReadOnlyCollection<int> candidateIds)
    {
        foreach (WeeklyQuest quest in quests)
        {
            if (candidateIds.Contains(quest.GetInstanceID()))
                return quest;
        }

        return null;
    }

    private static WeeklyQuest FindUnprotectedQuest(
        IEnumerable<WeeklyQuest> quests,
        IReadOnlySet<int> protectedIds)
    {
        foreach (WeeklyQuest quest in quests)
        {
            if (!protectedIds.Contains(quest.GetInstanceID()))
                return quest;
        }

        return null;
    }

    private static WeeklyQuestReroll ResolveReroll()
    {
        WeeklyQuestReroll instance = WeeklyQuestReroll.instance;
        if (instance != null)
            return instance;

        foreach (WeeklyQuestReroll candidate in
                 Resources.FindObjectsOfTypeAll<WeeklyQuestReroll>())
        {
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private static List<WeeklyQuest> FindActiveWeeklyQuests()
    {
        List<WeeklyQuest> result = new();
        foreach (WeeklyQuest quest in
                 Resources.FindObjectsOfTypeAll<WeeklyQuest>())
        {
            if (quest != null && quest.active && !quest.isClaimed)
                result.Add(quest);
        }

        return result;
    }

    internal void Reset()
    {
        target = null;
        generatedQuestIds = Array.Empty<int>();
        processing = false;
        attempts = 0;
        transientRecoveryAttempts = 0;
        targetSelectionInitialized = false;
        rerollReadyAt = 0f;
        nextUnavailableLogAt = 0f;
        protectedQuestIds.Clear();
        WeeklyQuestGenerationBridge.DiscardPending();
    }
}
