using System;
using System.Reflection;
using AutoProgression.Diagnostics;
using Il2Cpp;

namespace AutoProgression.Quests;

// Reads AutoAdventurer's farm map (Farm Map setting) by reflection, so the two
// mods stay independent: without AutoAdventurer nothing is ever rerolled.
internal static class FarmMapBridge
{
    private static bool resolved;
    private static Func<Quest, bool> isDoable;
    private static Func<bool> isActive;

    internal static bool Active
    {
        get
        {
            Resolve();
            return isActive != null && isActive();
        }
    }

    // A Daily or Weekly quest to reroll: not doable on the farm map (or a
    // disabled Wind Dash kill quest), or a portal quest while farming.
    internal static bool ShouldReroll(Quest quest)
    {
        if (quest == null || !Plugin.Config.RerollQuestsOutsideFarmMap.Value)
            return false;
        Resolve();
        if (isDoable == null) return false;
        if (Active && QuestCompat.Kind(quest) == LegacyQuestType.GoThroughPortals)
            return true;
        return !isDoable(quest);
    }

    private static void Resolve()
    {
        if (resolved) return;
        resolved = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("AutoAdventurer.Quests.FarmMap");
            if (type == null) continue;
            MethodInfo doable = type.GetMethod("IsDoableOnFarmMap",
                BindingFlags.Public | BindingFlags.Static);
            PropertyInfo active = type.GetProperty("IsFarmMapActive",
                BindingFlags.Public | BindingFlags.Static);
            if (doable == null || active == null) break;
            isDoable = (Func<Quest, bool>)Delegate.CreateDelegate(
                typeof(Func<Quest, bool>), doable);
            isActive = (Func<bool>)Delegate.CreateDelegate(
                typeof(Func<bool>), active.GetGetMethod());
            return;
        }
        ProgressionLog.Warning(
            "Reroll Quests Outside Farm Map: AutoAdventurer farm map not found; nothing will be rerolled.");
    }
}
