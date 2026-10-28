using Il2Cpp;
using System;
using System.Collections.Generic;
using System.Globalization;
using AutoAdventurer.Diagnostics;
using UnityEngine;

namespace AutoAdventurer.Quests;

internal sealed class QuestTargetSelection
{
    private int? cachedRewardPriority;
    private static readonly HashSet<string> PriorityUnlockBenefits =
        new(StringComparer.Ordinal)
        {
            "IncreaseQuestsLevel",
            "UnlockMinion",
            "UnlockGiant",
            "UnlockCharacter",
            "SupplyBox",
            "UnlockStoneOfTime",
            "UnlockInJewelOfSoulTab",
            "UnlockEquipment",
            "UnlockNPCInVillage",
            "UnlockNewAscensionUpgrades",
            "BossFightFromSpecialRandomBox",
            "ElementalLock",
            "UnlockNewColorVariant",
            "UnlockLoadout"
        };

    internal Quest Quest { get; init; }
    internal Enemy Enemy { get; init; }
    internal BaseMap Map { get; init; }
    internal double RequiredKills { get; init; }
    internal double CurrentKills { get; init; }

    internal string QuestId => Quest?.name ?? "UnknownQuest";
    internal string QuestDisplayName =>
        string.IsNullOrWhiteSpace(Quest?.localizedName)
            ? QuestId
            : LogText.Normalize(Quest.localizedName);
    internal string EnemyId => Enemy?.name ?? "AnyEnemy";
    internal string MapId => Map?.name ?? "UnknownMap";
    internal string QuestTypeId => Quest != null ? QuestCompat.Kind(Quest).ToString() : "UnknownQuestType";
    internal string ObjectiveId => Quest?.enemyToKill?.name ??
        Quest?.enemyType?.name ?? QuestTypeId;
    internal string RuntimeType =>
        Quest?.GetIl2CppType()?.Name ?? "UnknownRuntimeType";
    internal bool SuppressesAutomaticRage =>
        Quest != null && QuestCompat.Kind(Quest) == LegacyQuestType.KillEnemiesWithArrows;
    internal CharacterSkin RequiredCharacter => Quest?.characterRequired;
    internal string RequiredCharacterId =>
        RequiredCharacter?.name ?? string.Empty;
    internal Upgrade UnlockReward => Quest?.unlocksUpgrade;
    internal bool HasUnlockReward => UnlockReward != null;
    internal string UnlockRewardId => UnlockReward?.name ?? string.Empty;
    internal string UnlockRewardDisplayName =>
        string.IsNullOrWhiteSpace(UnlockReward?.localizedName)
            ? UnlockRewardId
            : LogText.Normalize(UnlockReward.localizedName);
    internal string UnlockRewardBenefitId =>
        UnlockReward?.newBenefit?.benefit?.GetIl2CppType()?.Name ??
        string.Empty;
    internal int RewardPriority => cachedRewardPriority ??=
        IsQuestLevelUnlockReward || UnlocksFollowUpQuest
            ? 2
            : PriorityUnlockBenefits.Contains(UnlockRewardBenefitId)
                ? 1
                : 0;
    internal string RewardPriorityLabel => RewardPriority switch
    {
        2 => "Top",
        1 => "High",
        _ => "Normal"
    };
    internal bool HasPriorityUnlockReward =>
        RewardPriority > 0;
    private bool IsQuestLevelUnlockReward =>
        string.Equals(UnlockRewardBenefitId, "IncreaseQuestsLevel",
            StringComparison.Ordinal) ||
        (UnlockRewardId.StartsWith("upgrade_", StringComparison.Ordinal) &&
         UnlockRewardId.EndsWith("_quests", StringComparison.Ordinal));

    private bool UnlocksFollowUpQuest
    {
        get
        {
            Upgrade reward = UnlockReward;
            var quests = PlayerInventory.instance?.allQuests;
            if (reward == null || quests == null) return false;

            try
            {
                var futureBundles = new HashSet<string>(
                    StringComparer.Ordinal);
                for (int index = 0; index < quests.Count; index++)
                {
                    Quest followUp = quests[index];
                    Upgrade required = followUp?.bundleRequired;
                    if (followUp == null || required == null ||
                        followUp.isClaimed) continue;
                    if (string.Equals(required.name, reward.name,
                            StringComparison.Ordinal))
                        return true;

                    if (!IsUpgradeUnlocked(required))
                        futureBundles.Add(required.name);
                }

                // Many quest chains are indirect: the current quest rewards
                // an upgrade, and a later quest-bundle upgrade lists that
                // reward in upgradesRequired. Follow the internal dependency
                // graph instead of relying on localized text or upgrade-name
                // conventions.
                foreach (Upgrade bundle in
                         Resources.FindObjectsOfTypeAll<Upgrade>())
                {
                    if (bundle == null ||
                        !futureBundles.Contains(bundle.name)) continue;
                    if (DependsOnUpgrade(bundle, reward,
                            new HashSet<string>(StringComparer.Ordinal), 0))
                        return true;
                }
            }
            catch
            {
                // A transient catalogue wrapper must not break selection;
                // the existing reward metadata remains the fallback.
            }

            return false;
        }
    }

    private static bool DependsOnUpgrade(
        Upgrade candidate, Upgrade target, HashSet<string> visited, int depth)
    {
        if (candidate == null || target == null || depth >= 16 ||
            !visited.Add(candidate.name ?? $"upgrade_{depth}")) return false;
        var requirements = candidate.upgradesRequired;
        if (requirements == null) return false;

        for (int index = 0; index < requirements.Count; index++)
        {
            Upgrade required = requirements[index]?.upgrade;
            if (required == null) continue;
            if (string.Equals(required.name, target.name,
                    StringComparison.Ordinal)) return true;
            if (DependsOnUpgrade(required, target, visited, depth + 1))
                return true;
        }

        return false;
    }

    private static bool IsUpgradeUnlocked(Upgrade upgrade) =>
        upgrade != null && (upgrade.bought ||
            (upgrade.isSpecialUnlock && upgrade.specialUnlocked));
    internal double RemainingKills =>
        System.Math.Max(0d, RequiredKills - CurrentKills);
    internal string LockKey => BuildLockKey(Quest);

    internal static string BuildLockKey(Quest quest)
    {
        if (quest == null) return string.Empty;
        string runtimeType = quest.GetIl2CppType()?.Name ?? "UnknownRuntimeType";
        string enemy = quest.enemyToKill?.name ?? string.Empty;
        string enemyType = quest.enemyType?.name ?? string.Empty;
        string character = quest.characterRequired?.name ?? string.Empty;
        string goal = quest.questGoal.ToString(CultureInfo.InvariantCulture);
        return $"{runtimeType}|{quest.name}|{QuestCompat.Kind(quest)}|{enemy}|{enemyType}|{character}|{goal}";
    }
}
