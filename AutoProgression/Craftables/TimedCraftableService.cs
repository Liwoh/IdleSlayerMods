using AutoProgression.Diagnostics;
using AutoProgression.Materials;
using AutoProgression.Quests;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace AutoProgression.Craftables;

internal sealed class TimedCraftableService
{
    private const float CheckIntervalSeconds = 1f;
    private const float SummaryIntervalSeconds = 30f;

    private readonly MaterialPurchaseService materials = new();
    private readonly Entry[] entries;
    private float nextCheckTime;
    private float nextSummaryTime;
    private bool storyKillQuestSeen;
    private bool giantTargetSeen;

    internal TimedCraftableService()
    {
        entries = new[]
        {
            new Entry("craftable_item_whetstone", "Whetstone", () => Plugin.Config.EnableWhetstone),
            new Entry("craftable_item_alternate_dimension_staff", "Alternate Dimension Staff", () => Plugin.Config.EnableAlternateDimensionStaff),
            new Entry("craftable_item_bidimensional_staff", "Bidimensional Staff", () => Plugin.Config.EnableBidimensionalStaff),
            new Entry(
                "craftable_item_deathwave_scepter",
                "Deathwave Scepter",
                () => Plugin.Config.EnableDeathwaveScepter,
                HasDeathwaveFeatherReserve),
            new Entry(
                "craftable_item_shards_necklace",
                "Shards Necklace",
                () => Plugin.Config.EnableShardsNecklaceGiantStoryQuests,
                () => StoryKillQuestBridge.GiantTarget)
        };
    }

    internal bool Tick(float now)
    {
        FlushSummary(now);
        if (now < nextCheckTime) return false;
        nextCheckTime = now + CheckIntervalSeconds;
        LogStoryKillQuestChange();
        float refillAtMinutes = Mathf.Max(0f, Plugin.Config.TimedCraftablesTargetMinutes.Value * 0.5f);
        float targetMinutes = Mathf.Max(
            refillAtMinutes,
            Mathf.Max(0f, Plugin.Config.TimedCraftablesTargetMinutes.Value));
        double refillAtSeconds = refillAtMinutes * 60d;
        double targetSeconds = targetMinutes * 60d;

        foreach (Entry entry in entries)
        {
            if (!entry.Enabled().Value ||
                (Plugin.Config.StoryKillQuestCraftablesOnly.Value && !StoryKillQuestBridge.Active))
            {
                entry.Refilling = false;
                continue;
            }
            entry.Item ??= FindItem(entry.InternalName);
            if (entry.Item == null)
            {
                if (!entry.MissingLogged)
                {
                    ProgressionLog.Debug($"Craftable object unavailable: {entry.InternalName}.");
                    entry.MissingLogged = true;
                }
                continue;
            }
            entry.MissingLogged = false;

            if (!entry.Item.TabVisible() || !entry.Item.ExtraCondition()) continue;
            if (!entry.AdditionalCondition())
            {
                entry.Refilling = false;
                continue;
            }

            // Enter refill mode at the lower threshold, then keep stacking even
            // after crossing it until the upper target has been reached.
            if (!entry.Refilling && entry.Item.currentTime <= refillAtSeconds)
                entry.Refilling = true;

            if (!entry.Refilling) continue;
            if (entry.Item.currentTime >= targetSeconds)
            {
                entry.Refilling = false;
                continue;
            }

            if (entry.Item.HowManyCanCraft() <= 0 && Plugin.Config.BuyMissingMaterialsWithJewels.Value)
                BuyMissingRequirements(entry.Item);

            if (entry.Item.HowManyCanCraft() <= 0) continue;

            entry.Item.Craft();
            entry.CraftedSinceSummary++;
            if (nextSummaryTime <= 0f)
                nextSummaryTime = now + SummaryIntervalSeconds;
            return true;
        }

        return false;
    }

    private void BuyMissingRequirements(TemporaryCraftableItem item)
    {
        int percent = Plugin.Config.MaterialPurchasePercent.Value;
        var requirements = item.GetRequirements();
        if (requirements == null)
        {
            ProgressionLog.Debug($"Craftable requirements unavailable: {item.name}.");
            return;
        }

        foreach (MaterialRequirement requirement in requirements)
        {
            if (requirement == null || requirement.material == null) continue;
            if (requirement.material.amount >= requirement.amount) continue;
            materials.Buy(requirement.material, percent);
        }
    }

    private static TemporaryCraftableItem FindItem(string internalName)
    {
        foreach (TemporaryCraftableItem item in Resources.FindObjectsOfTypeAll<TemporaryCraftableItem>())
        {
            if (item != null && item.name == internalName) return item;
        }

        // Fallback for items whose exact internal name is not known.
        string wanted = Normalize(internalName.Replace("craftable_item_", string.Empty));
        foreach (TemporaryCraftableItem item in Resources.FindObjectsOfTypeAll<TemporaryCraftableItem>())
        {
            if (item != null && Normalize(item.name).Contains(wanted)) return item;
        }

        return null;
    }

    private void LogStoryKillQuestChange()
    {
        if (!Plugin.Config.StoryKillQuestCraftablesOnly.Value) return;
        bool story = StoryKillQuestBridge.Active;
        bool giant = StoryKillQuestBridge.GiantTarget;
        if (story == storyKillQuestSeen && giant == giantTargetSeen) return;
        storyKillQuestSeen = story;
        giantTargetSeen = giant;
        ProgressionLog.Debug(
            story
                ? $"Story kill quest in progress: timed craftables allowed; Giant target={giant}."
                : "No story kill quest in progress: timed craftables paused.",
            "Craftables");
    }

    private static string Normalize(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

    private static bool HasDeathwaveFeatherReserve()
    {
        Drop feather = Drops.list?.SimurghFeather;
        int reserve = System.Math.Max(
            0,
            Plugin.Config.DeathwaveScepterFeatherReserveAmount.Value);
        return feather != null && feather.amount > reserve;
    }

    internal void Reset()
    {
        nextCheckTime = 0f;
        nextSummaryTime = 0f;
        foreach (Entry entry in entries)
        {
            entry.Item = null;
            entry.MissingLogged = false;
            entry.Refilling = false;
            entry.CraftedSinceSummary = 0;
        }
    }

    private void FlushSummary(float now)
    {
        if (nextSummaryTime <= 0f || now < nextSummaryTime)
            return;

        System.Collections.Generic.List<string> parts = new();
        foreach (Entry entry in entries)
        {
            if (entry.CraftedSinceSummary <= 0) continue;
            parts.Add($"{entry.DisplayName} x{entry.CraftedSinceSummary}");
            entry.CraftedSinceSummary = 0;
        }

        if (parts.Count > 0)
            ProgressionLog.Debug(
                $"Timed craftables used in the last {SummaryIntervalSeconds:0} seconds: " +
                string.Join(", ", parts) + ".",
                "Craftables");
        nextSummaryTime = 0f;
    }

    private sealed class Entry
    {
        internal readonly string InternalName;
        internal readonly string DisplayName;
        internal readonly System.Func<MelonPreferences_Entry<bool>> Enabled;
        internal readonly System.Func<bool> AdditionalCondition;
        internal TemporaryCraftableItem Item;
        internal bool MissingLogged;
        internal bool Refilling;
        internal int CraftedSinceSummary;

        internal Entry(
            string internalName,
            string displayName,
            System.Func<MelonPreferences_Entry<bool>> enabled,
            System.Func<bool> additionalCondition = null)
        {
            InternalName = internalName;
            DisplayName = displayName;
            Enabled = enabled;
            AdditionalCondition = additionalCondition ?? (() => true);
        }
    }
}
