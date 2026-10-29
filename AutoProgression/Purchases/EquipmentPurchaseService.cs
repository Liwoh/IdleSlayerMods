using System;
using AutoProgression.Diagnostics;
using Il2Cpp;

namespace AutoProgression.Purchases;

internal sealed class EquipmentPurchaseService
{
    private const float CheckIntervalSeconds = 0.1f;
    private const float IdleCheckIntervalSeconds = 1f;
    private const int Stack10 = 10;
    private const int Stack50 = 50;

    private float nextCheckTime;
    private float noPurchaseSince = -1f;
    private float sleepUntil;
    private float nextScanLogTime;
    private bool missingLogged;
    private int roundPurchaseCount;
    private int roundPurchasedLevels;
    private int currentPowerIndex = -1;
    private int knownPowerCount = -1;

    internal bool Tick(float now)
    {
        if (!Plugin.Config.EnableEquipmentPurchases.Value || now < nextCheckTime) return false;
        nextCheckTime = now + CheckIntervalSeconds;

        if (now < sleepUntil) return false;
        if (sleepUntil > 0f)
        {
            sleepUntil = 0f;
            noPurchaseSince = now;
            ProgressionLog.Debug(
                "Equipment purchase sleep ended.",
                "Purchases");
        }

        ShopManager shop = ShopManager.instance;
        PowersList powersList = PowersList.instance;
        // Like the skill list, the game only refreshes this cache when the
        // shop is opened: after an Ascension it kept stale entries and nothing
        // was bought for an hour (observed 2026-10-29). Refresh per round.
        if (powersList != null && currentPowerIndex < 0)
        {
            try
            {
                powersList.RefreshList();
            }
            catch (Exception exception)
            {
                ProgressionLog.Exception("Equipment-list refresh", exception);
            }
        }
        var powers = powersList?.scrollListData;
        if (shop == null || powers == null)
        {
            if (!missingLogged)
            {
                ProgressionLog.Debug($"Equipment purchase objects unavailable. Shop={shop != null}, Powers={powers != null}.");
                missingLogged = true;
            }
            return false;
        }

        missingLogged = false;
        if (powers.Count != knownPowerCount)
        {
            knownPowerCount = powers.Count;
            currentPowerIndex = powers.Count - 1;
        }
        else if (currentPowerIndex < 0)
        {
            currentPowerIndex = powers.Count - 1;
        }

        int latestUnlockedIndex = FindLatestUnlockedIndex(powers);
        LogScanState(now, powersList, powers, latestUnlockedIndex);

        while (currentPowerIndex >= 0)
        {
            Power power = powers[currentPowerIndex];
            if (!IsUnlocked(power))
            {
                currentPowerIndex--;
                continue;
            }

            int affordableLevels = power.CalculateMaxCost().accumulated;
            int purchaseStack = currentPowerIndex == latestUnlockedIndex
                ? Stack10
                : Stack50;
            if (affordableLevels < purchaseStack)
            {
                currentPowerIndex--;
                return true;
            }

            int levelsToBuy = affordableLevels / purchaseStack * purchaseStack;

            int purchasedLevels = BuyLevels(shop, power, levelsToBuy);
            if (purchasedLevels > 0)
            {
                noPurchaseSince = now;
                roundPurchaseCount++;
                roundPurchasedLevels += purchasedLevels;
                return false;
            }

            currentPowerIndex--;
            return true;
        }

        CompleteRound();
        nextCheckTime = now + IdleCheckIntervalSeconds;
        if (noPurchaseSince < 0f) noPurchaseSince = now;
        float idleSeconds = Configuration.AutoProgressionConfig.EquipmentIdleBeforeSleepMinutes * 60f;
        if (now - noPurchaseSince < idleSeconds) return false;

        float sleepSeconds = Configuration.AutoProgressionConfig.EquipmentSleepMinutes * 60f;
        sleepUntil = now + sleepSeconds;
        noPurchaseSince = -1f;
        ProgressionLog.Debug(
            $"No equipment met its purchase threshold for {idleSeconds / 60f:0.##} minutes " +
            $"(latest unlocked: 10 levels; older equipment: 50 levels); " +
            $"equipment buyer sleeping for {sleepSeconds / 60f:0.##} minutes.",
            "Purchases");
        return false;
    }

    private void CompleteRound()
    {
        if (roundPurchaseCount <= 0) return;

        ProgressionLog.Debug(
            $"Equipment purchase round completed: {roundPurchaseCount} purchase(s), +{roundPurchasedLevels} total levels.");
        roundPurchaseCount = 0;
        roundPurchasedLevels = 0;
    }

    // TEMP: shows what the buyer sees, at most once a minute.
    private void LogScanState(
        float now, PowersList powersList,
        Il2CppSystem.Collections.Generic.List<Power> powers, int latestUnlockedIndex)
    {
        if (now < nextScanLogTime) return;
        nextScanLogTime = now + 60f;
        string latest = "none";
        if (latestUnlockedIndex >= 0)
        {
            Power power = powers[latestUnlockedIndex];
            latest = $"{power.name} level={power.level} affordable={power.CalculateMaxCost().accumulated}";
        }
        int lastCount = powersList.lastScrollListData != null
            ? powersList.lastScrollListData.Count
            : -1;
        ProgressionLog.Debug(
            $"Equipment scan: scrollListData={powers.Count}, lastScrollListData={lastCount}, " +
            $"index={currentPowerIndex}, latest unlocked: {latest}.",
            "Purchases");
    }

    private static bool IsUnlocked(Power power)
    {
        if (power == null) return false;

        // PowersList.scrollListData is already the game's filtered shop list.
        // Keep this explicit gate as a second safeguard against a stale list
        // during the frame in which a skill unlocks a new equipment item.
        Upgrade unlock = power.lockedBehindUpgrade;
        return unlock == null || unlock.bought;
    }

    private static int FindLatestUnlockedIndex(
        Il2CppSystem.Collections.Generic.List<Power> powers)
    {
        for (int index = powers.Count - 1; index >= 0; index--)
        {
            if (IsUnlocked(powers[index])) return index;
        }

        return -1;
    }

    private static int BuyLevels(ShopManager shop, Power power, int levels)
    {
        int previousStack = shop.stackSelected;
        int previousLevel = power.level;
        try
        {
            int remainingLevels = levels;
            if (remainingLevels >= Stack50)
            {
                shop.SelectStack(Stack50);
                int purchasesOf50 = remainingLevels / Stack50;
                for (int index = 0; index < purchasesOf50; index++)
                    shop.BuyPower(power);
                remainingLevels %= Stack50;
            }

            if (remainingLevels >= Stack10)
            {
                shop.SelectStack(Stack10);
                int purchasesOf10 = remainingLevels / Stack10;
                for (int index = 0; index < purchasesOf10; index++)
                    shop.BuyPower(power);
            }
            return Math.Max(0, power.level - previousLevel);
        }
        catch (Exception exception)
        {
            ProgressionLog.Exception(
                $"Equipment purchase '{power?.name ?? "unknown"}'",
                exception);
            return 0;
        }
        finally
        {
            shop.SelectStack(previousStack);
        }
    }

    internal void Reset()
    {
        nextCheckTime = 0f;
        noPurchaseSince = -1f;
        sleepUntil = 0f;
        missingLogged = false;
        roundPurchaseCount = 0;
        roundPurchasedLevels = 0;
        currentPowerIndex = -1;
        knownPowerCount = -1;
    }
}
