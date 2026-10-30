using IdleSlayerMods.Common;
using MelonLoader;
using AutoProgression.Configuration;
using AutoProgression.Diagnostics;
using AutoProgression.Runtime;
using Plugin = AutoProgression.Plugin;

[assembly: MelonInfo(typeof(Plugin), AutoProgression.AutoProgressionInfo.PluginName, AutoProgression.AutoProgressionInfo.PluginVersion, AutoProgression.AutoProgressionInfo.PluginAuthor)]
[assembly: MelonAdditionalDependencies("IdleSlayerMods.Common")]

namespace AutoProgression;

public class Plugin : MelonMod
{
    private static UnityEngine.GameObject runtimeObject;
    internal static AutoProgressionConfig Config;
    internal static ModHelper ModHelperInstance;
    internal static readonly MelonLogger.Instance Logger = Melon<Plugin>.Logger;
    
    public override void OnInitializeMelon()
    {
        ModHelper.ModHelperMounted += SetModHelperInstance;
        WarnIfConfigurationIsMissing();
        Config = new(AutoProgressionInfo.PluginGuid);
        HarmonyInstance.PatchAll();
        ProgressionLog.User(
            $"Plugin {AutoProgressionInfo.PluginGuid} v{AutoProgressionInfo.PluginVersion} " +
            $"(internal {AutoProgressionInfo.InternalVersion}) loaded; " +
            $"configuration schema v{AutoProgressionConfig.CurrentConfigurationVersion}.");
    }

    private static void WarnIfConfigurationIsMissing()
    {
        string path = System.IO.Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
            $"{AutoProgressionInfo.PluginGuid}.cfg");
        if (!System.IO.File.Exists(path))
            ProgressionLog.Warning(
                $"Configuration file was not found at '{path}'. Default settings will be used and a new file will be created when the game saves preferences. Verify that your Mod Manager edits this exact file.");
    }

    private static void SetModHelperInstance(ModHelper instance)
    {
        ModHelperInstance = instance;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != "Game") return;
        // A cloud-save reload loads "Game" again without removing the first
        // runtime, which then ran twice (2026-10-31). Keep only the new one.
        if (runtimeObject != null) UnityEngine.Object.Destroy(runtimeObject);
        runtimeObject = ModUtils.RegisterComponent<AutoProgressionRuntime>();
    }
}
