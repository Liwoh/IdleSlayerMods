using System.Collections.Generic;
using AutoProgression.Diagnostics;
using AutoProgression.Quests;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.SilverBoxes;

// Keeps activated Silver Boxes stored instead of spawning them, for the
// "have N unspent Silver Random Boxes" achievements. The game's method bodies
// are not readable, so the stock and spawn probes below tell whether blocking
// the spawn really keeps the stock and whether a normal box spawns instead.
internal sealed class SilverBoxStockpileService
{
    private const float CheckIntervalSeconds = 1f;
    private const float QuestCheckIntervalSeconds = 5f;
    private const float ProbeIntervalSeconds = 300f;

    internal static bool Blocking { get; private set; }
    internal static int BlockedSinceStockChange;
    internal static int BlockedInWindow;
    internal static readonly Dictionary<string, int> SpawnsByType = new();

    private float nextCheckTime;
    private float nextQuestCheckTime;
    private float nextProbeTime;
    private string releaseQuest = string.Empty;
    private double lastStock = -1d;
    private string lastState = string.Empty;

    internal void Tick(float now)
    {
        if (now < nextCheckTime) return;
        nextCheckTime = now + CheckIntervalSeconds;
        SilverRandomBoxManager manager = SilverRandomBoxManager.instance;
        if (manager == null) return;

        bool stockpile = Plugin.Config.StockpileSilverBoxes.Value;
        if (now >= nextQuestCheckTime)
        {
            nextQuestCheckTime = now + QuestCheckIntervalSeconds;
            releaseQuest = stockpile && Plugin.Config.ReleaseStockpileForSilverBoxQuests.Value
                ? FindSilverBoxQuest()
                : string.Empty;
        }

        Blocking = stockpile && releaseQuest.Length == 0;
        double stock = manager.silverBoxesLeft;
        LogState(stockpile, stock);
        ProbeStock(stock);
        ProbeSpawns(now, stock);
    }

    private void LogState(bool stockpile, double stock)
    {
        string state = !stockpile ? "off" : Blocking ? "blocking" : "released:" + releaseQuest;
        if (state == lastState) return;
        bool first = lastState.Length == 0;
        lastState = state;
        if (first && !stockpile) return;
        ProgressionLog.User(!stockpile
            ? $"Silver Box stockpile off: stored boxes spawn again (stored={stock:0})."
            : Blocking
                ? $"Silver Box stockpile on: Silver Box spawns blocked (stored={stock:0})."
                : $"Silver Box stockpile released for the quest {releaseQuest} (stored={stock:0}).");
    }

    private void ProbeStock(double stock)
    {
        if (lastStock >= 0d && stock != lastStock)
            ProgressionLog.SilverBoxesDebug(
                $"Silver Box stock: {lastStock:0} -> {stock:0}; blocked spawns since the last change={BlockedSinceStockChange}; blocking={Blocking}.");
        if (stock != lastStock) BlockedSinceStockChange = 0;
        lastStock = stock;
    }

    private void ProbeSpawns(float now, double stock)
    {
        if (nextProbeTime <= 0f) nextProbeTime = now + ProbeIntervalSeconds;
        if (now < nextProbeTime) return;
        nextProbeTime = now + ProbeIntervalSeconds;
        List<string> parts = new();
        foreach (KeyValuePair<string, int> entry in SpawnsByType)
            parts.Add($"{entry.Key}={entry.Value}");
        ProgressionLog.SilverBoxesDebug(
            $"Silver Box probe ({ProbeIntervalSeconds / 60f:0} min): Random Box spawns by type [{string.Join(", ", parts)}]; " +
            $"silver spawns blocked={BlockedInWindow}; blocking={Blocking}; stored={stock:0}.");
        SpawnsByType.Clear();
        BlockedInWindow = 0;
    }

    // Story and event quests only: the Daily filter rerolls Silver Box
    // Dailies, and Weekly ones are rerolled unless kept.
    private static string FindSilverBoxQuest()
    {
        PlayerInventory inventory = PlayerInventory.instance;
        if (inventory == null || inventory.allQuests == null) return string.Empty;
        var all = inventory.allQuests;
        for (int index = 0; index < all.Count; index++)
        {
            Quest quest = all[index];
            if (quest == null) continue;
            try
            {
                if (QuestCompat.Kind(quest) != LegacyQuestType.HitRandomSilverBoxes ||
                    quest.isClaimed ||
                    quest.TryCast<DailyQuest>() != null ||
                    quest.TryCast<WeeklyQuest>() != null ||
                    (quest.mapEvent != null && !quest.EventAvailable()) ||
                    !quest.CanBeCompleted() ||
                    quest.CanBeClaimed())
                    continue;
                return quest.name;
            }
            catch
            {
                // An unreadable catalogue entry does not release the stockpile.
            }
        }
        return string.Empty;
    }

    internal void Reset()
    {
        nextCheckTime = 0f;
        nextQuestCheckTime = 0f;
        lastStock = -1d;
    }
}

[HarmonyPatch(typeof(SilverRandomBoxManager), nameof(SilverRandomBoxManager.SpawnSilverRandomBox))]
internal static class SilverRandomBoxSpawnPatch
{
    private static bool Prefix(Vector2 __0, bool __1)
    {
        if (!SilverBoxStockpileService.Blocking) return true;
        SilverBoxStockpileService.BlockedSinceStockChange++;
        SilverBoxStockpileService.BlockedInWindow++;
        if (SilverBoxStockpileService.BlockedInWindow == 1)
            ProgressionLog.SilverBoxesDebug($"Silver Box spawn blocked; flag={__1}.");
        return false;
    }
}

[HarmonyPatch(typeof(RandomBox), nameof(RandomBox.OnObjectSpawn))]
internal static class RandomBoxSpawnProbePatch
{
    private static void Postfix(RandomBox __instance)
    {
        if (__instance == null) return;
        string type = __instance.type.ToString();
        SilverBoxStockpileService.SpawnsByType.TryGetValue(type, out int count);
        SilverBoxStockpileService.SpawnsByType[type] = count + 1;
    }
}
