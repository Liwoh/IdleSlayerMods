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
    // An empty result is valid data: every listed quest may already be done.
    internal static List<Quest> ListedQuests(QuestsList list)
    {
        if (list == null) return null;

        List<Quest> quests = new();
        bool built = AddQuests(quests, list.mainQuests, "main");
        built |= AddQuests(quests, list.dailyQuests, "daily");
        built |= AddQuests(quests, list.weeklyQuests, "weekly");
        built |= AddQuests(quests, list.eventQuests, "event");
        int raw = Count(list.mainQuests) + Count(list.dailyQuests) +
                  Count(list.weeklyQuests) + Count(list.eventQuests);
        if (list.collabQuests != null)
        {
            built = true;
            foreach (var entry in list.collabQuests)
            {
                AddQuests(quests, entry.Value, "collab");
                raw += Count(entry.Value);
            }
        }

        // The game fills the sections only once the quest panel has been
        // shown; until then rebuild the active list from the catalogue.
        if (built && raw == 0) AddCatalogueQuests(quests);
        return built ? quests : null;
    }

    private static int Count(Il2CppSystem.Collections.Generic.List<Quest> source) =>
        source != null ? source.Count : 0;

    // Same availability rules as the allQuests supplement used for travel.
    private static void AddCatalogueQuests(List<Quest> target)
    {
        var all = PlayerInventory.instance?.allQuests;
        if (all == null) return;
        for (int index = 0; index < all.Count; index++)
        {
            Quest quest = all[index];
            if (quest == null || quest is DailyQuest || quest is WeeklyQuest ||
                quest.isClaimed || quest.IsCompleted() || !IsEventLive(quest))
                continue;
            bool explicitElementQuest =
                Kind(quest) == LegacyQuestType.KillEnemiesOfType &&
                quest.enemyType != null;
            Upgrade bundle = quest.bundleRequired;
            bool available = explicitElementQuest
                ? bundle == null || bundle.bought ||
                  (bundle.isSpecialUnlock && bundle.specialUnlocked)
                : quest.CanBeCompleted();
            if (available) target.Add(quest);
        }
        AdventurerLog.QuestDebug(
            $"[QuestCompat] quest panel not shown yet; catalogue fallback kept {target.Count} quest(s).");
    }

    // Quest types of the October 2026 system that only count in Quest.map.
    internal static BaseMap RequiredMap(Quest quest) =>
        quest.type != null && quest.type.requiresMap ? quest.map : null;

    internal static bool IsVisitLocation(Quest quest) =>
        quest.type != null &&
        quest.type.name.Replace(" ", string.Empty) == "VisitLocation";

    private static readonly Dictionary<string, string> LoggedSectionCounts = new();

    // CanBeCompleted() stays true for quests of events that are not running.
    internal static bool IsEventLive(Quest quest) =>
        quest.mapEvent == null || quest.EventAvailable();

    private static bool AddQuests(
        List<Quest> target,
        Il2CppSystem.Collections.Generic.List<Quest> source,
        string section)
    {
        if (source == null) return false;
        int before = target.Count;
        for (int index = 0; index < source.Count; index++)
        {
            Quest quest = source[index];
            if (quest == null) continue;
            if (quest.CanBeCompletedOrClaimed() && IsEventLive(quest))
                target.Add(quest);
            else
                LogHiddenQuest(quest);
        }

        // TEMP: section sizes for the in-game check. Remove before PR.
        string counts = $"{target.Count - before}/{source.Count}";
        if (!LoggedSectionCounts.TryGetValue(section, out string last) ||
            last != counts)
        {
            LoggedSectionCounts[section] = counts;
            AdventurerLog.QuestDebug(
                $"[QuestCompat] section={section}; kept={counts}");
        }
        return true;
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
        string state;
        try
        {
            state = $"event={(quest.mapEvent != null ? quest.mapEvent.name : "none")}; " +
                $"eventAvailable={quest.EventAvailable()}; set={quest.eventSet}; " +
                $"collab={(quest.fromCollab != null ? quest.fromCollab.collabId : "none")}; " +
                $"canComplete={quest.CanBeCompleted()}; completed={quest.IsCompleted()}; claimed={quest.isClaimed}";
        }
        catch (System.Exception exception)
        {
            state = $"state unreadable ({exception.GetType().Name})";
        }
        AdventurerLog.User(
            $"[QuestCompat] quest={quest.name}; legacy={(int)kind} ({kind}); type={typeName}; {state}");
    }
}
