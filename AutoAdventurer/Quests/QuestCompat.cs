using System.Collections.Generic;
using AutoAdventurer.Diagnostics;
using Il2Cpp;

namespace AutoAdventurer.Quests;

// Values of the QuestType enum removed by the October 2026 game update. The
// game still exposes them through Quest.legacyQuestType.
internal enum LegacyQuestType
{
    KillEnemies = 0,
    PickUpCoins = 1,
    PickUpRubies = 2,
    KillEnemiesWithArrows = 3,
    PickUpSapphires = 4,
    KillFlyers = 5,
    PickUpEmeralds = 6,
    DoCriticals = 7,
    HitRandomBoxes = 8,
    HitRandomSilverBoxes = 9,
    KillGiants = 10,
    KillEnemiesWithRageMode = 11,
    Boost = 12,
    CraftTemporaryItems = 13,
    ChestHuntChests = 14,
    BonusStage = 15,
    BonusStageSections = 16,
    GetMaterials = 17,
    PickUpGemstones = 18,
    KillEnemiesWithCharacter = 19,
    KillEnemiesOfType = 20,
    FindArmoryItems = 21,
    GetCoinsFromRandomBox = 22,
    GoThroughPortals = 23,
    CompleteDailyQuests = 24,
    KillShinyEnemies = 25,
    PickUpDiamonds = 26,
    WindDashKills = 27,
    DigDigPiles = 28,
    UseRageMode = 29,
    CompleteAscendingHeights = 30,
    PickUpZyniums = 31,
}

internal static class QuestCompat
{
    private static readonly HashSet<string> LoggedQuestTypes = new();

    internal static LegacyQuestType Kind(Quest quest)
    {
        LegacyQuestType kind = (LegacyQuestType)quest.legacyQuestType;
        LogQuestType(quest, kind);
        return kind;
    }

    // QuestsList.lastScrollListData was split into one list per section.
    // The sections also hold quests the panel hides (e.g. every event's
    // quests), so only quests the game reports as live are kept.
    // Returns null while no section has been built yet, like the old cache.
    internal static List<Quest> ListedQuests(QuestsList list)
    {
        if (list == null) return null;

        List<Quest> quests = new();
        AddQuests(quests, list.mainQuests);
        AddQuests(quests, list.dailyQuests);
        AddQuests(quests, list.weeklyQuests);
        AddQuests(quests, list.eventQuests);
        if (list.collabQuests != null)
        {
            foreach (var entry in list.collabQuests)
                AddQuests(quests, entry.Value);
        }

        return quests.Count > 0 ? quests : null;
    }

    private static void AddQuests(
        List<Quest> target,
        Il2CppSystem.Collections.Generic.List<Quest> source)
    {
        if (source == null) return;
        for (int index = 0; index < source.Count; index++)
        {
            Quest quest = source[index];
            if (quest == null) continue;
            if (quest.CanBeCompletedOrClaimed())
                target.Add(quest);
            else
                LogHiddenQuest(quest);
        }
    }

    private static readonly HashSet<string> LoggedHiddenQuests = new();

    // TEMP: shows which section entries the filter drops. Remove before PR.
    private static void LogHiddenQuest(Quest quest)
    {
        if (!LoggedHiddenQuests.Add(quest.name)) return;
        AdventurerLog.User(
            $"[QuestCompat] hidden quest={quest.name}; event={(quest.mapEvent != null ? quest.mapEvent.name : "none")}");
    }

    // TEMP: validates the legacyQuestType mapping in game. Remove before PR.
    private static void LogQuestType(Quest quest, LegacyQuestType kind)
    {
        if (quest == null || !LoggedQuestTypes.Add(quest.name)) return;
        string typeName = quest.type != null ? quest.type.name : "null";
        AdventurerLog.User(
            $"[QuestCompat] quest={quest.name}; legacy={(int)kind} ({kind}); type={typeName}");
    }
}
