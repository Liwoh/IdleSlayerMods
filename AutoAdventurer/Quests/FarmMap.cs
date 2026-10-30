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
        if (!QuestTargetResolver.IsKillKind(quest)) return true;
        QuestTargetSelection selection = Resolver.ResolveLocked(quest, MapId);
        return selection != null &&
               string.Equals(selection.MapId, MapId, StringComparison.Ordinal);
    }
}
