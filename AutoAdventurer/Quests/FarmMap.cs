using System;
using AutoAdventurer.Diagnostics;
using Il2Cpp;

namespace AutoAdventurer.Quests;

// The dimension the player wants to farm (Farm Map setting). With "Current",
// it follows the player's own portal trips but ignores the mod's quest trips.
// Public because AutoProgression reaches IsDoableOnFarmMap by reflection.
public static class FarmMap
{
    private static readonly QuestTargetResolver Resolver = new();
    private static string modTravelTargetId = string.Empty;
    private static string lastSeenMapId = string.Empty;

    internal static string MapId { get; private set; } = string.Empty;
    internal static bool Active => MapId.Length > 0;

    internal static void NoteModTravel(string mapId) =>
        modTravelTargetId = mapId ?? string.Empty;

    // Called when Quest Automation (P) is enabled or disabled: with Current,
    // the next Observe() takes the dimension the player is in.
    internal static void Clear()
    {
        modTravelTargetId = string.Empty;
        lastSeenMapId = string.Empty;
        SetMap(string.Empty);
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
            if (!Active || (changed && !byMod)) SetMap(current);
        }
        lastSeenMapId = current;
        return !string.Equals(before, MapId, StringComparison.Ordinal);
    }

    private static void SetMap(string mapId)
    {
        if (string.Equals(mapId, MapId, StringComparison.Ordinal)) return;
        MapId = mapId;
        AdventurerLog.User(Active ? $"Farm map: {MapId}." : "Farm map: off.");
    }

    public static bool IsFarmMapActive => Active;

    private static BaseMap FindFarmMap()
    {
        BaseMap current = MapController.instance?.selectedMap;
        if (current != null && string.Equals(current.name, MapId, StringComparison.Ordinal))
            return current;
        foreach (Map map in UnityEngine.Resources.FindObjectsOfTypeAll<Map>())
        {
            if (map != null && string.Equals(map.name, MapId, StringComparison.Ordinal))
                return map;
        }
        return null;
    }

    // True when the quest can be completed on the farm map, or when it is not
    // tied to a map at all (collect, crits, boxes...). Wind Dash kill quests
    // are never doable while they are disabled, farm map or not.
    public static bool IsDoableOnFarmMap(Quest quest)
    {
        if (quest == null) return true;
        if (QuestCompat.Kind(quest) == LegacyQuestType.WindDashKills &&
            !Plugin.Config.WindDashKillQuests.Value)
            return false;
        if (!Active) return true;
        LegacyQuestType kind = QuestCompat.Kind(quest);
        if (kind == LegacyQuestType.GetMaterials)
            return SoulFarmingResolver.MaterialDrops(FindFarmMap()).Length > 0;
        if (!QuestTargetResolver.IsKillKind(quest)) return true;

        BaseMap required = QuestCompat.RequiredMap(quest);
        if (required != null)
            return string.Equals(required.name, MapId, StringComparison.Ordinal);
        // Kill counts with no target (any enemy, Rage kills, shiny enemies,
        // arrows) progress on any map. Flyers, giants and character quests
        // still depend on what the map spawns.
        bool generic = quest.enemyToKill == null && quest.enemyType == null &&
                       quest.characterRequired == null &&
                       kind is not (LegacyQuestType.KillFlyers or
                           LegacyQuestType.KillGiants or
                           LegacyQuestType.KillEnemiesWithCharacter);
        if (generic) return true;

        QuestTargetSelection selection = Resolver.ResolveLocked(quest, MapId);
        return selection != null &&
               string.Equals(selection.MapId, MapId, StringComparison.Ordinal);
    }
}
