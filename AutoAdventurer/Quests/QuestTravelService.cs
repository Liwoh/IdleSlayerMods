using System;
using System.Collections.Generic;
using AutoAdventurer.Diagnostics;
using AutoAdventurer.World;
using Il2Cpp;
using UnityEngine;
using UnityEngine.UI;

namespace AutoAdventurer.Quests;

internal sealed class QuestTravelService
{
    private const float ScanIntervalSeconds = 5f;
    private const float PortalRequestTimeoutSeconds = 60f;
    private const float CharacterSwitchStabilitySeconds = 0.5f;
    private const float CharacterSwitchCheckIntervalSeconds = 0.25f;
    private const float EventCheckIntervalSeconds = 0.5f;
    private const float EventTimerGraceSeconds = 1f;
    private const float EventEndConfirmationSeconds = 2f;
    private const float StalledProgressCheckSeconds = 300f;

    private readonly QuestDiscoveryService discovery = new();
    private readonly QuestTargetResolver resolver = new();
    private readonly SoulFarmingResolver soulFarming = new();
    private readonly QuestCharacterService characters = new();
    private readonly WorldInterruptionService interruptions = new();
    private float nextScanTime;
    private float portalRequestedAt;
    private float lastAutomaticArrivalAt = -1f;
    private string requestedMapId = string.Empty;
    private string requestedQuestId = string.Empty;
    private string requestedQuestKey = string.Empty;
    private string lastSelection = string.Empty;
    private string pendingQuestStatusReason = string.Empty;
    private float nextQuestStatusLogTime;
    private string lockedQuestId = string.Empty;
    private string lockedQuestKey = string.Empty;
    private string lockedQuestDisplayName = string.Empty;
    private int lockedQuestInstanceId;
    private string lockedTargetMapId = string.Empty;
    private string lockedRequiredCharacterId = string.Empty;
    private bool lockedQuestIsDaily;
    private bool lockedQuestSuppressesRage;
    private bool lockedQuestNeedsCharacterSwitch;
    private float lockedQuestStartedAt;
    private double lockedQuestProgressBaseline;
    private float nextLockedQuestProgressCheckAt;
    private float characterSwitchStableAt;
    private float nextCharacterSwitchCheckAt;
    private string characterWaitLoggedForQuestKey = string.Empty;
    private string proactiveCharacterId = string.Empty;
    private string proactiveCharacterQuestId = string.Empty;
    private bool lastQuestSnapshotAvailable;
    private bool requestedForSoulFallback;
    private bool pendingTravelObservedRage;
    private float pendingTravelPortalReadyAt;
    private float postRagePortalBlockedUntil;
    private string lastSoulFallbackMapId = string.Empty;
    private int sessionDailyQuestsCompleted;
    private int sessionNormalQuestsCompleted;
    private readonly List<string> pendingCompletionLogs = new();
    private readonly HashSet<int> countedDailyQuestInstances = new();
    private readonly HashSet<int> countedNormalQuestInstances = new();
    private readonly Dictionary<string, string> abnormalQuestReasons =
        new(StringComparer.Ordinal);
    private float nextEventCheckTime;
    private bool activeRandomEvent;
    private string activeRandomEventName = string.Empty;
    private string activeRandomEventKey = string.Empty;
    private float activeRandomEventProtectedUntil;
    private float activeRandomEventMissingSince = -1f;
    private bool activeRandomBox;
    private string activeRandomBoxDescription = string.Empty;
    private double observedUltraAscensions = -1d;

    private bool HasMapActivityBlocker =>
        activeRandomEvent || activeRandomBox;

    internal bool SuppressAutomaticRage =>
        !HasMapActivityBlocker &&
        (Time.unscaledTime < postRagePortalBlockedUntil ||
         !string.IsNullOrEmpty(requestedMapId) || lockedQuestSuppressesRage ||
         lockedQuestNeedsCharacterSwitch ||
         !string.IsNullOrEmpty(proactiveCharacterId) ||
         Time.unscaledTime < characterSwitchStableAt);

    internal void PrioritizePostRageScan() => nextScanTime = 0f;

    internal void DeferScanAfterElementAlignment(float readyAt)
    {
        nextScanTime = Math.Max(nextScanTime, readyAt);
        AdventurerLog.QuestDebug(string.IsNullOrEmpty(lockedQuestKey)
            ? "Element alignment completed; current quest lock=None. The next task scan will use the refreshed enemy state."
            : $"Element alignment completed; current quest lock={lockedQuestDisplayName} [{lockedQuestId}]. The element helper did not replace it.");
    }

    internal void Tick(float now, bool enabled)
    {
        if (!enabled) return;

        try
        {
            ObserveUltraAscensionReset();
            UpdateRandomEventState(now);
            if (HasMapActivityBlocker)
            {
                if (!string.IsNullOrEmpty(requestedMapId))
                {
                    string blocker = activeRandomEvent
                        ? activeRandomEventName
                        : activeRandomBoxDescription;
                    if (activeRandomEvent)
                        AdventurerLog.QuestDebug(
                            $"Travel intent released: active map event={blocker}; quest lock retained; automatic Rage suppression released.");
                    ClearPendingTravel();
                }
                return;
            }

            if (!string.IsNullOrEmpty(requestedMapId))
            {
                ObservePendingTravel(now);
                return;
            }

            if (!string.IsNullOrEmpty(proactiveCharacterId) &&
                now >= nextCharacterSwitchCheckAt)
            {
                nextCharacterSwitchCheckAt = now +
                                             CharacterSwitchCheckIntervalSeconds;
                TryCorrectProactiveCharacter(now);
            }

            // Character-selection windows can be much shorter than the main
            // five-second quest scan. Re-resolve and correct a locked
            // character task at frame-scale intervals while Runner is usable.
            if (lockedQuestNeedsCharacterSwitch &&
                now >= nextCharacterSwitchCheckAt)
            {
                nextCharacterSwitchCheckAt = now +
                                             CharacterSwitchCheckIntervalSeconds;
                TryCorrectLockedCharacter(now);
            }

            if (now < nextScanTime) return;
            nextScanTime = now + ScanIntervalSeconds;
            if (!IsSupportedRunnerState() || !IsMapStable()) return;

            QuestTargetSelection selection = SelectOrTrackLockedQuest();
            if (selection == null)
            {
                if (lastQuestSnapshotAvailable &&
                    string.IsNullOrEmpty(lockedQuestKey))
                    TryHandleSoulFallback(now);
                return;
            }

            LogSelection(selection);
            if (!EnsureRequiredCharacter(selection, now)) return;
            BaseMap currentMap = MapController.instance?.selectedMap;
            if (currentMap != null && string.Equals(currentMap.name,
                    selection.MapId, StringComparison.Ordinal)) return;
            if (!MinimumStayElapsed(now)) return;
            if (!CanConsiderPortalTravel())
            {
                AdventurerLog.QuestDebug(
                    $"Quest lock released: quest={selection.QuestDisplayName} [{selection.QuestId}]; " +
                    "reason=Portal is not currently usable for dimension travel; the task will be reconsidered on a later scan.");
                ClearQuestLock();
                nextScanTime = 0f;
                return;
            }

            BeginTravelIntent(selection);

            RageModeManager rage = RageModeManager.instance;
            if (rage != null &&
                rage.currentState == RageModeManager.RageModeStates.Execution)
            {
                pendingTravelObservedRage = true;
                AdventurerLog.User(
                    $"Quest travel waiting: Rage Mode must end naturally; " +
                    $"quest={selection.QuestDisplayName} [{selection.QuestId}]; " +
                    $"targetMap={selection.MapId}.");
                return;
            }

            TryRequestPortal(selection, now);
        }
        catch (Exception exception)
        {
            AdventurerLog.Exception("Quest Automation", exception);
            ClearPendingTravel();
            nextScanTime = now + ScanIntervalSeconds;
        }
    }

    private void ObservePendingTravel(float now)
    {
        if (!IsSupportedRunnerState() || !IsMapStable()) return;

        BaseMap current = MapController.instance?.selectedMap;
        if (current != null && string.Equals(current.name, requestedMapId,
                StringComparison.Ordinal))
        {
            string travelType = requestedForSoulFallback
                ? "Soul fallback travel"
                : "Quest travel";
            AdventurerLog.User(
                $"{travelType} arrived: map={requestedMapId}.");
            lastAutomaticArrivalAt = now;
            ClearPendingTravel();
            discovery.Reset();
            pendingQuestStatusReason = "arrived in target map";
            nextScanTime = now + ScanIntervalSeconds;
            return;
        }

        RageModeManager rage = RageModeManager.instance;
        if (portalRequestedAt <= 0f && rage != null &&
            rage.currentState == RageModeManager.RageModeStates.Execution)
        {
            pendingTravelObservedRage = true;
            return;
        }

        if (portalRequestedAt <= 0f && pendingTravelObservedRage)
        {
            if (pendingTravelPortalReadyAt <= 0f)
            {
                float protectionSeconds = Math.Max(0f,
                    Plugin.Config.PostRageObservationSecondsValue);
                pendingTravelPortalReadyAt = now +
                    protectionSeconds;
                postRagePortalBlockedUntil = Math.Max(
                    postRagePortalBlockedUntil,
                    pendingTravelPortalReadyAt);
                AdventurerLog.QuestDebug(
                    $"Travel intent: Rage Mode ended; protecting the map for {protectionSeconds:0.#} seconds before opening a Portal or allowing another Rage activation.");
            }

            if (now < pendingTravelPortalReadyAt) return;
            pendingTravelObservedRage = false;
            pendingTravelPortalReadyAt = 0f;
        }

        if (portalRequestedAt <= 0f)
        {
            if (requestedForSoulFallback)
            {
                // Soul fallback validation used to call the full quest
                // discovery path every rendered frame while waiting for Rage
                // or Portal readiness. Keep it on the same five-second cadence
                // as ordinary quest scans.
                if (now < nextScanTime) return;
                nextScanTime = now + ScanIntervalSeconds;
                ObserveSoulFallbackIntent(now, current);
                return;
            }

            // A pending quest travel can wait here for Rage to end naturally.
            // Keep live Daily reroll validation, but never rescan every frame.
            if (now < nextScanTime) return;
            nextScanTime = now + ScanIntervalSeconds;

            QuestTargetSelection selection = SelectOrTrackLockedQuest();
            if (!string.Equals(lockedQuestKey, requestedQuestKey,
                    StringComparison.Ordinal))
            {
                AdventurerLog.QuestDebug(
                    "Travel intent: locked quest changed; released the stale travel lock for recalculation.");
                ClearPendingTravel();
                return;
            }

            if (selection == null)
            {
                AdventurerLog.QuestDebug(
                    $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                    "reason=target dimension is no longer reachable through the current Portal list.");
                ClearPendingTravel();
                ClearQuestLock();
                nextScanTime = 0f;
                return;
            }

            if (!EnsureRequiredCharacter(selection, now)) return;

            if (!string.Equals(selection.MapId, requestedMapId,
                    StringComparison.Ordinal))
            {
                AdventurerLog.QuestDebug(
                    "Travel intent: target map changed; released the stale travel lock for recalculation.");
                ClearPendingTravel();
                return;
            }

            if (!CanConsiderPortalTravel())
            {
                AdventurerLog.QuestDebug(
                    $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                    "reason=Portal became unavailable before it could be opened.");
                ClearPendingTravel();
                ClearQuestLock();
                nextScanTime = 0f;
                return;
            }

            TryRequestPortal(selection, now);
            return;
        }

        if (now - portalRequestedAt >= PortalRequestTimeoutSeconds)
        {
            string travelType = requestedForSoulFallback
                ? "Soul fallback travel"
                : "Quest travel";
            AdventurerLog.Warning(
                $"{travelType} to {requestedMapId} timed out; the request was released safely.");
            ClearPendingTravel();
        }
    }

    private QuestTargetSelection SelectOrTrackLockedQuest()
    {
        List<Quest> quests = discovery.SnapshotActiveIncomplete(lockedQuestKey);
        lastQuestSnapshotAvailable = discovery.LastSnapshotAvailable;
        if (!lastQuestSnapshotAvailable) return null;
        MaintainProactiveCharacter(quests, Time.unscaledTime);

        // DailyQuest components are recycled when the active set rerolls.
        // Their IL2CPP instance IDs therefore identify a slot, not a unique
        // quest occurrence. A new active set begins a fresh Daily dedupe
        // generation while normal quest instances remain session-stable.
        if (discovery.ActiveDailySetChanged)
        {
            countedDailyQuestInstances.Clear();
            AdventurerLog.QuestDebug(
                "Quest statistics: active Daily set changed; Daily completion dedupe generation reset.");
        }

        if (!string.IsNullOrEmpty(lockedQuestKey))
        {
            double watchdogMinutes = Math.Max(0d,
                Plugin.Config.MaximumQuestTimeMinutesValue);
            if (watchdogMinutes > 0d && lockedQuestStartedAt > 0f &&
                Time.unscaledTime - lockedQuestStartedAt >=
                watchdogMinutes * 60f)
            {
                Quest expired = FindQuest(quests, lockedQuestKey);
                if (expired != null)
                {
                    string expiredKey = lockedQuestKey;
                    var alternatives = new List<Quest>();
                    foreach (Quest quest in quests)
                    {
                        if (quest != null && !IsAbnormalQuest(quest) &&
                            !string.Equals(
                                QuestTargetSelection.BuildLockKey(quest),
                                expiredKey, StringComparison.Ordinal))
                            alternatives.Add(quest);
                    }

                    QuestTargetSelection alternative =
                        resolver.Select(alternatives);
                    if (alternative != null)
                    {
                        AdventurerLog.QuestDebug(
                            $"Quest maximum time reached: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                            $"elapsedLimit={watchdogMinutes:0.##} minute(s); switching to another executable quest={alternative.QuestDisplayName} [{alternative.QuestId}].");
                        ClearPendingTravel();
                        ClearQuestLock();
                        // Keep the expired task out of this selection pass so
                        // ranking cannot immediately choose it again.
                        quests = alternatives;
                    }
                    else
                    {
                        QuestTargetSelection validation =
                            resolver.ResolveLocked(expired,
                                lockedTargetMapId);
                        if (validation != null)
                        {
                            double currentProgress = Math.Max(0d,
                                expired.questCurrentGoal);
                            AdventurerLog.QuestDebug(
                                $"Quest maximum time reached: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                                "no other executable quest is available; target validation passed, so execution will continue with a fresh timer.");
                            lockedQuestStartedAt = Time.unscaledTime;
                            lockedQuestProgressBaseline = currentProgress;
                            nextLockedQuestProgressCheckAt =
                                Time.unscaledTime +
                                StalledProgressCheckSeconds;
                        }
                        else
                        {
                            QuarantineQuest(expiredKey,
                                lockedQuestDisplayName, lockedQuestId,
                                "maximum time reached and target validation failed");
                            AdventurerLog.QuestDebug(
                                $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                                $"reason=maximum time reached and target validation failed after {watchdogMinutes:0.##} minute(s); a full rescan will be made.");
                            ClearPendingTravel();
                            ClearQuestLock();
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(lockedQuestKey))
        {
            Quest locked = FindQuest(quests, lockedQuestKey);
            if (locked == null)
            {
                bool completed = discovery.WatchedQuestCompleted;
                string releaseReason = completed
                    ? "completed"
                    : "removed from the active list";
                AdventurerLog.QuestDebug(
                    $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; reason={releaseReason}; rageSuppression={lockedQuestSuppressesRage}.");
                if (completed)
                    RecordCompletedQuest(
                        lockedQuestInstanceId, lockedQuestIsDaily,
                        lockedQuestDisplayName, lockedQuestId);
                ClearQuestLock();
            }
            else
            {
                lockedQuestSuppressesRage =
                    QuestCompat.Kind(locked) == LegacyQuestType.KillEnemiesWithArrows;
                QuestTargetSelection tracked = resolver.ResolveLocked(
                    locked, lockedTargetMapId);
                lockedQuestNeedsCharacterSwitch =
                    characters.RequiresSwitch(locked);
                lockedRequiredCharacterId =
                    locked.characterRequired?.name ?? string.Empty;
                if (Time.unscaledTime >= nextLockedQuestProgressCheckAt)
                {
                    double currentProgress = Math.Max(0d,
                        locked.questCurrentGoal);
                    if (currentProgress <= lockedQuestProgressBaseline)
                    {
                        QuarantineQuest(lockedQuestKey,
                            lockedQuestDisplayName, lockedQuestId,
                            $"progress did not increase for {StalledProgressCheckSeconds / 60f:0.#} minutes at {currentProgress:0.##}");
                        AdventurerLog.QuestDebug(
                            $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                            $"reason=progress did not increase during the last {StalledProgressCheckSeconds / 60f:0.#} minutes " +
                            $"(progress={currentProgress:0.##}); a full rescan will be made.");
                        ClearPendingTravel();
                        ClearQuestLock();
                    }
                    else
                    {
                        AdventurerLog.QuestDebug(
                            $"Quest watchdog healthy: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                            $"progress={lockedQuestProgressBaseline:0.##}->{currentProgress:0.##}; next check in 5 minutes.");
                        lockedQuestProgressBaseline = currentProgress;
                        nextLockedQuestProgressCheckAt =
                            Time.unscaledTime + StalledProgressCheckSeconds;
                    }
                }

                if (string.IsNullOrEmpty(lockedQuestKey))
                {
                    // The stalled-progress watchdog released this lock. Fall
                    // through and select again from the fresh snapshot.
                }
                else if (tracked == null)
                {
                    AdventurerLog.QuestDebug(
                        $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                        "reason=no currently reachable target dimension; selecting another available task.");
                    ClearPendingTravel();
                    ClearQuestLock();
                }
                else
                {
                if (tracked != null && !string.Equals(tracked.MapId,
                        lockedTargetMapId, StringComparison.Ordinal))
                {
                    AdventurerLog.QuestDebug(
                        $"Quest target map changed: quest={lockedQuestDisplayName} [{lockedQuestId}]; " +
                        $"previousMap={lockedTargetMapId}; newMap={tracked.MapId}; " +
                        "reason=previous map no longer has a valid unlocked target.");
                    lockedTargetMapId = tracked.MapId;
                }
                FlushPendingCompletionLogs(tracked);
                return tracked;
                }
            }
        }

        QuestTargetSelection selection = resolver.Select(
            ExcludeAbnormalQuests(quests), CanConsiderPortalTravel());
        if (selection == null)
        {
            FlushPendingCompletionLogs(null);
            return null;
        }

        lockedQuestId = selection.QuestId;
        lockedQuestKey = selection.LockKey;
        lockedQuestDisplayName = selection.QuestDisplayName;
        lockedQuestInstanceId = selection.Quest?.GetInstanceID() ?? 0;
        lockedTargetMapId = selection.MapId;
        lockedRequiredCharacterId = selection.RequiredCharacterId;
        lockedQuestIsDaily = IsDailyQuest(selection.Quest);
        lockedQuestSuppressesRage = selection.SuppressesAutomaticRage;
        lockedQuestNeedsCharacterSwitch = characters.RequiresSwitch(selection);
        lockedQuestStartedAt = Time.unscaledTime;
        lockedQuestProgressBaseline = Math.Max(0d,
            selection.CurrentKills);
        nextLockedQuestProgressCheckAt = Time.unscaledTime +
                                         StalledProgressCheckSeconds;
        lastSoulFallbackMapId = string.Empty;
        FlushPendingCompletionLogs(selection);
        return selection;
    }

    private static Quest FindQuest(List<Quest> quests, string lockKey)
    {
        foreach (Quest quest in quests)
        {
            if (quest != null && string.Equals(
                    QuestTargetSelection.BuildLockKey(quest), lockKey,
                    StringComparison.Ordinal))
                return quest;
        }

        return null;
    }

    private bool IsAbnormalQuest(Quest quest) =>
        quest != null && abnormalQuestReasons.ContainsKey(
            QuestTargetSelection.BuildLockKey(quest));

    private List<Quest> ExcludeAbnormalQuests(List<Quest> quests)
    {
        if (abnormalQuestReasons.Count == 0) return quests;

        var eligible = new List<Quest>(quests.Count);
        foreach (Quest quest in quests)
        {
            if (quest != null && !IsAbnormalQuest(quest))
                eligible.Add(quest);
        }
        return eligible;
    }

    private void QuarantineQuest(string lockKey, string displayName,
        string questId, string reason)
    {
        if (string.IsNullOrEmpty(lockKey) ||
            abnormalQuestReasons.ContainsKey(lockKey)) return;

        string description =
            $"quest={displayName} [{questId}]; reason={reason}";
        abnormalQuestReasons.Add(lockKey, description);
        AdventurerLog.QuestDebug(
            $"Quest added to the P-session abnormal list: {description}; " +
            $"excludedCount={abnormalQuestReasons.Count}. It will remain excluded until P is disabled.");
    }

    private void RecordCompletedQuest(int instanceId, bool isDaily,
        string displayName, string questId)
    {
        if (instanceId != 0)
        {
            HashSet<int> counted = isDaily
                ? countedDailyQuestInstances
                : countedNormalQuestInstances;
            if (!counted.Add(instanceId)) return;
        }

        if (isDaily)
            sessionDailyQuestsCompleted++;
        else
            sessionNormalQuestsCompleted++;

        int total = sessionDailyQuestsCompleted + sessionNormalQuestsCompleted;
        string questLabel = string.IsNullOrWhiteSpace(displayName)
            ? questId
            : displayName;
        pendingCompletionLogs.Add(
            $"Quest completed: {questLabel} [{questId}]. Session total: {total} " +
            $"(Daily: {sessionDailyQuestsCompleted}, Normal: {sessionNormalQuestsCompleted}).");
        if (Plugin.Config.QuestCompletionNotifications.Value)
            Plugin.ModHelperInstance?.ShowNotification(
                $"Quests completed: {total}", true);
    }

    private void FlushPendingCompletionLogs(
        QuestTargetSelection currentSelection,
        string noTaskStatus = "Current task: none; rescanning.")
    {
        if (pendingCompletionLogs.Count == 0) return;

        string currentTask = currentSelection == null
            ? noTaskStatus
            : $"Current task: {currentSelection.QuestDisplayName} " +
              $"[{currentSelection.QuestId}].";
        foreach (string completion in pendingCompletionLogs)
            AdventurerLog.User($"{completion} {currentTask}");
        pendingCompletionLogs.Clear();
    }

    private static bool IsDailyQuest(Quest quest)
    {
        if (quest is DailyQuest) return true;
        string runtimeType = quest?.GetIl2CppType()?.Name ?? string.Empty;
        return string.Equals(runtimeType, nameof(DailyQuest),
            StringComparison.Ordinal);
    }

    internal void ObserveClaimedQuest(ClaimedQuestEvent claimed, bool enabled)
    {
        if (!enabled || claimed.IsWeekly) return;

        // Count every normal or Daily quest that is actually claimed while
        // this P session is active. This also covers Claim calls made by
        // another mod; the bridge suppresses duplicate Harmony callbacks.
        RecordCompletedQuest(claimed.InstanceId, claimed.IsDaily,
            claimed.QuestDisplayName, claimed.QuestId);

        if (string.IsNullOrEmpty(lockedQuestKey) ||
            !string.Equals(lockedQuestKey, claimed.LockKey,
                StringComparison.Ordinal)) return;

        AdventurerLog.QuestDebug(
            $"Quest lock released: quest={lockedQuestDisplayName} [{lockedQuestId}]; reason=claimed after completion; rageSuppression={lockedQuestSuppressesRage}.");
        ClearQuestLock();
        ClearPendingTravel();
        nextScanTime = 0f;
    }

    private bool EnsureRequiredCharacter(
        QuestTargetSelection selection, float now)
    {
        if (selection?.RequiredCharacter == null)
        {
            lockedQuestNeedsCharacterSwitch = false;
            return now >= characterSwitchStableAt;
        }

        lockedQuestNeedsCharacterSwitch = characters.RequiresSwitch(selection);
        if (!lockedQuestNeedsCharacterSwitch)
            return now >= characterSwitchStableAt;

        if (GameState.current != GameStates.RunnerMode)
        {
            if (GameState.current == GameStates.RageMode && !string.Equals(
                    characterWaitLoggedForQuestKey, selection.LockKey,
                    StringComparison.Ordinal))
            {
                characterWaitLoggedForQuestKey = selection.LockKey;
                AdventurerLog.QuestDebug(
                    $"Character switch pending: quest={selection.QuestDisplayName} [{selection.QuestId}]; " +
                    $"requiredCharacter={selection.RequiredCharacterId}; waiting for Rage Mode to end naturally.");
            }
            return false;
        }

        if (!IsMapStable())
            return false;
        if (!characters.TryApply(selection)) return false;

        lockedQuestNeedsCharacterSwitch = false;
        // Character correction starts a fresh, real observation window. Zero
        // would make the next five-second quest scan look like a completed
        // five-minute watchdog interval and quarantine the task immediately.
        lockedQuestStartedAt = now;
        lockedQuestProgressBaseline = Math.Max(0d,
            selection.CurrentKills);
        nextLockedQuestProgressCheckAt = now +
                                         StalledProgressCheckSeconds;
        characterWaitLoggedForQuestKey = string.Empty;
        characterSwitchStableAt = now + CharacterSwitchStabilitySeconds;
        return false;
    }

    private void MaintainProactiveCharacter(List<Quest> quests, float now)
    {
        // Once a character-specific task is formally locked, its own
        // verification is authoritative. This prevents two character tasks
        // from switching the player back and forth on every scan.
        Quest locked = string.IsNullOrEmpty(lockedQuestKey)
            ? null
            : FindQuest(quests, lockedQuestKey);
        if (locked?.characterRequired != null)
        {
            ClearProactiveCharacter();
            return;
        }

        Quest first = null;
        foreach (Quest quest in quests)
        {
            CharacterSkin required = quest?.characterRequired;
            if (required == null || !required.unlocked) continue;
            first = quest;
            break;
        }

        if (first == null || !characters.RequiresSwitch(first))
        {
            ClearProactiveCharacter();
            return;
        }

        proactiveCharacterId = first.characterRequired.name ?? string.Empty;
        proactiveCharacterQuestId = first.name ?? "UnknownQuest";

        if (GameState.current != GameStates.RunnerMode || !IsMapStable())
            return;

        if (!characters.TryApply(new QuestTargetSelection { Quest = first }))
            return;

        AdventurerLog.QuestDebug(
            $"Character pre-alignment completed: quest=" +
            $"{(string.IsNullOrWhiteSpace(first.localizedName) ? proactiveCharacterQuestId : LogText.Normalize(first.localizedName))} " +
            $"[{proactiveCharacterQuestId}]; " +
            "reason=first active character-specific task in the quest scan.");
        ClearProactiveCharacter();
        characterSwitchStableAt = now + CharacterSwitchStabilitySeconds;
    }

    private void TryCorrectProactiveCharacter(float now)
    {
        if (GameState.current != GameStates.RunnerMode || !IsMapStable())
            return;

        string characterId = proactiveCharacterId;
        string questId = proactiveCharacterQuestId;
        if (!characters.TryApply(characterId, questId)) return;

        AdventurerLog.QuestDebug(
            $"Character pre-alignment completed: quest={questId}; " +
            "reason=first active character-specific task in the quest scan.");
        ClearProactiveCharacter();
        characterSwitchStableAt = now + CharacterSwitchStabilitySeconds;
        nextScanTime = 0f;
    }

    private void ClearProactiveCharacter()
    {
        proactiveCharacterId = string.Empty;
        proactiveCharacterQuestId = string.Empty;
    }

    private void TryCorrectLockedCharacter(float now)
    {
        if (GameState.current != GameStates.RunnerMode || !IsMapStable() ||
            string.IsNullOrEmpty(lockedQuestKey) ||
            string.IsNullOrEmpty(lockedRequiredCharacterId)) return;

        if (!characters.TryApply(lockedRequiredCharacterId,
                lockedQuestId)) return;

        lockedQuestNeedsCharacterSwitch = false;
        characterWaitLoggedForQuestKey = string.Empty;
        characterSwitchStableAt = now + CharacterSwitchStabilitySeconds;
        nextScanTime = 0f;
    }

    private void BeginTravelIntent(QuestTargetSelection selection)
    {
        requestedForSoulFallback = false;
        requestedQuestId = selection.QuestId;
        requestedQuestKey = selection.LockKey;
        requestedMapId = selection.MapId;
        portalRequestedAt = 0f;
        pendingTravelObservedRage = false;
        pendingTravelPortalReadyAt = 0f;
        AdventurerLog.QuestDebug(
            $"Travel lock acquired: quest={selection.QuestDisplayName} [{requestedQuestId}]; targetMap={requestedMapId}; automaticRageSuppressed=true.");
    }

    private void TryRequestPortal(QuestTargetSelection selection, float now)
    {
        if (now < postRagePortalBlockedUntil) return;
        PortalButton portal = PortalButton.instance;
        if (!CanClickPortalButton(portal)) return;
        if (interruptions.TryGetBlocker(out _)) return;
        if (selection.Map == null || !selection.Map.IsAvailable()) return;

        portalRequestedAt = now;
        portal.SpawnPortal(selection.Map, false);
        AdventurerLog.User(
            $"Quest portal opened: quest={selection.QuestDisplayName} " +
            $"[{selection.QuestId}]; target={selection.EnemyId}; " +
            $"map={selection.MapId}.");
    }

    private void TryHandleSoulFallback(float now)
    {
        SoulFarmingSelection selection = soulFarming.SelectBestAvailable();
        if (selection == null) return;

        LogSoulFallbackSelection(selection);
        if (string.Equals(selection.CurrentMapId, selection.MapId,
                StringComparison.Ordinal)) return;
        if (!MinimumStayElapsed(now)) return;
        if (!CanPreparePortalTravel()) return;

        BeginSoulFallbackTravelIntent(selection);

        RageModeManager rage = RageModeManager.instance;
        if (rage != null &&
            rage.currentState == RageModeManager.RageModeStates.Execution)
        {
            pendingTravelObservedRage = true;
            AdventurerLog.QuestDebug(
                $"Soul fallback: waiting for Rage Mode to end naturally before travelling to {selection.MapId}.");
            return;
        }

        TryRequestSoulPortal(selection, now);
    }

    private void ObserveSoulFallbackIntent(float now, BaseMap currentMap)
    {
        QuestTargetSelection questSelection = SelectOrTrackLockedQuest();
        if (questSelection != null)
        {
            AdventurerLog.QuestDebug(
                $"Soul fallback: executable quest became available; cancelled travel to {requestedMapId}.");
            ClearPendingTravel();
            nextScanTime = now;
            return;
        }
        if (!lastQuestSnapshotAvailable || !string.IsNullOrEmpty(lockedQuestKey))
            return;

        SoulFarmingSelection selection = soulFarming.SelectBestAvailable();
        if (selection == null) return;
        if (currentMap != null && string.Equals(currentMap.name,
                selection.MapId, StringComparison.Ordinal))
        {
            AdventurerLog.QuestDebug(
                $"Soul fallback: current map is now optimal; cancelled travel to {requestedMapId}.");
            ClearPendingTravel();
            return;
        }
        if (!string.Equals(selection.MapId, requestedMapId,
                StringComparison.Ordinal))
        {
            AdventurerLog.QuestDebug(
                $"Soul fallback: best map changed from {requestedMapId} to {selection.MapId}; released the stale travel lock.");
            ClearPendingTravel();
            nextScanTime = now;
            return;
        }

        TryRequestSoulPortal(selection, now);
    }

    private void BeginSoulFallbackTravelIntent(SoulFarmingSelection selection)
    {
        requestedForSoulFallback = true;
        requestedQuestId = string.Empty;
        requestedQuestKey = string.Empty;
        requestedMapId = selection.MapId;
        portalRequestedAt = 0f;
        pendingTravelObservedRage = false;
        pendingTravelPortalReadyAt = 0f;
        AdventurerLog.QuestDebug(
            $"Soul fallback travel lock acquired: targetMap={selection.MapId}; soulScore={selection.SoulScore:0.###}; automaticRageSuppressed=true.");
    }

    private void TryRequestSoulPortal(SoulFarmingSelection selection, float now)
    {
        if (now < postRagePortalBlockedUntil) return;
        PortalButton portal = PortalButton.instance;
        if (!CanClickPortalButton(portal)) return;
        if (interruptions.TryGetBlocker(out _)) return;
        if (selection.Map == null || !selection.Map.IsAvailable()) return;

        portalRequestedAt = now;
        portal.SpawnPortal(selection.Map, false);
        AdventurerLog.User(
            $"Soul-farming portal opened: map={selection.MapId}; " +
            $"soulScore={selection.SoulScore:0.###}.");
    }

    private void LogSoulFallbackSelection(SoulFarmingSelection selection)
    {
        if (string.Equals(selection.MapId, lastSoulFallbackMapId,
                StringComparison.Ordinal)) return;

        lastSoulFallbackMapId = selection.MapId;
        AdventurerLog.QuestDebug(
            $"Soul fallback selected: no executable kill quest; bestMap={selection.MapId}; " +
            $"bestSoulScore={selection.SoulScore:0.###}; currentMap={selection.CurrentMapId}; " +
            $"currentSoulScore={selection.CurrentSoulScore:0.###}.");
    }

    private bool CanPreparePortalTravel()
    {
        PortalButton portal = PortalButton.instance;
        return Time.unscaledTime >= postRagePortalBlockedUntil &&
               portal != null && portal.currentCd <= 0d &&
               !HasMapActivityBlocker &&
               !interruptions.TryGetBlocker(out _);
    }

    private bool CanConsiderPortalTravel()
    {
        PortalButton portal = PortalButton.instance;
        if (portal == null || portal.currentCd > 0d) return false;

        // Rage temporarily hides/disables the button even though the Portal
        // system itself is ready. That state is allowed to wait for a natural
        // Rage ending; Runner must expose a genuinely clickable Portal.
        return GameState.current == GameStates.RageMode ||
               CanClickPortalButton(portal);
    }

    private void UpdateRandomEventState(float now)
    {
        if (now < nextEventCheckTime) return;
        nextEventCheckTime = now + EventCheckIntervalSeconds;

        bool wasActive = activeRandomEvent;
        string previousName = activeRandomEventName;
        string previousKey = activeRandomEventKey;
        bool boxWasActive = activeRandomBox;
        string previousBox = activeRandomBoxDescription;
        bool eventObserved = interruptions.TryGetActiveRandomEvent(
            out string eventName, out double eventRemainingSeconds);
        if (eventObserved)
        {
            activeRandomEvent = true;
            activeRandomEventName = eventName;
            activeRandomEventKey = GetStableEventKey(eventName);
            activeRandomEventMissingSince = -1f;
            activeRandomEventProtectedUntil = Math.Max(
                activeRandomEventProtectedUntil,
                now + (float)eventRemainingSeconds + EventTimerGraceSeconds);
        }
        else if (wasActive)
        {
            // RandomEvent components can briefly disappear from Unity's
            // active-object scan while their map content is still running.
            // Keep the event latched through its last observed timer, then
            // require consecutive missing observations before declaring it
            // finished. A transient lookup gap must never open a Portal.
            if (now < activeRandomEventProtectedUntil)
            {
                activeRandomEvent = true;
            }
            else
            {
                if (activeRandomEventMissingSince < 0f)
                    activeRandomEventMissingSince = now;
                activeRandomEvent =
                    now - activeRandomEventMissingSince <
                    EventEndConfirmationSeconds;
            }
        }
        else
        {
            activeRandomEvent = false;
            activeRandomEventName = string.Empty;
            activeRandomEventKey = string.Empty;
            activeRandomEventProtectedUntil = 0f;
            activeRandomEventMissingSince = -1f;
        }
        activeRandomBox =
            interruptions.TryGetActiveRandomBox(out string boxDescription);
        activeRandomBoxDescription = activeRandomBox
            ? boxDescription
            : string.Empty;

        if (activeRandomEvent && (!wasActive || !string.Equals(previousKey,
                activeRandomEventKey, StringComparison.Ordinal)))
        {
            AdventurerLog.QuestDebug(
                $"Map event detected: event={activeRandomEventName}; dimension travel paused; automatic Rage suppression released.");
        }
        else if (!activeRandomEvent && wasActive)
        {
            AdventurerLog.QuestDebug(
                $"Map event ended: event={previousName}; quest and Portal conditions will be re-evaluated.");
            lastSelection = string.Empty;
            pendingQuestStatusReason = $"map event ended ({previousName})";
            nextScanTime = 0f;
            activeRandomEventName = string.Empty;
            activeRandomEventKey = string.Empty;
            activeRandomEventProtectedUntil = 0f;
            activeRandomEventMissingSince = -1f;
        }

        if (!activeRandomBox && boxWasActive && !activeRandomEvent)
        {
            lastSelection = string.Empty;
            nextScanTime = 0f;
        }
    }

    private static string GetStableEventKey(string eventName)
    {
        // The diagnostic description contains a decreasing timeLeft value.
        // It must not be part of event identity or every poll looks new.
        int timerIndex = eventName.IndexOf(", timeLeft=",
            StringComparison.Ordinal);
        return timerIndex >= 0 ? eventName[..timerIndex] : eventName;
    }

    private static bool CanClickPortalButton(PortalButton portal)
    {
        if (portal == null || portal.currentCd > 0d ||
            !portal.isActiveAndEnabled || portal.gameObject == null ||
            !portal.gameObject.activeInHierarchy)
            return false;

        Button button = portal.GetComponent<Button>();
        return button != null && button.isActiveAndEnabled &&
               button.gameObject.activeInHierarchy && button.interactable;
    }

    private void LogSelection(QuestTargetSelection selection)
    {
        string key = $"{selection.LockKey}|{selection.EnemyId}|{selection.MapId}";
        bool changed = !string.Equals(key, lastSelection,
            StringComparison.Ordinal);
        bool resumed = !string.IsNullOrEmpty(pendingQuestStatusReason);
        bool periodic = Time.unscaledTime >= nextQuestStatusLogTime;
        if (!changed && !resumed && !periodic) return;

        lastSelection = key;
        string currentMap = MapController.instance?.selectedMap?.name ??
                            "UnknownMap";
        string mapState = string.Equals(currentMap, selection.MapId,
            StringComparison.Ordinal)
            ? "on-target-map"
            : "travel-required";
        string heading = resumed
            ? $"Current quest lock resumed ({pendingQuestStatusReason})"
            : changed
                ? "Current quest lock acquired"
                : "Current quest lock progress";
        pendingQuestStatusReason = string.Empty;
        nextQuestStatusLogTime = Time.unscaledTime + 60f;
        AdventurerLog.QuestDebug(
            $"{heading}: quest={selection.QuestDisplayName} [{selection.QuestId}]; " +
            $"questType={selection.QuestTypeId}; objective={selection.ObjectiveId}; " +
            $"matchedEnemy={selection.EnemyId}; currentMap={currentMap}; " +
            $"targetMap={selection.MapId}; mapState={mapState}; " +
            $"progress={selection.CurrentKills:0.##}/{selection.RequiredKills:0.##}; " +
            $"remainingKills={selection.RemainingKills:0.##}; " +
            $"unlockReward={(selection.HasUnlockReward ? $"{selection.UnlockRewardDisplayName} [{selection.UnlockRewardId}]" : "None")}; " +
            $"rewardBenefit={(!string.IsNullOrEmpty(selection.UnlockRewardBenefitId) ? selection.UnlockRewardBenefitId : "None")}; " +
            $"rewardPriority={selection.RewardPriorityLabel}; " +
            $"requiredCharacter={selection.RequiredCharacterId}; " +
            $"automaticRageSuppressed={lockedQuestSuppressesRage || lockedQuestNeedsCharacterSwitch}.");
    }

    private bool MinimumStayElapsed(float now)
    {
        if (lastAutomaticArrivalAt < 0f) return true;
        double seconds = Math.Max(0d,
            Plugin.Config.MinimumDimensionStayMinutesValue) * 60f;
        return now - lastAutomaticArrivalAt >= seconds;
    }

    private static bool IsSupportedRunnerState() =>
        GameState.current is GameStates.RunnerMode or GameStates.RageMode;

    private static bool IsMapStable()
    {
        MapController maps = MapController.instance;
        return maps != null && maps.initialized && !maps.changingMap;
    }

    private void ObserveUltraAscensionReset()
    {
        PlayerInventory inventory = PlayerInventory.instance;
        if (inventory == null) return;
        double current = Math.Max(0d, inventory.ultraAscensions);
        if (observedUltraAscensions < 0d)
        {
            observedUltraAscensions = current;
            return;
        }
        if (current <= observedUltraAscensions) return;

        observedUltraAscensions = current;
        // Preserve the P-session completion totals, but discard every cached
        // quest/travel/object decision from the pre-UA progression state.
        ClearQuestLock();
        ClearPendingTravel();
        discovery.Reset();
        soulFarming.Reset();
        abnormalQuestReasons.Clear();
        countedDailyQuestInstances.Clear();
        countedNormalQuestInstances.Clear();
        lastQuestSnapshotAvailable = false;
        lastSoulFallbackMapId = string.Empty;
        lastAutomaticArrivalAt = -1f;
        nextScanTime = 0f;
        nextEventCheckTime = 0f;
        activeRandomEvent = false;
        activeRandomEventName = string.Empty;
        activeRandomEventKey = string.Empty;
        activeRandomEventProtectedUntil = 0f;
        activeRandomEventMissingSince = -1f;
        activeRandomBox = false;
        activeRandomBoxDescription = string.Empty;
        postRagePortalBlockedUntil = 0f;
        AdventurerLog.QuestDebug(
            $"Ultra Ascension detected: count={current:0}; all quest, travel, scene-object, abnormal-task, and temporary Divinity caches were reset.");
    }

    private void ClearPendingTravel()
    {
        requestedForSoulFallback = false;
        requestedMapId = string.Empty;
        requestedQuestId = string.Empty;
        requestedQuestKey = string.Empty;
        portalRequestedAt = 0f;
        pendingTravelObservedRage = false;
        pendingTravelPortalReadyAt = 0f;
    }

    private void ClearQuestLock()
    {
        lockedQuestId = string.Empty;
        lockedQuestKey = string.Empty;
        lockedQuestDisplayName = string.Empty;
        lockedQuestInstanceId = 0;
        lockedTargetMapId = string.Empty;
        lockedRequiredCharacterId = string.Empty;
        lockedQuestIsDaily = false;
        lockedQuestSuppressesRage = false;
        lockedQuestNeedsCharacterSwitch = false;
        characterSwitchStableAt = 0f;
        nextCharacterSwitchCheckAt = 0f;
        characterWaitLoggedForQuestKey = string.Empty;
        ClearProactiveCharacter();
        lastSelection = string.Empty;
        pendingQuestStatusReason = string.Empty;
        nextQuestStatusLogTime = 0f;
    }

    internal void Reset()
    {
        nextScanTime = 0f;
        lastAutomaticArrivalAt = -1f;
        lastQuestSnapshotAvailable = false;
        lastSoulFallbackMapId = string.Empty;
        nextEventCheckTime = 0f;
        activeRandomEvent = false;
        activeRandomEventName = string.Empty;
        activeRandomEventKey = string.Empty;
        activeRandomEventProtectedUntil = 0f;
        activeRandomEventMissingSince = -1f;
        activeRandomBox = false;
        activeRandomBoxDescription = string.Empty;
        postRagePortalBlockedUntil = 0f;
        observedUltraAscensions = -1d;
        ClearQuestLock();
        ClearPendingTravel();
        discovery.Reset();
        soulFarming.Reset();
    }

    internal void BeginSession()
    {
        sessionDailyQuestsCompleted = 0;
        sessionNormalQuestsCompleted = 0;
        pendingCompletionLogs.Clear();
        countedDailyQuestInstances.Clear();
        countedNormalQuestInstances.Clear();
        abnormalQuestReasons.Clear();
        Reset();
        AdventurerLog.QuestDebug(
            "Quest completion session started; counters reset to zero.");
    }

    internal void EndSession()
    {
        FlushPendingCompletionLogs(
            null, "Current task: none; Quest Automation disabled.");
        int total = sessionDailyQuestsCompleted +
                    sessionNormalQuestsCompleted;
        string message =
            $"Quest Automation session ended: completed={total} " +
            $"(Daily: {sessionDailyQuestsCompleted}, Normal: {sessionNormalQuestsCompleted}).";
        AdventurerLog.User(message);
        if (Plugin.Config.QuestCompletionNotifications.Value)
            Plugin.ModHelperInstance?.ShowNotification(
                $"Session quests completed: {total}", false);

        if (abnormalQuestReasons.Count > 0)
        {
            AdventurerLog.QuestDebug(
                $"P-session abnormal quest summary: count={abnormalQuestReasons.Count}.");
            foreach (string description in abnormalQuestReasons.Values)
                AdventurerLog.QuestDebug(
                    $"P-session abnormal quest: {description}.");
        }
        abnormalQuestReasons.Clear();
    }

    internal void PauseForSceneTransition()
    {
        discovery.InvalidateSceneObjects();
        soulFarming.InvalidateSceneObjects();

        // If no automation Portal was spawned, another transition (Bonus
        // Stage, village, boss, etc.) interrupted preparation. Preserve any
        // task lock but release the travel lock and recalculate on return.
        if (!string.IsNullOrEmpty(requestedMapId) && portalRequestedAt <= 0f)
        {
            AdventurerLog.QuestDebug(
                "Travel intent interrupted by another scene; retained the quest lock and released only the travel lock for recalculation after return.");
            ClearPendingTravel();
        }
    }
}
