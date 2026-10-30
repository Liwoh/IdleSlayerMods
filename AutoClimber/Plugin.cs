using IdleSlayerMods.Common;
using MelonLoader;
using HarmonyLib;
using UnityEngine;
using AutoClimber.Configuration;
using AutoClimber.Diagnostics;

[assembly: MelonInfo(typeof(AutoClimber.AutoClimberPlugin), AutoClimber.AutoClimberInfo.PluginName, AutoClimber.AutoClimberInfo.PluginVersion, AutoClimber.AutoClimberInfo.PluginAuthor)]
[assembly: MelonAdditionalDependencies("IdleSlayerMods.Common")]

namespace AutoClimber;

public sealed class AutoClimberPlugin : MelonMod
{
    internal static AutoClimberConfig Config;
    internal static ModHelper ModHelperInstance;
    private static bool harmonyPatchesApplied;
    private static UnityEngine.GameObject runtimeObject;
    internal static readonly MelonLogger.Instance Logger =
        Melon<AutoClimberPlugin>.Logger;
    
    public override void OnInitializeMelon()
    {
        Application.runInBackground = true;
        ModHelper.ModHelperMounted +=
            instance => ModHelperInstance = instance;
        WarnIfConfigurationIsMissing();
        Config = new(AutoClimberInfo.PluginGuid);
        ClimberLog.User(
            $"Plugin {AutoClimberInfo.PluginGuid} " +
            $"v{AutoClimberInfo.PluginVersion} " +
            $"(internal {AutoClimberInfo.InternalVersion}) loaded; " +
            $"configuration schema " +
            $"v{AutoClimberConfig.CurrentConfigurationVersion}.");
    }

    private static void WarnIfConfigurationIsMissing()
    {
        string path = System.IO.Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
            $"{AutoClimberInfo.PluginGuid}.cfg");
        if (!System.IO.File.Exists(path))
            ClimberLog.Warning(
                $"Configuration file was not found at '{path}'. Default settings will be used and a new file will be created when the game saves preferences. Verify that your Mod Manager edits this exact file.");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != "Game") return;
        Application.runInBackground = true;
        // A cloud-save reload loads "Game" again without removing the first
        // runtime, which then ran twice (2026-10-31). Keep only the new one.
        if (runtimeObject != null) UnityEngine.Object.Destroy(runtimeObject);
        runtimeObject = ModUtils.RegisterComponent<AutoClimberRuntime>();

        // AutoClimberRuntime must be registered in the IL2CPP domain before
        // Harmony resolves patches that target its managed methods.
        if (!harmonyPatchesApplied)
        {
            HarmonyInstance.PatchAll();
            harmonyPatchesApplied = true;
        }
    }
}
