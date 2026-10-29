using System.Collections.Generic;
using IdleSlayerMods.Common.Config;
using MelonLoader;

namespace AutoProgression.Configuration;

internal sealed class AutoProgressionConfig(string configName) : BaseConfig(configName)
{
    internal const int CurrentConfigurationVersion = 32;
    internal const float AutomaticAscensionCheckIntervalMinutes = 5f;
    internal const float RagePillMinimumIntervalSeconds = 10f;
    internal const float QuestAssistCraftableCooldownMinutes = 5f;
    internal const float EquipmentIdleBeforeSleepMinutes = 1f;
    internal const float EquipmentSleepMinutes = 10f;
    private const string LegacySection = "AutoProgression";

    internal MelonPreferences_Entry<int> ConfigurationVersion;
    internal MelonPreferences_Entry<bool> DebugMode;
    internal MelonPreferences_Entry<string> ToggleKey;
    internal MelonPreferences_Entry<bool> EnableAutomaticAscension;
    internal MelonPreferences_Entry<bool> EnableAutomaticUltraAscension;
    internal MelonPreferences_Entry<float> AutomaticAscensionSoulBonusPercent;
    internal MelonPreferences_Entry<bool> BuyAscensionSkillsAfterAutomaticAscension;
    internal MelonPreferences_Entry<bool> EnablePaidBonuses;
    internal MelonPreferences_Entry<bool> EnableMinionClaimAndSend;
    internal MelonPreferences_Entry<bool> EnableAutomaticMinionPrestige;
    internal MelonPreferences_Entry<bool> EnableEggOpening;
    internal MelonPreferences_Entry<int> DragonEggReserveAmount;
    internal MelonPreferences_Entry<int> SimurghEggReserveAmount;
    internal MelonPreferences_Entry<int> ArmoryBoxesPerPress;
    internal MelonPreferences_Entry<string> ArmoryBoxSelectKey;
    internal MelonPreferences_Entry<string> ArmoryBoxOpenKey;
    internal MelonPreferences_Entry<bool> EnableCrawlerEyeBulkPurchase;
    internal MelonPreferences_Entry<string> CrawlerEyePurchaseKey;
    internal MelonPreferences_Entry<int> CrawlerEyesPerPress;
    internal MelonPreferences_Entry<bool> EnableAutomaticSilverBoxClaim;
    internal MelonPreferences_Entry<bool> EnableQuestAutomation;
    internal MelonPreferences_Entry<bool> AutoClaimCompletedQuests;
    internal MelonPreferences_Entry<bool> RegenerateDailyQuests;
    internal MelonPreferences_Entry<bool> RegenerateWeeklyQuests;
    internal MelonPreferences_Entry<bool> UnlimitedQuestRerolls;
    internal MelonPreferences_Entry<bool> PreferMinimumRageWeeklyQuest;
    internal MelonPreferences_Entry<bool> FilterGeneratedDailyQuests;
    internal MelonPreferences_Entry<bool> ResetPortalCooldown;
    internal MelonPreferences_Entry<bool> AddExtraQuests;
    internal MelonPreferences_Entry<bool> FilterActiveDailyQuests;
    internal MelonPreferences_Entry<bool> EnableCraftableAutomation;
    internal MelonPreferences_Entry<bool> EnableRagePill;
    internal MelonPreferences_Entry<bool> EnableWhetstone;
    internal MelonPreferences_Entry<bool> EnableAlternateDimensionStaff;
    internal MelonPreferences_Entry<bool> EnableBidimensionalStaff;
    internal MelonPreferences_Entry<bool> EnableDeathwaveScepter;
    internal MelonPreferences_Entry<int> DeathwaveScepterFeatherReserveAmount;
    internal MelonPreferences_Entry<bool> EnableShardsNecklaceScrapOverflow;
    internal MelonPreferences_Entry<float> ShardsNecklaceScrapThresholdPercent;
    internal MelonPreferences_Entry<bool> EnableDragonScaleOverflowCraftables;
    internal MelonPreferences_Entry<float> DragonScaleOverflowThresholdPercent;
    internal MelonPreferences_Entry<bool> EnableAscendantBadgeBoost;
    internal MelonPreferences_Entry<bool> EnableQuestAssistCraftables;
    internal MelonPreferences_Entry<int> QuestAssistFeatherThresholdAmount;
    internal MelonPreferences_Entry<float> TimedCraftablesTargetMinutes;
    internal MelonPreferences_Entry<bool> BuyMissingMaterialsWithJewels;
    internal MelonPreferences_Entry<int> MaterialPurchasePercent;
    internal MelonPreferences_Entry<bool> EnableSkillPurchases;
    internal MelonPreferences_Entry<bool> DisableVerticalMagnetSkills;
    internal MelonPreferences_Entry<bool> EnableEquipmentPurchases;

    protected override void SetBindings()
    {
        ConfigurationVersion = Bind("Configuration Version",
            CurrentConfigurationVersion,
            $"Internal preference migration version. Current version: {CurrentConfigurationVersion}. Do not edit manually; modified values are restored automatically.");
        DebugMode = Bind("Debug Mode", false,
            "Show detailed state, timer, object lookup, and diagnostic logs. User actions, warnings, and errors are always logged.");

        bool migrateLegacyValues = ConfigurationVersion.Value < CurrentConfigurationVersion;

        ToggleKey = BindMigrated(
            "General", "Toggle Key", "Toggle Key", "T",
            "Key used to enable or pause the main AutoProgression runtime. Default: T. Use a Unity KeyCode name such as T, F8, or Keypad0, and avoid keys assigned to the manual tools below.",
            migrateLegacyValues);

        EnableAutomaticAscension = BindMigrated(
            "Ascension", "Automatic Ascension Enabled", "Ascension - Automatic Ascension Enabled", true,
            "Automatically perform normal Ascension at the configured soul bonus threshold.", migrateLegacyValues);
        EnableAutomaticUltraAscension = BindMigrated(
            "Ascension", "Automatic Ultra Ascension Enabled", "Ascension - Automatic Ultra Ascension Enabled", false,
            "WARNING: Ultra Ascension performs a major progression reset. Automatically Ultra Ascend only after the game's native Ultra Ascension requirements are active and the current Ascension upgrades grant at least 24 Astral Keys. The 24-key threshold is fixed.", migrateLegacyValues);
        AutomaticAscensionSoulBonusPercent = BindMigrated(
            "Ascension", "Soul Bonus Threshold Percent", "Ascension - Soul Bonus Threshold Percent", 50f,
            "Ascend when pending Slayer Points reach this percentage of lifetime Slayer Points.", migrateLegacyValues);
        BuyAscensionSkillsAfterAutomaticAscension = BindMoved(
            "Ascension", "Buy Skills After Automatic Ascension", "Ascension", "Buy Skills After Ascension",
            "Ascension - Buy Skills After Ascension", true,
            "After an automatic normal Ascension, repeatedly use the Ascension Skill Tree Buy All action until no more Slayer Points can be spent. Manual Ascension does not trigger this action.", migrateLegacyValues);

        EnableSkillPurchases = BindMigrated(
            "Purchases", "Skills Enabled", "Purchases - Skills Enabled", true,
            "While the main AutoProgression runtime is active, buy all currently affordable and eligible shop skills every 5 seconds.", migrateLegacyValues);
        EnableEquipmentPurchases = BindMigrated(
            "Purchases", "Equipment Enabled", "Purchases - Equipment Enabled", true,
            "While the main AutoProgression runtime is active, buy levels for unlocked normal equipment, starting from the newest unlocked item.", migrateLegacyValues);
        DisableVerticalMagnetSkills = BindMoved(
            "Purchases", "Disable Vertical Magnet Upgrades", "Skills", "Disable Vertical Magnet Upgrades",
            "Skills - Disable Vertical Magnet Upgrades", true,
            "Block both Random Box vertical magnet upgrades from automatic and manual purchase. This protection is always active and does not depend on the main automation toggle.", migrateLegacyValues);

        EnablePaidBonuses = BindMigrated(
            "Paid Bonuses", "Use Paid 500x Bonuses", "Use Paid 500x Bonuses", false,
            "WARNING: This option spends Jewels of Soul. Use both paid 500x bonuses while AutoProgression is active.", migrateLegacyValues);

        EnableMinionClaimAndSend = BindMigrated(
            "Minions", "Auto Claim and Send", "Minions - Auto Claim and Send", true,
            "While AutoProgression is active, claim completed unlocked Minion missions and send standing Minions again when their Slayer Point cost is affordable.", migrateLegacyValues);
        EnableAutomaticMinionPrestige = BindMigrated(
            "Minions", "Automatic Maximum-Level Prestige", "Minions - Automatic Maximum-Level Prestige", false,
            "Use maximum-level prestige for every manually prestiged Minion, independently from the main automation toggle. Automatic prestige requires AutoProgression to be active and processes only Minions whose maximum level is at least 70.", migrateLegacyValues);

        EnableEggOpening = BindMigrated(
            "Egg Opening", "Enabled", "Egg Opening - Enabled", false,
            "Master switch for automatic Dragon Egg and Simurgh Egg opening while AutoProgression is active.", migrateLegacyValues);
        DragonEggReserveAmount = BindMigrated(
            "Egg Opening", "Dragon Egg Reserve Amount", "Egg Opening - Dragon Egg Reserve Amount", 300,
            "Open normal Dragon Eggs one at a time while the inventory amount is greater than this value.", migrateLegacyValues);
        SimurghEggReserveAmount = BindMigrated(
            "Egg Opening", "Simurgh Egg Reserve Amount", "Egg Opening - Simurgh Egg Reserve Amount", 10,
            "Open normal Simurgh Eggs one at a time while the inventory amount is greater than this value.", migrateLegacyValues);

        EnableAutomaticSilverBoxClaim = BindMigrated(
            "Silver Boxes", "Auto Claim Reward", "Silver Boxes - Auto Claim Reward", true,
            "Automatically claim an available Silver Box reward after entering the game. This feature is independent from the main automation toggle.", migrateLegacyValues);
        EnableQuestAutomation = BindMigrated(
            "Quests", "Enabled", "Quests - Enabled", true,
            "Master switch for all quest automation. When disabled, every other option in this section is inactive.", migrateLegacyValues);
        AutoClaimCompletedQuests = BindMigrated(
            "Quests", "Auto Claim Completed Quests", "Quests - Auto Claim Completed Quests", true,
            "Automatically claim completed Daily and Weekly Quests.", migrateLegacyValues);
        RegenerateDailyQuests = BindMigrated(
            "Quests", "Regenerate Daily Quests", "Quests - Regenerate Daily Quests", true,
            "Generate another set of Daily Quests when no active Daily Quests remain.", migrateLegacyValues);
        RegenerateWeeklyQuests = BindMigrated(
            "Quests", "Regenerate Weekly Quests", "Quests - Regenerate Weekly Quests", true,
            "Generate another set of Weekly Quests when no active Weekly Quests remain.", migrateLegacyValues);
        UnlimitedQuestRerolls = BindMigrated(
            "Quests", "Unlimited Quest Rerolls", "Quests - Unlimited Quest Rerolls", true,
            "Keep Daily and Weekly Quest rerolls enabled while AutoProgression is active.", migrateLegacyValues);
        PreferMinimumRageWeeklyQuest = BindMigrated(
            "Quests", "Prefer 180k Rage Weekly Quest", "Quests - Prefer 180k Rage Weekly Quest", false,
            "After a new Weekly Quest is generated, reroll one generated slot until the 180,000 Rage Mode kill quest appears. Existing additional Weekly Quests are preserved, and manual rerolls do not trigger this feature.", migrateLegacyValues);
        FilterGeneratedDailyQuests = BindMigrated(
            "Quests", "Filter Generated Daily Quests", "Quests - Filter Generated Daily Quests", false,
            "After a new Daily Quest set is generated, reroll Goblin kills, material collection, Chest Hunt chests, normal or Silver Random Boxes, normal Boost uses, Rage Mode uses, Bonus Stage entry/full completion/sections, Ascending Heights, and Grapple Run objectives. Rage Mode kill and Wind Dash kill quests are retained. Manual rerolls do not trigger this feature.", migrateLegacyValues);
        FilterActiveDailyQuests = Bind("Quests", "Filter Active Daily Quests", false,
            "Every 30 seconds, apply the same reroll rules to Daily Quests that are still active, not only right after generation. A set only regenerates once all its quests are done, so this keeps one slow quest from blocking the next set.");
        ResetPortalCooldown = BindMigrated(
            "Quests", "Reset Portal Cooldown", "Quests - Reset Portal Cooldown", true,
            "Keep the normal Portal cooldown at zero while AutoProgression is active.", migrateLegacyValues);
        AddExtraQuests = Bind("Quests", "Add Extra Quests", true,
            "Once per Daily and Weekly set, press the game's extra quests button (the bonus that adds quests to the active set) when it has not been used yet, while AutoProgression is active.");

        EnableCraftableAutomation = BindMigrated(
            "Craftables", "Enabled", "Craftables - Enabled", false,
            "MASTER SWITCH: Enable all automatic craftable-item logic while the main AutoProgression runtime is active. When false, every other Craftables setting, including automatic material purchases, is inactive.", migrateLegacyValues);
        BuyMissingMaterialsWithJewels = BindMoved(
            "Craftables", "Buy Missing With Jewels", "Materials", "Buy Missing With Jewels",
            "Materials - Buy Missing With Jewels", false,
            "WARNING - SPENDS JEWELS OF SOUL: Allow enabled craftable services to buy eligible missing materials automatically. This setting works only when Craftables Enabled is true. Scrap, Simurgh Feathers, and Dragon Scales are protected and are never purchased by this option.", migrateLegacyValues);
        MaterialPurchasePercent = BindMoved(
            "Craftables", "Material Purchase Percent", "Materials", "Purchase Percent",
            "Materials - Purchase Percent", 100,
            "Size of each automatic Jewel material refill. Supported values are 25, 50, and 100 percent. Larger values can spend more Jewels of Soul per transaction. This value has no effect when Buy Missing With Jewels is false.", migrateLegacyValues);
        TimedCraftablesTargetMinutes = BindMigrated(
            "Craftables", "Timed Items Target Minutes", "Craftables - Timed Items Target Minutes", 6f,
            "Shared duration target for timed and duration-based overflow items. Refilling begins at half this value and stops at this value; for example, 6 minutes refills from 3 to 6 minutes.", migrateLegacyValues);
        EnableRagePill = BindMigrated(
            "Craftables", "Rage Pill Enabled", "Craftables - Rage Pill Enabled", true,
            "Use Rage Pills to refresh Rage while Rage has an active cooldown. WARNING: This can spend Jewels of Soul on missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        EnableWhetstone = BindMigrated(
            "Craftables", "Whetstone Enabled", "Craftables - Whetstone Enabled", true,
            "Keep the Whetstone temporary effect active. WARNING: This can spend Jewels of Soul on missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        EnableAlternateDimensionStaff = BindMigrated(
            "Craftables", "Alternate Dimension Staff Enabled", "Craftables - Alternate Dimension Staff Enabled", true,
            "Keep the Alternate Dimension Staff temporary effect active. WARNING: This can spend Jewels of Soul on missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        EnableBidimensionalStaff = BindMigrated(
            "Craftables", "Bidimensional Staff Enabled", "Craftables - Bidimensional Staff Enabled", true,
            "Keep the Bidimensional Staff temporary effect active. WARNING: This can spend Jewels of Soul on missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        EnableDeathwaveScepter = BindMigrated(
            "Craftables", "Deathwave Scepter Enabled", "Craftables - Deathwave Scepter Enabled", true,
            "Keep the Deathwave Scepter temporary effect active while the Simurgh Feather reserve condition is met. WARNING: This can spend Jewels of Soul on other missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        DeathwaveScepterFeatherReserveAmount = BindMigrated(
            "Craftables", "Deathwave Scepter Feather Reserve Amount", "Craftables - Deathwave Scepter Feather Reserve Amount", 300,
            "Craft Deathwave Scepters only while the Simurgh Feather amount is greater than this reserve value.", migrateLegacyValues);
        EnableShardsNecklaceScrapOverflow = BindMigrated(
            "Craftables", "Shards Necklace Scrap Overflow Enabled", "Craftables - Shards Necklace Scrap Overflow Enabled", true,
            "Craft Shards Necklaces when Scrap reaches the configured capacity percentage. WARNING: This can spend Jewels of Soul on missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        ShardsNecklaceScrapThresholdPercent = BindMigrated(
            "Craftables", "Shards Necklace Scrap Threshold Percent", "Craftables - Shards Necklace Scrap Threshold Percent", 97f,
            "Craft Shards Necklaces at or above this Scrap percentage and stop below it. This item does not use the timed-item duration limit.", migrateLegacyValues);
        EnableDragonScaleOverflowCraftables = BindMigrated(
            "Craftables", "Dragon Scale Overflow Craftables Enabled", "Craftables - Dragon Scale Overflow Craftables Enabled", true,
            "Craft one of each supported Dragon Scale overflow item per overflow cycle. WARNING: This can spend Jewels of Soul on other missing materials when the global material-purchase option is enabled.", migrateLegacyValues);
        DragonScaleOverflowThresholdPercent = BindMigrated(
            "Craftables", "Dragon Scale Overflow Threshold Percent", "Craftables - Dragon Scale Overflow Threshold Percent", 95f,
            "Start one Dragon Scale overflow crafting cycle when Dragon Scale storage rises above this percentage. Each effect also respects the Timed Items Target Minutes limit.", migrateLegacyValues);
        EnableAscendantBadgeBoost = BindMigrated(
            "Craftables", "Ascendant Badge Boost Enabled",
            "Craftables - Ascendant Badge Boost Enabled", false,
            "Craft Ascendant Badge Boost when Dragon Scale storage is strictly above the fixed 50% requirement and the game's native one-use state is available. This setting requires Craftables Enabled and the main AutoProgression runtime. Dragon Scales are never purchased; other missing materials follow Buy Missing With Jewels.", migrateLegacyValues);
        EnableQuestAssistCraftables = BindMigrated(
            "Craftables", "Quest Assist Craftables Enabled", "Craftables - Quest Assist Craftables Enabled", true,
            "Enable Specialization and Key Manifest while Craftables and the main AutoProgression runtime are active. Specialization supports active normal Goblin or Bonus Stage quests and may also run when Scrap is above 80% and Dragon Scales are above 50%; it pauses that overflow path during active normal, Silver, or Golden Random Box quests and preserves at least 50% Scrap. Key Manifest supports active normal Chest Hunt quests and also has an independent Feather-overflow trigger. Daily and Weekly Quests do not trigger either item.", migrateLegacyValues);
        QuestAssistFeatherThresholdAmount = BindMoved(
            "Craftables", "Quest Assist Feather Threshold Amount",
            "Craftables", "Key Manifest Feather Overflow Amount",
            "Craftables - Key Manifest Feather Overflow Amount", 1000,
            "Shared Simurgh Feather requirement for Specialization and Key Manifest. Set to 0 to disable both items completely. Above 0, either item may be crafted only when the current Feather amount is strictly greater than this value and crafting would not reduce it below this value. Key Manifest also uses this threshold as its independent Feather-overflow trigger. Simurgh Feathers are never purchased; other missing materials follow Buy Missing With Jewels.", migrateLegacyValues);
        ArmoryBoxesPerPress = BindMoved(
            "Manual Armory Boxes", "Boxes Per Press",
            "Armory Boxes", "Boxes Per Press", "Armory Boxes - Boxes Per Press", 10,
            "Maximum number of the selected Armory box, Dragon Egg, or Simurgh Egg opened in the background per Open Boxes Key press.", migrateLegacyValues);
        ArmoryBoxSelectKey = BindMoved(
            "Manual Armory Boxes", "Select Box Key",
            "Armory Boxes", "Select Box Key", "Armory Boxes - Select Box Key", "B",
            "Record the Armory box or egg currently highlighted in the Armory crafting screen. This key must differ from Open Boxes Key.", migrateLegacyValues);
        ArmoryBoxOpenKey = BindMoved(
            "Manual Armory Boxes", "Open Boxes Key",
            "Armory Boxes", "Open Boxes Key", "Armory Boxes - Open Boxes Key", "N",
            "Open the selected Armory box or egg in the background. This manual feature is available regardless of the main automation toggle.", migrateLegacyValues);

        EnableCrawlerEyeBulkPurchase = BindMoved(
            "Manual Casino Crawler Eyes", "Enabled",
            "Casino Crawler Eyes", "Enabled", "Casino Crawler Eyes - Enabled", false,
            "WARNING - SPENDS JEWELS OF SOUL: Enable the manual bulk-purchase key on the Village Casino Crawler Eye purchase screen. This manual feature is available regardless of the main automation toggle.", migrateLegacyValues);
        CrawlerEyePurchaseKey = BindMoved(
            "Manual Casino Crawler Eyes", "Purchase Key",
            "Casino Crawler Eyes", "Purchase Key", "Casino Crawler Eyes - Purchase Key", "M",
            "Start one sequential Crawler Eye bulk-purchase operation while the correct Casino purchase screen is open.", migrateLegacyValues);
        CrawlerEyesPerPress = BindMoved(
            "Manual Casino Crawler Eyes", "Eyes Per Press",
            "Casino Crawler Eyes", "Eyes Per Press", "Casino Crawler Eyes - Eyes Per Press", 1000,
            "Requested number of Crawler Eyes per key press. The value is rounded down to a multiple of 10 and purchasing stops safely if the screen closes or Jewels are insufficient.", migrateLegacyValues);

        if (migrateLegacyValues)
        {
            MigrateChangedDefaults();
            RemoveLegacyEntries();
            RemoveRetiredEntries();
        }

        if (ConfigurationVersion.Value != CurrentConfigurationVersion)
        {
            ConfigurationVersion.Value = CurrentConfigurationVersion;
            MelonPreferences.Save();
        }
    }

    private MelonPreferences_Entry<T> BindMigrated<T>(
        string section,
        string key,
        string legacyKey,
        T defaultValue,
        string description,
        bool migrateLegacyValue)
    {
        bool hasLegacyValue = migrateLegacyValue &&
                              MelonPreferences.HasEntry(LegacySection, legacyKey);
        T legacyValue = hasLegacyValue
            ? MelonPreferences.GetEntryValue<T>(LegacySection, legacyKey)
            : defaultValue;

        MelonPreferences_Entry<T> entry = Bind(section, key, defaultValue, description);
        if (hasLegacyValue)
            entry.Value = legacyValue;

        return entry;
    }

    private MelonPreferences_Entry<T> BindMoved<T>(
        string section,
        string key,
        string oldSection,
        string oldKey,
        string legacyKey,
        T defaultValue,
        string description,
        bool migrateValue)
    {
        bool hasMovedValue = migrateValue &&
                             MelonPreferences.HasEntry(oldSection, oldKey);
        T movedValue = hasMovedValue
            ? MelonPreferences.GetEntryValue<T>(oldSection, oldKey)
            : defaultValue;

        MelonPreferences_Entry<T> entry = BindMigrated(
            section, key, legacyKey, defaultValue, description, migrateValue);
        if (hasMovedValue)
            entry.Value = movedValue;

        return entry;
    }

    private static void RemoveLegacyEntries()
    {
        MelonPreferences_Category category = MelonPreferences.GetCategory(LegacySection);
        if (category == null) return;

        foreach (string key in LegacyKeys)
            category.DeleteEntry(key);
    }

    private static void RemoveRetiredEntries()
    {
        MelonPreferences.GetCategory("Purchases")?.DeleteEntry("Priority");
        MelonPreferences.GetCategory("Ascension")?.DeleteEntry("Buy Skills After Ascension");
        MelonPreferences.GetCategory("Materials")?.DeleteEntry("Buy Missing With Jewels");
        MelonPreferences.GetCategory("Materials")?.DeleteEntry("Purchase Percent");
        MelonPreferences.GetCategory("Skills")?.DeleteEntry("Disable Vertical Magnet Upgrades");
        MelonPreferences.GetCategory("Equipment")?.DeleteEntry("No Purchase Before Sleep Minutes");
        MelonPreferences.GetCategory("Equipment")?.DeleteEntry("Sleep Minutes");
        MelonPreferences.GetCategory("Ascension")?.DeleteEntry("Check Interval Minutes");
        MelonPreferences.GetCategory("Craftables")?.DeleteEntry("Rage Pill Minimum Interval Seconds");
        MelonPreferences.GetCategory("Craftables")?.DeleteEntry("Quest Assist Craftable Cooldown Minutes");
        MelonPreferences.GetCategory("Craftables")?.DeleteEntry("Timed Items Refill At Minutes");
        MelonPreferences.GetCategory("Craftables")?.DeleteEntry("Key Manifest Feather Overflow Amount");
        MelonPreferences.GetCategory("Purchases")?.DeleteEntry("Equipment No Purchase Before Sleep Minutes");
        MelonPreferences.GetCategory("Purchases")?.DeleteEntry("Equipment Sleep Minutes");
        MelonPreferences.GetCategory("Armory Boxes")?.DeleteEntry("Boxes Per Press");
        MelonPreferences.GetCategory("Armory Boxes")?.DeleteEntry("Select Box Key");
        MelonPreferences.GetCategory("Armory Boxes")?.DeleteEntry("Open Boxes Key");
        MelonPreferences.GetCategory("Casino Crawler Eyes")?.DeleteEntry("Enabled");
        MelonPreferences.GetCategory("Casino Crawler Eyes")?.DeleteEntry("Purchase Key");
        MelonPreferences.GetCategory("Casino Crawler Eyes")?.DeleteEntry("Eyes Per Press");
    }

    private void MigrateChangedDefaults()
    {
        if (ShardsNecklaceScrapThresholdPercent.Value == 95f)
            ShardsNecklaceScrapThresholdPercent.Value = 97f;
        if (ArmoryBoxSelectKey.Value is "I" or "[")
            ArmoryBoxSelectKey.Value = "B";
        if (ArmoryBoxOpenKey.Value is "O" or "]")
            ArmoryBoxOpenKey.Value = "N";
        if (CrawlerEyePurchaseKey.Value is "\\" or "|")
            CrawlerEyePurchaseKey.Value = "M";
        if (AutomaticAscensionSoulBonusPercent.Value == 100f)
            AutomaticAscensionSoulBonusPercent.Value = 50f;
        if (TimedCraftablesTargetMinutes.Value == 60f)
            TimedCraftablesTargetMinutes.Value = 15f;
        if (TimedCraftablesTargetMinutes.Value == 15f)
            TimedCraftablesTargetMinutes.Value = 6f;
    }

    private static readonly IReadOnlyList<string> LegacyKeys = new[]
    {
        "Ascension - Automatic Ascension Enabled",
        "Toggle Key",
        "Ascension - Soul Bonus Threshold Percent",
        "Ascension - Check Interval Minutes",
        "Ascension - Buy Skills After Ascension",
        "Use Paid 500x Bonuses",
        "Minions - Auto Claim and Send",
        "Minions - Automatic Maximum-Level Prestige",
        "Egg Opening - Dragon Egg Reserve Amount",
        "Egg Opening - Simurgh Egg Reserve Amount",
        "Casino Crawler Eyes - Enabled",
        "Casino Crawler Eyes - Purchase Key",
        "Casino Crawler Eyes - Eyes Per Press",
        "Quests - Enabled",
        "Quests - Auto Claim Completed Quests",
        "Quests - Regenerate Daily Quests",
        "Quests - Regenerate Weekly Quests",
        "Quests - Unlimited Quest Rerolls",
        "Quests - Prefer 180k Rage Weekly Quest",
        "Quests - Filter Generated Daily Quests",
        "Quests - Reset Portal Cooldown",
        "Craftables - Enabled",
        "Craftables - Rage Pill Enabled",
        "Craftables - Rage Pill Minimum Interval Seconds",
        "Craftables - Whetstone Enabled",
        "Craftables - Alternate Dimension Staff Enabled",
        "Craftables - Bidimensional Staff Enabled",
        "Craftables - Deathwave Scepter Enabled",
        "Craftables - Deathwave Scepter Feather Reserve Amount",
        "Craftables - Shards Necklace Scrap Overflow Enabled",
        "Craftables - Shards Necklace Scrap Threshold Percent",
        "Craftables - Dragon Scale Overflow Craftables Enabled",
        "Craftables - Dragon Scale Overflow Threshold Percent",
        "Craftables - Ascendant Badge Boost Enabled",
        "Craftables - Quest Assist Craftables Enabled",
        "Craftables - Quest Assist Craftable Cooldown Minutes",
        "Craftables - Timed Items Refill At Minutes",
        "Craftables - Timed Items Target Minutes",
        "Materials - Buy Missing With Jewels",
        "Materials - Purchase Percent",
        "Purchases - Priority",
        "Purchases - Skills Enabled",
        "Purchases - Equipment Enabled",
        "Skills - Disable Vertical Magnet Upgrades",
        "Equipment - No Purchase Before Sleep Minutes",
        "Equipment - Sleep Minutes"
    };
}
