using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Quests;

internal static class WeeklyQuestGenerationBridge
{
    private static readonly Queue<int[]> PendingGenerations = new();
    private static HashSet<int> activeBeforeGeneration;
    private static int generationDepth;
    private static int manualRerollDepth;

    internal static void BeginGeneration()
    {
        if (manualRerollDepth > 0) return;

        if (generationDepth++ == 0)
            activeBeforeGeneration = SnapshotActiveIds();
    }

    internal static void EndGeneration()
    {
        if (manualRerollDepth > 0 || generationDepth <= 0) return;
        if (--generationDepth > 0) return;

        HashSet<int> activeAfter = SnapshotActiveIds();
        List<int> newlyActive = new();
        foreach (int id in activeAfter)
        {
            if (activeBeforeGeneration == null || !activeBeforeGeneration.Contains(id))
                newlyActive.Add(id);
        }

        PendingGenerations.Enqueue(newlyActive.ToArray());
        activeBeforeGeneration = null;
        GenerationCount++;
    }

    // Incremented for every native Weekly generation (new set or extra
    // quests), never for rerolls.
    internal static int GenerationCount { get; private set; }

    internal static void BeginManualReroll() => manualRerollDepth++;

    internal static void EndManualReroll()
    {
        if (manualRerollDepth > 0) manualRerollDepth--;
    }

    internal static bool TryDequeue(out int[] newlyActiveIds)
    {
        if (PendingGenerations.Count > 0)
        {
            newlyActiveIds = PendingGenerations.Dequeue();
            return true;
        }

        newlyActiveIds = null;
        return false;
    }

    internal static void DiscardPending()
    {
        PendingGenerations.Clear();
        activeBeforeGeneration = null;
        generationDepth = 0;
        manualRerollDepth = 0;
    }

    private static HashSet<int> SnapshotActiveIds()
    {
        HashSet<int> result = new();
        foreach (WeeklyQuest quest in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
        {
            if (quest != null && quest.active && !quest.isClaimed)
                result.Add(quest.GetInstanceID());
        }

        return result;
    }
}

[HarmonyPatch(typeof(WeeklyQuestsManager), nameof(WeeklyQuestsManager.RegenerateWeeklies))]
internal static class WeeklyQuestRegenerationPatch
{
    private static void Prefix() => WeeklyQuestGenerationBridge.BeginGeneration();
    private static void Postfix() => WeeklyQuestGenerationBridge.EndGeneration();
}

[HarmonyPatch(typeof(WeeklyQuestsManager), nameof(WeeklyQuestsManager.AddQuests))]
internal static class WeeklyQuestAdditionPatch
{
    private static void Prefix() => WeeklyQuestGenerationBridge.BeginGeneration();
    private static void Postfix() => WeeklyQuestGenerationBridge.EndGeneration();
}

[HarmonyPatch(typeof(WeeklyQuestReroll), nameof(WeeklyQuestReroll.RewardForShowing))]
internal static class WeeklyQuestManualRerollPatch
{
    private static void Prefix() => WeeklyQuestGenerationBridge.BeginManualReroll();

    private static Exception Finalizer(
        WeeklyQuestReroll __instance,
        Exception __exception)
    {
        WeeklyQuestGenerationBridge.EndManualReroll();

        if (__exception == null)
            return null;

        try
        {
            // RewardForShowing first replaces the quest data and then updates
            // optional quest UI. Background rerolls can reach that final UI
            // step while its objects are unavailable. Once the bound target
            // is inactive, the authoritative replacement has succeeded and
            // the trailing UI exception is non-fatal. Suppress it at the
            // Harmony boundary so IL2CPP does not report a false trampoline
            // error. Real failures leave the target active and still surface.
            WeeklyQuest target = __instance?.weeklyQuestToReroll;
            if (target != null && !target.active)
                return null;
        }
        catch
        {
            // If the native objects cannot be inspected, retain the original
            // exception instead of assuming the reroll succeeded.
        }

        return __exception;
    }
}
