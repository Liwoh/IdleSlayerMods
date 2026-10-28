using System;
using System.Collections.Generic;
using AutoProgression.Diagnostics;
using AutoProgression.Materials;
using AutoProgression.Quests;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Craftables;

internal sealed class QuestAssistCraftableService
{
    private const float CheckIntervalSeconds = 1f;
    private const double SpecializationScrapReserveRatio = 0.50d;
    private const double SpecializationOverflowScrapRatio = 0.80d;
    private const double SpecializationOverflowDragonScaleRatio = 0.50d;
    private readonly MaterialPurchaseService materials = new();

    private readonly Entry goblinSupport = new(
        "craftable_item_specialization",
        "Specialization",
        "specialization",
        NeedsSpecialRandomBox);

    private readonly Entry chestHuntSupport = new(
        "craftable_item_key_manifest",
        "Key Manifest",
        "keymanifest",
        HasChestHuntObjective);

    private float nextCheckTime;
    private QuestsList questsList;
    private string overflowBlockReason;

    internal bool Tick(float now)
    {
        if (!Plugin.Config.EnableQuestAssistCraftables.Value ||
            Plugin.Config.QuestAssistFeatherThresholdAmount.Value <= 0 ||
            now < nextCheckTime)
            return false;

        nextCheckTime = now + CheckIntervalSeconds;
        List<Quest> quests = SnapshotNormalQuests();
        string blockReason = GetSpecializationOverflowBlockReason();
        bool blockOverflowSpecialization = blockReason != null;
        LogOverflowBlockerTransition(blockReason);

        // Resource overflow is independent from the quest-trigger cooldown.
        // Native ExtraCondition remains the repeat-use gate after crafting.
        // Do not keep Special Random Boxes active while any unfinished quest
        // requires normal, Silver, or Golden Random Boxes, because that would
        // prevent progress.
        if (!blockOverflowSpecialization &&
            HasSpecializationOverflowResources() &&
            TryUse(goblinSupport, quests, now, true, false))
            return true;

        return TryUse(goblinSupport, quests, now, false, true) ||
               TryUse(chestHuntSupport, quests, now, false, true);
    }

    private bool TryUse(
        Entry entry,
        List<Quest> quests,
        float now,
        bool allowWithoutQuest,
        bool useQuestCooldown)
    {
        if ((useQuestCooldown && now < entry.CooldownUntil) ||
            (!allowWithoutQuest && !ContainsMatchingQuest(quests, entry)))
            return false;

        entry.Item ??= FindItem(entry);
        if (entry.Item == null)
        {
            if (!entry.MissingLogged)
            {
                ProgressionLog.Debug(
                    $"Quest assist craftable unavailable: {entry.InternalName}.");
                entry.MissingLogged = true;
            }
            return false;
        }

        entry.MissingLogged = false;
        if (!entry.Item.TabVisible() || !entry.Item.ExtraCondition())
            return false;

        if (!PreservesQuestAssistFeatherThreshold(entry.Item))
            return false;

        if (ReferenceEquals(entry, goblinSupport) &&
            !HasSpecializationDragonScaleReserve())
            return false;

        if (entry.Item.HowManyCanCraft() <= 0)
        {
            if (!HasRequiredProtectedMaterials(entry.Item))
                return false;

            if (Plugin.Config.BuyMissingMaterialsWithJewels.Value)
                BuyMissingPurchasableMaterials(entry.Item);

            if (entry.Item.HowManyCanCraft() <= 0)
                return false;
        }

        if (ReferenceEquals(entry, goblinSupport) &&
            !PreservesSpecializationScrapReserve(entry.Item))
            return false;

        entry.Item.Craft();
        if (useQuestCooldown)
        {
            float cooldownSeconds = Mathf.Max(0f,
                Configuration.AutoProgressionConfig.QuestAssistCraftableCooldownMinutes) * 60f;
            entry.CooldownUntil = now + cooldownSeconds;
            ProgressionLog.Debug(
                $"Quest assist: crafted {entry.DisplayName}; " +
                $"its next quest-triggered use is available in " +
                $"{cooldownSeconds / 60f:0.##} minute(s).",
                "Craftables");
        }
        else
        {
            ProgressionLog.Debug(
                $"Resource overflow: crafted {entry.DisplayName}; " +
                "the native one-use state controls its next use.",
                "Craftables");
        }
        return true;
    }

    private static bool HasSpecializationOverflowResources()
    {
        Drop scrap = Drops.list?.Scrap;
        Drop dragonScale = Drops.list?.DragonScale;
        if (scrap == null || dragonScale == null) return false;

        double scrapMaximum = scrap.GetMaxAmount();
        double scaleMaximum = dragonScale.GetMaxAmount();
        return scrapMaximum > 0d && scaleMaximum > 0d &&
               scrap.amount / scrapMaximum > SpecializationOverflowScrapRatio &&
               dragonScale.amount / scaleMaximum >
               SpecializationOverflowDragonScaleRatio;
    }

    private static bool HasSpecializationDragonScaleReserve()
    {
        Drop dragonScale = Drops.list?.DragonScale;
        if (dragonScale == null) return false;

        double maximum = dragonScale.GetMaxAmount();
        return maximum > 0d &&
               dragonScale.amount / maximum >
               SpecializationOverflowDragonScaleRatio;
    }

    private static bool PreservesSpecializationScrapReserve(
        TemporaryCraftableItem item)
    {
        Drop scrap = Drops.list?.Scrap;
        var requirements = item.GetRequirements();
        if (scrap == null || requirements == null) return false;

        double maximum = scrap.GetMaxAmount();
        if (maximum <= 0d) return false;

        double scrapCost = 0d;
        foreach (MaterialRequirement requirement in requirements)
        {
            if (requirement?.material != null &&
                IsScrapMaterial(requirement.material))
                scrapCost += requirement.amount;
        }

        return scrapCost <= 0d ||
               scrap.amount - scrapCost >=
               maximum * SpecializationScrapReserveRatio;
    }

    private static bool PreservesQuestAssistFeatherThreshold(
        TemporaryCraftableItem item)
    {
        int threshold =
            Plugin.Config.QuestAssistFeatherThresholdAmount.Value;
        Drop feather = Drops.list?.SimurghFeather;
        var requirements = item.GetRequirements();
        if (threshold <= 0 || feather == null || requirements == null ||
            feather.amount <= threshold)
            return false;

        double featherCost = 0d;
        foreach (MaterialRequirement requirement in requirements)
        {
            if (requirement?.material != null &&
                IsSimurghFeather(requirement.material))
                featherCost += requirement.amount;
        }

        return feather.amount - featherCost >= threshold;
    }

    private static bool HasRequiredProtectedMaterials(
        TemporaryCraftableItem item)
    {
        var requirements = item.GetRequirements();
        if (requirements == null) return false;

        foreach (MaterialRequirement requirement in requirements)
        {
            if (requirement?.material == null ||
                !IsProtectedMaterial(requirement.material))
                continue;
            if (requirement.material.amount < requirement.amount)
                return false;
        }

        return true;
    }

    private void BuyMissingPurchasableMaterials(TemporaryCraftableItem item)
    {
        var requirements = item.GetRequirements();
        if (requirements == null) return;

        int percent = Plugin.Config.MaterialPurchasePercent.Value;
        foreach (MaterialRequirement requirement in requirements)
        {
            Drop material = requirement?.material;
            if (material == null || IsProtectedMaterial(material) ||
                material.amount >= requirement.amount)
                continue;
            materials.Buy(material, percent);
        }
    }

    private static bool IsProtectedMaterial(Drop material)
    {
        if (material == null) return false;
        Drops drops = Drops.list;
        if (IsScrapMaterial(material) ||
            IsSimurghFeather(material) ||
            ReferenceEquals(material, drops?.DragonScale))
            return true;

        string name = Normalize(material.name);
        return name.Contains("simurghfeather") ||
               name.Contains("dragonscale");
    }

    private static bool IsScrapMaterial(Drop material) =>
        material != null &&
        (ReferenceEquals(material, Drops.list?.Scrap) ||
         Normalize(material.name).Contains("scrap"));

    private static bool IsSimurghFeather(Drop material) =>
        material != null &&
        (ReferenceEquals(material, Drops.list?.SimurghFeather) ||
         Normalize(material.name).Contains("simurghfeather"));

    private List<Quest> SnapshotNormalQuests()
    {
        questsList ??= QuestsList.instance ?? FindLoadedQuestList();
        var source = QuestCompat.ListedQuests(questsList);
        List<Quest> result = new();
        if (source == null) return result;

        for (int index = 0; index < source.Count; index++)
        {
            Quest quest = source[index];
            if (quest == null || quest is DailyQuest || quest is WeeklyQuest)
                continue;

            try
            {
                if (!quest.isClaimed && !quest.IsCompleted() &&
                    quest.CanBeCompleted())
                    result.Add(quest);
            }
            catch
            {
                // A quest can be replaced while this read-only snapshot is
                // being built. Skip that stale IL2CPP object safely.
            }
        }

        return result;
    }

    private static bool ContainsMatchingQuest(List<Quest> quests, Entry entry)
    {
        foreach (Quest quest in quests)
        {
            try
            {
                if (entry.Matches(quest)) return true;
            }
            catch
            {
                // Treat a concurrently replaced quest as unavailable.
            }
        }

        return false;
    }

    private static string GetSpecializationOverflowBlockReason()
    {
        HashSet<int> seen = new();

        QuestsList list = QuestsList.instance ?? FindLoadedQuestList();
        var visible = QuestCompat.ListedQuests(list);
        if (visible == null)
            return "current quest data is not available yet";

        for (int index = 0; index < visible.Count; index++)
        {
            Quest quest = visible[index];
            if (IsActiveRandomBoxObjective(quest, seen))
                return "an active quest requires normal, Silver, or Golden Random Boxes";
        }

        return null;
    }

    private static bool IsActiveRandomBoxObjective(
        Quest quest,
        HashSet<int> seen)
    {
        if (quest == null ||
            QuestCompat.Kind(quest) is not (
                LegacyQuestType.HitRandomBoxes or
                LegacyQuestType.HitRandomSilverBoxes or
                LegacyQuestType.HitRandomGoldenBoxes))
            return false;

        try
        {
            if (!seen.Add(quest.GetInstanceID()) ||
                quest.isClaimed || quest.IsCompleted())
                return false;

            if (quest is DailyQuest daily)
                return daily.active && daily.CheckIfIsValid();
            if (quest is WeeklyQuest weekly)
                return weekly.active;

            return quest.CanBeCompleted();
        }
        catch
        {
            // A concurrently replaced quest cannot safely authorize or clear
            // the blocker in this frame.
            return false;
        }
    }

    private void LogOverflowBlockerTransition(string reason)
    {
        if (string.Equals(
                reason,
                overflowBlockReason,
                StringComparison.Ordinal))
            return;

        bool wasBlocked = overflowBlockReason != null;
        overflowBlockReason = reason;
        if (reason == null && !wasBlocked)
            return;

        ProgressionLog.Debug(
            reason != null
                ? $"Specialization resource-overflow use paused because {reason}."
                : "Specialization resource-overflow use resumed; no active quest requires Random Boxes.",
            "Craftables");
    }

    private static bool HasGoblinObjective(Quest quest)
    {
        if (ContainsGoblin(quest.enemyToKill) ||
            ContainsText(quest.enemyType?.name, "goblin"))
            return true;

        return ContainsText(quest.name, "goblin");
    }

    private static bool NeedsSpecialRandomBox(Quest quest) =>
        HasGoblinObjective(quest) ||
        QuestCompat.Kind(quest) is LegacyQuestType.BonusStage or
            LegacyQuestType.BonusStageSections;

    private static bool ContainsGoblin(Enemy enemy)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        Enemy current = enemy;
        for (int depth = 0; current != null && depth < 32; depth++)
        {
            string name = current.name ?? string.Empty;
            if (ContainsText(name, "goblin")) return true;
            if (!visited.Add(name)) break;
            current = current.evolutionBackward;
        }

        return false;
    }

    private static bool HasChestHuntObjective(Quest quest)
    {
        string type = QuestCompat.Kind(quest).ToString();
        return ContainsText(type, "chesthunt") ||
               ContainsText(quest.name, "chest_hunt") ||
               ContainsText(quest.name, "chesthunt");
    }

    private static bool ContainsText(string value, string fragment) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

    private static TemporaryCraftableItem FindItem(Entry entry)
    {
        foreach (TemporaryCraftableItem item in
                 Resources.FindObjectsOfTypeAll<TemporaryCraftableItem>())
        {
            if (item == null) continue;
            if (string.Equals(item.name, entry.InternalName,
                    StringComparison.Ordinal) ||
                Normalize(item.name).Contains(entry.NormalizedName))
                return item;
        }

        return null;
    }

    private static QuestsList FindLoadedQuestList()
    {
        foreach (QuestsList candidate in Resources.FindObjectsOfTypeAll<QuestsList>())
        {
            if (candidate == null || !candidate.gameObject.scene.IsValid() ||
                !candidate.gameObject.scene.isLoaded)
                continue;
            return candidate;
        }

        return null;
    }

    private static string Normalize(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();

    internal void Reset()
    {
        nextCheckTime = 0f;
        questsList = null;
        goblinSupport.Item = null;
        goblinSupport.MissingLogged = false;
        chestHuntSupport.Item = null;
        chestHuntSupport.MissingLogged = false;
        overflowBlockReason = null;
        // Successful-use cooldowns intentionally survive scene transitions
        // and ascension cache resets within the current game session.
    }

    private sealed class Entry
    {
        internal readonly string InternalName;
        internal readonly string DisplayName;
        internal readonly string NormalizedName;
        internal readonly Func<Quest, bool> Matches;
        internal TemporaryCraftableItem Item;
        internal float CooldownUntil;
        internal bool MissingLogged;

        internal Entry(
            string internalName,
            string displayName,
            string normalizedName,
            Func<Quest, bool> matches)
        {
            InternalName = internalName;
            DisplayName = displayName;
            NormalizedName = normalizedName;
            Matches = matches;
        }
    }
}
