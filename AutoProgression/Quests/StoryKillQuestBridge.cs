using System;
using System.Reflection;
using AutoProgression.Diagnostics;

namespace AutoProgression.Quests;

// Reads AutoAdventurer's current story kill quest by reflection. Without
// AutoAdventurer, no story kill quest is ever reported.
internal static class StoryKillQuestBridge
{
    private static bool resolved;
    private static Func<bool> active;
    private static Func<bool> giantTarget;

    internal static bool Active
    {
        get
        {
            Resolve();
            return active != null && active();
        }
    }

    internal static bool GiantTarget
    {
        get
        {
            Resolve();
            return giantTarget != null && giantTarget();
        }
    }

    private static void Resolve()
    {
        if (resolved) return;
        resolved = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType("AutoAdventurer.Quests.StoryKillQuest");
            if (type == null) continue;
            MethodInfo isActive = type.GetProperty("Active",
                BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            MethodInfo isGiant = type.GetProperty("GiantTarget",
                BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            if (isActive == null || isGiant == null) break;
            active = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), isActive);
            giantTarget = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), isGiant);
            return;
        }
        ProgressionLog.Warning(
            "Story Kill Quests Only: AutoAdventurer story quest state not found; story-only craftables will not be crafted.");
    }
}
