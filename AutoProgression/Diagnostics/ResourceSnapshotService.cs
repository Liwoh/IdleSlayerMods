using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;

namespace AutoProgression.Diagnostics;

// Writes one [Resources] line every five minutes so a session's resource
// curve can be read back from Latest.log.
internal sealed class ResourceSnapshotService
{
    private const float IntervalSeconds = 300f;
    private const float FirstSnapshotDelaySeconds = 30f;
    private float nextSnapshotTime = -1f;
    private bool catalogueLogged;

    // Once per session: every Daily and Weekly quest the game can draw, by
    // type with goal range, plus the egg caps.
    private static void LogCatalogue()
    {
        LogQuestPool("Daily", UnityEngine.Resources.FindObjectsOfTypeAll<DailyQuest>());
        LogQuestPool("Weekly", UnityEngine.Resources.FindObjectsOfTypeAll<WeeklyQuest>());
        Drops special = Drops.list;
        if (special != null)
            ProgressionLog.User(
                $"[Catalogue] egg caps: dragon={Cap(special.DragonEgg)}, simurgh={Cap(special.SimurghEgg)}.");
    }

    private static void LogQuestPool<T>(string label, IEnumerable<T> quests) where T : Quest
    {
        var byType = new SortedDictionary<string, (int count, int active, int min, int max)>(StringComparer.Ordinal);
        foreach (T quest in quests)
        {
            if (quest == null) continue;
            string key = Quests.QuestCompat.Kind(quest).ToString();
            if (quest.enemyToKill != null) key += ":target";
            else if (quest.enemyType != null) key += ":element";
            bool active = quest.TryCast<DailyQuest>()?.active ?? quest.TryCast<WeeklyQuest>()?.active ?? false;
            byType.TryGetValue(key, out var entry);
            entry = (entry.count + 1, entry.active + (active ? 1 : 0),
                entry.count == 0 ? quest.questGoal : Math.Min(entry.min, quest.questGoal),
                Math.Max(entry.max, quest.questGoal));
            byType[key] = entry;
        }
        var parts = new List<string>();
        foreach (var pair in byType)
            parts.Add($"{pair.Key}={pair.Value.count} (goal {pair.Value.min}-{pair.Value.max}, active {pair.Value.active})");
        ProgressionLog.User($"[Catalogue] {label} pool: " + string.Join("; ", parts));
    }

    private static string Cap(Drop drop)
    {
        if (drop == null) return "?";
        try { return Format(drop.GetMaxAmount()); }
        catch { return Format(drop.maxAmount); }
    }

    internal void Tick(float now)
    {
        if (nextSnapshotTime < 0f) nextSnapshotTime = now + FirstSnapshotDelaySeconds;
        if (now < nextSnapshotTime) return;
        nextSnapshotTime = now + IntervalSeconds;

        PlayerInventory inventory = PlayerInventory.instance;
        if (inventory == null) return;
        try
        {
            ProgressionLog.User("[Resources] " + Describe(inventory));
            if (!catalogueLogged)
            {
                catalogueLogged = true;
                LogCatalogue();
            }
        }
        catch (Exception exception)
        {
            ProgressionLog.Exception("Resource snapshot", exception);
        }
    }

    private static string Describe(PlayerInventory inventory)
    {
        var parts = new List<string>
        {
            $"SP={Format(SlayerPoints.points)}",
            $"pendingSP={Format(SlayerPoints.pre)}",
            $"USP={Format(UltraSlayerPoints.points)}",
            $"pendingUSP={Format(UltraSlayerPoints.pre)}",
            $"jewels={Format(inventory.jewelsOfSoul)}",
            $"divinity={Format(inventory.divinityPoints)}",
            $"silverBoxes={Format(inventory.silverRandomBoxesAmount)}"
        };

        Drops special = Drops.list;
        if (special != null)
        {
            parts.Add($"dragonEggs={Amount(special.DragonEgg)}");
            parts.Add($"simurghEggs={Amount(special.SimurghEgg)}");
            parts.Add($"scrap={Amount(special.Scrap)}");
        }

        WeaponsManager weapons = WeaponsManager.instance;
        if (weapons != null)
        {
            int count = 0;
            foreach (WeaponItemSlot slot in weapons.allSlots)
            {
                ItemObject item = slot != null
                    ? slot.GetComponentInChildren<ItemObject>() : null;
                if (item != null && item.currentItem != null) count++;
            }
            parts.Add($"armory={count}");
        }
        parts.Add($"weaponsFound={Format(inventory.weaponsFound)}");
        parts.Add($"weaponsBroken={Format(inventory.weaponsBroken)}");

        var materials = new List<string>();
        if (inventory.drops != null)
        {
            foreach (Drop drop in inventory.drops)
            {
                if (drop == null || drop.locked || drop.amount <= 0d) continue;
                string name = drop.name.StartsWith("drop_")
                    ? drop.name.Substring(5) : drop.name;
                materials.Add($"{name}={Format(drop.amount)}");
            }
        }
        materials.Sort(StringComparer.Ordinal);
        parts.Add("materials: " + string.Join(", ", materials));
        return string.Join("; ", parts);
    }

    private static string Amount(Drop drop) =>
        drop != null ? Format(drop.amount) : "?";

    private static string Format(double value) =>
        Math.Abs(value) < 1e6
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.###e0", CultureInfo.InvariantCulture);
}
