using IdleSlayerMods.Common;
using MelonLoader;
using AutoAdventurer.Configuration;
using AutoAdventurer.Diagnostics;
using AutoAdventurer.Runtime;

[assembly: MelonInfo(typeof(AutoAdventurer.Plugin), AutoAdventurer.AutoAdventurerInfo.PluginName, AutoAdventurer.AutoAdventurerInfo.PluginVersion, AutoAdventurer.AutoAdventurerInfo.PluginAuthor)]
[assembly: MelonAdditionalDependencies("IdleSlayerMods.Common")]

namespace AutoAdventurer;

public class Plugin : MelonMod
{
    private static UnityEngine.GameObject runtimeObject;
    internal static AutoAdventurerConfig Config;
    internal static ModHelper ModHelperInstance;
    internal static readonly MelonLogger.Instance Logger = Melon<Plugin>.Logger;

    public override void OnInitializeMelon()
    {
        ModHelper.ModHelperMounted += instance => ModHelperInstance = instance;
        HarmonyInstance.PatchAll();
        WarnIfConfigurationIsMissing();
        Config = new(AutoAdventurerInfo.PluginGuid);
        AdventurerLog.User(
            $"Plugin {AutoAdventurerInfo.PluginGuid} v{AutoAdventurerInfo.PluginVersion} " +
            $"(internal {AutoAdventurerInfo.InternalVersion}) loaded; " +
            $"configuration schema v{AutoAdventurerConfig.CurrentConfigurationVersion}.");
    }

    private static void WarnIfConfigurationIsMissing()
    {
        string path = System.IO.Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
            $"{AutoAdventurerInfo.PluginGuid}.cfg");
        if (!System.IO.File.Exists(path))
            AdventurerLog.Warning(
                $"Configuration file was not found at '{path}'. Default settings will be used and a new file will be created when the game saves preferences. Verify that your Mod Manager edits this exact file.");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != "Game") return;
        // A cloud-save reload loads "Game" again without removing the first
        // runtime, which then ran twice (2026-10-31). Keep only the new one.
        if (runtimeObject != null) UnityEngine.Object.Destroy(runtimeObject);
        runtimeObject = ModUtils.RegisterComponent<AutoAdventurerRuntime>();
    }
}
