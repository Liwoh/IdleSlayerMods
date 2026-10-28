using System;
using System.Collections.Generic;
using AutoAdventurer.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoAdventurer.Quests;

/// <summary>
/// Keeps the mutually-exclusive elemental Dark Divinity aligned with an
/// executable elemental kill quest. When none is active, it can activate the
/// required unlocked choice if the player can afford it.
/// </summary>
internal sealed class QuestElementService
{
    private const float CheckIntervalSeconds = 5f;
    private string lastSwitchKey = string.Empty;
    private string lastDiagnosticKey = string.Empty;
    private float nextObservationTime;
    private QuestsList resolvedQuestList;

    internal bool Tick(float now, bool enabled)
    {
        if (!enabled) return false;
        if (now < nextObservationTime) return false;
        nextObservationTime = now + CheckIntervalSeconds;

        try
        {
            return ObserveCurrentQuestState();
        }
        catch (Exception exception)
        {
            AdventurerLog.ElementDebug(
                $"Elemental Dark Divinity maintenance deferred safely; exception={exception.GetType().Name}. The next five-second scan will resolve fresh game objects.");
            resolvedQuestList = null;
            return false;
        }
    }

    private bool ObserveCurrentQuestState()
    {

        // Element alignment is a passive session-wide helper. It reads every
        // active elemental objective directly and never waits for that quest
        // to be selected, prioritized, locked, or considered travel-ready.
        var quests = new List<Quest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        QuestsList list = IsLoadedQuestList(QuestsList.instance)
            ? QuestsList.instance
            : IsLoadedQuestList(resolvedQuestList)
                ? resolvedQuestList
                : FindLoadedQuestList();
        resolvedQuestList = list;

        var cached = QuestCompat.ListedQuests(list);
        if (cached == null) return false;
        for (int index = 0; index < cached.Count; index++)
            AddActiveElementQuest(cached[index], quests, seen);

        // Do not supplement from PlayerInventory.allQuests here. That is the
        // game's complete catalogue, not an active-task list: it can contain
        // future elemental definitions that would wrongly override a manual
        // element choice every five seconds. The live quest-list cache is the
        // only authority for changing an elemental Dark Divinity.

        return TryAlign(quests);
    }

    private static bool IsLoadedQuestList(QuestsList candidate)
    {
        if (candidate == null || candidate.gameObject == null) return false;
        var scene = candidate.gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private static QuestsList FindLoadedQuestList()
    {
        foreach (QuestsList candidate in
                 Resources.FindObjectsOfTypeAll<QuestsList>())
        {
            if (IsLoadedQuestList(candidate)) return candidate;
        }

        return null;
    }

    private static void AddActiveElementQuest(
        Quest quest, List<Quest> result, HashSet<string> seen)
    {
        // Element switching is intentionally a normal-quest helper. Daily
        // and Weekly objectives must never override the player's manually
        // selected element when there is no active normal quest.
        if (IsDailyOrWeeklyQuest(quest) ||
            !CanUseQuest(quest) || quest.isClaimed) return;
        try
        {
            if (quest.IsCompleted()) return;
            string key = QuestTargetSelection.BuildLockKey(quest);
            if (seen.Add(key)) result.Add(quest);
        }
        catch
        {
            // Retry unreadable IL2CPP quest data on the next observation.
        }
    }

    private static bool IsDailyOrWeeklyQuest(Quest quest)
    {
        if (quest == null) return false;
        if (quest is DailyQuest || quest is WeeklyQuest) return true;

        // IL2CPP's quest-list cache can expose a recycled Daily/Weekly entry
        // through the base Quest wrapper. In that case normal C# `is` checks
        // are false even though the authoritative runtime type or internal
        // quest ID still identifies the rotating task. Never use localized
        // display text for this decision.
        string runtimeType = quest.GetIl2CppType()?.Name ?? string.Empty;
        if (string.Equals(runtimeType, nameof(DailyQuest),
                StringComparison.Ordinal) ||
            string.Equals(runtimeType, nameof(WeeklyQuest),
                StringComparison.Ordinal))
            return true;

        return string.Equals(quest.name, "quest_daily_quest",
                   StringComparison.Ordinal) ||
               string.Equals(quest.name, "quest_weekly_quest",
                   StringComparison.Ordinal);
    }

    internal bool TryAlign(List<Quest> quests)
    {
        quests ??= new List<Quest>();

        Divinity active = null;
        var elemental = new List<Divinity>();
        foreach (Divinity divinity in Resources.FindObjectsOfTypeAll<Divinity>())
        {
            try
            {
                if (divinity == null || !divinity.isElemental ||
                    divinity.newBenefit?.enemyType == null) continue;
                elemental.Add(divinity);
                if (divinity.unlocked) active = divinity;
            }
            catch
            {
                // Resources can briefly retain an invalid IL2CPP wrapper
                // while the Divinity panel is rebuilding. Ignore only that
                // definition and resolve everything again next cycle.
            }
        }

        if (elemental.Count == 0)
        {
            LogDiagnosticOnce("no-elemental-divinities",
                $"Elemental quest scan found no elemental Dark Divinity definitions; quest={quests[0].name}.");
            return false;
        }

        Quest chosenQuest = null;
        EnemyType requiredType = null;
        Divinity desired = null;
        foreach (Quest quest in quests)
        {
            try
            {
                EnemyType candidateType = GetRequiredElementType(
                    quest, elemental);
                Divinity candidateDivinity = FindForType(
                    elemental, candidateType);
                if (candidateType == null || candidateDivinity == null)
                    continue;
                chosenQuest = quest;
                requiredType = candidateType;
                desired = candidateDivinity;
                break;
            }
            catch
            {
                // A completed/replaced quest can retain an invalid IL2CPP
                // enemy wrapper for a few frames. Skip only that definition
                // so it cannot abort every subsequent five-second scan.
            }
        }

        bool usingSoulMultiplierFallback = chosenQuest == null;
        if (chosenQuest == null)
        {
            desired = FindHighestSoulMultiplierElement(elemental);
            requiredType = desired?.newBenefit?.enemyType;
            if (desired == null || requiredType == null)
            {
                lastDiagnosticKey = string.Empty;
                return false;
            }
        }

        // Element maintenance deliberately follows the first active
        // elemental task in the game's live list. It does not compare quest
        // priority and does not oscillate between multiple element tasks.
        // When there is no active elemental task, use the element with the
        // highest live EnemyType soul multiplier.
        DivinitiesManager manager = DivinitiesManager.instance;
        if (manager == null) return false;
        string alignmentSource = usingSoulMultiplierFallback
            ? $"soulMultiplierFallback={requiredType.name} " +
              $"(score={GetSoulMultiplierScore(requiredType):0.####})"
            : $"helperQuest={GetQuestLabel(chosenQuest)}";
        List<Divinity> preservedNonElemental =
            CaptureActiveNonElementalDivinities(elemental);

        // Manual input can race the five-second maintenance while the
        // Divinity panel is open and leave more than one mutually-exclusive
        // element active. Always normalize the group to the desired single
        // choice before deciding that no work is required.
        bool removedExtraElement = false;
        foreach (Divinity divinity in elemental)
        {
            if (divinity == null || ReferenceEquals(divinity, desired))
                continue;
            try
            {
                if (!divinity.unlocked) continue;
                SetDivinityState(manager, divinity, false);
                removedExtraElement = true;
            }
            catch (Exception exception)
            {
                RestoreNonElementalDivinities(
                    manager, preservedNonElemental);
                AdventurerLog.ElementDebug(
                    $"Extra elemental Dark Divinity cleanup deferred safely; divinity={divinity.name ?? "UnknownDivinity"}; exception={exception.GetType().Name}.");
            }
        }

        if (desired.unlocked)
        {
            if (removedExtraElement)
            {
                RestoreNonElementalDivinities(
                    manager, preservedNonElemental);
                AdventurerLog.ElementDebug(
                    $"Elemental Dark Divinity exclusivity restored: {alignmentSource}; retained={desired.name}; all other active elements were disabled.");
                lastDiagnosticKey = string.Empty;
                return true;
            }
            lastDiagnosticKey = string.Empty;
            return false;
        }

        if (active == null)
        {
            try
            {
                double available = manager.CalculateDivinityPointsLeft();
                if (!desired.IsAvailable() || available < desired.cost)
                {
                    LogDiagnosticOnce($"cannot-activate:{desired.name}",
                        $"Elemental Dark Divinity alignment cannot activate its target; {alignmentSource}; enemyType={desired.newBenefit.enemyType.name}; divinity={desired.name}; availableDivinityPoints={available:0.##}; cost={desired.cost:0.##}; divinityAvailable={desired.IsAvailable()}.");
                    return false;
                }

                SetDivinityState(manager, desired, true);
                RestoreNonElementalDivinities(
                    manager, preservedNonElemental);
                if (!desired.unlocked)
                {
                    LogDiagnosticOnce($"activation-failed:{desired.name}",
                        $"Elemental Dark Divinity activation did not stick; quest={chosenQuest.name}; enemyType={desired.newBenefit.enemyType.name}; divinity={desired.name}.");
                    return false;
                }

                string activationKey = $"none->{desired.name}";
                if (!string.Equals(lastSwitchKey, activationKey,
                        StringComparison.Ordinal))
                {
                    lastSwitchKey = activationKey;
                    AdventurerLog.ElementDebug(
                        $"Elemental Dark Divinity activated: {alignmentSource}; " +
                        $"enemyType={desired.newBenefit.enemyType.name}; from=None; to={desired.name}; " +
                        "quest priority and the current quest lock were unchanged.");
                }
                lastDiagnosticKey = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                RestoreNonElementalDivinities(
                    manager, preservedNonElemental);
                AdventurerLog.ElementDebug(
                    $"Elemental Dark Divinity activation deferred safely: " +
                    $"{alignmentSource}; exception={exception.GetType().Name}.");
                return false;
            }
        }

        string switchKey = $"{active.name}->{desired.name}";
        try
        {
            SetDivinityState(manager, active, false);
            SetDivinityState(manager, desired, true);
            RestoreNonElementalDivinities(manager, preservedNonElemental);
            if (!desired.unlocked)
            {
                // Activation did not stick. Restore the player's previous
                // element so a failed automation attempt never leaves all
                // elemental choices disabled.
                if (!active.unlocked) manager.ActivateDivinity(active.name);
                return false;
            }

            if (!string.Equals(lastSwitchKey, switchKey,
                    StringComparison.Ordinal))
            {
                lastSwitchKey = switchKey;
                AdventurerLog.ElementDebug(
                    $"Elemental Dark Divinity switched: {alignmentSource}; " +
                    $"enemyType={desired.newBenefit.enemyType.name}; " +
                    $"from={active.name}; to={desired.name}; " +
                    "quest priority and the current quest lock were unchanged.");
            }
            lastDiagnosticKey = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                if (!active.unlocked) manager.ActivateDivinity(active.name);
                RestoreNonElementalDivinities(
                    manager, preservedNonElemental);
            }
            catch
            {
                // The original exception is the useful diagnostic.
            }
            AdventurerLog.ElementDebug(
                $"Elemental Dark Divinity switch deferred safely: " +
                $"{alignmentSource}; exception={exception.GetType().Name}.");
            return false;
        }
    }

    private static Divinity FindHighestSoulMultiplierElement(
        List<Divinity> elemental)
    {
        Divinity best = null;
        double bestMultiplier = double.MinValue;
        foreach (Divinity divinity in elemental)
        {
            try
            {
                EnemyType type = divinity?.newBenefit?.enemyType;
                if (type == null) continue;
                double multiplier = GetSoulMultiplierScore(type);
                if (best == null || multiplier > bestMultiplier)
                {
                    best = divinity;
                    bestMultiplier = multiplier;
                }
            }
            catch
            {
                // Retry rebuilt IL2CPP definitions on the next scan.
            }
        }
        return best;
    }

    private static double GetSoulMultiplierScore(EnemyType type)
    {
        var modifiers = type?.soulsMultiplier;
        if (modifiers == null) return double.MinValue;

        double total = 0d;
        for (int index = 0; index < modifiers.Count; index++)
            total += modifiers[index];
        return total;
    }

    private static List<Divinity> CaptureActiveNonElementalDivinities(
        List<Divinity> elemental)
    {
        var elementalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Divinity divinity in elemental)
        {
            if (!string.IsNullOrWhiteSpace(divinity?.name))
                elementalNames.Add(divinity.name);
        }

        var preserved = new List<Divinity>();
        var preservedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Divinity divinity in Resources.FindObjectsOfTypeAll<Divinity>())
        {
            try
            {
                if (divinity == null || !divinity.unlocked ||
                    string.IsNullOrWhiteSpace(divinity.name) ||
                    elementalNames.Contains(divinity.name) ||
                    !preservedNames.Add(divinity.name))
                    continue;
                preserved.Add(divinity);
            }
            catch
            {
                // A rebuilt panel entry can be retried on the next scan.
            }
        }
        return preserved;
    }

    private static void RestoreNonElementalDivinities(
        DivinitiesManager manager, List<Divinity> preserved)
    {
        if (manager == null || preserved == null) return;
        foreach (Divinity divinity in preserved)
        {
            try
            {
                if (divinity == null || divinity.unlocked) continue;
                manager.ActivateDivinity(divinity.name);
                if (!divinity.unlocked) divinity.unlocked = true;
                divinity.SaveData();
                divinity.divinityObjectComponent?.RefreshColors();
                AdventurerLog.ElementDebug(
                    $"Elemental switch restored unrelated active Dark Divinity: divinity={divinity.name}.");
            }
            catch (Exception exception)
            {
                AdventurerLog.ElementDebug(
                    $"Unrelated Dark Divinity restoration deferred safely: divinity={divinity?.name ?? "UnknownDivinity"}; exception={exception.GetType().Name}.");
            }
        }
    }

    private static void SetDivinityState(
        DivinitiesManager manager, Divinity divinity, bool enabled)
    {
        if (manager == null || divinity == null) return;
        if (divinity.unlocked == enabled) return;
        if (enabled) manager.ActivateDivinity(divinity.name);
        else manager.DeactivateDivinity(divinity.name);
        if (divinity.unlocked != enabled)
            divinity.unlocked = enabled;
        divinity.SaveData();
        manager.CheckAvailableDarkDivinities();
        divinity.divinityObjectComponent?.RefreshColors();
    }

    private void LogDiagnosticOnce(string key, string message)
    {
        if (string.Equals(lastDiagnosticKey, key, StringComparison.Ordinal))
            return;
        lastDiagnosticKey = key;
        AdventurerLog.ElementDebug(message);
    }

    private static bool CanUseQuest(Quest quest)
    {
        if (quest == null) return false;
        try
        {
            // Do not call CanBeCompleted here. For elemental objectives the
            // game can report false precisely because a different mutually-
            // exclusive elemental Dark Divinity is currently active. This
            // service must be allowed to see that quest before switching the
            // element that makes it executable. Discovery already supplies
            // only active, incomplete quests.
            if (quest.characterRequired != null &&
                !quest.characterRequired.unlocked) return false;
            return (QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesOfType &&
                    quest.enemyType != null) || quest.enemyToKill != null;
        }
        catch
        {
            return false;
        }
    }

    private static EnemyType GetRequiredElementType(
        Quest quest, List<Divinity> elemental)
    {
        if (quest == null) return null;
        if (QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesOfType &&
            quest.enemyType != null)
            return quest.enemyType;

        Enemy target = quest.enemyToKill;
        EnemyType targetType = target?.type;
        if (targetType == null || FindForType(elemental, targetType) == null)
            return null;

        // An exact elemental enemy only needs alignment when another
        // elemental Dark Divinity can replace a stage in the same evolution
        // chain. Voltage Worm is one such case: an active Fire choice can
        // override its Electric stage.
        Enemy first = target;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; first != null && first.evolutionBackward != null &&
             depth < 32; depth++)
        {
            string key = first.name ?? $"backward_{depth}";
            if (!visited.Add(key)) break;
            first = first.evolutionBackward;
        }

        visited.Clear();
        for (Enemy stage = first; stage != null; stage = stage.evolutionForward)
        {
            string key = stage.name ?? $"forward_{visited.Count}";
            if (!visited.Add(key) || visited.Count > 32) break;
            EnemyType stageType = stage.type;
            if (stageType != null &&
                !string.Equals(stageType.name, targetType.name,
                    StringComparison.Ordinal) &&
                FindForType(elemental, stageType) != null)
                return targetType;
        }

        return null;
    }

    private static Divinity FindForType(
        List<Divinity> elemental, EnemyType required)
    {
        foreach (Divinity divinity in elemental)
        {
            if (string.Equals(divinity.newBenefit?.enemyType?.name,
                    required.name, StringComparison.Ordinal))
                return divinity;
        }
        return null;
    }

    private static string GetQuestLabel(Quest quest)
    {
        if (quest == null) return "UnknownQuest";
        string display = string.IsNullOrWhiteSpace(quest.localizedName)
            ? quest.name
            : LogText.Normalize(quest.localizedName);
        return $"{display} [{quest.name}]";
    }

    internal void Reset()
    {
        lastSwitchKey = string.Empty;
        lastDiagnosticKey = string.Empty;
        nextObservationTime = 0f;
        resolvedQuestList = null;
    }
}
