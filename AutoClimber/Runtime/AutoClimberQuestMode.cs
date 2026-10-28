using System;
using System.Collections.Generic;
using AutoClimber.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoClimber;

internal static class AutoClimberQuestMode
{
    private static bool decisionLatched;
    private static bool latchedQuickSkip;

    internal static bool IsQuickSkipActive
    {
        get
        {
            string mode = GetConfiguredMode();

            if (mode == "Auto")
            {
                // Before the pre-modal decision is available, fail safe to
                // the full route. Never infer Skip from missing quest data.
                return decisionLatched && latchedQuickSkip;
            }

            return mode == "Skip";
        }
    }

    internal static void BeginRunDecision()
    {
        if (decisionLatched)
        {
            return;
        }

        decisionLatched = true;

        string mode = GetConfiguredMode();

        if (mode != "Auto")
        {
            latchedQuickSkip = mode == "Skip";
            return;
        }

        bool questDataAvailable;
        bool needsAscendingEnemies =
            HasIncompleteAscendingEnemyQuest(
                out questDataAvailable
            );

        latchedQuickSkip =
            questDataAvailable &&
            !needsAscendingEnemies;

        ClimberLog.User(
            "Automatic quest mode: " +
            (needsAscendingEnemies
                ? "Ascending Heights enemy quest found; full route enabled."
                : questDataAvailable
                    ? "No Ascending Heights enemy quest found; quick skip enabled."
                    : "quest data unavailable; full route enabled safely.")
        );
    }

    internal static void EndRunDecision()
    {
        decisionLatched = false;
        latchedQuickSkip = false;
    }

    internal static string ConfiguredMode => GetConfiguredMode();

    private static string GetConfiguredMode()
    {
        string configured =
            AutoClimberPlugin.Config?.Mode?.Value;

        if (string.Equals(configured, "Skip",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Skip";
        }

        if (string.Equals(configured, "Normal",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Normal";
        }

        return "Auto";
    }

    // The October 2026 game update split QuestsList.lastScrollListData into
    // one list per quest section. Sections also hold quests the panel hides
    // (e.g. every event's quests), so only live quests are kept.
    private static List<Quest> CollectListedQuests(QuestsList list)
    {
        if (list == null)
        {
            return null;
        }

        List<Quest> quests = new List<Quest>();
        AddQuests(quests, list.mainQuests);
        AddQuests(quests, list.dailyQuests);
        AddQuests(quests, list.weeklyQuests);
        AddQuests(quests, list.eventQuests);

        if (list.collabQuests != null)
        {
            foreach (var entry in list.collabQuests)
            {
                AddQuests(quests, entry.Value);
            }
        }

        return quests;
    }

    private static void AddQuests(
        List<Quest> target,
        Il2CppSystem.Collections.Generic.List<Quest> source)
    {
        if (source == null)
        {
            return;
        }

        for (int index = 0; index < source.Count; index++)
        {
            Quest quest = source[index];

            // CanBeCompleted() stays true for quests of events that are not
            // running, hence the extra EventAvailable() check.
            if (quest != null &&
                quest.CanBeCompletedOrClaimed() &&
                (quest.mapEvent == null || quest.EventAvailable()))
            {
                target.Add(quest);
            }
        }
    }

    private static bool HasIncompleteAscendingEnemyQuest(
        out bool questDataAvailable)
    {
        questDataAvailable = false;

        Enemy frozenSouls =
            AscendingHeightsController.instance?.frozenSouls ??
            Enemies.list?.FrozenSouls;

        Enemy frozenCoins =
            AscendingHeightsController.instance?.frozenCoins ??
            Enemies.list?.FrozenCoins;

        if (frozenSouls == null && frozenCoins == null)
        {
            return false;
        }

        HashSet<int> inspected = new HashSet<int>();
        List<Quest> quests = CollectListedQuests(QuestsList.instance);

        if (quests != null)
        {
            questDataAvailable = true;

            for (int index = 0; index < quests.Count; index++)
            {
                Quest quest = quests[index];

                if (IsRelevantIncompleteQuest(
                        quest,
                        frozenSouls,
                        frozenCoins,
                        inspected))
                {
                    return true;
                }
            }
        }

        // Daily quest components are authoritative even when the quest UI
        // cache is stale or the panel has never been opened.
        try
        {
            DailyQuest[] dailyQuests =
                Resources.FindObjectsOfTypeAll<DailyQuest>();

            if (dailyQuests != null)
            {
                questDataAvailable = true;

                foreach (DailyQuest daily in dailyQuests)
                {
                    if (daily == null ||
                        !daily.active ||
                        !daily.CheckIfIsValid())
                    {
                        continue;
                    }

                    if (IsRelevantIncompleteQuest(
                            daily,
                            frozenSouls,
                            frozenCoins,
                            inspected))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            // A partially rebuilt DailyQuest pool is not trustworthy. Keep
            // the full route rather than accidentally skipping a quest.
            questDataAvailable = false;
        }

        return false;
    }

    private static bool IsRelevantIncompleteQuest(
        Quest quest,
        Enemy frozenSouls,
        Enemy frozenCoins,
        HashSet<int> inspected)
    {
        if (quest == null ||
            !inspected.Add(quest.GetInstanceID()))
        {
            return false;
        }

        try
        {
            if (quest.isClaimed || quest.IsCompleted())
            {
                return false;
            }

            Enemy target = quest.enemyToKill;

            return target != null &&
                   (SharesEvolutionFamily(target, frozenSouls) ||
                    SharesEvolutionFamily(target, frozenCoins));
        }
        catch
        {
            return false;
        }
    }

    private static bool SharesEvolutionFamily(
        Enemy left,
        Enemy right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        Enemy leftRoot = GetEvolutionRoot(left);
        Enemy rightRoot = GetEvolutionRoot(right);

        return leftRoot != null &&
               rightRoot != null &&
               leftRoot.GetInstanceID() ==
                   rightRoot.GetInstanceID();
    }

    private static Enemy GetEvolutionRoot(Enemy enemy)
    {
        Enemy current = enemy;
        HashSet<int> visited = new HashSet<int>();

        while (current != null &&
               visited.Add(current.GetInstanceID()) &&
               current.evolutionBackward != null)
        {
            current = current.evolutionBackward;
        }

        return current;
    }
}
