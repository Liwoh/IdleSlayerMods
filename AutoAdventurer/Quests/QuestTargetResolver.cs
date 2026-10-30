using System;
using System.Collections.Generic;
using AutoAdventurer.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoAdventurer.Quests;

internal sealed class QuestTargetResolver
{
    private readonly HashSet<string> loggedUnavailableStages = new();
    private readonly HashSet<string> loggedUnavailableCharacterQuests = new();
    private readonly HashSet<string> loggedUnresolvedElementQuests = new();

    internal QuestTargetSelection Select(
        List<Quest> quests, bool allowDimensionTravel = true)
    {
        QuestTargetSelection best = null;
        foreach (Quest quest in quests)
        {
            if (!IsExecutableKillQuest(quest)) continue;

            bool farmBound = FarmMap.Active && !IsStoryQuest(quest);
            QuestTargetSelection candidate = farmBound
                ? ResolveMap(quest, FarmMap.MapId)
                : ResolveMap(quest);
            if (farmBound && candidate != null && !string.Equals(
                    candidate.MapId, FarmMap.MapId, StringComparison.Ordinal))
                continue;
            if (candidate == null)
            {
                LogUnresolvedElementQuestOnce(quest);
                continue;
            }
            loggedUnresolvedElementQuests.Remove(
                QuestTargetSelection.BuildLockKey(quest));
            if (!allowDimensionTravel)
            {
                string currentMap = MapController.instance?.selectedMap?.name;
                if (!string.Equals(candidate.MapId, currentMap,
                        StringComparison.Ordinal))
                    continue;
            }

            int storyOrder = best != null
                ? PriorityTier(candidate.Quest).CompareTo(PriorityTier(best.Quest))
                : 0;
            if (best == null || storyOrder > 0 ||
                (storyOrder == 0 &&
                 (candidate.RewardPriority > best.RewardPriority ||
                  (candidate.RewardPriority == best.RewardPriority &&
                   candidate.RequiredKills < best.RequiredKills))))
                best = candidate;
        }

        return best;
    }

    // With Prioritize Story Quests: 2 = story quest with a target, 1 = daily or
    // weekly, 0 = story quest that counts any enemy on any map (arrows, Wind
    // Dash, character). Those progress in the background, so dailies go first.
    internal static int PriorityTier(Quest quest)
    {
        if (!Plugin.Config.PrioritizeStoryQuests.Value || quest == null) return 0;
        if (!IsStoryQuest(quest)) return 1;
        bool background = quest.enemyToKill == null &&
                          quest.enemyType == null &&
                          QuestCompat.RequiredMap(quest) == null &&
                          QuestCompat.Kind(quest) is LegacyQuestType.KillEnemiesWithArrows or
                              LegacyQuestType.WindDashKills or
                              LegacyQuestType.KillEnemiesWithCharacter;
        return background ? 0 : 2;
    }

    internal static bool IsKillKind(Quest quest) =>
        QuestCompat.Kind(quest) is LegacyQuestType.KillEnemies or
            LegacyQuestType.KillEnemiesWithArrows or
            LegacyQuestType.KillFlyers or
            LegacyQuestType.KillGiants or
            LegacyQuestType.KillEnemiesWithRageMode or
            LegacyQuestType.KillEnemiesWithCharacter or
            LegacyQuestType.KillEnemiesOfType or
            LegacyQuestType.KillShinyEnemies or
            LegacyQuestType.WindDashKills;

    // Daily and Weekly quests are recycled components; everything else is a
    // story or event quest from the normal quest catalogue.
    private static bool IsStoryQuest(Quest quest)
    {
        string runtimeType = quest?.GetIl2CppType()?.Name ?? string.Empty;
        return runtimeType != nameof(DailyQuest) && runtimeType != nameof(WeeklyQuest);
    }

    internal QuestTargetSelection ResolveLocked(
        Quest quest, string preferredMapId)
    {
        if (quest == null || !IsExecutableKillQuest(quest)) return null;
        return ResolveMap(quest, preferredMapId);
    }

    // TEMP: why the last ResolveLocked call returned null. Remove before PR.
    internal string LastRejectReason { get; private set; } = string.Empty;

    private bool IsExecutableKillQuest(Quest quest)
    {
        LastRejectReason = "map resolution";
        try
        {
            if (quest is DailyQuest daily &&
                (!daily.active || !daily.CheckIfIsValid()))
            {
                LastRejectReason = $"daily active={daily.active}, valid={daily.CheckIfIsValid()}";
                return false;
            }
            bool explicitElementQuest =
                QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesOfType &&
                quest.enemyType != null;
            // CanBeCompleted() is not authoritative for explicit elemental
            // objectives. The game may return false under a different active
            // elemental Dark Divinity even when a matching enemy (for example
            // a naturally Fire enemy unaffected by Ice) is already present.
            // These quests came from the live active-task cache; validate them
            // against actual current evolution stages and unlocked maps below.
            if (!explicitElementQuest && !quest.CanBeCompleted())
            {
                LastRejectReason = $"CanBeCompleted=false (completed={quest.IsCompleted()}, claimed={quest.isClaimed})";
                return false;
            }
            bool characterQuest =
                QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesWithCharacter ||
                quest.characterRequired != null;
            if (characterQuest && (quest.characterRequired == null ||
                                   !quest.characterRequired.unlocked))
            {
                string key = QuestTargetSelection.BuildLockKey(quest);
                if (loggedUnavailableCharacterQuests.Add(key))
                    AdventurerLog.QuestDebug(
                        $"Quest skipped: quest={GetQuestLabel(quest)}; reason=required character is unavailable; character={quest.characterRequired?.name ?? "UnknownCharacter"}.");
                return false;
            }

            if (!IsKillKind(quest) || QuestCompat.IsVisitLocation(quest))
            {
                LastRejectReason = $"not a kill quest ({QuestCompat.Kind(quest)})";
                return false;
            }
            if (QuestCompat.Kind(quest) == LegacyQuestType.WindDashKills &&
                !Plugin.Config.WindDashKillQuests.Value)
            {
                LastRejectReason = "Wind Dash kill quests are disabled";
                return false;
            }

            // Exact targets, native types, provable map categories, and
            // generic arrow or Wind Dash kills are executable without parsing
            // text. Wind Dash kills need the ability, see QuestAbilityService.
            return quest.enemyToKill != null ||
                   explicitElementQuest ||
                   QuestCompat.Kind(quest) == LegacyQuestType.KillFlyers ||
                   QuestCompat.Kind(quest) == LegacyQuestType.KillGiants ||
                   QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesWithArrows ||
                   QuestCompat.Kind(quest) == LegacyQuestType.WindDashKills ||
                   characterQuest;
        }
        catch (Exception exception)
        {
            LastRejectReason = $"exception {exception.GetType().Name}";
            return false;
        }
    }

    // Location quests from the October 2026 quest system only count kills in
    // Quest.map, so never resolve them to another dimension.
    private QuestTargetSelection ResolveMap(
        Quest quest, string preferredMapId = "")
    {
        BaseMap required = QuestCompat.RequiredMap(quest);
        if (required == null) return ResolveAnyMap(quest, preferredMapId);

        QuestTargetSelection selection = ResolveAnyMap(quest, required.name);
        return selection != null && string.Equals(selection.MapId,
            required.name, StringComparison.Ordinal) ? selection : null;
    }

    private QuestTargetSelection ResolveAnyMap(
        Quest quest, string preferredMapId)
    {
        MapController controller = MapController.instance;
        BaseMap currentMap = controller?.selectedMap;
        if (controller == null || currentMap == null) return null;

        if (quest.enemyToKill != null)
            return ResolveExactEnemy(controller, currentMap, quest,
                preferredMapId);

        Func<Enemy, bool> predicate;
        if (QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesOfType &&
            quest.enemyType != null)
        {
            EnemyType requiredType = quest.enemyType;
            predicate = enemy => SameEnemyType(enemy?.type, requiredType);
        }
        else if (QuestCompat.Kind(quest) == LegacyQuestType.KillFlyers)
            predicate = enemy => enemy?.isFlying == true;
        else if (QuestCompat.Kind(quest) == LegacyQuestType.KillGiants)
            predicate = enemy => enemy?.isGiant == true;
        else if (QuestCompat.Kind(quest) is LegacyQuestType.KillEnemiesWithArrows or
                 LegacyQuestType.WindDashKills)
            predicate = enemy => enemy != null;
        else if (QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesWithCharacter ||
                 quest.characterRequired != null)
            predicate = enemy => enemy != null;
        else
            return null;

        return ResolveMatchingEnemy(controller, currentMap, quest,
            predicate, preferredMapId);
    }

    private QuestTargetSelection ResolveExactEnemy(
        MapController controller, BaseMap currentMap, Quest quest,
        string preferredMapId)
    {
        Enemy target = quest.enemyToKill;
        Enemy firstStage = GetEnemyFirstStage(target);
        if (firstStage == null) return null;

        // A target evolution must itself be unlocked. Killing that evolution
        // or any later descendant in the same branch satisfies the quest;
        // lower stages and sibling branches do not.
        if (target.evolutionBackward != null &&
            !IsEnemyEvolutionUnlocked(target))
        {
            string key = $"{quest.name}|{target.name}";
            if (loggedUnavailableStages.Add(key))
            {
                Upgrade unlock = target.upgradeToUnlockIt;
                AdventurerLog.QuestDebug(
                    $"Quest skipped: quest={GetQuestLabel(quest)}; reason=required enemy evolution is unavailable; enemy={target.name}; unlockUpgrade={unlock?.name ?? "None"}; bought={unlock?.bought.ToString() ?? "N/A"}; specialUnlocked={unlock?.specialUnlocked.ToString() ?? "N/A"}.");
            }
            return null;
        }

        Func<Enemy, bool> predicate = enemy =>
            IsSameOrDescendant(enemy, target);
        return ResolveMatchingEnemy(controller, currentMap, quest, predicate,
            preferredMapId);
    }

    private static QuestTargetSelection ResolveMatchingEnemy(
        MapController controller, BaseMap currentMap, Quest quest,
        Func<Enemy, bool> predicate, string preferredMapId)
    {
        Enemy currentEnemy = FindMatchingQuestEnemy(
            currentMap, quest, predicate);
        if (currentEnemy == null &&
            QuestCompat.Kind(quest) == LegacyQuestType.KillGiants &&
            !string.IsNullOrEmpty(preferredMapId) &&
            string.Equals(currentMap.name, preferredMapId,
                StringComparison.Ordinal))
            currentEnemy = FindUnlockedGiantInEvolutionChains(currentMap);

        // A dimension the player is already running in is usable even when
        // the Portal availability list temporarily omits or rejects it.
        if (IsStandardPortalDimension(currentMap) && currentEnemy != null &&
            (string.IsNullOrEmpty(preferredMapId) || string.Equals(
                currentMap.name, preferredMapId, StringComparison.Ordinal)))
        {
            return CreateSelection(quest, currentEnemy, currentMap);
        }

        // This is the authoritative list used by the normal Portal selector.
        // includeCurrent=false because the current dimension was checked above.
        var maps = controller.GetAvailableMaps(false);
        if (maps == null) return null;

        // Keep a locked quest on its chosen dimension while that dimension
        // remains Portal-selectable and still contains a matching enemy.
        if (!string.IsNullOrEmpty(preferredMapId))
        {
            for (int index = 0; index < maps.Count; index++)
            {
                Map preferred = maps[index];
                if (preferred == null || !string.Equals(preferred.name,
                        preferredMapId, StringComparison.Ordinal) ||
                    !preferred.IsAvailable()) continue;
                Enemy matched = FindMatchingQuestEnemy(
                    preferred, quest, predicate);
                if (matched == null &&
                    QuestCompat.Kind(quest) == LegacyQuestType.KillGiants)
                    matched = FindUnlockedGiantInEvolutionChains(preferred);
                if (matched != null)
                    return CreateSelection(quest, matched, preferred);
            }
        }

        // The preferred map is no longer valid. The caller may update the
        // string lock to a newly valid current or available dimension.
        if (IsStandardPortalDimension(currentMap) && currentEnemy != null)
            return CreateSelection(quest, currentEnemy, currentMap);

        for (int index = 0; index < maps.Count; index++)
        {
            Map map = maps[index];
            if (!IsStandardPortalDimension(map) || !map.IsAvailable()) continue;
            Enemy matched = FindMatchingQuestEnemy(map, quest, predicate);
            if (matched == null) continue;

            return CreateSelection(quest, matched, map);
        }

        return null;
    }

    private static Enemy FindUnlockedGiantInEvolutionChains(BaseMap map)
    {
        var enemies = map?.enemies;
        if (enemies == null) return null;

        for (int index = 0; index < enemies.Count; index++)
        {
            Enemy stage = GetEnemyFirstStage(enemies[index]);
            HashSet<string> visited = new(StringComparer.Ordinal);
            for (int depth = 0; stage != null && depth < 32; depth++)
            {
                string identity = stage.name ?? $"stage_{index}_{depth}";
                if (!visited.Add(identity)) break;
                if (stage.isGiant && IsEnemyEvolutionUnlocked(stage))
                    return stage;
                stage = stage.evolutionForward;
            }
        }

        return null;
    }

    // MapController.selectedMap is wrapped as BaseMap, so a C# `is Map` test
    // is always false for it; TryCast checks the actual IL2CPP type.
    private static bool IsStandardPortalDimension(BaseMap map) =>
        map != null && map.TryCast<Map>() != null;

    private static QuestTargetSelection CreateSelection(
        Quest quest, Enemy enemy, BaseMap map) =>
        new()
        {
            Quest = quest,
            Enemy = enemy,
            Map = map,
            // GetGoal() returns zero for a number of normal quest assets.
            // questGoal is the serialized kill requirement shown by the UI.
            RequiredKills = Math.Max(0d, quest.questGoal),
            CurrentKills = Math.Max(0d, quest.questCurrentGoal)
        };

    private static Enemy FindMatchingCurrentEnemy(
        BaseMap map, Func<Enemy, bool> predicate)
    {
        var enemies = map.enemies;
        if (enemies == null) return null;

        for (int index = 0; index < enemies.Count; index++)
        {
            Enemy candidate = enemies[index];
            if (candidate == null) continue;
            Enemy firstStage = GetEnemyFirstStage(candidate);
            Enemy currentStage = firstStage?.GetCurrentStage();
            if (currentStage != null && predicate(currentStage))
                return currentStage;
        }

        return null;
    }

    private static Enemy FindMatchingQuestEnemy(
        BaseMap map, Quest quest, Func<Enemy, bool> predicate)
    {
        // The selected map's serialized enemy list can lag behind an enemy
        // evolution unlocked by a newly claimed quest. Runtime enemy objects
        // are authoritative for what is actually spawning on screen, so use
        // them first for the current dimension. Never retain these IL2CPP
        // objects beyond this call or use them to infer another dimension.
        BaseMap selectedMap = MapController.instance?.selectedMap;
        if (selectedMap != null && string.Equals(map?.name,
                selectedMap.name, StringComparison.Ordinal))
        {
            Enemy runtime = FindMatchingRuntimeEnemy(predicate);
            if (runtime != null) return runtime;
        }

        Enemy current = FindMatchingCurrentEnemy(map, predicate);
        if (current != null) return current;

        if (quest != null && QuestCompat.Kind(quest) == LegacyQuestType.KillEnemiesOfType &&
            quest.enemyType != null)
        {
            EnemyType activeElementType = GetActiveElementType();
            // With no active elemental Divinity, do not infer a future
            // elemental evolution from a stale chain. Only the game's actual
            // current stage above is authoritative in that state.
            if (activeElementType == null) return null;
            Enemy elemental = FindUnlockedTypeInEvolutionChains(
                map, quest.enemyType, activeElementType);
            if (elemental != null) return elemental;
        }

        return null;
    }

    private static Enemy FindMatchingRuntimeEnemy(Func<Enemy, bool> predicate)
    {
        foreach (EnemyGameObject enemyObject in
                 Resources.FindObjectsOfTypeAll<EnemyGameObject>())
        {
            try
            {
                if (enemyObject == null || enemyObject.gameObject == null ||
                    !enemyObject.gameObject.activeInHierarchy) continue;
                var scene = enemyObject.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded) continue;

                Enemy enemy = enemyObject.enemyStats?.enemy;
                if (enemy != null && predicate(enemy)) return enemy;
            }
            catch
            {
                // Spawn pools may invalidate an IL2CPP wrapper while this
                // snapshot is being inspected. The next scan retries it.
            }
        }

        return null;
    }

    private static Enemy FindUnlockedTypeInEvolutionChains(
        BaseMap map, EnemyType requiredType, EnemyType activeElementType)
    {
        var enemies = map?.enemies;
        if (enemies == null || requiredType == null) return null;

        for (int index = 0; index < enemies.Count; index++)
        {
            Enemy stage = GetEnemyFirstStage(enemies[index]);
            HashSet<string> visited = new(StringComparer.Ordinal);
            Enemy requiredStage = null;
            bool activeElementOverridesChain = false;
            for (int depth = 0; stage != null && depth < 32; depth++)
            {
                string identity = stage.name ?? $"stage_{index}_{depth}";
                if (!visited.Add(identity)) break;
                if (IsEnemyEvolutionUnlocked(stage))
                {
                    if (requiredStage == null &&
                        SameEnemyType(stage.type, requiredType))
                        requiredStage = stage;
                    if (activeElementType != null &&
                        !SameEnemyType(activeElementType, requiredType) &&
                        SameEnemyType(stage.type, activeElementType))
                        activeElementOverridesChain = true;
                }
                stage = stage.evolutionForward;
            }

            // A different active element only suppresses the requested
            // element when that same enemy chain actually contains an
            // unlocked stage for the active element. For example, Ice does
            // not suppress a Fire Bat chain that has no Ice evolution.
            if (requiredStage != null && !activeElementOverridesChain)
                return requiredStage;
        }

        return null;
    }

    private static EnemyType GetActiveElementType()
    {
        foreach (Divinity divinity in
                 UnityEngine.Resources.FindObjectsOfTypeAll<Divinity>())
        {
            if (divinity == null || !divinity.unlocked ||
                !divinity.isElemental) continue;
            EnemyType type = divinity.newBenefit?.enemyType;
            if (type != null) return type;
        }

        return null;
    }

    private static bool IsElementalDivinityActiveFor(EnemyType requiredType)
    {
        return SameEnemyType(GetActiveElementType(), requiredType);
    }

    private void LogUnresolvedElementQuestOnce(Quest quest)
    {
        if (quest == null || QuestCompat.Kind(quest) != LegacyQuestType.KillEnemiesOfType ||
            quest.enemyType == null) return;
        string key = QuestTargetSelection.BuildLockKey(quest);
        if (!loggedUnresolvedElementQuests.Add(key)) return;
        AdventurerLog.QuestDebug(
            $"Quest skipped: quest={GetQuestLabel(quest)}; " +
            $"reason=no unlocked Portal-selectable map currently exposes an enemy stage for elemental type; " +
            $"enemyType={quest.enemyType.name}; matchingElementActive={IsElementalDivinityActiveFor(quest.enemyType)}.");
    }

    private static Enemy GetEnemyFirstStage(Enemy enemy)
    {
        // EnemyUtility.GetEnemyFirstStage can resolve the wrong asset for
        // enemies whose runtime definitions share lookup metadata. Following
        // the serialized chain on the enemy itself preserves its identity.
        Enemy current = enemy;
        HashSet<string> visited = new(StringComparer.Ordinal);
        for (int depth = 0; current != null && depth < 32; depth++)
        {
            string identity = current.name ?? $"stage_{depth}";
            if (!visited.Add(identity) || current.evolutionBackward == null)
                return current;
            current = current.evolutionBackward;
        }

        return current;
    }

    private static bool IsEnemyEvolutionUnlocked(Enemy enemy)
    {
        Upgrade unlock = enemy?.upgradeToUnlockIt;
        if (unlock == null) return true;
        return unlock.bought || (unlock.isSpecialUnlock && unlock.specialUnlocked);
    }

    private static bool IsSameOrDescendant(Enemy candidate, Enemy target)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        Enemy current = candidate;
        for (int depth = 0; current != null && depth < 32; depth++)
        {
            if (SameEnemy(current, target)) return true;
            string identity = current.name ?? $"stage_{depth}";
            if (!visited.Add(identity)) return false;
            current = current.evolutionBackward;
        }

        return false;
    }

    private static bool SameEnemyType(EnemyType left, EnemyType right)
    {
        if (left == null || right == null) return false;
        return string.Equals(left.name, right.name, StringComparison.Ordinal);
    }

    private static bool SameEnemy(Enemy left, Enemy right)
    {
        if (left == null || right == null) return false;
        // bestiaryIndex is not unique for every runtime enemy definition.
        // Internal names are the stable identity used by quest targets.
        return string.Equals(left.name, right.name, StringComparison.Ordinal);
    }

    private static string GetQuestLabel(Quest quest)
    {
        string id = quest?.name ?? "UnknownQuest";
        string displayName = quest?.localizedName;
        return string.IsNullOrWhiteSpace(displayName)
            ? id
            : $"{LogText.Normalize(displayName)} ({id})";
    }
}
