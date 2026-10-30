using IdleSlayerMods.Common;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using AutoBonusRunner.Configuration;
using AutoBonusRunner.Control;
using AutoBonusRunner.Diagnostics;
using AutoBonusRunner.Runtime;
using UnityEngine;

[assembly: MelonInfo(typeof(AutoBonusRunner.Plugin), AutoBonusRunner.AutoBonusRunnerInfo.PluginName, AutoBonusRunner.AutoBonusRunnerInfo.PluginVersion, AutoBonusRunner.AutoBonusRunnerInfo.PluginAuthor)]
[assembly: MelonAdditionalDependencies("IdleSlayerMods.Common")]

namespace AutoBonusRunner;

public sealed class Plugin : MelonMod
{
    private static UnityEngine.GameObject runtimeObject;
    internal static AutoBonusRunnerConfig Config;
    internal static ModHelper ModHelperInstance;
    private static bool harmonyInventoryLogged;
    internal static readonly MelonLogger.Instance Logger = Melon<Plugin>.Logger;
    
    public override void OnInitializeMelon()
    {
        Application.runInBackground = true;
        ModHelper.ModHelperMounted += instance => ModHelperInstance = instance;
        WarnIfConfigurationIsMissing();
        Config = new(AutoBonusRunnerInfo.PluginGuid);
        BonusRunnerLog.InitializeSessionTrace();
        BonusRunnerLog.User(
            $"Plugin {AutoBonusRunnerInfo.PluginGuid} v{AutoBonusRunnerInfo.PluginVersion} " +
            $"(internal {AutoBonusRunnerInfo.InternalVersion}) loaded; " +
            $"configuration schema v{AutoBonusRunnerConfig.CurrentConfigurationVersion}.");
        if (string.IsNullOrWhiteSpace(
                BonusRunnerLog.SessionTracePath))
        {
            BonusRunnerLog.Warning(
                "Independent session trace could not be opened; " +
                "MelonLoader logging remains active.");
        }
        else
        {
            BonusRunnerLog.Debug(
                $"Independent session trace opened: path=" +
                $"{BonusRunnerLog.SessionTracePath}.",
                "Runtime");
        }
        BonusRunnerLog.Debug(
            "Debug observation remains active when automatic control is disabled.",
            "Runtime");
    }

    private static void WarnIfConfigurationIsMissing()
    {
        string path = System.IO.Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
            $"{AutoBonusRunnerInfo.PluginGuid}.cfg");
        if (!System.IO.File.Exists(path))
            BonusRunnerLog.Warning(
                $"Configuration file was not found at '{path}'. Default settings will be used and a new file will be created when the game saves preferences. Verify that your Mod Manager edits this exact file.");
    }

    public override void OnDeinitializeMelon()
    {
        BonusRunnerLog.ShutdownSessionTrace();
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != "Game") return;
        Application.runInBackground = true;
        // A cloud-save reload loads "Game" again without removing the first
        // runtime, which then ran twice (2026-10-31). Keep only the new one.
        if (runtimeObject != null) UnityEngine.Object.Destroy(runtimeObject);
        runtimeObject = ModUtils.RegisterComponent<AutoBonusRunnerRuntime>();
        if (!harmonyInventoryLogged)
        {
            harmonyInventoryLogged = true;
            LogHarmonyPatchInventory();
        }
    }

    private static void LogHarmonyPatchInventory()
    {
        try
        {
            var update = AccessTools.Method(
                typeof(PlayerMovement),
                "Update");
            var fixedUpdate = AccessTools.Method(
                typeof(PlayerMovement),
                "FixedUpdate");
            var secondWindSuggest = AccessTools.Method(
                typeof(SecondWind),
                nameof(SecondWind.SecondWindSuggest));
            var secondWindReward = AccessTools.Method(
                typeof(SecondWind),
                nameof(SecondWind.RewardForShowing));
            var secondWindError = AccessTools.Method(
                typeof(SecondWind),
                nameof(SecondWind.OnError));
            var secondWindClose = AccessTools.Method(
                typeof(SecondWind),
                nameof(SecondWind.OnClose));
            var popupShow = AccessTools.Method(
                typeof(Popup),
                nameof(Popup.Show),
                new System.Type[] { typeof(PopupData), typeof(bool) });
            var sphereRequirement = AccessTools.Method(
                typeof(BonusSection),
                nameof(BonusSection.GetRequiredSpheres));
            Patches updatePatches =
                HarmonyLib.Harmony.GetPatchInfo(update);
            Patches fixedPatches =
                HarmonyLib.Harmony.GetPatchInfo(fixedUpdate);
            Patches promptPatches =
                HarmonyLib.Harmony.GetPatchInfo(secondWindSuggest);
            Patches rewardPatches =
                HarmonyLib.Harmony.GetPatchInfo(secondWindReward);
            Patches errorPatches =
                HarmonyLib.Harmony.GetPatchInfo(secondWindError);
            Patches closePatches =
                HarmonyLib.Harmony.GetPatchInfo(secondWindClose);
            Patches popupPatches =
                HarmonyLib.Harmony.GetPatchInfo(popupShow);
            Patches sphereRequirementPatches =
                HarmonyLib.Harmony.GetPatchInfo(sphereRequirement);
            int ownedUpdatePrefixes = updatePatches?.Prefixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(PlayerMovementUpdateJumpHoldPatch)) ?? 0;
            int ownedFixedPrefixes = fixedPatches?.Prefixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(PlayerMovementFixedUpdateJumpHoldPatch)) ?? 0;
            int ownedFixedPostfixes = fixedPatches?.Postfixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(PlayerMovementFixedUpdateJumpHoldPatch)) ?? 0;
            int ownedPromptPostfixes = promptPatches?.Postfixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(BonusStageSecondWindPromptPatch)) ?? 0;
            int ownedRewardPostfixes = rewardPatches?.Postfixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(BonusStageSecondWindRewardPatch)) ?? 0;
            int ownedErrorPostfixes = errorPatches?.Postfixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(BonusStageSecondWindErrorPatch)) ?? 0;
            int ownedClosePostfixes = closePatches?.Postfixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(BonusStageSecondWindClosePatch)) ?? 0;
            int ownedPopupPrefixes = popupPatches?.Prefixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(BonusStageRetryPopupPatch)) ?? 0;
            int ownedPopupPostfixes = popupPatches?.Postfixes.Count(
                patch => patch.PatchMethod?.DeclaringType ==
                    typeof(BonusStageRetryPopupPatch)) ?? 0;
            int ownedSphereRequirementPostfixes =
                sphereRequirementPatches?.Postfixes.Count(
                    patch => patch.PatchMethod?.DeclaringType ==
                        typeof(BonusStageSphereRequirementPatch)) ?? 0;
            string message =
                $"HarmonyAutoPatchInventory UpdatePrefixes=" +
                $"{ownedUpdatePrefixes}, FixedPrefixes=" +
                $"{ownedFixedPrefixes}, FixedPostfixes=" +
                $"{ownedFixedPostfixes}, RetryPromptPostfixes=" +
                $"{ownedPromptPostfixes}, RetryPopupPrefixes=" +
                $"{ownedPopupPrefixes}, RetryPopupPostfixes=" +
                $"{ownedPopupPostfixes}, RetryRewardPostfixes=" +
                $"{ownedRewardPostfixes}, RetryErrorPostfixes=" +
                $"{ownedErrorPostfixes}, RetryClosePostfixes=" +
                $"{ownedClosePostfixes}, SphereRequirementPostfixes=" +
                $"{ownedSphereRequirementPostfixes}. MelonLoader owns automatic " +
                "Harmony discovery; AutoBonusRunner does not call PatchAll.";
            bool inventoryReady =
                ownedUpdatePrefixes == 1 &&
                ownedFixedPrefixes == 1 &&
                ownedFixedPostfixes == 1 &&
                ownedPromptPostfixes == 1 &&
                ownedPopupPrefixes == 1 &&
                ownedPopupPostfixes == 1 &&
                ownedRewardPostfixes == 1 &&
                ownedErrorPostfixes == 1 &&
                ownedClosePostfixes == 1;
            BonusStageRetryBridge.SetPatchInventoryReady(
                inventoryReady,
                message);
            if (ownedSphereRequirementPostfixes != 1)
            {
                BonusRunnerLog.Warning(
                    $"SphereRequirementPatchInventory Postfixes=" +
                    $"{ownedSphereRequirementPostfixes}; expected exactly " +
                    "one. Auto/Skip mode will fail safely to the game's " +
                    "native requirement if this patch is unavailable.");
            }
            if (inventoryReady)
            {
                BonusRunnerLog.Debug(message, "Control");
            }
            else
            {
                BonusRunnerLog.Warning(
                    message + " Expected exactly one callback of each " +
                    "control and retry kind; missing retry callbacks disable " +
                    "verified automatic continuation rather than invoking " +
                    "an unverified compiler-generated delegate.");
            }
        }
        catch (System.Exception exception)
        {
            BonusStageRetryBridge.SetPatchInventoryReady(
                false,
                $"InventoryException={exception.GetType().Name}:" +
                exception.Message);
            BonusRunnerLog.Exception(
                "Harmony patch inventory",
                exception);
        }
    }
}
