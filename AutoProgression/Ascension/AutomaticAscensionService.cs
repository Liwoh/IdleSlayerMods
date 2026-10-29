using System;
using System.Reflection;
using AutoProgression.Diagnostics;
using Il2Cpp;
using UnityEngine;

namespace AutoProgression.Ascension;

internal sealed class AutomaticAscensionService
{
    private const float PostAscensionPurchaseIntervalSeconds = 1f;
    private const int RequiredStablePurchaseRounds = 2;

    private static readonly MethodInfo AscendMethod = typeof(AscensionManager).GetMethod(
        "Ascend",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private float nextCheckTime;
    private bool waitingForPostAscension;
    private bool ascensionResetObserved;
    private bool leftMainScreenAfterAscension;
    private int stablePurchaseRounds;

    internal bool StartedAscensionThisTick { get; private set; }

    internal bool Tick(float now, bool firstSkillResetDetected)
    {
        StartedAscensionThisTick = false;

        if (firstSkillResetDetected && waitingForPostAscension)
            ascensionResetObserved = true;

        if (waitingForPostAscension)
        {
            if ((!ascensionResetObserved && !leftMainScreenAfterAscension) || now < nextCheckTime)
                return true;

            if (!Plugin.Config.BuyAscensionSkillsAfterAutomaticAscension.Value)
            {
                FinishPostAscensionProcessing(false);
                return false;
            }

            nextCheckTime = now + PostAscensionPurchaseIntervalSeconds;
            PurchaseAllAscensionSkills();
            return true;
        }

        if (!Plugin.Config.EnableAutomaticAscension.Value || now < nextCheckTime)
            return false;

        nextCheckTime = now + GetCheckIntervalSeconds();

        double pending = SlayerPoints.pre;
        double lifetime = SlayerPoints.lifetime;
        if (pending <= 0d || double.IsNaN(pending) || double.IsInfinity(pending))
            return false;

        double ratioPercent = lifetime > 0d
            ? pending / lifetime * 100d
            : 100d;
        double thresholdPercent = Math.Max(
            0d,
            Plugin.Config.AutomaticAscensionSoulBonusPercent.Value);

        ProgressionLog.Debug(
            $"Automatic ascension status: PendingSP={pending:0.##}, LifetimeSP={lifetime:0.##}, " +
            $"SoulBonus={ratioPercent:0.##}%, Threshold={thresholdPercent:0.##}%.");

        if (ratioPercent < thresholdPercent)
            return false;

        AscensionManager manager = AscensionManager.instance;
        if (manager == null || AscendMethod == null)
        {
            ProgressionLog.Debug(
                $"Automatic ascension objects unavailable. Manager={manager != null}, AscendMethod={AscendMethod != null}.");
            return false;
        }

        try
        {
            // Ascend(false) is the normal ascension path. Never pass true here.
            AscendMethod.Invoke(manager, new object[] { false });
            waitingForPostAscension = true;
            ascensionResetObserved = false;
            leftMainScreenAfterAscension = false;
            stablePurchaseRounds = 0;
            nextCheckTime = 0f;
            StartedAscensionThisTick = true;
            ProgressionLog.User(
                $"Automatic normal ascension started at {ratioPercent:0.##}% soul bonus.");
            return true;
        }
        catch (Exception exception)
        {
            ProgressionLog.Exception("Automatic normal ascension", exception);
            return false;
        }
    }

    private void PurchaseAllAscensionSkills()
    {
        AscensionManager manager = AscensionManager.instance;
        if (manager == null)
            return;

        double before = SlayerPoints.points;
        try
        {
            // Since the October 2026 update BuyAll() only opens the "Buy all"
            // confirmation popup, which stayed open over the game (observed in
            // game). BuyAllAction() is the purchase the Confirm button runs.
            manager.BuyAllAction();
        }
        catch (Exception exception)
        {
            ProgressionLog.Exception("Post-ascension Buy All", exception);
            waitingForPostAscension = false;
            return;
        }

        double after = SlayerPoints.points;
        if (after < before)
        {
            stablePurchaseRounds = 0;
            ProgressionLog.Debug(
                $"Post-ascension skills purchased; Slayer Points spent={before - after:0.##}, remaining={after:0.##}.");
            return;
        }

        stablePurchaseRounds++;
        if (stablePurchaseRounds < RequiredStablePurchaseRounds)
            return;

        FinishPostAscensionProcessing(true);
    }

    private void FinishPostAscensionProcessing(bool skillsPurchased)
    {
        waitingForPostAscension = false;
        ascensionResetObserved = false;
        leftMainScreenAfterAscension = false;
        stablePurchaseRounds = 0;
        nextCheckTime = Time.unscaledTime + GetCheckIntervalSeconds();

        if (skillsPurchased)
            ProgressionLog.Debug(
                "Post-ascension skill purchasing completed.",
                "Ascension");
    }

    internal void Reset()
    {
        nextCheckTime = 0f;
        waitingForPostAscension = false;
        ascensionResetObserved = false;
        leftMainScreenAfterAscension = false;
        stablePurchaseRounds = 0;
        StartedAscensionThisTick = false;
    }

    internal void NotifyMainScreenUnavailable()
    {
        if (waitingForPostAscension)
            leftMainScreenAfterAscension = true;
    }

    private static float GetCheckIntervalSeconds() => Math.Max(
        1f,
        Configuration.AutoProgressionConfig.AutomaticAscensionCheckIntervalMinutes * 60f);
}
