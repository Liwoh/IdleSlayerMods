using System.Collections.Generic;
using Il2Cpp;

namespace AutoProgression.Quests;

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
    HitRandomGoldenBoxes = 32,
    CompleteGrappleRuns = 33,
}

internal static class QuestCompat
{
    internal static LegacyQuestType Kind(Quest quest) =>
        (LegacyQuestType)quest.legacyQuestType;

    // QuestsList.lastScrollListData was split into one list per section.
    // The sections also hold quests the panel hides (e.g. every event's
    // quests), so only live quests are kept. Returns null while no section
    // exists yet; an empty result is valid data. The game fills the sections
    // only once the quest panel has been shown.
    internal static List<Quest> ListedQuests(QuestsList list)
    {
        if (list == null) return null;

        List<Quest> quests = new();
        bool built = AddQuests(quests, list.mainQuests);
        built |= AddQuests(quests, list.dailyQuests);
        built |= AddQuests(quests, list.weeklyQuests);
        built |= AddQuests(quests, list.eventQuests);
        if (list.collabQuests != null)
        {
            built = true;
            foreach (var entry in list.collabQuests)
                AddQuests(quests, entry.Value);
        }

        return built ? quests : null;
    }

    private static bool AddQuests(
        List<Quest> target,
        Il2CppSystem.Collections.Generic.List<Quest> source)
    {
        if (source == null) return false;
        for (int index = 0; index < source.Count; index++)
        {
            Quest quest = source[index];
            // CanBeCompleted() stays true for quests of events that are not
            // running, hence the extra EventAvailable() check.
            if (quest != null && quest.CanBeCompletedOrClaimed() &&
                (quest.mapEvent == null || quest.EventAvailable()))
                target.Add(quest);
        }
        return true;
    }
}
