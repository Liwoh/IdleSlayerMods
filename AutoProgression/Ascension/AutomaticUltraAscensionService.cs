using System;
using System.Linq;
using System.Reflection;
using AutoProgression.Diagnostics;
using Il2Cpp;

namespace AutoProgression.Ascension;

internal sealed class AutomaticUltraAscensionService
{
    // The confirm callback is a compiler-generated lambda whose number changes
    // with game updates (b__42_0, b__50_0, b__58_0...). The popup's first
    // lambda has always been the confirmation.
    private static readonly MethodInfo ConfirmUltraAscensionMethod =
        typeof(AscensionManager)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetParameters().Length == 0 &&
                             method.Name.StartsWith("_UltraAscendPopup_b__", StringComparison.Ordinal) &&
                             method.Name.EndsWith("_0", StringComparison.Ordinal))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    // Several Ultra Ascensions can happen in one game session; this only
    // keeps the next check clear of the reset still in progress.
    private const float PauseAfterStartSeconds = 600f;
    private float nextCheckTime;

    internal bool Tick(float now)
    {
        if (!Plugin.Config.EnableAutomaticUltraAscension.Value ||
            now < nextCheckTime)
            return false;

        nextCheckTime = now + GetCheckIntervalSeconds();
        AscensionManager manager = AscensionManager.instance;
        AscensionSkill ultraAscension = AscensionSkills.list?.UltraAscension;
        if (manager == null || ultraAscension == null ||
            ConfirmUltraAscensionMethod == null)
        {
            ProgressionLog.Debug(
                $"Automatic Ultra Ascension objects unavailable. " +
                $"Manager={manager != null}, Skill={ultraAscension != null}, " +
                $"ConfirmMethod={ConfirmUltraAscensionMethod != null}.");
            return false;
        }

        try
        {
            // Let the game own unlock and prerequisite rules across all
            // progression paths instead of duplicating them in this mod.
            if (!ultraAscension.IsActive()) return false;

            double astralKeys = manager.GetAstralKeys();
            int requiredAstralKeys = Math.Max(0,
                Plugin.Config.UltraAscensionMinimumAstralKeys.Value);
            ProgressionLog.User(
                $"Automatic Ultra Ascension status: AstralKeys={astralKeys:0.##}, " +
                $"Required={requiredAstralKeys}, Confirm={ConfirmUltraAscensionMethod.Name}.");
            if (double.IsNaN(astralKeys) || double.IsInfinity(astralKeys) ||
                astralKeys < requiredAstralKeys)
                return false;

            // This is the native callback bound to the confirmation action in
            // UltraAscendPopup. Ascend(true) is not the Ultra Ascension entry
            // point and only produces a transient cutscene without resetting.
            ConfirmUltraAscensionMethod.Invoke(manager, Array.Empty<object>());
            nextCheckTime = now + Math.Max(PauseAfterStartSeconds, GetCheckIntervalSeconds());
            ProgressionLog.User(
                $"Automatic Ultra Ascension started with " +
                $"{astralKeys:0.##} Astral Keys available.");
            return true;
        }
        catch (Exception exception)
        {
            ProgressionLog.Exception("Automatic Ultra Ascension", exception);
            return false;
        }
    }

    private static float GetCheckIntervalSeconds() => Math.Max(
        1f,
        Configuration.AutoProgressionConfig.AutomaticAscensionCheckIntervalMinutes * 60f);
}
