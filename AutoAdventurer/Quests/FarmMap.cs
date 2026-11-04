using System;
using AutoAdventurer.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoAdventurer.Quests;

// The dimension the player wants to farm (Farm Map setting). With "Current",
// it follows the player's own portal trips but ignores the mod's quest trips.
// While a targeted story quest keeps the mod elsewhere, Daily and Weekly
// quests are checked against that map instead, so they progress meanwhile.
// Public because AutoProgression reaches IsDoableOnFarmMap by reflection.
public static class FarmMap
{
    private static readonly QuestTargetResolver Resolver = new();
    private static string modTravelTargetId = string.Empty;
    private static string lastSeenMapId = string.Empty;
    private static string storyMapId = string.Empty;
    private static float storyMapReachedAt = -1f;
    private static string referenceMapId = string.Empty;
    private static string mapBeforeLastChange = string.Empty;
    private static float lastChangeAt = -1f;
    private static double observedUltraAscensions = -1d;
    private static float ultraAscensionSeenAt = -1f;

    internal static string MapId { get; private set; } = string.Empty;
    internal static bool Active => MapId.Length > 0;

    internal static void NoteModTravel(string mapId) =>
        modTravelTargetId = mapId ?? string.Empty;

    // The map of the locked targeted story quest, or empty when none is locked.
    internal static void HoldForStoryQuest(string mapId)
    {
        mapId ??= string.Empty;
        if (string.Equals(mapId, storyMapId, StringComparison.Ordinal)) return;
        storyMapId = mapId;
        storyMapReachedAt = -1f;
    }

    // Called when Quest Automation (P) is enabled or disabled: with Current,
    // the next Observe() takes the dimension the player is in.
    internal static void Clear()
    {
        modTravelTargetId = string.Empty;
        lastSeenMapId = string.Empty;
        HoldForStoryQuest(string.Empty);
        SetMap(string.Empty);
        Save();
    }

    // Returns true when the farm map just changed, so travel already planned
    // towards the previous one can be dropped.
    internal static bool Observe()
    {
        string before = MapId;
        string setting = (Plugin.Config.FarmMap.Value ?? string.Empty).Trim();
        string current = MapController.instance?.selectedMap?.name ?? string.Empty;
        if (setting.Length == 0 || setting.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            SetMap(string.Empty);
        }
        else if (!setting.Equals("Current", StringComparison.OrdinalIgnoreCase))
        {
            SetMap(setting);
        }
        else if (current.Length > 0)
        {
            bool changed = !string.Equals(current, lastSeenMapId, StringComparison.Ordinal);
            bool byMod = string.Equals(current, modTravelTargetId, StringComparison.Ordinal);
            // The game may have been closed during a quest trip, so a new
            // launch starts from the saved farm map, not from where it reopens.
            string saved = (Plugin.Config.FarmMapSaved.Value ?? string.Empty).Trim();
            if (!Active && saved.Length > 0)
            {
                SetMap(saved);
                AdventurerLog.User($"Farm map restored from the previous launch: {MapId}.");
            }
            else if (!Active || (changed && !byMod && !JustUltraAscended()))
                SetMap(current);
        }
        ObserveUltraAscension();
        if (setting.Equals("Current", StringComparison.OrdinalIgnoreCase)) Save();
        lastSeenMapId = current;
        ObserveReferenceMap(current);
        return !string.Equals(before, MapId, StringComparison.Ordinal);
    }

    // A short story trip (some end within seconds) would reroll the dailies
    // twice for nothing, so the story map only counts after a minute there.
    private static void ObserveReferenceMap(string current)
    {
        if (storyMapId.Length == 0 ||
            !string.Equals(current, storyMapId, StringComparison.Ordinal))
            storyMapReachedAt = -1f;
        else if (storyMapReachedAt < 0f)
            storyMapReachedAt = Time.unscaledTime;

        string reference = Active && storyMapReachedAt >= 0f &&
                           Time.unscaledTime - storyMapReachedAt >= StoryMapDelaySeconds
            ? storyMapId
            : MapId;
        if (string.Equals(reference, referenceMapId, StringComparison.Ordinal)) return;
        referenceMapId = reference;
        if (Active)
            AdventurerLog.User(string.Equals(reference, MapId, StringComparison.Ordinal)
                ? $"Daily and Weekly quests checked against the farm map again: {MapId}."
                : $"Story quest in progress: Daily and Weekly quests checked against {reference}.");
    }

    private const float StoryMapDelaySeconds = 60f;
    private const float UltraAscensionMoveSeconds = 15f;

    // An Ultra Ascension sends the player back to the first dimension. That
    // move is not the player's choice, so with Current the farm map stays
    // where it was and the fallback travels back once the portal offers it.
    // The move and the counter update may land a few frames apart, in either
    // order, hence the window on both sides.
    private static void ObserveUltraAscension()
    {
        PlayerInventory inventory = PlayerInventory.instance;
        if (inventory == null) return;
        double count = Math.Max(0d, inventory.ultraAscensions);
        if (observedUltraAscensions < 0d || count <= observedUltraAscensions)
        {
            observedUltraAscensions = count;
            return;
        }
        observedUltraAscensions = count;
        ultraAscensionSeenAt = Time.unscaledTime;
        if (lastChangeAt >= 0f && mapBeforeLastChange.Length > 0 &&
            Time.unscaledTime - lastChangeAt <= UltraAscensionMoveSeconds)
            SetMap(mapBeforeLastChange);
        if (Active)
            AdventurerLog.User($"Ultra Ascension detected: farm map kept on {MapId}.");
    }

    private static bool JustUltraAscended() =>
        ultraAscensionSeenAt >= 0f &&
        Time.unscaledTime - ultraAscensionSeenAt <= UltraAscensionMoveSeconds;

    private static void SetMap(string mapId)
    {
        if (string.Equals(mapId, MapId, StringComparison.Ordinal)) return;
        mapBeforeLastChange = MapId;
        lastChangeAt = Time.unscaledTime;
        MapId = mapId;
        AdventurerLog.User(Active ? $"Farm map: {MapId}." : "Farm map: off.");
    }

    private static void Save()
    {
        if (!string.Equals(Plugin.Config.FarmMapSaved.Value, MapId, StringComparison.Ordinal))
            Plugin.Config.FarmMapSaved.Value = MapId;
    }

    public static bool IsFarmMapActive => Active;

    private static BaseMap FindMap(string mapId)
    {
        BaseMap current = MapController.instance?.selectedMap;
        if (current != null && string.Equals(current.name, mapId, StringComparison.Ordinal))
            return current;
        foreach (Map map in UnityEngine.Resources.FindObjectsOfTypeAll<Map>())
        {
            if (map != null && string.Equals(map.name, mapId, StringComparison.Ordinal))
                return map;
        }
        return null;
    }

    // True when the quest can be completed on the farm map (or on the map of
    // the story quest in progress), or when it is not tied to a map at all
    // (collect, crits, boxes...). Wind Dash kill quests
    // are never doable while they are disabled, farm map or not.
    public static bool IsDoableOnFarmMap(Quest quest)
    {
        if (quest == null) return true;
        if (QuestCompat.Kind(quest) == LegacyQuestType.WindDashKills &&
            !Plugin.Config.WindDashKillQuests.Value)
            return false;
        if (!Active) return true;
        string mapId = referenceMapId.Length > 0 ? referenceMapId : MapId;
        LegacyQuestType kind = QuestCompat.Kind(quest);
        if (kind == LegacyQuestType.GetMaterials)
            return SoulFarmingResolver.MaterialDrops(FindMap(mapId)).Length > 0;
        if (!QuestTargetResolver.IsKillKind(quest)) return true;

        BaseMap required = QuestCompat.RequiredMap(quest);
        if (required != null)
            return string.Equals(required.name, mapId, StringComparison.Ordinal);
        // Kill counts with no target (any enemy, Rage kills, shiny enemies,
        // arrows) progress on any map. Flyers, giants and character quests
        // still depend on what the map spawns.
        bool generic = quest.enemyToKill == null && quest.enemyType == null &&
                       quest.characterRequired == null &&
                       kind is not (LegacyQuestType.KillFlyers or
                           LegacyQuestType.KillGiants or
                           LegacyQuestType.KillEnemiesWithCharacter);
        if (generic) return true;

        QuestTargetSelection selection = Resolver.ResolveLocked(quest, mapId);
        return selection != null &&
               string.Equals(selection.MapId, mapId, StringComparison.Ordinal);
    }
}
