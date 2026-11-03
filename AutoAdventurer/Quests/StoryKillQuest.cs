using Il2Cpp;

namespace AutoAdventurer.Quests;

// Whether the quest being done is a story kill quest, and whether its target
// is a Giant. Public because AutoProgression reads it by reflection to craft
// its kill-boosting temporary items only for story quests.
public static class StoryKillQuest
{
    public static bool Active { get; private set; }
    public static bool GiantTarget { get; private set; }

    internal static void Set(QuestTargetSelection selection)
    {
        Quest quest = selection?.Quest;
        if (quest == null || !QuestTargetResolver.IsStoryQuest(quest))
        {
            Clear();
            return;
        }
        LegacyQuestType kind = QuestCompat.Kind(quest);
        Active = IsKillQuest(kind);
        GiantTarget = Active && (kind == LegacyQuestType.KillGiants ||
                                 (selection.Enemy != null && selection.Enemy.isGiant));
    }

    internal static void Clear()
    {
        Active = false;
        GiantTarget = false;
    }

    private static bool IsKillQuest(LegacyQuestType kind) =>
        kind is LegacyQuestType.KillEnemies or LegacyQuestType.KillEnemiesWithArrows or
            LegacyQuestType.KillFlyers or LegacyQuestType.KillGiants or
            LegacyQuestType.KillEnemiesWithRageMode or LegacyQuestType.KillEnemiesWithCharacter or
            LegacyQuestType.KillEnemiesOfType or LegacyQuestType.KillShinyEnemies or
            LegacyQuestType.WindDashKills;
}
