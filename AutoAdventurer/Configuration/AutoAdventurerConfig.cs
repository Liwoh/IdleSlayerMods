using System;
using System.Globalization;
using System.IO;
using AutoAdventurer.Diagnostics;
using IdleSlayerMods.Common.Config;
using MelonLoader;
using MelonLoader.Utils;

namespace AutoAdventurer.Configuration;

internal sealed class AutoAdventurerConfig(string configName) : BaseConfig(configName)
{
    internal const int CurrentConfigurationVersion = 35;
    private const string MainSection = "AutoAdventurer";
    private const string RageSection = "Automatic Rage";
    private const string GameplaySection = "Gameplay";
    private const string SilverBoxAutomationSection = "Silver Box Automation";
    private const string AutoBoostSection = "Auto Movement & Combat";
    private const string LegacyAutoBoostSection = "Auto Boost";
    private const string QuestAutomationSection = "Quest Automation";

    internal MelonPreferences_Entry<int> ConfigurationVersion;
    internal MelonPreferences_Entry<bool> DebugMode;
    internal MelonPreferences_Entry<string> ToggleKey;
    internal MelonPreferences_Entry<string> StopKey;
    internal MelonPreferences_Entry<double> ActivationCheckIntervalSeconds;
    internal MelonPreferences_Entry<double> MaximumRageDurationSeconds;
    internal MelonPreferences_Entry<double> PostRageObservationSeconds;
    internal MelonPreferences_Entry<bool> SkipBonusStartSlider;
    internal MelonPreferences_Entry<bool> AutoBoss;
    internal MelonPreferences_Entry<string> AutoBoostToggleKey;
    internal MelonPreferences_Entry<bool> AutoMovementEnableOnStartup;
    internal MelonPreferences_Entry<bool> WindDashRequireGrounded;
    internal MelonPreferences_Entry<bool> AutoJump;
    internal MelonPreferences_Entry<bool> AutoShootArrows;
    internal MelonPreferences_Entry<string> ArrowAttackFrequency;
    internal MelonPreferences_Entry<string> QuestAutomationToggleKey;
    internal MelonPreferences_Entry<bool> QuestCompletionNotifications;
    internal MelonPreferences_Entry<bool> AutoAlignElementalDivinities;
    internal MelonPreferences_Entry<bool> EnableSilverBoxControl;
    internal MelonPreferences_Entry<bool> AutoReleaseSilverBoxLock;
    internal MelonPreferences_Entry<double> PermanentSilverBoxReleaseAboveDivinityPoints;
    internal MelonPreferences_Entry<double> MinimumDimensionStayMinutes;
    internal MelonPreferences_Entry<double> MaximumQuestTimeMinutes;
    internal MelonPreferences_Entry<string> FallbackMap;
    internal MelonPreferences_Entry<string> MaterialQuestMap;

    internal double MinimumDimensionStayMinutesValue =>
        Math.Max(0d, MinimumDimensionStayMinutes.Value);

    internal double MaximumQuestTimeMinutesValue =>
        Math.Max(0d, MaximumQuestTimeMinutes.Value);

    internal float ActivationCheckIntervalSecondsValue =>
        (float)Math.Max(0d, ActivationCheckIntervalSeconds.Value);

    internal float MaximumRageDurationSecondsValue =>
        (float)Math.Max(0d, MaximumRageDurationSeconds.Value);

    internal float PostRageObservationSecondsValue =>
        (float)Math.Max(0d, PostRageObservationSeconds.Value);

    // Internal timing safeguard. This is intentionally not user-facing:
    // keeping one known value avoids culture/parser issues and prevents
    // unsafe zero-delay Wind Dash loops.
    internal double AutoBoostActivationDelaySecondsValue => 0.2d;

    internal double PermanentSilverBoxReleaseAboveDivinityPointsValue =>
        Math.Max(0d, PermanentSilverBoxReleaseAboveDivinityPoints.Value);

    protected override void SetBindings()
    {
        ConfigurationVersion = Bind(MainSection, "Configuration Version",
            CurrentConfigurationVersion,
            $"Internal preference migration version. Current version: {CurrentConfigurationVersion}. Do not edit manually; modified values are restored automatically.");
        DebugMode = Bind(MainSection, "Debug Mode", false,
            "Show detailed AutoAdventurer diagnostic logs. User actions, warnings, and errors are always logged.");

        // Global helpers are bound first so they appear above the hotkey-
        // controlled automation sections in newly generated configurations.
        SkipBonusStartSlider = Bind(GameplaySection,
            "Skip Bonus Start Slider", true,
            "Global setting with no hotkey or manual trigger. Automatically confirm the timing slider shown before supported bonus minigames whenever it appears.");
        AutoBoss = Bind(GameplaySection, "Auto Boss", true,
            "Global setting with no hotkey or manual trigger. Automatically reduce bosses to 1 HP, advance boss dialogue, perform the finishing attack, and close supported result screens whenever a boss encounter appears.");

        // L-controlled movement helpers belong directly below the global
        // gameplay settings in newly generated configurations.
        bool migrateAutoMovementSection =
            ConfigurationVersion.Value < 33;
        string autoMovementToggleKey = migrateAutoMovementSection
            ? ReadLegacyValue(LegacyAutoBoostSection, "Toggle Key", "L")
            : "L";
        bool autoJump = migrateAutoMovementSection
            ? ReadLegacyBoolean(LegacyAutoBoostSection, "Auto Jump", true)
            : true;
        bool autoShootArrows = migrateAutoMovementSection
            ? ReadLegacyBoolean(LegacyAutoBoostSection,
                "Auto Shoot Arrows", true)
            : true;
        string arrowAttackFrequency = migrateAutoMovementSection
            ? NormalizeArrowAttackFrequency(ReadLegacyValue(
                LegacyAutoBoostSection, "Arrow Attack Frequency", "Medium"))
            : "Medium";
        bool windDashRequireGrounded = migrateAutoMovementSection
            ? ReadLegacyBoolean(LegacyAutoBoostSection,
                "Wind Dash Require Grounded", true)
            : true;

        AutoMovementEnableOnStartup = Bind(AutoBoostSection,
            "Enable On Startup", true,
            "Automatically enable Auto Movement & Combat when the game starts. The Toggle Key can still disable or re-enable it at any time.");
        AutoBoostToggleKey = Bind(AutoBoostSection, "Toggle Key", autoMovementToggleKey,
            "Keyboard key used to enable or disable smart movement automation in the central Runner/Rage scene. While enabled, the mod can perform minimum-height jumps and arrow attacks, detects whether Boost or Wind Dash is selected, activates that ability automatically, and follows later ability changes without another toggle. Bonus stages and other minigames are excluded.");
        AutoJump = Bind(AutoBoostSection, "Auto Jump", autoJump,
            "While L automation is enabled, repeatedly perform the shortest possible jump in the central Runner/Rage scene.");
        AutoShootArrows = Bind(AutoBoostSection, "Auto Shoot Arrows", autoShootArrows,
            "While L automation is enabled, repeatedly request arrow attacks in the central Runner/Rage scene. The game remains responsible for bow availability and attack cooldown.");
        ArrowAttackFrequency = Bind(AutoBoostSection,
            "Arrow Attack Frequency", arrowAttackFrequency,
            "Arrow request frequency used by Auto Shoot Arrows. Supported values: Light, Medium, High, Extra High, or Ultra (every rendered frame). Invalid values fall back to Medium.");
        WindDashRequireGrounded = Bind(AutoBoostSection,
            "Wind Dash Require Grounded", windDashRequireGrounded,
            "Require the player to be at ground level before automatically activating Wind Dash, preventing it from passing over portals or elite enemies.");
        if (migrateAutoMovementSection)
            DeleteLegacyAutoBoostEntries();
        DeleteHiddenAutoBoostDelayEntry();

        bool migrateSilverRelease = ConfigurationVersion.Value < 24 &&
            MelonPreferences.HasEntry(QuestAutomationSection,
                "Auto Release Silver Box Lock");
        bool autoReleaseSilverBoxLock = migrateSilverRelease
            ? MelonPreferences.GetEntryValue<bool>(QuestAutomationSection,
                "Auto Release Silver Box Lock")
            : true;
        double permanentReleaseThreshold = migrateSilverRelease &&
            MelonPreferences.HasEntry(QuestAutomationSection,
                "Permanent Release Above Divinity Points")
            ? MelonPreferences.GetEntryValue<double>(QuestAutomationSection,
                "Permanent Release Above Divinity Points")
            : ReadLegacyNumber(SilverBoxAutomationSection,
                "Permanent Release Above Divinity Points", 0d);
        bool migrateSilverMasterSwitch = ConfigurationVersion.Value < 26 &&
            MelonPreferences.HasEntry(SilverBoxAutomationSection,
                "Enable Silver Box Control");
        bool enableSilverBoxControl = migrateSilverMasterSwitch
            ? MelonPreferences.GetEntryValue<bool>(SilverBoxAutomationSection,
                "Enable Silver Box Control")
            : true;
        EnableSilverBoxControl = Bind(SilverBoxAutomationSection,
            "Enabled", enableSilverBoxControl,
            "Master switch for all Silver Box automation. When disabled, every other option in this section is inactive and Silver Bank remains under manual control.");
        AutoReleaseSilverBoxLock = Bind(SilverBoxAutomationSection,
            "Auto Release Silver Box Lock", autoReleaseSilverBoxLock,
            "Global setting independent from Quest Automation and its P hotkey. When an active normal quest requires Silver Random Boxes, disable Silver Bank so those boxes can spawn.");
        if (ConfigurationVersion.Value < 30)
            MelonPreferences.GetCategory(SilverBoxAutomationSection)?
                .DeleteEntry("Permanent Release Above Divinity Points");
        PermanentSilverBoxReleaseAboveDivinityPoints = Bind(
            SilverBoxAutomationSection,
            "Permanent Release Above Divinity Points",
            permanentReleaseThreshold,
            "Global five-second Silver Bank threshold. Above this normalized available Divinity Point value Silver Bank is disabled; at or below it Silver Bank is enabled when affordable. Task-triggered release overrides this threshold. Set to 0 to disable threshold control completely and leave the normal Silver Bank state unchanged.");

        // Quest Automation is the mod's primary feature, so keep it directly
        // below the global helpers in newly generated configurations.
        QuestAutomationToggleKey = Bind(QuestAutomationSection, "Toggle Key", "P",
            "Keyboard key used to enable or disable quest-guided dimension travel.");
        // Quest diagnostics now share the main Debug Mode. Remove the former
        // section-level switch even from configurations already migrated to v20.
        bool removeLegacyQuestDebug =
            MelonPreferences.HasEntry(QuestAutomationSection, "Debug Mode");
        if (removeLegacyQuestDebug)
            MelonPreferences.GetCategory(QuestAutomationSection)?
                .DeleteEntry("Debug Mode");
        QuestCompletionNotifications = Bind(QuestAutomationSection,
            "Show Completion Notifications", true,
            "Show an in-game notification with session quest statistics whenever Quest Automation completes a task.");
        AutoAlignElementalDivinities = Bind(QuestAutomationSection,
            "Auto Align Elemental Divinities", true,
            "While Quest Automation is enabled with P, check every five seconds and align elemental Dark Divinities with the first active elemental kill quest in the live quest list. The mod corrects later manual changes, switches an active element, or attempts to activate the required unlocked element when none is active and enough Divinity Points are available. This does not change quest priority or replace the current quest lock.");
        bool removeLegacyAutoClaim = ConfigurationVersion.Value < 20 &&
            MelonPreferences.HasEntry(QuestAutomationSection,
                "Auto Claim Completed Quests");
        bool migrateTimingValues = ConfigurationVersion.Value < 30;
        double minimumDimensionStayMinutes = ReadLegacyNumber(
            QuestAutomationSection, "Minimum Dimension Stay Minutes", 10d);
        bool migrateQuestTime = ConfigurationVersion.Value < 17 &&
            MelonPreferences.HasEntry(QuestAutomationSection,
                "Quest Lock Rescan Minutes");
        double maximumQuestTime = migrateQuestTime
            ? MelonPreferences.GetEntryValue<float>(QuestAutomationSection,
                "Quest Lock Rescan Minutes")
            : ReadLegacyNumber(QuestAutomationSection,
                "Maximum Quest Time Minutes", 5d);
        if (migrateTimingValues)
        {
            MelonPreferences.GetCategory(QuestAutomationSection)?
                .DeleteEntry("Minimum Dimension Stay Minutes");
            MelonPreferences.GetCategory(QuestAutomationSection)?
                .DeleteEntry("Maximum Quest Time Minutes");
        }
        MinimumDimensionStayMinutes = Bind(QuestAutomationSection,
            "Minimum Dimension Stay Minutes", minimumDimensionStayMinutes,
            "Stay in a dimension for at least this many minutes after automatic travel before changing dimension again. Actual travel timing is still limited by the game's Portal cooldown and Portal availability.");
        MaximumQuestTimeMinutes = Bind(QuestAutomationSection,
            "Maximum Quest Time Minutes", maximumQuestTime,
            "Allow one selected quest to run for at most this many minutes before releasing it and fully rescanning. Set to 0 to disable the maximum time limit.");
        FallbackMap = Bind(QuestAutomationSection, "Fallback Map", "Auto",
            "Where to go when no kill quest can be done. Auto = the dimension with the best live soul score. Off = stay in the current dimension. Or an internal map name such as map_funky_space; the available names are listed in the log when Quest Automation starts.");
        MaterialQuestMap = Bind(QuestAutomationSection, "Material Quest Map", "Auto",
            "Where to go when no kill quest can be done but a Get Materials quest is active. Auto = the best soul score among dimensions where an enemy's current stage drops a material (listed in the log). Off = ignore material quests. Or an internal map name.");

        ToggleKey = Bind(RageSection, "Toggle Key", "K",
            "Keyboard key used to enable or disable Automatic Rage.");
        StopKey = Bind(RageSection, "Stop Key", "J",
            "Keyboard key used to end the current Rage Mode immediately.");
        bool migrateAllNumericValues = ConfigurationVersion.Value < 30;
        double activationCheckIntervalSeconds = ReadLegacyNumber(RageSection,
            "Activation Check Interval Seconds", 12d);
        double maximumRageDurationSeconds = ReadLegacyNumber(RageSection,
            "Maximum Rage Duration Seconds", 20d);
        bool migratePostRageProtection = ConfigurationVersion.Value < 18 &&
            MelonPreferences.HasEntry(RageSection,
                "Post Rage Observation Seconds");
        double postRageProtectionSeconds = migratePostRageProtection
            ? MelonPreferences.GetEntryValue<float>(RageSection,
                "Post Rage Observation Seconds")
            : ReadLegacyNumber(RageSection,
                "Post Rage Protection Seconds", 8d);
        if (migrateAllNumericValues)
        {
            MelonPreferences.GetCategory(RageSection)?
                .DeleteEntry("Activation Check Interval Seconds");
            MelonPreferences.GetCategory(RageSection)?
                .DeleteEntry("Maximum Rage Duration Seconds");
            MelonPreferences.GetCategory(RageSection)?
                .DeleteEntry("Post Rage Protection Seconds");
        }
        ActivationCheckIntervalSeconds = Bind(RageSection,
            "Activation Check Interval Seconds", activationCheckIntervalSeconds,
            "How often Automatic Rage checks whether Rage Mode is ready to activate.");
        MaximumRageDurationSeconds = Bind(RageSection,
            "Maximum Rage Duration Seconds", maximumRageDurationSeconds,
            "End an automatically started Rage Mode after this many seconds. Set to 0 to disable the time limit.");
        PostRageObservationSeconds = Bind(RageSection,
            "Post Rage Protection Seconds", postRageProtectionSeconds,
            "After Rage ends, protect the map for this long while checking quests, boxes, events, minigame triggers, and portals before allowing travel or another Rage activation.");

        if (ConfigurationVersion.Value != CurrentConfigurationVersion ||
            removeLegacyQuestDebug)
        {
            if (migratePostRageProtection)
                MelonPreferences.GetCategory(RageSection)?
                    .DeleteEntry("Post Rage Observation Seconds");
            if (migrateQuestTime)
                MelonPreferences.GetCategory(QuestAutomationSection)?
                    .DeleteEntry("Quest Lock Rescan Minutes");
            if (removeLegacyAutoClaim)
                MelonPreferences.GetCategory(QuestAutomationSection)?
                    .DeleteEntry("Auto Claim Completed Quests");
            if (migrateSilverRelease)
            {
                MelonPreferences.GetCategory(QuestAutomationSection)?
                    .DeleteEntry("Auto Release Silver Box Lock");
                MelonPreferences.GetCategory(QuestAutomationSection)?
                    .DeleteEntry("Permanent Release Above Divinity Points");
            }
            if (migrateSilverMasterSwitch)
                MelonPreferences.GetCategory(SilverBoxAutomationSection)?
                    .DeleteEntry("Enable Silver Box Control");
            ConfigurationVersion.Value = CurrentConfigurationVersion;
            MelonPreferences.Save();
        }
    }

    private static double ParseNonNegativeNumber(string rawValue, double fallback)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return fallback;

        string value = rawValue.Trim();
        if (double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed) ||
            double.TryParse(value, NumberStyles.Float,
                CultureInfo.CurrentCulture, out parsed) ||
            double.TryParse(value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed))
            return Math.Max(0d, parsed);

        return fallback;
    }

    private static string NormalizeArrowAttackFrequency(string value)
    {
        if (string.Equals(value, "Extreme", StringComparison.OrdinalIgnoreCase))
            return "Ultra";
        if (string.Equals(value, "Low", StringComparison.OrdinalIgnoreCase))
            return "Light";
        if (string.Equals(value, "Extra High", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "ExtraHigh", StringComparison.OrdinalIgnoreCase))
            return "Extra High";
        if (string.Equals(value, "Light", StringComparison.OrdinalIgnoreCase))
            return "Light";
        if (string.Equals(value, "Medium", StringComparison.OrdinalIgnoreCase))
            return "Medium";
        if (string.Equals(value, "High", StringComparison.OrdinalIgnoreCase))
            return "High";
        if (string.Equals(value, "Ultra", StringComparison.OrdinalIgnoreCase))
            return "Ultra";
        return "Medium";
    }

    private static bool ReadLegacyBoolean(string section, string key,
        bool fallback)
    {
        string raw = ReadLegacyValue(section, key,
            fallback ? "true" : "false");
        return bool.TryParse(raw, out bool value) ? value : fallback;
    }

    private static void DeleteLegacyAutoBoostEntries()
    {
        MelonPreferences_Category category =
            MelonPreferences.GetCategory(LegacyAutoBoostSection);
        if (category == null) return;

        category.DeleteEntry("Toggle Key");
        category.DeleteEntry("Activation Delay Seconds");
        category.DeleteEntry("Wind Dash Require Grounded");
        category.DeleteEntry("Auto Jump");
        category.DeleteEntry("Auto Shoot Arrows");
        category.DeleteEntry("Arrow Attack Frequency");
    }

    private static void DeleteHiddenAutoBoostDelayEntry()
    {
        MelonPreferences_Category category =
            MelonPreferences.GetCategory(AutoBoostSection);
        category?.DeleteEntry("Activation Delay Seconds");
    }

    private static double ReadLegacyNumber(string section, string key,
        double fallback) => ParseNonNegativeNumber(
            ReadLegacyValue(section, key,
                fallback.ToString("R", CultureInfo.InvariantCulture)), fallback);

    private static string ReadLegacyValue(string section, string key,
        string fallback)
    {
        try
        {
            string path = Path.Combine(MelonEnvironment.UserDataDirectory,
                "AutoAdventurer.cfg");
            if (!File.Exists(path)) return fallback;

            bool inQuestAutomationSection = false;
            foreach (string sourceLine in File.ReadLines(path))
            {
                string line = sourceLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inQuestAutomationSection =
                        string.Equals(line, $"[\"{section}\"]",
                            StringComparison.Ordinal) ||
                        string.Equals(line, $"[{section}]",
                            StringComparison.Ordinal);
                    continue;
                }

                if (!inQuestAutomationSection) continue;
                string prefix = $"\"{key}\"";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0) continue;

                string raw = line[(equalsIndex + 1)..].Trim();
                int commentIndex = raw.IndexOf('#');
                if (commentIndex >= 0) raw = raw[..commentIndex].Trim();
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = raw[1..^1];
                return string.IsNullOrWhiteSpace(raw) ? fallback : raw;
            }
        }
        catch (Exception exception)
        {
            AdventurerLog.Warning(
                $"Could not migrate legacy timing setting '{key}'; using {fallback}. {exception.GetType().Name}: {exception.Message}");
        }

        return fallback;
    }
}
