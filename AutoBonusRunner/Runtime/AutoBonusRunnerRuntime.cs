using AutoBonusRunner.Control;
using AutoBonusRunner.Detection;
using AutoBonusRunner.Diagnostics;
using AutoBonusRunner.Physics;
using AutoBonusRunner.Routing;
using Il2Cpp;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace AutoBonusRunner.Runtime;

internal enum WallActionPhase
{
    None,
    EnteringTrench,
    ApproachJumpInFlight,
    AwaitingWallContact,
    WallJumpPhaseOne,
    AwaitingNextWallPress,
    WallJumpPhaseTwo,
    AttachedObjectiveDescent,
    ExitFlight,
    Completed,
    Failed
}

public sealed class AutoBonusRunnerRuntime : MonoBehaviour
{
    private static AutoBonusRunnerRuntime activeRuntime;
    private const bool AutomaticJumpingEnabled = true;
    private const bool CompletionRewardActionsEnabled = true;
    private const bool CompletionWindDashEnabled = true;
    private const float DiagnosticIntervalSeconds = 0.35f;
    // BonusSphere objects are already cached and section-scoped. Supplying
    // the whole active section keeps the route-objective signature stable as
    // the camera/player advances; individual souls no longer enter a sliding
    // 30-unit window one at a time and force repeated full V0.64 proofs.
    // Every planner still filters this inventory to its actual source,
    // target, or trajectory corridor before assigning pickup value.
    private const float SectionObjectiveHorizon = 512f;
    private const float WallStallConfirmationSeconds = 0.01f;
    private const float FallbackWallRecoveryHoldSeconds = 0.115f;
    // Successful manual wall presses in the captured runs were roughly
    // 0.118-0.160s. A forced 0.180s press climbed far above the lip and
    // landed 6+ units beyond the planned tower, so wall recovery has its own
    // demonstrated range rather than blindly using the normal-jump cap.
    private const float MinimumWallRecoveryHoldSeconds = 0.115f;
    private const float MaximumWallRecoveryHoldSeconds = 0.165f;
    // Landing-targeted wall presses need the full native range. A short press
    // can clear a low lip and settle on its top, while a longer press can
    // transfer directly to the following platform. The narrower recovery
    // range above remains the fallback for a genuinely multi-bounce climb.
    private const float MinimumWallLandingHoldSeconds = 0.020f;
    private const float MaximumWallLandingHoldSeconds = 0.180f;
    private static readonly float[] WallEnvelopeHoldCandidates =
    {
        0.02f, 0.04f, 0.06f, 0.08f, 0.10f,
        0.12f, 0.14f, 0.16f, 0.18f
    };
    // A two-unit wall top cannot retain a normal released wall jump: as soon
    // as the body clears the lip, horizontal speed resumes and carries it off
    // the far edge.  When neither the wall top nor the downstream support is
    // reachable by the first press, use the shortest hold supported by the
    // captured successful wall presses and solve the real exit from the next
    // contact. A 0.060s release proved too short to preserve attachment;
    // 0.115s is the lower bound currently supported by manual evidence.
    private const float MinimumAttachedWallPulseHoldSeconds = 0.075f;
    private const float MaximumAttachedWallPulseHoldSeconds = 0.135f;
    // Ground 7's narrow S2 top is followed by a lower finite face roughly
    // ten units away.  A strict top-landing solution is not always available,
    // but retained V0.39 traces prove that a native-cap press reaches that
    // face and lets the normal wall controller finish the route.  Keep the
    // face window bounded so this liveness path cannot become a blind long
    // jump on unrelated geometry.
    private const float CommittedExitFaceDepth = 4.50f;
    private const float CommittedExitFaceBottomClearance = 0.35f;
    private const float CommittedExitFaceTopClearance = 0.25f;
    private const float OwnedTargetFaceLipTolerance = 0.10f;
    private const float CommittedExitFacePreferredDepth = 2.00f;
    private const float CommittedExitFaceMaximumGap = 15.50f;
    // The first Ground 3 face in the latest trace was contacted 0.37 units
    // higher than the later successful face. At that height even the regular
    // 0.075s attached pulse crossed the setup ceiling, so the planner rejected
    // every candidate and deliberately dropped the player. This extra-light
    // pulse is restricted to the mandatory face setup phase; proven attached
    // climb and exit pulses retain their existing lower bound.
    private const float MinimumMandatoryFaceSetupHoldSeconds = 0.060f;
    private const float StagedWallTopMaximumWidth = 4.25f;
    // A released wall pulse is still a live vertical impulse.  The V0.27
    // trace repeatedly re-pressed at VY=+17.253 while only 0.31-0.57 units
    // below the release height, resetting VY to 20 and skipping the next
    // trench.  Manual multi-pulse evidence instead waits through the apex and
    // sends the next DOWN only while descending.  Keep this wait bounded so a
    // frozen/stale velocity sample cannot own control forever.
    private const float WallResidualRiseVelocityThreshold = 0.25f;
    private const float WallResidualRiseSafetyMargin = 0.08f;
    private const float WallResidualRiseMaximumWaitSeconds = 0.45f;
    private const float MandatoryWallFaceSetupSafetyMargin = 0.30f;
    private const float MandatoryWallFaceSetupMaximumWaitSeconds = 1.20f;
    private const float MandatoryWallFaceMinimumContactOffset = 2.50f;
    private const float MandatoryWallFaceMaximumContactTopClearance = 0.55f;
    private const float MandatoryWallFacePreferredContactOffset = 3.30f;
    private const float MandatoryWallFaceHorizontalResumeVelocity = 0.35f;
    private const float Ground3ObjectiveFaceHeight = 5.0f;
    private const float MandatoryWallFacePhysicalBottomClearance = 0.10f;
    private const float MandatoryWallFacePhysicalTopClearance = 0.15f;
    private const float MandatoryWallFaceRayXMatchTolerance = 0.35f;
    private const float MandatoryWallFaceBodyXMatchTolerance = 0.16f;
    private const float MandatoryWallFaceContactWatchSeconds = 1.25f;
    // Keep wall objective height consistent with the planner's trajectory
    // pickup envelope. Player trajectory Y is the feet coordinate; the body
    // plus sphere trigger can collect a sphere up to 2.15 units above it.
    private const float WallSpherePickupAboveFeet = 2.15f;
    // Separation is already enforced by synthetic UP plus a real FixedUpdate
    // barrier. A second 0.20s timer kept the runner attached long enough to
    // miss the legal second press and slide off narrow walls. Retain a tiny
    // post-classification gap without discarding the observed contact window.
    private const float WallRecoveryCooldownSeconds = 0.015f;
    // A demonstrated section-two pillar climb used three distinct light
    // presses (0.132s, 0.132s, 0.090s). The game accepts a pulse train whose
    // count and hold depend on remaining height; cap it generously enough for
    // authored tall walls while still preventing unbounded death-wall spam.
    private const int MaximumWallRecoveriesPerAirborneSequence = 6;
    private const float WallAttachmentVelocityTolerance = 3.0f;
    private const float WallAttachmentPositionTolerance = 0.55f;
    private const float PitDescentYThreshold = -3.50f;
    private const float PitDescentVelocityThreshold = -8.00f;
    private const int PitDescentConfirmationFixedSteps = 2;
    // A passive wall route can outlive the jump sample that delivered the
    // player to its target top. Two distinct, non-rising physics steps on the
    // exact target support are enough to complete that stale route before it
    // suppresses the next terrain launch window.
    private const int PassiveWallTargetLandingConfirmationFixedSteps = 2;
    private const float RecoverableWallProbeDistance = 1.20f;
    // A recoverable edge/corner landing can report Grounded while native
    // collision response has already launched the body upward.  In Spirit
    // Boost section four, waiting until the nominal wall-foot ArmX then loses
    // the verified adjacent-wall route before the next FixedUpdate.  Preserve
    // ownership only when that exact rising pulse is observed and the planned
    // wall face is less than this bounded travel time away.  This contract
    // stores the target; it never sends DOWN before physical wall contact.
    private const float SpiritSection3ReboundMinimumVelocityY = 2.50f;
    private const float SpiritSection3ReboundMaximumWallTravelSeconds = 0.65f;
    private readonly BonusStageDetector detector = new();
    private readonly BonusRewardTargetDetector rewardTargetDetector = new();
    private readonly JumpController jumpController = new();
    private const float CompletionStallSeconds = 3f;
    private const float CompletionStallPositionTolerance = 0.05f;
    private const float CompletionStallFreedDistance = 0.3f;
    private const float CompletionStallMaximumPauseSeconds = 4f;
    private bool completionStallTracking;
    private float completionStallX;
    private float completionStallSince;
    private float completionStallPauseUntil;
    private float completionStallEpisodeStart;
    private int completionStallPauses;
    private readonly CompletionRewardController completionRewardController =
        new();
    private readonly BonusPlatformScanner platformScanner = new();
    private readonly BonusHazardScanner hazardScanner = new();
    private readonly BonusWallDetector wallDetector = new();
    private readonly BonusJumpPlanner jumpPlanner = new();
    private readonly JumpPhysicsFeedback jumpPhysicsFeedback = new();
    private bool automationEnabled;
    private KeyCode toggleKey = KeyCode.U;
    private bool previousBonusState;
    private int previousSectionIndex = -1;
    private int previousPlayerInstanceId;
    private float nextDiagnosticTime;
    private float nextErrorLogTime;
    private float nextMapRegistryRefreshTime;
    private int lastMapRegistryGeneration = -1;
    private string lastMapRegistryStatus = string.Empty;
    private string lastMapRoutingProfile = string.Empty;
    private bool movementInitialized;
    private Vector3 previousMovementPosition;
    private float previousMovementObservedAt;
    private long previousMovementObservedFixedStep = -1;
    private long lastRenderGroundPlanningFixedStep = -1;
    private bool bonusGameplayStarted;
    private bool previousGrounded;
    private float previousVelocityY;
    private bool previousObservedJumpInput;
    private float observedJumpInputStartedAt;
    private bool automaticJumpArmed = true;
    private bool airborneAfterAutomaticJump;
    private float automaticJumpRequestedAt;
    private bool automaticJumpVelocityConfirmed;
    private bool partialAutomaticJumpImpulseOwnedFlight;
    private long partialAutomaticJumpImpulseAttemptId;
    private float partialAutomaticJumpObservedVelocityY;
    private float partialAutomaticJumpExpectedVelocityY;
    private long stage2LowRouteOverheadPreservedAttemptId;
    private float nextAutomaticAttemptTime;
    private bool pitDescentGuardActive;
    private bool pitRespawnImmediateTakeoverEligible;
    private string pitRespawnTakeoverEvidence = string.Empty;
    private bool terrainContinuationEpochBlocked;
    private long pitRespawnLastFixedStep = -1;
    private int pitRespawnStableFixedSteps;
    private long pitDescentCandidateLastFixedStep = -1;
    private int pitDescentCandidateFixedSteps;
    private float nextPitDescentEvidenceTime;
    private bool runTrackingActive;
    private float runStartedAtRealtime;
    private int runDeaths;
    private int runHighestSection;
    private bool runSpiritBoostObserved;
    private bool runFinalCompletionReached;
    private int sessionRunCount;
    private int sessionSuccessCount;
    private int sessionFailureCount;
    private int sessionDeathlessSuccessCount;
    private int sessionDeathCount;
    private float sessionSuccessfulDurationTotal;
    private float sessionBestSuccessfulDuration = float.PositiveInfinity;
    private float wallStallStartedAt = -1f;
    private float nextWallRecoveryTime;
    private long wallInputSeparationReleaseFixedStep = -1;
    private float nextWallProbeLogTime;
    private int wallRecoveryAttempts;
    private bool wallRecoveryContactLatched;
    private bool wallRecoverySawUpwardMotion;
    private float wallRecoveryContactX;
    private float wallRecoveryContactY;
    private float wallRecoveryCommitmentUntil;
    private float wallRecoveryImpulseStartY;
    private float wallRecoveryImpulseStartVelocityY;
    private float wallRecoveryLastObservedY;
    private float nextWallClimbFrameLogTime;
    private int wallRecoveryUpwardPhysicsSteps;
    private bool wallRecoveryImpulseConfirmed;
    private bool wallRecoveryImpulseFailureLogged;
    private long wallRecoveryImpulseReleaseObservedFixedStep = -1;
    private bool wallRecoveryPrematureReleaseLogged;
    private bool wallRecoveryLipCrossed;
    // Physical wall-top release and vertical collection objective are separate
    // constraints. Horizontal movement resumes at the former even when a high
    // soul row asks the attached climb to continue above it.
    private float wallRecoveryPhysicalLipY;
    private float wallRecoveryPhysicalLeft;
    private float wallRecoveryPhysicalRight;
    private float wallRecoveryPhysicalSafeLeft;
    private float wallRecoveryPhysicalSafeRight;
    private bool wallRecoveryPhysicalLipFrozen;
    private float wallRecoveryRequiredReleaseY;
    private bool wallObjectiveCacheActive;
    private float wallObjectiveCacheTargetLeft;
    private float wallObjectiveCacheTargetRight;
    private float wallObjectiveCacheTargetTop;
    private int wallObjectiveCacheCount;
    private float wallObjectiveCacheMinimumY = float.NaN;
    private float wallObjectiveCacheMaximumY = float.NaN;
    private bool wallExitTargetActive;
    private BonusBoardSegment wallExitTarget;
    private bool wallExitPreparedPlanActive;
    private BonusJumpPlan wallExitPreparedPlan;
    private float wallExitPreparedSpeed;
    private BonusBoardSegment wallExitPreparedTarget;
    private float wallExitPredictedLandingX;
    private float wallExitPredictedTravel;
    private float wallExitPredictedFlightSeconds;
    private bool wallExitTransferAcceptedRawBodyFit;
    private float wallExitTransferSafeTolerance;
    private string wallExitPlanSummary = string.Empty;
    private bool wallExitTransferCommitted;
    private bool wallLandingFlightCommitted;
    private bool wallTopLandingSequenceCommitted;
    private bool wallExitContactWatchActive;
    private long wallExitContactWatchDeadlineFixedStep = -1;
    // This is deliberately separate from wallExitFaceContactRequired.  The
    // latter is a mandatory lower-objective contract where a top landing is a
    // failure.  Here the planner intentionally aims at a downstream face only
    // after strict landing failed, and either real face contact or a verified
    // landing on that same target is success.
    private bool wallExitFaceInterceptCommitted;
    // Section 3's low-soul lane also deliberately aims at a downstream face,
    // but it is not interchangeable with the optional FaceOrTop fallback.
    // Landing on top is physically recoverable, yet it means the collection
    // route missed its objective and must be reported/replanned as such.
    private bool wallExitCollectionFaceInterceptCommitted;
    // A narrow target top can be occupied for exactly one physics step.  In
    // background mode several FixedUpdates may run before the next render
    // Update, so retain that physical support as route evidence until the
    // normal learning/lifecycle code consumes it.
    private bool wallExitSupportFixedStepLatched;
    private long wallExitSupportLatchedAttemptId;
    private long wallExitSupportLatchedFixedStep = -1;
    private int wallExitSupportLatchedPlayerInstanceId;
    private BonusBoardSegment wallExitSupportLatchedTarget;
    private BonusBoardSegment wallExitSupportLatchedSurface;
    private Vector3 wallExitSupportLatchedPosition;
    private Vector2 wallExitSupportLatchedVelocity;
    private float wallExitSupportLatchedPlayerHalfWidth;
    private float wallExitSupportLatchedAt;
    private bool wallExitSupportLatchedFaceOrTop;
    private bool wallExitSupportLatchedCollectionFace;
    private bool wallExitSupportLatchedCompletionNarrow;
    private bool wallExitSupportLatchedAutomaticTarget;
    private bool wallResidualRiseWaitActive;
    private float wallResidualRiseWaitStartedAt;
    private float wallResidualRiseWaitStartFeetY;
    private float wallResidualRiseWaitTargetLeft;
    private float nextWallResidualRiseLogTime;
    private bool wallExitFaceContactRequired;
    private int wallExitObjectiveCountAtCapture;
    private float wallExitObjectiveMinimumY = float.NaN;
    private float wallExitObjectiveMaximumY = float.NaN;
    private bool wallMandatoryFaceSetupActive;
    private float wallMandatoryFaceSetupDeadline;
    private long wallMandatoryFaceSetupDeadlineFixedStep = -1;
    private bool wallMandatoryFaceInterceptCommitted;
    private float wallMandatoryFaceInterceptStartedAt;
    private long wallMandatoryFaceInterceptStartedFixedStep = -1;
    private long wallMandatoryFaceContactWatchDeadlineFixedStep = -1;
    private float wallMandatoryFaceTargetContactX = float.NaN;
    private float wallMandatoryFacePredictedTopClearFeetY = float.NaN;
    private float wallMandatoryFacePredictedContactFeetY = float.NaN;
    private float wallMandatoryFacePredictedContactVelocityY = float.NaN;
    private float wallMandatoryFacePredictedContactSeconds;
    private float wallMandatoryFaceReleasePredictedContactFeetY = float.NaN;
    private float wallMandatoryFaceReleasePredictedContactVelocityY = float.NaN;
    private float wallMandatoryFaceReleaseFeetY = float.NaN;
    private float wallMandatoryFaceReleaseVelocityY = float.NaN;
    private long wallMandatoryFaceContactObservedFixedStep = -1;
    private float nextMandatoryFaceWindowLogTime;
    private long wallReleaseObservedFixedStep = -1;
    private long wallDetachedLastFixedStep = -1;
    private int wallDetachedConfirmationSteps;
    private bool passiveWallApproachActive;
    private long passiveWallTargetSupportLastFixedStep = -1;
    private int passiveWallTargetSupportFixedSteps;
    private int passiveWallTargetSupportPlayerInstanceId;
    private BonusBoardSegment passiveWallTargetSupportTarget;
    private BonusBoardSegment passiveWallTargetSupportSurface;
    // Exact physical/expected leading face owned by the shared wall action.
    // Target bounds are not a face identity: on Stage 2's touching stairs,
    // the next real face can be exactly the prior target's right edge.
    private float sharedWallOwnedFaceX = float.NaN;
    private WallActionPhase wallActionPhase;
    private float lastManualInputTime = -10f;
    private float lastReliableHorizontalSpeed = 9.5f;
    private float sectionCruiseHorizontalSpeed;
    private float sectionCruiseCandidateSpeed;
    private long sectionCruiseCandidateLastFixedStep = -1;
    private int sectionCruiseCandidateFixedSteps;
    private bool wallRouteSpeedLatched;
    private float wallRouteHorizontalSpeed;
    private float positiveHorizontalAccelerationEnvelope;
    private float lastAcceptedHorizontalSpeedAt = -1f;
    private readonly float[] completionTraversalSpeedCeilingBySection =
        new float[4];
    private float nextSpeedObservationLogTime;
    private string lastSpeedObservationDecision = string.Empty;
    private long speedPlanningResumeFixedStep = -1;
    private long completionDashPlanningResumeFixedStep = -1;
    private float nextSpeedPlanningBarrierLogTime;
    private float nextCompletionDashDeferralLogTime;
    private bool attachedObjectiveDescentActive;
    private float attachedObjectiveDescentDeadline;
    private long attachedObjectiveDescentDeadlineFixedStep = -1;
    private float attachedObjectiveDescentTargetFeetY;
    private int attachedObjectiveDescentSphereCount;
    private float nextAttachedObjectiveDescentLogTime;
    private bool ground5HighestPillarSinkActive;
    private long ground5HighestPillarSinkArmedFixedStep = -1;
    private double ground5HighestPillarSinkArmedFixedTime = -1d;
    private double ground5HighestPillarSinkDeadlineFixedTime = -1d;
    private float ground5HighestPillarSinkFixedDelta = 0.02f;
    private double ground5HighestPillarSinkLastObservedFixedTime = -1d;
    private bool ground5HighestPillarSinkBackgroundCatchUpObserved;
    private float ground5HighestPillarSinkStartFeetY;
    private float ground5HighestPillarSinkTargetFeetY;
    private int ground5HighestPillarSinkSphereCount;
    private float nextRouteLogTime;
    private string lastIntentionalDropSignature = string.Empty;
    private float nextIntentionalDropLogTime;
    private float noSupportStallStartedAt = -1f;
    private float nextNoSupportStallLogTime;
    private float nextHazardLogTime;
    private string lastHazardSignature = string.Empty;
    private string lastRouteSignature = string.Empty;
    private string lastBoostRouteSelection = string.Empty;
    private string lastLiveRouteSelection = string.Empty;
    // A Spirit slow/fast envelope is orders of magnitude more expensive than
    // an ordinary ballistic check.  While the runner is still far before one
    // proved launch X, the exact same source, target, physics, objectives and
    // trigger inventory used to be solved again in every FixedUpdate and then
    // once more in LateUpdate.  Retain only a grounded WAIT command; it never
    // authorizes DOWN and is discarded before the launch window so the native
    // fixed-step controller still performs the final live proof.
    private bool spiritWaitPlanCacheActive;
    private string spiritWaitPlanCacheMap = string.Empty;
    private int spiritWaitPlanCacheSection = -1;
    private BonusBoardScanResult spiritWaitPlanCacheScan;
    private BonusJumpPlan spiritWaitPlanCachePlan;
    private int spiritWaitPlanCachePhysicsRevision = -1;
    private float spiritWaitPlanCacheSpeed;
    private int spiritWaitPlanCacheSphereProgress = -1;
    private int spiritWaitPlanCacheObjectiveSignature;
    private Vector2[] spiritWaitPlanCacheObjectives = Array.Empty<Vector2>();
    private int spiritWaitPlanCacheTriggerSignature;
    private int spiritWaitPlanCacheHazardSignature;
    private long spiritWaitPlanCacheArmedFixedStep = -1;
    private float spiritWaitPlanCacheReplanX;
    private bool spiritWaitPlanCacheHitLogged;
    private float nextSpiritPlanningCostLogTime;
    private float nextGroundPlanningPhaseCostLogTime;
    private JumpPhysicsSnapshot latestPhysicsSnapshot;
    private BonusStageState latestState;
    private bool learningSampleActive;
    private bool learningMayLearnGroundKinematics;
    private long nextLearningSampleId;
    private long learningSampleId;
    private long nextRouteDecisionId;
    private long activeRouteDecisionId;
    private long automaticAttemptId;
    private string learningSource;
    private string learningMap;
    private int learningSection;
    private float learningInputDownTime;
    private float learningInputUpTime;
    private float learningTakeoffTime;
    private Vector3 learningTriggerPosition;
    private Vector2 learningTriggerVelocity;
    private Vector3 learningTakeoffPosition;
    private Vector2 learningTakeoffVelocity;
    private Vector3 learningApexPosition;
    private float learningMaximumY;
    private bool learningSpiritBoostReadAvailable;
    private bool learningSpiritBoostModeEnabled;
    private float learningStartingSpiritBoostComponent;
    private float learningMaximumSpiritBoostComponent;
    private float learningStartingHorizontalSpeed;
    private float learningMaximumHorizontalSpeed;
    private float learningPreviousVelocityY;
    private Vector3 learningLastObservedPosition;
    private float learningLastObservedAt;
    private long learningLastObservedFixedStep = -1;
    private bool learningTookOff;
    private bool learningFirstApexCaptured;
    private bool learningInputReleased;
    // A nominal landing sample can close one or two physics ticks before the
    // body reaches the target's vertical face. Preserve only enough immutable
    // identity to let exact authored Stage-3 collision correct that race.
    // This is not a coordinate route and cannot authorize a merely visible
    // wall.
    private const int RecentAutomaticFlightContactMaximumFixedSteps = 8;
    private const float RecentAutomaticFlightContactMaximumSeconds = 0.30f;
    // The user-visible contract is one pickup. This threshold is used only
    // for a same-target preferred plan; failure retains the original safe
    // route instead of invalidating it or forcing a farther support.
    private const int Stage1CrossSphereMinimumHits = 1;
    private bool recentAutomaticFlightContactActive;
    private long recentAutomaticFlightAttemptId;
    private long recentAutomaticFlightRouteId;
    private long recentAutomaticFlightEndedFixedStep = -1;
    private float recentAutomaticFlightEndedAt = -1f;
    private float lastLoggedStage1CrossSphereCenterX = float.NaN;
    private float lastCommittedStage1CrossSphereCenterX = float.NaN;
    private bool stage1CrossSpherePriorityLatched;
    private Vector2 stage1CrossSpherePriorityCenter;
    private BonusBoardSegment stage1CrossSpherePriorityTarget;
    private int recentAutomaticFlightPlayerInstanceId;
    private string recentAutomaticFlightMap = string.Empty;
    private int recentAutomaticFlightSection = -1;
    private string recentAutomaticFlightOutcome = string.Empty;
    private string recentAutomaticFlightPlan = string.Empty;
    private BonusManeuverKind recentAutomaticFlightManeuver;
    private BonusBoardSegment recentAutomaticFlightSource;
    private BonusBoardSegment recentAutomaticFlightTarget;
    private float recentAutomaticFlightPredictedLandingX;
    private Vector2 recentAutomaticFlightTriggerVelocity;
    private long landingCandidateLastFixedStep = -1;
    private int landingCandidateStableFixedSteps;
    private float landingCandidateTop;
    private int landingCandidateColliderId;
    private float landingCandidateFirstObservedAt = -1f;
    // A support captured in PlayerMovement.FixedUpdate can be several physics
    // ticks old by the time the render loop closes the learning sample. Keep
    // that physical surface and body width as one-shot scoring authority so a
    // current collider scan is never mixed with a historical position.
    private bool authoritativeLandingEvidenceActive;
    private bool authoritativeLandingEvidenceHistorical;
    private BonusBoardSegment authoritativeLandingEvidenceSurface;
    private float authoritativeLandingEvidencePlayerHalfWidth;
    private float authoritativeLandingEvidenceAt;
    private long authoritativeLandingEvidenceFixedStep = -1;
    private bool automaticPredictionActive;
    private float automaticPredictedLandingX;
    private float automaticPredictedFlightSeconds;
    private float automaticPredictedHorizontalTravel;
    private float automaticMinimumPredictedHorizontalTravel;
    private float automaticMaximumPredictedHorizontalTravel;
    private bool automaticFutureSpeedTransitionExpected;
    private string automaticSpiritBoostRouteEvidence = "Unavailable";
    private float automaticPredictionLaunchFeetY;
    private float automaticPlannedTravelScale;
    private float automaticPlannedLandingBias;
    private float automaticPlannedHold;
    private float automaticTriggerSpeed;
    private float automaticTargetHeightDelta;
    private int automaticPhysicsRevision;
    private float automaticTargetSafeLeft;
    private float automaticTargetSafeRight;
    private bool automaticLandingAllowsRawBodyFit;
    private float automaticLandingSafeTolerance;
    private float automaticTargetLeft;
    private float automaticTargetRight;
    private float automaticTargetTop;
    private int automaticTargetColliderId;
    private string automaticTargetColliderName;
    private string automaticTargetMapPieceName;
    private float automaticTargetMapPieceOriginX;
    private int automaticTargetMapPieceInstanceId;
    private int automaticTargetRegistryGeneration;
    private int automaticTargetStaticSurfaceIndex;
    private string automaticPlanReason;
    private BonusManeuverKind automaticManeuver;
    private Vector3 automaticPlanTriggerPosition;
    private float automaticPlannedLaunchX;
    private float automaticLaunchWindowLeft;
    private float automaticLaunchWindowRight;
    private BonusBoardSegment automaticSourceSegment;
    private string automaticHazardAtPlan;
    private int automaticSphereCountAtPlan = -1;
    private int automaticRemainingSpheresAtPlan = -1;
    private int automaticRawExpectedSphereHits;
    private int automaticExpectedSphereHits;
    private int automaticExpectedSpeedBoostHits;
    private string automaticSpheresAtPlan = "Unavailable";
    private bool automaticControlSuspended;
    private JumpPhysicsSnapshot automaticPlanPhysicsSnapshot;
    private bool automaticTrajectoryCompatible;
    private float nextTrajectoryMonitorLogTime;
    private float nextDynamicPlanLogTime;
    private float nextLandingDeferredLogTime;
    // Stage 2 sometimes exposes only the support beyond a stepped composite
    // wall. The initial maximum jump and every later zero-VX climb pulse keep
    // one downstream support identity until a real landing resolves it.
    private bool stage2UnmappedWallTraverseActive;
    private BonusBoardSegment stage2UnmappedWallTraverseTarget;
    private int stage2UnmappedWallTraversePulses;
    private long stage2UnmappedWallStallLastFixedStep = -1;
    private int stage2UnmappedWallStallFixedSteps;
    private Vector3 stage2UnmappedWallLastPulsePosition;
    private float nextStage2UnmappedWallLogTime;
    // A Stage-2 composite staircase can stop the player flush against its
    // next face while the forward ray reports NoForwardWallHit.  Preserve a
    // separate two-physics-step proof for that grounded contact; it is not a
    // general zero-speed escape and never exists without an already verified
    // adjacent-wall route.
    private long stage2GroundedWallContactLastFixedStep = -1;
    private int stage2GroundedWallContactFixedSteps;
    private float stage2GroundedWallContactTargetLeft = float.NaN;
    private float stage2GroundedWallContactTargetTop = float.NaN;
    // Shared error-correction evidence. Normal routing never enters this
    // state: the player must remain grounded and motionless against the same
    // physically touched, climbable face for distinct physics steps while
    // terrain control is expected and no reward object owns input.
    private const int SharedGroundedWallStallConfirmationFixedSteps = 3;
    private const float SharedGroundedWallStallMaximumHorizontalDrift = 0.08f;
    private const float SharedGroundedWallStallMaximumVerticalDrift = 0.10f;
    private long sharedGroundedWallStallLastFixedStep = -1;
    private int sharedGroundedWallStallFixedSteps;
    private float sharedGroundedWallStallFaceX = float.NaN;
    private Vector3 sharedGroundedWallStallStartPosition;
    private bool secondStagePreviewActive;
    private BonusBoardSegment secondStageExpectedSupport;
    private float secondStageExpectedLandingX;
    private BonusBoardScanResult secondStageProjectedScan;
    private BonusJumpPlan secondStageProjectedPlan;
    private string secondStageSource;
    private string secondStageSignature;
    private float nextSecondStageRefreshTime;
    private bool secondStageObservedAirborne;
    private bool manualWallSequenceActive;
    private int manualWallJumpCount;
    private float manualWallJumpDownTime;
    private float manualWallJumpHold;
    private Vector3 manualWallJumpPosition;
    private Vector2 manualWallJumpStartVelocity;
    private Vector2 manualWallPoint;
    private float manualWallLastPulseTime = -10f;
    private long nextManualWallSequenceId;
    private long manualWallSequenceId;
    private int manualWallColliderId;
    private string manualWallColliderName = string.Empty;
    private bool manualDemonstrationActive;
    private long nextManualDemonstrationId;
    private long manualDemonstrationId;
    private int manualDemonstrationMousePresses;
    private float manualDemonstrationStartedAt;
    private Vector3 manualDemonstrationStartPosition;
    private int manualDemonstrationStartSection;
    private int manualDemonstrationStartSpheres = -1;
    private bool manualDemonstrationPitLogged;
    private long manualPitCandidateLastFixedStep = -1;
    private int manualPitCandidateFixedSteps;
    private float nextManualPitEvidenceTime;
    private bool previousObservedMouseInput;
    private float nextManualDemonstrationFrameTime;
    private string lastManualDemonstrationSignature = string.Empty;
    private long lastEvidenceFixedStep = -1;
    private string lastControlGateSignature = string.Empty;
    private string lastEvidenceSignature = string.Empty;
    private float nextGeneralEvidenceTime;
    private float nextCompletionNavigationLogTime;
    private bool postQuotaRoadActive;
    private int postQuotaCompletedSection = -1;
    private bool incompleteQuotaObserved;
    private int incompleteQuotaSection = -1;
    private int quotaEvidencePlayerInstanceId;
    private int lastQuotaObserverSection = -1;
    private bool waitFalseObservedAfterIncompleteQuota;
    private bool postQuotaRewardTriggerObserved;
    private bool postQuotaGivingRewardsObserved;
    private bool previousRewardFlagsAvailable;
    private bool previousNativeWaitingForRewardZone;
    private bool previousNativeRewardZone;
    private bool previousNativeGivingRewards;
    private string lastRewardPhaseSignature = string.Empty;
    private BonusRewardTargetObservation rewardTargetObservation;
    private int lastActiveTerrainSection = -1;
    private string lastRewardTargetObservationSignature = string.Empty;
    private bool rewardLatchObservedDuringInactiveFrame;
    private bool rewardTargetEmptyBaselineRequired;
    private bool rewardTargetRearmDeferralLogged;
    private int rewardTargetConsecutiveEmptyScans;
    private int rewardTargetLastEmptyScanFrame = -1;
    private float nextRewardTargetScanWarningTime;
    private bool retryModalControlSuspended;
    private readonly BonusStageSliderSkip sliderSkip = new();

    public void Start()
    {
        activeRuntime = this;
        jumpPhysicsFeedback.Attach();
        automationEnabled = Plugin.Config?.EnabledOnStartup?.Value == true;
        string configuredKey = Plugin.Config?.ToggleKey?.Value;
        if (!string.IsNullOrWhiteSpace(configuredKey) &&
            System.Enum.TryParse(configuredKey.Trim(), true, out KeyCode parsedKey) &&
            parsedKey != KeyCode.None)
        {
            toggleKey = parsedKey;
        }
        else
        {
            BonusRunnerLog.Warning($"Invalid Toggle Key '{configuredKey}'. Using U.");
        }

        BonusRunnerLog.User(
            $"AutoBonusRunner runtime ready; automation is {(automationEnabled ? "enabled" : "disabled")}. " +
            $"Press {toggleKey} to toggle automatic control. Detection and manual-jump learning stay active. " +
            $"AutoRetry={Plugin.Config?.EnableAutoRetry?.Value == true}, " +
            $"SkipStartSlider={Plugin.Config?.SkipStartSlider?.Value == true}, " +
            $"Mode={BonusStageSphereRequirementMode.ConfiguredMode}.");
    }

    public void Update()
    {
        try
        {
            // The slider helper is independently configured and remains
            // active when U disables terrain control.
            sliderSkip.Tick(Time.unscaledTime);

            if (Input.GetKeyDown(toggleKey))
            {
                automationEnabled = !automationEnabled;
                jumpController.Release();
                if (!automationEnabled &&
                    learningSampleActive &&
                    learningSource == "Automatic" &&
                    latestState.HasPlayer)
                {
                    FinishLearningSample(latestState, "AutomationDisabled");
                }
                ResetAutomaticControlState();
                completionRewardController.Reset(
                    automationEnabled
                        ? "AutomationEnabled"
                        : "AutomationDisabled");
                if (automationEnabled)
                {
                    nextAutomaticAttemptTime = 0f;
                    EndManualDemonstration(
                        latestState,
                        "AutomationEnabled");
                }
                else
                {
                    BonusStageRetryBridge.Reset("AutomationDisabled");
                    retryModalControlSuspended = false;
                }
                string message = automationEnabled
                    ? "Auto Bonus Runner Enabled!"
                    : "Auto Bonus Runner Disabled!";
                BonusRunnerLog.User(message);
                Plugin.ModHelperInstance?.ShowNotification(message, automationEnabled);
            }

            BonusStageState state = detector.Capture();
            latestState = state;
            TryAutoRetry(state);
            if (state.IsBonusStage && !previousBonusState)
            {
                BonusStageInspector.ResetSceneObjectCaches(
                    "BonusStageEntry");
                hazardScanner.ResetCache("BonusStageEntry");
                platformScanner.ResetDynamicCaches("BonusStageEntry");
                rewardTargetDetector.Reset("BonusStageEntry");
                rewardTargetObservation = default;
                lastRewardTargetObservationSignature = string.Empty;
                rewardLatchObservedDuringInactiveFrame = false;
                rewardTargetEmptyBaselineRequired = false;
                rewardTargetRearmDeferralLogged = false;
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
                lastActiveTerrainSection = -1;
                bonusGameplayStarted = false;
                terrainContinuationEpochBlocked = false;
                ResetQuotaTransitionEvidence("BonusStageEntry");
            }
            ObserveRewardObjectPhase(state);
            RefreshMapRegistry(state);
            if (!BonusStageRetryBridge.BlocksTerrainControl)
                ObserveReliableHorizontalSpeed(state);
            bool retryBoundaryPending =
                !state.IsBonusStage &&
                BonusStageRetryBridge.BlocksTerrainControl;
            if (state.IsBonusStage != previousBonusState &&
                !retryBoundaryPending)
            {
                previousBonusState = state.IsBonusStage;
                if (!state.IsBonusStage)
                    FinishRunTracking();
                bool hasPlayableEntrySection =
                    state.SectionIndex is >= 0 and <= 3;
                if (!state.IsBonusStage)
                {
                    BonusRunnerLog.User("Bonus Stage ended.");
                }
                else if (hasPlayableEntrySection)
                {
                    BonusRunnerLog.User(
                        $"Bonus Stage progress: 0 -> " +
                        $"{state.SectionIndex + 1}. Map={state.MapName}.");
                }

                // BonusMapController can briefly retain the completed
                // sentinel Section=4 while the next Bonus Stage scene is
                // entering. Do not turn that stale lifecycle value into a
                // user-visible "4 -> 0" transition. The first active,
                // playable section below will publish the normal detection
                // line and initialize section-scoped state.
                previousSectionIndex =
                    state.IsBonusStage && !hasPlayableEntrySection
                        ? -1
                        : state.SectionIndex;
                previousPlayerInstanceId = state.PlayerInstanceId;
                if (state.IsBonusStage)
                {
                    ResetStage1CrossSpherePriority();
                    movementInitialized = false;
                    pitDescentGuardActive = false;
                    pitRespawnImmediateTakeoverEligible = false;
                    pitRespawnTakeoverEvidence = string.Empty;
                    pitRespawnLastFixedStep = -1;
                    pitRespawnStableFixedSteps = 0;
                    lastReliableHorizontalSpeed = 9.5f;
                    ResetSectionCruiseSpeed();
                    speedPlanningResumeFixedStep = -1;
                    completionDashPlanningResumeFixedStep = -1;
                    nextAutomaticAttemptTime = 0f;
                    lastRouteSignature = string.Empty;
                    jumpPhysicsFeedback.ResetRouteCalibration(
                        "BonusStageEntered:Section0");
                    BonusStageInspector.LogControllerSnapshot("BonusStageEntered");
                }
            }

            if (!state.IsBonusStage)
            {
                ResetStage1CrossSpherePriority();
                BonusStageInspector.ResetSceneObjectCaches("StageEnded");
                hazardScanner.ResetCache("StageEnded");
                platformScanner.ResetDynamicCaches("StageEnded");
                EndManualDemonstration(state, "StageEnded");
                FinishLearningSample(state, "StageEnded");
                jumpController.Release();
                jumpPhysicsFeedback.ResetTransient("StageEnded");
                ResetAutomaticControlState();
                completionRewardController.Reset("StageEnded");
                ResetPostQuotaRoadPhase("StageEnded");
                rewardTargetDetector.Reset("StageEnded");
                rewardTargetObservation = default;
                lastRewardTargetObservationSignature = string.Empty;
                rewardLatchObservedDuringInactiveFrame = false;
                rewardTargetEmptyBaselineRequired = false;
                rewardTargetRearmDeferralLogged = false;
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
                lastActiveTerrainSection = -1;
                movementInitialized = false;
                bonusGameplayStarted = false;
                terrainContinuationEpochBlocked = false;
                pitRespawnImmediateTakeoverEligible = false;
                pitRespawnTakeoverEvidence = string.Empty;
                ResetSectionCruiseSpeed();
                speedPlanningResumeFixedStep = -1;
                completionDashPlanningResumeFixedStep = -1;
                return;
            }

            UpdateRunTracking(state);

            PlayerMovement observedPlayer = PlayerMovement.instance;
            if (state.HasPlayer && observedPlayer != null)
            {
                latestPhysicsSnapshot = jumpPhysicsFeedback.CaptureSnapshot(
                    observedPlayer);
            }

            if (state.IsActiveGameplay &&
                state.SectionIndex != previousSectionIndex)
            {
                bool enteringFirstPlayableSection =
                    previousSectionIndex < 0 &&
                    state.SectionIndex is >= 0 and <= 3;
                ResetStage1CrossSpherePriority();
                BonusStageInspector.ResetSceneObjectCaches(
                    $"SectionChanged:{previousSectionIndex}->{state.SectionIndex}");
                hazardScanner.ResetCache(
                    $"SectionChanged:{previousSectionIndex}->{state.SectionIndex}");
                platformScanner.ResetDynamicCaches(
                    $"SectionChanged:{previousSectionIndex}->{state.SectionIndex}");
                EndManualDemonstration(state, "SectionChanged");
                BonusRunnerLog.User(
                    enteringFirstPlayableSection
                        ? $"Bonus Stage progress: 0 -> " +
                          $"{state.SectionIndex + 1}. Map={state.MapName}."
                        : $"Bonus Stage progress: " +
                          $"{previousSectionIndex + 1} -> " +
                          $"{state.SectionIndex + 1}.");
                // BonusMapController advances its mutable section index while
                // the runner is still traversing the completed section's
                // terrain.  Only a real active-gameplay frame makes the new
                // section authoritative and permits section-scoped state to
                // be cleared.
                FinishLearningSample(state, "SectionChanged");
                jumpController.Release();
                jumpPhysicsFeedback.ResetTransient("SectionChanged");
                jumpPhysicsFeedback.ResetRouteCalibration(
                    $"SectionChanged:{previousSectionIndex}->{state.SectionIndex}");
                ResetAutomaticControlState();
                completionRewardController.Reset("SectionChanged");
                nextAutomaticAttemptTime = 0f;
                movementInitialized = false;
                if (!terrainContinuationEpochBlocked)
                {
                    pitDescentGuardActive = false;
                    pitRespawnImmediateTakeoverEligible = false;
                    pitRespawnTakeoverEvidence = string.Empty;
                    pitRespawnLastFixedStep = -1;
                    pitRespawnStableFixedSteps = 0;
                }
                ResetSectionCruiseSpeed();
                speedPlanningResumeFixedStep = -1;
                completionDashPlanningResumeFixedStep = -1;
                previousSectionIndex = state.SectionIndex;
                BonusStageInspector.LogControllerSnapshot("SectionChanged");
            }

            if (state.PlayerInstanceId != 0 && state.PlayerInstanceId != previousPlayerInstanceId)
            {
                EndManualDemonstration(state, "PlayerInstanceChanged");
                BonusRunnerLog.Debug(
                    $"Player instance changed: {previousPlayerInstanceId} -> {state.PlayerInstanceId}. This may indicate spawn or recovery.",
                    "Detection");
                FinishLearningSample(state, "PlayerInstanceChanged");
                jumpController.Release();
                jumpPhysicsFeedback.ResetTransient("PlayerInstanceChanged");
                ResetAutomaticControlState();
                ResetCompletionTraversalSpeedEvidence(
                    "PlayerInstanceChanged");
                completionRewardController.Reset("PlayerInstanceChanged");
                rewardTargetDetector.BeginEpoch(
                    state,
                    "PlayerInstanceChanged");
                rewardTargetObservation =
                    rewardTargetDetector.LastObservation;
                lastRewardTargetObservationSignature = string.Empty;
                rewardLatchObservedDuringInactiveFrame = false;
                rewardTargetEmptyBaselineRequired =
                    state.IsBonusStage &&
                    state.IsSupportedBonusMap;
                rewardTargetRearmDeferralLogged = false;
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
                // A replacement player is a new life epoch. Even when the
                // controller is temporarily non-active, old terrain and
                // reward ownership must not resume until the respawn guard
                // sees stable gameplay motion (or real active gameplay).
                bonusGameplayStarted = false;
                terrainContinuationEpochBlocked = true;
                pitDescentGuardActive = true;
                pitRespawnImmediateTakeoverEligible = false;
                pitRespawnTakeoverEvidence = "PlayerInstanceChanged";
                pitRespawnLastFixedStep = -1;
                pitRespawnStableFixedSteps = 0;
                nextAutomaticAttemptTime = Math.Max(
                    nextAutomaticAttemptTime,
                    Time.unscaledTime + 0.45f);
                previousPlayerInstanceId = state.PlayerInstanceId;
                movementInitialized = false;
                lastRouteSignature = string.Empty;
            }

            LogMovementEvents(state);
            UpdateLearningSample(state);
            ValidateAutomaticJumpResponse(state);

            if (BonusRunnerLog.IsDebugMode && Time.unscaledTime >= nextDiagnosticTime)
            {
                nextDiagnosticTime = Time.unscaledTime + DiagnosticIntervalSeconds;
                BonusRunnerLog.Debug(
                    $"State={state.GameStateName}, Map={state.MapName}, Section={state.SectionIndex}, " +
                    $"PlayerId={state.PlayerInstanceId}, TimeScale={Time.timeScale:F2}, " +
                    $"Player={(state.HasPlayer ? $"({state.PlayerPosition.x:F2},{state.PlayerPosition.y:F2})" : "Unavailable")}, " +
                    $"Velocity=({state.PlayerVelocity.x:F2},{state.PlayerVelocity.y:F2}), Grounded={state.IsGrounded}, " +
                    $"Spheres={(state.HasSphereProgress ? $"{state.CollectedSpheres}/{Math.Ceiling(state.RequiredSpheres):F0}" : "Unavailable")}, " +
                    $"RequiredRaw={(state.HasSphereProgress ? state.RequiredSpheres.ToString("F3") : "Unavailable")}, " +
                    $"Remaining={state.RemainingRequiredSpheres}, Timer={state.CurrentTime:F2}/{state.MaximumTime:F2}, " +
                    $"TimerVisible={state.IsTimerVisible}, SupportedMap={state.IsSupportedBonusMap}, " +
                    $"ActiveGameplay={state.IsActiveGameplay}, FellOff={state.CharacterFellOff}, " +
                    $"SpiritBoost={state.SpiritBoostEnabled}, RewardFlags[Available=" +
                    $"{state.RewardFlagsAvailable},Wait=" +
                    $"{state.WaitingForRewardZone},Trigger=" +
                    $"{state.RewardZoneEntered},Giving=" +
                    $"{state.GivingRewards}], TerrainLifecycle[LastActive=" +
                    $"{lastActiveTerrainSection},Continuation=" +
                    $"{IsSuccessfulCompletionTraversal(state)},Guard=" +
                    $"{pitDescentGuardActive},Immediate=" +
                    $"{pitRespawnImmediateTakeoverEligible},Evidence=" +
                    $"{pitRespawnTakeoverEvidence}], " +
                    $"RewardTarget[{rewardTargetDetector.Describe()}]",
                    "Detection");
            }
        }
        catch (System.Exception exception)
        {
            jumpController.Release();
            jumpPhysicsFeedback.ResetTransient("RuntimeException");
            ResetAutomaticControlState();
            nextAutomaticAttemptTime = Time.unscaledTime + 0.50f;
            if (Time.unscaledTime >= nextErrorLogTime)
            {
                nextErrorLogTime = Time.unscaledTime + 5f;
                BonusRunnerLog.Exception(
                    "Runtime observation",
                    exception);
            }
        }
    }

    public void OnEnable()
    {
        activeRuntime = this;
        jumpPhysicsFeedback.Attach();
    }

    public void OnDisable()
    {
        if (activeRuntime == this)
            activeRuntime = null;
        BonusStageRetryBridge.Reset("RuntimeDisabled");
        retryModalControlSuspended = false;
        jumpController.Release();
        ResetAutomaticControlState();
        completionRewardController.Reset("RuntimeDisabled");
        rewardTargetDetector.Reset("RuntimeDisabled");
        rewardLatchObservedDuringInactiveFrame = false;
        rewardTargetEmptyBaselineRequired = false;
        rewardTargetRearmDeferralLogged = false;
        rewardTargetConsecutiveEmptyScans = 0;
        rewardTargetLastEmptyScanFrame = -1;
        rewardTargetObservation = default;
        lastRewardTargetObservationSignature = string.Empty;
        bonusGameplayStarted = false;
        lastActiveTerrainSection = -1;
        terrainContinuationEpochBlocked = false;
        pitDescentGuardActive = false;
        pitRespawnImmediateTakeoverEligible = false;
        pitRespawnTakeoverEvidence = string.Empty;
        pitRespawnLastFixedStep = -1;
        pitRespawnStableFixedSteps = 0;
        jumpPhysicsFeedback.ResetTransient("RuntimeDisabled");
        jumpPhysicsFeedback.Detach();
    }

    public void OnDestroy()
    {
        if (activeRuntime == this)
            activeRuntime = null;
        BonusStageRetryBridge.Reset("RuntimeDestroyed");
        retryModalControlSuspended = false;
        jumpController.Release();
        ResetAutomaticControlState();
        completionRewardController.Reset("RuntimeDestroyed");
        rewardTargetDetector.Reset("RuntimeDestroyed");
        rewardLatchObservedDuringInactiveFrame = false;
        rewardTargetEmptyBaselineRequired = false;
        rewardTargetRearmDeferralLogged = false;
        rewardTargetConsecutiveEmptyScans = 0;
        rewardTargetLastEmptyScanFrame = -1;
        rewardTargetObservation = default;
        lastRewardTargetObservationSignature = string.Empty;
        bonusGameplayStarted = false;
        lastActiveTerrainSection = -1;
        terrainContinuationEpochBlocked = false;
        pitDescentGuardActive = false;
        pitRespawnImmediateTakeoverEligible = false;
        pitRespawnTakeoverEvidence = string.Empty;
        pitRespawnLastFixedStep = -1;
        pitRespawnStableFixedSteps = 0;
        jumpPhysicsFeedback.ResetTransient("RuntimeDestroyed");
        jumpPhysicsFeedback.Detach();
    }

    private void TryAutoRetry(BonusStageState state)
    {
        BonusStageRetryBridge.ObserveStageState(
            state.IsBonusStage,
            state.IsActiveGameplay,
            state.HasPlayer,
            state.CharacterFellOff,
            state.GameStateName);

        if (BonusStageRetryBridge.TryTakeOutcome(
                out bool succeeded,
                out bool warning,
                out string outcome))
        {
            string message = $"Auto retry outcome: {outcome}";
            if (!succeeded || warning)
                BonusRunnerLog.Warning(message);
            else
                BonusRunnerLog.User(message);
        }

        if (!automationEnabled)
        {
            BonusStageRetryBridge.Reset(
                "AutomationDisabledBeforeRetryAction");
            return;
        }

        if (BonusStageRetryBridge.BlocksTerrainControl)
            SuspendTerrainControlForRetry(state);

        bool continueRequested =
            Plugin.Config?.EnableAutoRetry?.Value == true;
        if (!BonusStageRetryBridge.TryBeginInvocation(
                continueRequested,
                out Popup popup,
                out long sequence,
                out int attempt,
                out bool actualContinueRequested,
                out string evidence))
        {
            return;
        }

        string retryAction =
            actualContinueRequested ? "Continue" : "No";
        BonusRunnerLog.User(
            $"Auto retry action requested: {retryAction}.");
        BonusRunnerLog.Debug(
            $"AutoRetryRequestDispatching Action={retryAction}, " +
            $"Sequence={sequence}, Attempt={attempt}, Evidence=" +
            $"[{evidence}]. Native acknowledgement is still pending.",
            "Retry");
        try
        {
            if (actualContinueRequested)
            {
                if (popup.confirmButton == null)
                {
                    throw new InvalidOperationException(
                        "Validated Continue button became unavailable.");
                }
                popup.confirmButton.onClick.Invoke();
            }
            else
            {
                popup.PressCancelButton();
            }

            BonusRunnerLog.Debug(
                $"AutoRetryUiInvocationReturned Action=" +
                $"{(actualContinueRequested ? "Continue" : "No")}, " +
                $"Sequence={sequence}, Attempt={attempt}. Awaiting native " +
                (actualContinueRequested
                    ? "RewardForShowing and gameplay-resume acknowledgement."
                    : "popup-close and Bonus Stage exit acknowledgement."),
                "Retry");
        }
        catch (System.Exception exception)
        {
            BonusStageRetryBridge.ReportInvocationFailure(
                sequence,
                $"{exception.GetType().Name}:{exception.Message}",
                out bool willRetry);
            BonusRunnerLog.Exception(
                $"Auto retry invocation (sequence={sequence}; " +
                $"attempt={attempt}; willRetry=" +
                $"{willRetry.ToString().ToLowerInvariant()})",
                exception);
        }
    }

    private void SuspendTerrainControlForRetry(BonusStageState state)
    {
        jumpController.Release();
        if (retryModalControlSuspended)
            return;

        if (learningSampleActive &&
            learningSource == "Automatic" &&
            state.HasPlayer)
        {
            FinishLearningSample(state, "RetryModalOwnershipHandoff");
        }
        jumpPhysicsFeedback.ResetTransient("RetryModalOwnershipHandoff");
        ResetAutomaticControlState();
        ClearAutomaticAttemptIdentityAfterLifecycleHandoff();
        completionRewardController.Reset("RetryModalOwnershipHandoff");
        bonusGameplayStarted = false;
        rewardTargetDetector.BeginEpoch(
            state,
            "RetryModalOwnershipHandoff");
        rewardTargetObservation = rewardTargetDetector.LastObservation;
        lastRewardTargetObservationSignature = string.Empty;
        rewardLatchObservedDuringInactiveFrame = false;
        rewardTargetEmptyBaselineRequired = true;
        rewardTargetRearmDeferralLogged = false;
        rewardTargetConsecutiveEmptyScans = 0;
        rewardTargetLastEmptyScanFrame = -1;
        ResetPostQuotaRoadPhase("RetryModalOwnershipHandoff");
        ResetQuotaTransitionEvidence("RetryModalOwnershipHandoff");
        ResetCompletionTraversalSpeedEvidence(
            "RetryModalOwnershipHandoff");
        retryModalControlSuspended = true;
        BonusRunnerLog.Debug(
            $"RetryModalOwnershipHandoff Frame={Time.frameCount}, " +
            $"State=[{BonusStageRetryBridge.ControlGateSummary}]. Terrain, " +
            "wall, and completion input were released while the native " +
            "failure dialog owns control.",
            "Retry");
    }

    /// <summary>
    /// Keeps terrain ownership until a concrete, interactable reward object
    /// has been observed twice. Sphere quota and BonusMapController reward
    /// flags remain diagnostics only: they can change several seconds before
    /// the completed section's road has actually ended.
    /// </summary>
    private void ObserveRewardObjectPhase(BonusStageState state)
    {
        if (!state.IsBonusStage)
        {
            if (rewardTargetDetector.IsLatched)
                rewardTargetDetector.Reset("OutsideBonusStage");
            rewardTargetObservation = default;
            lastActiveTerrainSection = -1;
            lastRewardTargetObservationSignature = string.Empty;
            rewardLatchObservedDuringInactiveFrame = false;
            rewardTargetEmptyBaselineRequired = false;
            rewardTargetRearmDeferralLogged = false;
            rewardTargetConsecutiveEmptyScans = 0;
            rewardTargetLastEmptyScanFrame = -1;
            return;
        }

        bool rewardLifecycleUnavailable =
            !state.IsSupportedBonusMap ||
            !state.HasPlayer;
        if (rewardLifecycleUnavailable && bonusGameplayStarted)
        {
            BonusRunnerLog.Debug(
                $"TerrainContinuationEpochRevoked Reason=" +
                $"{(!state.IsSupportedBonusMap ? "UnsupportedMap" : "PlayerUnavailable")}, " +
                $"ControllerSection={state.SectionIndex}, LastActive=" +
                $"{lastActiveTerrainSection}, Target=" +
                $"[{rewardTargetDetector.Describe()}]. A fresh real " +
                "active-gameplay frame is required before terrain or " +
                "reward ownership can resume.",
                "Lifecycle");
            rewardTargetDetector.BeginEpoch(
                state,
                !state.IsSupportedBonusMap
                    ? "UnsupportedMapEpoch"
                    : "PlayerUnavailableEpoch");
            rewardTargetObservation =
                rewardTargetDetector.LastObservation;
            lastRewardTargetObservationSignature = string.Empty;
            rewardLatchObservedDuringInactiveFrame = false;
            rewardTargetEmptyBaselineRequired = true;
            rewardTargetRearmDeferralLogged = false;
            rewardTargetConsecutiveEmptyScans = 0;
            rewardTargetLastEmptyScanFrame = -1;
            if (learningSampleActive &&
                string.Equals(
                    learningSource,
                    "Automatic",
                    StringComparison.Ordinal))
            {
                FinishLearningSample(
                    state,
                    "TerrainContinuationEpochRevoked");
            }
            jumpController.Release();
            ResetAutomaticControlState();
            ResetCompletionTraversalSpeedEvidence(
                "TerrainContinuationEpochRevoked");
            bonusGameplayStarted = false;
            terrainContinuationEpochBlocked = true;
            pitDescentGuardActive = true;
            pitRespawnImmediateTakeoverEligible = false;
            pitRespawnTakeoverEvidence = "TerrainLifecycleUnavailable";
            pitRespawnLastFixedStep = -1;
            pitRespawnStableFixedSteps = 0;
            nextAutomaticAttemptTime = Math.Max(
                nextAutomaticAttemptTime,
                Time.unscaledTime + 0.45f);
        }

        if (state.IsActiveGameplay &&
            state.IsSupportedBonusMap &&
            state.HasPlayer &&
            !terrainContinuationEpochBlocked)
        {
            if (!bonusGameplayStarted &&
                lastActiveTerrainSection >= 0)
            {
                BonusRunnerLog.Debug(
                    $"TerrainContinuationEpochRearmed Section=" +
                    $"{state.SectionIndex}, Player=" +
                    $"{state.PlayerInstanceId}. Real active gameplay is " +
                    "authoritative again.",
                    "Lifecycle");
            }
            bool realSectionBoundary =
                lastActiveTerrainSection >= 0 &&
                state.SectionIndex != lastActiveTerrainSection;
            bool completedRewardInterval =
                rewardLatchObservedDuringInactiveFrame;
            if (realSectionBoundary || completedRewardInterval)
            {
                BonusRunnerLog.Debug(
                    $"RewardObjectPhaseReset Reason=NextActiveSection, " +
                    $"ControllerSection={state.SectionIndex}, Previous=" +
                    $"[{rewardTargetDetector.Describe()}]. The next real " +
                    "section has begun and terrain control is authoritative. " +
                    $"SectionBoundary={realSectionBoundary}, " +
                    $"InactiveRewardFrameObserved=" +
                    $"{completedRewardInterval}.",
                    "Completion");
                rewardTargetDetector.BeginEpoch(
                    state,
                    $"NextActiveSection{state.SectionIndex}");
                rewardLatchObservedDuringInactiveFrame = false;
                rewardTargetEmptyBaselineRequired = true;
                rewardTargetRearmDeferralLogged = false;
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
            }

            if (!rewardTargetDetector.IsLatched)
                rewardLatchObservedDuringInactiveFrame = false;

            bonusGameplayStarted = true;
            lastActiveTerrainSection = state.SectionIndex;
        }

        bool observationAllowed =
            bonusGameplayStarted &&
            state.IsSupportedBonusMap &&
            state.HasPlayer;
        rewardTargetObservation = rewardTargetDetector.Observe(
            state,
            observationAllowed);
        if (observationAllowed &&
            rewardTargetObservation.ScanPerformed &&
            !rewardTargetObservation.ScanSucceeded &&
            Time.unscaledTime >= nextRewardTargetScanWarningTime)
        {
            nextRewardTargetScanWarningTime = Time.unscaledTime + 5f;
            BonusRunnerLog.Warning(
                $"RewardTargetScanIncomplete Target=" +
                $"[{rewardTargetObservation.Describe()}], " +
                $"EpochInventoryIncomplete=" +
                $"{rewardTargetDetector.IsEpochInventoryIncomplete}. " +
                "Reward ownership remains fail-closed; terrain/lifecycle " +
                "control continues and no timeout can authorize rewards.");
        }
        if (rewardTargetEmptyBaselineRequired &&
            (!observationAllowed ||
             rewardTargetObservation.ScanPerformed &&
             !rewardTargetObservation.ScanSucceeded))
        {
            rewardTargetConsecutiveEmptyScans = 0;
            rewardTargetLastEmptyScanFrame = -1;
        }
        else if (rewardTargetEmptyBaselineRequired &&
                 observationAllowed &&
                 rewardTargetObservation.ScanPerformed &&
                 rewardTargetObservation.ScanSucceeded)
        {
            if (rewardTargetObservation.CandidateQualified)
            {
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
                if (rewardTargetObservation.IsLatched)
                {
                    rewardTargetEmptyBaselineRequired = false;
                    rewardTargetRearmDeferralLogged = false;
                    BonusRunnerLog.Debug(
                        $"RewardTargetFreshEpochLatched Target=" +
                        $"[{rewardTargetObservation.Describe()}]. The " +
                        "instance was not in the retired prior-epoch set, " +
                        "so it may establish reward ownership without a " +
                        "global-empty gap.",
                        "Completion");
                }
                else if (!rewardTargetRearmDeferralLogged)
                {
                    rewardTargetRearmDeferralLogged = true;
                    BonusRunnerLog.Debug(
                        $"RewardTargetFreshEpochPending Target=" +
                        $"[{rewardTargetObservation.Describe()}]. Waiting " +
                        "for the second observation of this distinct, " +
                        "non-retired instance.",
                        "Completion");
                }
            }
            else
            {
                bool completePhysicalEmpty = string.Equals(
                    rewardTargetObservation.Reason,
                    "NoQualifiedActiveRewardTarget",
                    StringComparison.Ordinal);
                if (!completePhysicalEmpty)
                {
                    rewardTargetConsecutiveEmptyScans = 0;
                    rewardTargetLastEmptyScanFrame = -1;
                }
                else if (Time.frameCount !=
                         rewardTargetLastEmptyScanFrame)
                {
                    rewardTargetLastEmptyScanFrame = Time.frameCount;
                    rewardTargetConsecutiveEmptyScans++;
                }

                if (completePhysicalEmpty &&
                    rewardTargetConsecutiveEmptyScans >= 2)
                {
                    rewardTargetEmptyBaselineRequired = false;
                    rewardTargetRearmDeferralLogged = false;
                    BonusRunnerLog.Debug(
                        $"RewardTargetRearmBaselineEstablished Section=" +
                        $"{state.SectionIndex}, EmptyScans=" +
                        $"{rewardTargetConsecutiveEmptyScans}/2, " +
                        $"TargetScan=[{rewardTargetObservation.Describe()}]. " +
                        "Future typed targets belong to the new section epoch.",
                        "Completion");
                }
                else if (completePhysicalEmpty)
                {
                    BonusRunnerLog.Debug(
                        $"RewardTargetRearmEmptyScan Section=" +
                        $"{state.SectionIndex}, EmptyScans=" +
                        $"{rewardTargetConsecutiveEmptyScans}/2, " +
                        $"TargetScan=[{rewardTargetObservation.Describe()}].",
                        "Completion");
                }
            }
        }
        if (rewardTargetObservation.IsLatched &&
            !state.IsActiveGameplay)
        {
            rewardLatchObservedDuringInactiveFrame = true;
        }

        string signature =
            $"{rewardTargetObservation.ScanSucceeded}:" +
            $"{rewardTargetObservation.CandidateQualified}:" +
            $"{rewardTargetObservation.IsLatched}:" +
            $"{rewardTargetObservation.InstanceId}:" +
            $"{rewardTargetObservation.ConsecutiveObservations}:" +
            $"{rewardTargetObservation.Reason}:" +
            $"{state.RewardFlagsAvailable}:" +
            $"{state.WaitingForRewardZone}:" +
            $"{state.RewardZoneEntered}:{state.GivingRewards}";
        bool observationChanged = !string.Equals(
                signature,
                lastRewardTargetObservationSignature,
                StringComparison.Ordinal);
        if (observationChanged)
        {
            lastRewardTargetObservationSignature = signature;
            BonusRunnerLog.Debug(
                $"RewardTargetObservation Allowed={observationAllowed}, " +
                $"ControllerSection={state.SectionIndex}, TerrainSection=" +
                $"{lastActiveTerrainSection}, GameState=" +
                $"{state.GameStateName}, Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Target[{rewardTargetObservation.Describe()}], NativeFlags" +
                $"[Available={state.RewardFlagsAvailable},Wait=" +
                $"{state.WaitingForRewardZone},Trigger=" +
                $"{state.RewardZoneEntered},Giving={state.GivingRewards}].",
                "Completion");
        }

        if (rewardTargetObservation.LatchStarted)
        {
            BonusRunnerLog.Debug(
                $"RewardObjectPhaseLatched TerrainSection=" +
                $"{lastActiveTerrainSection}, ControllerSection=" +
                $"{state.SectionIndex}, Target=" +
                $"[{rewardTargetObservation.Describe()}]. Terrain input " +
                "will be released atomically before minimum jumps, arrows, " +
                "and optional grounded Wind Dash begin.",
                "Completion");
        }
        else if (state.GivingRewards &&
                 !rewardTargetObservation.IsLatched &&
                 observationChanged)
        {
            BonusRunnerLog.Debug(
                $"RewardObjectGateMismatch NativeGiving=True, TypedLatch=" +
                $"False, Target[{rewardTargetObservation.Describe()}]. " +
                "Terrain navigation remains authoritative until an actual " +
                "box or reward collectable is confirmed.",
                "Completion");
        }
    }

    private void ObservePostQuotaRoadPhase(BonusStageState state)
    {
        if (!state.IsBonusStage)
        {
            ResetPostQuotaRoadPhase("OutsideBonusStage");
            ResetQuotaTransitionEvidence("OutsideBonusStage");
            previousRewardFlagsAvailable = false;
            previousNativeWaitingForRewardZone = false;
            previousNativeRewardZone = false;
            previousNativeGivingRewards = false;
            return;
        }

        // A supported Bonus Stage advances section indexes monotonically.
        // Seeing the controller wrap from a late section back to the opening
        // section is therefore a new run even if no non-bonus render frame
        // was captured between the two sessions. Clear old 158/158-style
        // evidence before it can authorize the new run.
        if (lastQuotaObserverSection >= 3 &&
            state.SectionIndex >= 0 &&
            state.SectionIndex <= 1 &&
            state.SectionIndex < lastQuotaObserverSection)
        {
            BonusRunnerLog.Debug(
                $"QuotaEvidenceEpochChanged Section=" +
                $"{lastQuotaObserverSection}->{state.SectionIndex}. " +
                "A controller-index wrap starts a fresh Bonus Stage; stale " +
                "quota/reward-road evidence was discarded.",
                "Completion");
            ResetPostQuotaRoadPhase("ControllerSectionWrapped");
            ResetQuotaTransitionEvidence("ControllerSectionWrapped");
        }
        lastQuotaObserverSection = state.SectionIndex;
        if (quotaEvidencePlayerInstanceId == 0 && state.PlayerInstanceId != 0)
            quotaEvidencePlayerInstanceId = state.PlayerInstanceId;

        bool quotaMet =
            state.HasSphereProgress &&
            state.CollectedSpheres >=
                (int)Math.Ceiling(state.RequiredSpheres);
        bool quotaIncomplete =
            state.HasSphereProgress &&
            state.RequiredSpheres > 0d &&
            state.CollectedSpheres <
                (int)Math.Ceiling(state.RequiredSpheres);
        bool incompleteQuotaProvenBeforeFrame =
            incompleteQuotaObserved;

        // Any real active-gameplay frame for a different section proves that
        // the prior road/reward phase is over. Stop here after recording a
        // fresh incomplete quota; falling through and re-arming on a stale
        // collected value was the old cross-section race.
        if (postQuotaRoadActive &&
            state.IsActiveGameplay &&
            state.SectionIndex != postQuotaCompletedSection)
        {
            int completedSection = postQuotaCompletedSection;
            ResetPostQuotaRoadPhase(
                $"NextSectionGameplay:{completedSection}->" +
                $"{state.SectionIndex}");
            ResetQuotaTransitionEvidence(
                $"NextSectionGameplay:{completedSection}->" +
                $"{state.SectionIndex}");
            lastQuotaObserverSection = state.SectionIndex;
            quotaEvidencePlayerInstanceId = state.PlayerInstanceId;
            if (quotaIncomplete)
            {
                incompleteQuotaObserved = true;
                incompleteQuotaSection = state.SectionIndex;
                quotaEvidencePlayerInstanceId = state.PlayerInstanceId;
                BonusRunnerLog.Debug(
                    $"IncompleteQuotaObserved Section=" +
                    $"{incompleteQuotaSection}, Spheres=" +
                    $"{state.CollectedSpheres}/" +
                    $"{Math.Ceiling(state.RequiredSpheres):F0}, " +
                    "Source=NextSectionGameplayBarrier.",
                    "Completion");
            }
            ObserveRewardWaitBaseline(
                state,
                "NextSectionGameplayBarrier");
            previousRewardFlagsAvailable =
                state.RewardFlagsAvailable;
            if (state.RewardFlagsAvailable)
            {
                previousNativeWaitingForRewardZone =
                    state.WaitingForRewardZone;
                previousNativeRewardZone = state.RewardZoneEntered;
                previousNativeGivingRewards = state.GivingRewards;
            }
            return;
        }

        // The completed section identity comes only from a current-run frame
        // where that same section was genuinely below quota. Neither
        // runTrackingActive nor mutable previousSectionIndex is sufficient:
        // both can coexist with a pooled controller's stale totals.
        if (state.IsActiveGameplay &&
            incompleteQuotaObserved &&
            state.SectionIndex != incompleteQuotaSection)
        {
            ResetQuotaTransitionEvidence(
                $"FreshActiveSection:{incompleteQuotaSection}->" +
                $"{state.SectionIndex}");
            incompleteQuotaProvenBeforeFrame = false;
            lastQuotaObserverSection = state.SectionIndex;
            quotaEvidencePlayerInstanceId = state.PlayerInstanceId;
        }
        if (state.IsActiveGameplay && quotaIncomplete &&
            (!incompleteQuotaObserved ||
             incompleteQuotaSection != state.SectionIndex))
        {
            incompleteQuotaObserved = true;
            incompleteQuotaSection = state.SectionIndex;
            quotaEvidencePlayerInstanceId = state.PlayerInstanceId;
            BonusRunnerLog.Debug(
                $"IncompleteQuotaObserved Section=" +
                $"{incompleteQuotaSection}, Spheres=" +
                $"{state.CollectedSpheres}/" +
                $"{Math.Ceiling(state.RequiredSpheres):F0}, " +
                $"GameState={state.GameStateName}, Player=" +
                $"{quotaEvidencePlayerInstanceId}.",
                "Completion");
        }
        ObserveRewardWaitBaseline(state, "CurrentSectionGameplay");

        bool waitForRewardRising =
            state.RewardFlagsAvailable &&
            previousRewardFlagsAvailable &&
            state.WaitingForRewardZone &&
            !previousNativeWaitingForRewardZone;
        bool waitTransitionObserved =
            state.RewardFlagsAvailable &&
            waitFalseObservedAfterIncompleteQuota &&
            state.WaitingForRewardZone;
        bool quotaCompletionObserved =
            quotaMet &&
            (state.SectionIndex == incompleteQuotaSection ||
             !state.IsActiveGameplay);
        bool nativeWaitCompletionObserved =
            waitTransitionObserved &&
            incompleteQuotaProvenBeforeFrame &&
            !state.IsActiveGameplay &&
            state.SectionIndex != incompleteQuotaSection;
        bool completionTransitionObserved =
            incompleteQuotaObserved &&
            (quotaCompletionObserved ||
             nativeWaitCompletionObserved);
        if (!postQuotaRoadActive && completionTransitionObserved)
        {
            postQuotaRoadActive = true;
            postQuotaCompletedSection = incompleteQuotaSection;
            postQuotaRewardTriggerObserved = state.RewardZoneEntered;
            postQuotaGivingRewardsObserved = state.GivingRewards;
            lastRewardPhaseSignature = string.Empty;
            BonusRunnerLog.Debug(
                $"PostQuotaRoadArmed CompletedSection=" +
                $"{postQuotaCompletedSection}, ControllerSection=" +
                $"{state.SectionIndex}, GameState={state.GameStateName}, " +
                $"Position=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Spheres=" +
                $"{state.CollectedSpheres}/" +
                $"{Math.Ceiling(state.RequiredSpheres):F0}, " +
                $"RewardFlags[Wait={state.WaitingForRewardZone}," +
                $"Trigger={state.RewardZoneEntered},Giving=" +
                $"{state.GivingRewards},Available=" +
                $"{state.RewardFlagsAvailable}], Proof=" +
                $"IncompleteSection{incompleteQuotaSection}/Player" +
                $"{quotaEvidencePlayerInstanceId}, Transition=" +
                $"{(quotaCompletionObserved ? "QuotaMet" : "NativeWaitAfterFalseBaselineAndSectionAdvance")}. " +
                "The completed section's map " +
                "identity remains authoritative until the native reward " +
                "detector/giving phase is observed.",
                "Completion");
        }

        bool rewardTriggerRising =
            state.RewardFlagsAvailable &&
            previousRewardFlagsAvailable &&
            state.RewardZoneEntered && !previousNativeRewardZone;
        bool givingRewardsRising =
            state.RewardFlagsAvailable &&
            previousRewardFlagsAvailable &&
            state.GivingRewards && !previousNativeGivingRewards;
        if (postQuotaRoadActive)
        {
            postQuotaRewardTriggerObserved |= state.RewardZoneEntered;
            postQuotaGivingRewardsObserved |= state.GivingRewards;
        }

        string signature =
            $"{postQuotaRoadActive}:{postQuotaCompletedSection}:" +
            $"{postQuotaRewardTriggerObserved}:" +
            $"{postQuotaGivingRewardsObserved}:" +
            $"{state.RewardFlagsAvailable}:" +
            $"{state.WaitingForRewardZone}:{state.RewardZoneEntered}:" +
            $"{state.GivingRewards}:{state.SectionIndex}:" +
            $"{state.GameStateName}";
        if (!string.Equals(
                signature,
                lastRewardPhaseSignature,
                StringComparison.Ordinal))
        {
            lastRewardPhaseSignature = signature;
            BonusRunnerLog.Debug(
                $"RewardPhaseObservation RoadActive=" +
                $"{postQuotaRoadActive}, CompletedSection=" +
                $"{postQuotaCompletedSection}, ControllerSection=" +
                $"{state.SectionIndex}, Available=" +
                $"{state.RewardFlagsAvailable}, Wait=" +
                $"{state.WaitingForRewardZone}, " +
                $"Trigger={state.RewardZoneEntered}, Giving=" +
                $"{state.GivingRewards}, TriggerRising=" +
                $"{rewardTriggerRising}, WaitRising=" +
                $"{waitForRewardRising}, GivingRising=" +
                $"{givingRewardsRising}, Navigation=" +
                $"{IsSuccessfulCompletionTraversal(state)}, " +
                $"RewardActions={IsNativeRewardActionPhase(state)}.",
                "Completion");
        }

        previousRewardFlagsAvailable =
            state.RewardFlagsAvailable;
        if (state.RewardFlagsAvailable)
        {
            previousNativeWaitingForRewardZone =
                state.WaitingForRewardZone;
            previousNativeRewardZone = state.RewardZoneEntered;
            previousNativeGivingRewards = state.GivingRewards;
        }
    }

    private void ResetQuotaTransitionEvidence(string reason)
    {
        if (incompleteQuotaObserved)
        {
            BonusRunnerLog.Debug(
                $"QuotaTransitionEvidenceReset Reason={reason}, " +
                $"IncompleteSection={incompleteQuotaSection}, Player=" +
                $"{quotaEvidencePlayerInstanceId}.",
                "Completion");
        }
        incompleteQuotaObserved = false;
        incompleteQuotaSection = -1;
        quotaEvidencePlayerInstanceId = 0;
        lastQuotaObserverSection = -1;
        waitFalseObservedAfterIncompleteQuota = false;
    }

    private void ObserveRewardWaitBaseline(
        BonusStageState state,
        string source)
    {
        if (waitFalseObservedAfterIncompleteQuota ||
            !incompleteQuotaObserved ||
            !state.IsActiveGameplay ||
            state.SectionIndex != incompleteQuotaSection ||
            !state.HasSphereProgress ||
            state.RemainingRequiredSpheres <= 0 ||
            !state.RewardFlagsAvailable ||
            state.WaitingForRewardZone)
        {
            return;
        }

        waitFalseObservedAfterIncompleteQuota = true;
        BonusRunnerLog.Debug(
            $"RewardWaitFalseBaselineObserved Section=" +
            $"{incompleteQuotaSection}, ControllerSection=" +
            $"{state.SectionIndex}, Source={source}. A later readable " +
            "Wait=true may corroborate quota completion; a sticky level " +
            "without this current-section false baseline cannot arm the " +
            "reward road.",
            "Completion");
    }

    private void ResetPostQuotaRoadPhase(string reason)
    {
        if (postQuotaRoadActive)
        {
            BonusRunnerLog.Debug(
                $"PostQuotaRoadReset Reason={reason}, CompletedSection=" +
                $"{postQuotaCompletedSection}, TriggerObserved=" +
                $"{postQuotaRewardTriggerObserved}, GivingObserved=" +
                $"{postQuotaGivingRewardsObserved}.",
                "Completion");
        }
        postQuotaRoadActive = false;
        postQuotaCompletedSection = -1;
        postQuotaRewardTriggerObserved = false;
        postQuotaGivingRewardsObserved = false;
        lastRewardPhaseSignature = string.Empty;
    }

    private BonusStageState GetRoutingState(BonusStageState state)
    {
        if (!IsSuccessfulCompletionTraversal(state) ||
            lastActiveTerrainSection < 0 ||
            state.SectionIndex == lastActiveTerrainSection)
        {
            return state;
        }

        return state with
        {
            SectionIndex = lastActiveTerrainSection
        };
    }

    private void RefreshMapRegistry(BonusStageState state)
    {
        BonusStageState routingState = GetRoutingState(state);
        int routingSection = routingState.SectionIndex;
        if (!state.IsBonusStage || routingSection < 0)
        {
            platformScanner.SetLiveDownstreamAlternatives(false);
            if (lastMapRegistryGeneration >= 0)
                platformScanner.ResetStaticMap("OutsideBonusStage");
            lastMapRegistryGeneration = -1;
            lastMapRegistryStatus = string.Empty;
            nextMapRegistryRefreshTime = 0f;
            return;
        }

        BonusMapPieceRegistry registry = platformScanner.MapRegistry;
        if (routingState.UsesStage2LiveRouting)
        {
            platformScanner.SetLiveDownstreamAlternatives(true);
            const string stage2Profile =
                "Stage2AuthoredTopologyAndLiveTalents";
            if (!string.Equals(
                    lastMapRoutingProfile,
                    stage2Profile,
                    StringComparison.Ordinal))
            {
                lastMapRoutingProfile = stage2Profile;
                nextMapRegistryRefreshTime = 0f;
                lastMapRegistryGeneration = -1;
                lastMapRegistryStatus = string.Empty;
                BonusRunnerLog.Debug(
                    $"MapRoutingProfile Map={routingState.MapName}, " +
                    $"Section={routingSection}, Profile={stage2Profile}. " +
                    "The extracted Stage-2 ground cycle supplies stable " +
                    "stairs, tunnels, flat lanes, and wall tops while live " +
                    "colliders remain authoritative for talents and physical " +
                    "contact. Stage-1 and Stage-3 policies are unchanged.",
                    "Map");
            }

            bool stage2SectionChanged =
                registry.Profile != BonusMapProfile.Stage2 ||
                registry.SectionIndex != routingSection;
            if (!stage2SectionChanged &&
                registry.State == BonusMapPieceRegistryState.Ready &&
                Time.unscaledTime < nextMapRegistryRefreshTime)
            {
                return;
            }

            bool stage2Ready = platformScanner.RefreshStaticMap(
                routingSection,
                BonusMapProfile.Stage2);
            nextMapRegistryRefreshTime =
                Time.unscaledTime + (stage2Ready ? 0.25f : 0.05f);
            string stage2Status =
                $"{registry.Profile}:{registry.State}:" +
                $"{registry.StatusReason}:Section={registry.SectionIndex}:" +
                $"Pieces={registry.PieceCount}";
            if (registry.Generation == lastMapRegistryGeneration &&
                string.Equals(
                    stage2Status,
                    lastMapRegistryStatus,
                    StringComparison.Ordinal))
            {
                return;
            }

            bool stage2GenerationChanged =
                lastMapRegistryGeneration >= 0 &&
                registry.Generation != lastMapRegistryGeneration;
            BonusRunnerLog.Debug(
                $"Stage2StaticMapRegistry State={registry.State}, " +
                $"Section={registry.SectionIndex}, ControllerSection=" +
                $"{state.SectionIndex}, LastActiveTerrainSection=" +
                $"{lastActiveTerrainSection}, Generation=" +
                $"{registry.Generation}, Pieces={registry.PieceCount}, " +
                $"Reason={registry.StatusReason}, GenerationChanged=" +
                $"{stage2GenerationChanged}. " +
                (stage2Ready
                    ? "The fixed Stage-2 prefab cycle is aligned to the live " +
                      "pool; exact local stair and wall tops now supplement " +
                      "the collider scan."
                    : "The live scanner remains authoritative while the " +
                      "Stage-2 pool is between generations."),
                "Map");
            lastMapRegistryGeneration = registry.Generation;
            lastMapRegistryStatus = stage2Status;
            return;
        }

        if (!routingState.UsesStage3AuthoredRouting)
        {
            platformScanner.SetLiveDownstreamAlternatives(true);
            string profile = $"LiveGeometrySoulAware:{routingState.MapName}";
            if (!string.Equals(
                    lastMapRoutingProfile,
                    profile,
                    StringComparison.Ordinal))
            {
                platformScanner.ResetStaticMap(profile);
                lastMapRegistryGeneration = -1;
                lastMapRegistryStatus = string.Empty;
                nextMapRegistryRefreshTime = 0f;
                lastMapRoutingProfile = profile;
                BonusRunnerLog.Debug(
                    $"MapRoutingProfile Map={routingState.MapName}, " +
                    $"Section={routingSection}, Profile=LiveGeometrySoulAware. " +
                    "The live all-layer collider scan, dynamic speed model, " +
                    "hazard checks, wall recovery, and active-sphere objectives " +
                    "are enabled. Stage-3 static pieces and authored route " +
                    "contracts are explicitly disabled for this map.",
                    "Map");
            }
            return;
        }

        platformScanner.SetLiveDownstreamAlternatives(false);
        const string authoredProfile = "Stage3AuthoredStaticAndLive";
        if (!string.Equals(
                lastMapRoutingProfile,
                authoredProfile,
                StringComparison.Ordinal))
        {
            // A prior Stage 1/2 run intentionally reset the registry. Force a
            // fresh Stage 3 registration without altering any Stage 3 route
            // or physics policy.
            lastMapRoutingProfile = authoredProfile;
            nextMapRegistryRefreshTime = 0f;
            BonusRunnerLog.Debug(
                $"MapRoutingProfile Map={routingState.MapName}, " +
                $"Section={routingSection}, " +
                "Profile=Stage3AuthoredStaticAndLive. Existing mature " +
                "Stage-3 route policy is preserved.",
                "Map");
        }
        bool sectionChanged = registry.SectionIndex != routingSection;
        if (!sectionChanged &&
            registry.State == BonusMapPieceRegistryState.Ready &&
            Time.unscaledTime < nextMapRegistryRefreshTime)
            return;

        bool ready = platformScanner.RefreshStaticMap(routingSection);
        nextMapRegistryRefreshTime = Time.unscaledTime + (ready ? 0.25f : 0.05f);
        string status = $"{registry.State}:{registry.StatusReason}:" +
                        $"Section={registry.SectionIndex}:Pieces={registry.PieceCount}";
        if (registry.Generation == lastMapRegistryGeneration &&
            string.Equals(status, lastMapRegistryStatus, StringComparison.Ordinal))
        {
            return;
        }

        bool generationChanged =
            lastMapRegistryGeneration >= 0 &&
            registry.Generation != lastMapRegistryGeneration;
        BonusRunnerLog.Debug(
            $"StaticMapRegistry State={registry.State}, " +
            $"Section={registry.SectionIndex}, ControllerSection=" +
            $"{state.SectionIndex}, LastActiveTerrainSection=" +
            $"{lastActiveTerrainSection}, Generation={registry.Generation}, " +
            $"Pieces={registry.PieceCount}, Reason={registry.StatusReason}, " +
            $"GenerationChanged={generationChanged}. " +
            (ready
                ? "Ground clone transforms are aligned with the embedded static map."
                : "Static routing is pending; live geometry remains the fallback."),
            "Map");
        // Pool recycling behind the player changes the registry generation,
        // but it does not invalidate an unchanged live target ahead. Route
        // locks already revalidate target bounds, height, collider/static
        // identity, speed and physics every frame. Clearing them globally on
        // every remote clone hand-off repeatedly erased launch windows and
        // wall follow-through previews.
        lastMapRegistryGeneration = registry.Generation;
        lastMapRegistryStatus = status;
    }

    private void ObserveReliableHorizontalSpeed(BonusStageState state)
    {
        if (!state.IsBonusStage || !state.HasPlayer)
            return;

        float observed = Mathf.Abs(state.PlayerVelocity.x);
        bool inRange = observed > 1f && observed < 80f;
        bool wallControlActive =
            passiveWallApproachActive ||
            attachedObjectiveDescentActive ||
            wallExitContactWatchActive ||
            wallActionPhase != WallActionPhase.None &&
            wallActionPhase != WallActionPhase.Completed &&
            wallActionPhase != WallActionPhase.Failed;
        bool collisionLikeAirborneDrop =
            !state.IsGrounded &&
            lastReliableHorizontalSpeed > 2f &&
            observed < lastReliableHorizontalSpeed * 0.65f;
        bool legitimateWallBoostIncrease =
            wallControlActive &&
            wallRouteSpeedLatched &&
            state.SpiritBoostEnabled &&
            inRange &&
            observed > wallRouteHorizontalSpeed + 1.0f &&
            observed > lastReliableHorizontalSpeed + 1.0f;
        bool completionTraversal = IsSuccessfulCompletionTraversal(state);
        if (completionTraversal && inRange)
        {
            int completionTerrainSection = Mathf.Clamp(
                lastActiveTerrainSection >= 0
                    ? lastActiveTerrainSection
                    : state.SectionIndex,
                0,
                completionTraversalSpeedCeilingBySection.Length - 1);
            completionTraversalSpeedCeilingBySection[completionTerrainSection] =
                Mathf.Max(
                    completionTraversalSpeedCeilingBySection[
                        completionTerrainSection],
                    observed);
        }
        bool routeMotionActive =
            state.IsActiveGameplay || completionTraversal;
        bool accepted =
            legitimateWallBoostIncrease ||
            inRange &&
            routeMotionActive &&
            !pitDescentGuardActive &&
            !wallControlActive &&
            !collisionLikeAirborneDrop;

        string decision = legitimateWallBoostIncrease
            ? "AcceptedWallSpiritBoostIncrease"
            : accepted
            ? completionTraversal
                ? "AcceptedCompletionTraversal"
                : "AcceptedStableRun"
            : !inRange
                ? "RejectedOutOfRange"
                : !routeMotionActive
                    ? "RejectedInactiveGameplay"
                    : pitDescentGuardActive
                        ? "RejectedPitLifecycle"
                        : wallControlActive
                            ? "RejectedWallAction"
                            : collisionLikeAirborneDrop
                                ? "RejectedAirborneCollisionDrop"
                                : "RejectedUnknown";
        float prior = lastReliableHorizontalSpeed;
        if (accepted)
        {
            float acceptedSampleGap = lastAcceptedHorizontalSpeedAt >= 0f
                ? Time.unscaledTime - lastAcceptedHorizontalSpeedAt
                : float.NaN;
            bool accelerationSampleEligible =
                !float.IsNaN(acceptedSampleGap) &&
                acceptedSampleGap >= 0.008f &&
                acceptedSampleGap <= 0.50f &&
                observed > prior + 0.05f &&
                (state.SpiritBoostEnabled || completionTraversal);
            if (accelerationSampleEligible)
            {
                float accelerationSample = Mathf.Clamp(
                    (observed - prior) / acceptedSampleGap,
                    0f,
                    120f);
                positiveHorizontalAccelerationEnvelope = Mathf.Max(
                    positiveHorizontalAccelerationEnvelope * 0.90f,
                    accelerationSample);
            }
            else if (!state.SpiritBoostEnabled && !completionTraversal)
            {
                positiveHorizontalAccelerationEnvelope *= 0.80f;
            }
            else
            {
                positiveHorizontalAccelerationEnvelope *= 0.97f;
            }
            lastAcceptedHorizontalSpeedAt = Time.unscaledTime;
            lastReliableHorizontalSpeed = observed;
            if (legitimateWallBoostIncrease)
                wallRouteHorizontalSpeed = observed;

            // A large one-physics-step velocity discontinuity is an ability or
            // pickup transition, not evidence that an already calculated
            // launch window remains valid.  Recompute immediately, but defer
            // an actual ground-jump DOWN until one subsequent FixedUpdate has
            // exposed the new velocity.  Passive wall-contact routes remain
            // legal because they send no airborne input.
            float discontinuity = Mathf.Abs(observed - prior);
            float discontinuityThreshold = Mathf.Max(4.0f, prior * 0.20f);
            if (prior > 1f && discontinuity >= discontinuityThreshold)
            {
                speedPlanningResumeFixedStep = Math.Max(
                    speedPlanningResumeFixedStep,
                    JumpPhysicsFeedback.FixedStepSequence + 1);
            }
        }
        ObserveSectionCruiseSpeed(
            state,
            observed,
            accepted && !legitimateWallBoostIncrease && !wallControlActive);

        bool decisionChanged = !string.Equals(
            decision,
            lastSpeedObservationDecision,
            StringComparison.Ordinal);
        if (BonusRunnerLog.IsDebugMode &&
            (decisionChanged || Time.unscaledTime >= nextSpeedObservationLogTime))
        {
            lastSpeedObservationDecision = decision;
            nextSpeedObservationLogTime = Time.unscaledTime + 0.50f;
            BonusRunnerLog.Debug(
                $"HorizontalSpeedObservation ObservedVX={observed:F3}, " +
                $"PriorRunSpeed={prior:F3}, RunSpeedEstimate=" +
                $"{lastReliableHorizontalSpeed:F3}, WallEntrySpeed=" +
                $"{(wallRouteSpeedLatched ? wallRouteHorizontalSpeed.ToString("F3") : "Unavailable")}, " +
                $"SectionCruiseVX=" +
                $"{(sectionCruiseHorizontalSpeed > 1f ? sectionCruiseHorizontalSpeed.ToString("F3") : "Unresolved")}, " +
                $"CruiseCandidate={sectionCruiseCandidateSpeed:F3}/" +
                $"{sectionCruiseCandidateFixedSteps}, " +
                $"PositiveAccelerationEnvelope=" +
                $"{positiveHorizontalAccelerationEnvelope:F3}, " +
                $"SpeedUpdateAccepted={accepted}, Decision={decision}, " +
                $"RouteMotionActive={routeMotionActive}, " +
                $"JumpResumeFixedStep={speedPlanningResumeFixedStep}, " +
                $"Grounded={state.IsGrounded}, SpiritBoost=" +
                $"{state.SpiritBoostEnabled}, WallPhase={wallActionPhase}.",
                "Physics");
        }
    }

    private void ObserveSectionCruiseSpeed(
        BonusStageState state,
        float observed,
        bool acceptedStableRun)
    {
        bool eligible =
            acceptedStableRun &&
            state.IsActiveGameplay &&
            state.IsGrounded &&
            Mathf.Abs(state.PlayerVelocity.y) <= 2.5f &&
            observed > 1f && observed < 80f;
        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (!eligible)
        {
            if (sectionCruiseCandidateLastFixedStep >= 0 &&
                fixedStep - sectionCruiseCandidateLastFixedStep > 2)
            {
                sectionCruiseCandidateSpeed = 0f;
                sectionCruiseCandidateFixedSteps = 0;
                sectionCruiseCandidateLastFixedStep = -1;
            }
            return;
        }

        if (fixedStep == sectionCruiseCandidateLastFixedStep)
            return;

        const float plateauTolerance = 0.04f;
        bool samePlateau =
            sectionCruiseCandidateFixedSteps > 0 &&
            Mathf.Abs(observed - sectionCruiseCandidateSpeed) <=
                plateauTolerance;
        if (!samePlateau)
        {
            sectionCruiseCandidateSpeed = observed;
            sectionCruiseCandidateFixedSteps = 1;
        }
        else
        {
            sectionCruiseCandidateSpeed =
                (sectionCruiseCandidateSpeed *
                    sectionCruiseCandidateFixedSteps + observed) /
                (sectionCruiseCandidateFixedSteps + 1);
            sectionCruiseCandidateFixedSteps++;
        }
        sectionCruiseCandidateLastFixedStep = fixedStep;

        if (sectionCruiseCandidateFixedSteps < 3)
            return;

        bool firstResolution = sectionCruiseHorizontalSpeed <= 1f;
        bool sameEstablishedTier =
            !firstResolution &&
            Mathf.Abs(
                sectionCruiseCandidateSpeed - sectionCruiseHorizontalSpeed) <=
                0.20f;
        if (!firstResolution && !sameEstablishedTier)
            return;

        sectionCruiseHorizontalSpeed = firstResolution
            ? sectionCruiseCandidateSpeed
            : Mathf.Lerp(
                sectionCruiseHorizontalSpeed,
                sectionCruiseCandidateSpeed,
                0.25f);
        if (firstResolution)
        {
            BonusRunnerLog.Debug(
                $"SectionCruiseSpeedEstablished Section={state.SectionIndex}, " +
                $"CruiseVX={sectionCruiseHorizontalSpeed:F3}, " +
                $"CandidateFixedSteps={sectionCruiseCandidateFixedSteps}, " +
                $"ObservedVX={observed:F3}, SpiritBoostFlag=" +
                $"{state.SpiritBoostEnabled}. Three exact grounded plateaus " +
                "define the section deceleration floor. A Spirit pickup " +
                "boost changes by about 0.10 per fixed step while decaying, " +
                "so it cannot satisfy this plateau gate.",
                "Physics");
        }
    }

    private void ResetCompletionTraversalSpeedEvidence(string reason)
    {
        float priorEnvelope = positiveHorizontalAccelerationEnvelope;
        float priorMaximumCeiling = 0f;
        for (int index = 0;
             index < completionTraversalSpeedCeilingBySection.Length;
             index++)
        {
            priorMaximumCeiling = Mathf.Max(
                priorMaximumCeiling,
                completionTraversalSpeedCeilingBySection[index]);
        }

        positiveHorizontalAccelerationEnvelope = 0f;
        lastAcceptedHorizontalSpeedAt = -1f;
        System.Array.Clear(
            completionTraversalSpeedCeilingBySection,
            0,
            completionTraversalSpeedCeilingBySection.Length);

        if (BonusRunnerLog.IsDebugMode &&
            (priorEnvelope > 0.01f || priorMaximumCeiling > 0.01f))
        {
            BonusRunnerLog.Debug(
                $"CompletionSpeedEpochReset Reason={reason}, " +
                $"PriorAccelerationEnvelope={priorEnvelope:F3}, " +
                $"PriorMaximumSectionCeiling={priorMaximumCeiling:F3}. " +
                "No speed evidence from the previous life/terrain epoch " +
                "may influence a new route.",
                "Physics");
        }
    }

    private void ResetSectionCruiseSpeed()
    {
        sectionCruiseHorizontalSpeed = 0f;
        sectionCruiseCandidateSpeed = 0f;
        sectionCruiseCandidateLastFixedStep = -1;
        sectionCruiseCandidateFixedSteps = 0;
        // Completion acceleration is learned only inside the current terrain
        // epoch.  Carrying a prior section/run ceiling into a fresh normal run
        // would make the planner invent speed that the current character has
        // not demonstrated.
        ResetCompletionTraversalSpeedEvidence("SectionCruiseReset");
    }

    private void LatchWallRouteSpeed(BonusStageState state, string source)
    {
        float observed = Mathf.Abs(state.PlayerVelocity.x);
        bool observedUsable = observed > 1f && observed < 80f &&
            (lastReliableHorizontalSpeed <= 1f ||
             observed >= lastReliableHorizontalSpeed * 0.65f ||
             observed > lastReliableHorizontalSpeed);
        wallRouteHorizontalSpeed = observedUsable
            ? observed
            : Mathf.Max(1f, lastReliableHorizontalSpeed);
        wallRouteSpeedLatched = true;
        BonusRunnerLog.Debug(
            $"WallRouteSpeedLatched Source={source}, ObservedVX={observed:F3}, " +
            $"ReliableVX={lastReliableHorizontalSpeed:F3}, " +
            $"WallEntrySpeed={wallRouteHorizontalSpeed:F3}, " +
            $"ObservedAccepted={observedUsable}, SpiritBoost=" +
            $"{state.SpiritBoostEnabled}. Collision/stall samples cannot " +
            "change this route's horizontal prediction until verified landing.",
            "Physics");
    }

    private float GetWallRouteSpeed() => wallRouteSpeedLatched
        ? Mathf.Max(1f, wallRouteHorizontalSpeed)
        : Mathf.Max(1f, lastReliableHorizontalSpeed);

    private float GetCompletionTraversalSpeedCeiling(
        BonusStageState state)
    {
        int section = Mathf.Clamp(
            lastActiveTerrainSection >= 0
                ? lastActiveTerrainSection
                : state.SectionIndex,
            0,
            completionTraversalSpeedCeilingBySection.Length - 1);
        return completionTraversalSpeedCeilingBySection[section];
    }

    [HideFromIl2Cpp]
    private SpiritBoostRouteContext CaptureRouteSpeedContext(
        BonusStageState state,
        PlayerMovement player,
        float left,
        float right,
        float verifiedBaseSpeed)
    {
        bool completionTraversal =
            IsSuccessfulCompletionTraversal(state);
        bool nativeTransientSpeedMayApply =
            state.SpiritBoostEnabled || completionTraversal;
        SpiritBoostRouteContext context =
            BonusStageInspector.CaptureSpiritBoostRouteContext(
                player,
                nativeTransientSpeedMayApply,
                left,
                right,
                verifiedBaseSpeed);

        // A completed section remains ordinary terrain until a typed reward
        // target is actually latched. Native boost fields may still expose a
        // real pending pickup while the Spirit reward flag is false, but an
        // empty or unreadable trigger scan is not acceleration evidence.
        // V0.83 inverted an empty scan into an immediate maximum-speed reset;
        // that made otherwise normal post-quota gaps fail the slow/fast
        // envelope even though no reward object had taken control.
        if (completionTraversal &&
            !state.SpiritBoostEnabled)
        {
            if (!context.KinematicsAvailable ||
                !context.TriggerScanSucceeded)
            {
                return SpiritBoostRouteContext.Disabled(
                    $"CompletionTransientSpeedUnverified[{context.Evidence}]");
            }

            bool verifiedPendingTrigger =
                context.ActiveTriggers != null &&
                context.ActiveTriggers.Any(trigger => trigger.IsValid);
            if (!verifiedPendingTrigger)
            {
                return SpiritBoostRouteContext.Disabled(
                    $"CompletionNoPendingBoostTrigger[{context.Evidence}]");
            }

            context = context with
            {
                Evidence =
                    context.Evidence +
                    ";CompletionVerifiedPendingBoostTrigger"
            };
        }

        return context;
    }

    private float GetWallExitPlanningSpeed(
        BonusStageState state,
        float observedPostLipSpeed,
        out bool accelerationEnvelopeApplied)
    {
        accelerationEnvelopeApplied = false;
        float rawObserved = Mathf.Clamp(
            Mathf.Max(1f, observedPostLipSpeed),
            1f,
            80f);
        float verifiedResumeFloor =
            state.IsActiveGameplay &&
            sectionCruiseHorizontalSpeed > 1f &&
            sectionCruiseHorizontalSpeed < 80f
                ? sectionCruiseHorizontalSpeed
                : 1f;
        // Wall attachment reports VX=0, but the runner restores its section
        // cruise tier immediately after the lip. The contact/entry estimate
        // may therefore be lower than the actual airborne speed. Use the
        // verified grounded plateau as a physical resume floor in every mode.
        float observed = Mathf.Clamp(
            Mathf.Max(rawObserved, verifiedResumeFloor),
            1f,
            80f);
        float observedCompletionCeiling =
            GetCompletionTraversalSpeedCeiling(state);
        bool completionTraversal = IsSuccessfulCompletionTraversal(state);
        bool accelerationEvidence =
            positiveHorizontalAccelerationEnvelope >= 4f;
        bool ceilingEvidence =
            observedCompletionCeiling > observed + 1f;
        // A Spirit wall lip can briefly report Grounded while the body is
        // still rising.  The game may apply its run-speed tier on that one
        // physics step: the retained failure entered the wall at 15.5, had
        // only seen 17.2 in the terrain epoch, then resumed at 22.4 after the
        // lip.  Landing/overshoot planning therefore needs a bounded unseen
        // resume tier even before the first such impulse has been observed.
        // Once the current route is already faster than that measured tier,
        // the live/epoch/envelope evidence remains authoritative.
        const float spiritWallResumeTier = 22.75f;
        float spiritResumeFloor =
            completionTraversal && state.SpiritBoostEnabled
                ? Mathf.Max(observed, spiritWallResumeTier)
                : observed;
        bool acceleratingCompletionTraversal =
            completionTraversal &&
            (accelerationEvidence || ceilingEvidence ||
             spiritResumeFloor > observed + 0.25f);
        if (!acceleratingCompletionTraversal)
        {
            if (completionTraversal)
            {
                BonusRunnerLog.Debug(
                    $"CompletionWallExitSpeedProjection Observed=" +
                    $"{rawObserved:F3}, ResumeFloor=" +
                    $"{verifiedResumeFloor:F3}, Resolved=" +
                    $"{observed:F3}, Ceiling=" +
                    $"{observedCompletionCeiling:F3}, Envelope=" +
                    $"{positiveHorizontalAccelerationEnvelope:F3}, " +
                    $"Planning={observed:F3}, Spirit=" +
                    $"{state.SpiritBoostEnabled}, " +
                    "ProjectionMode=ObservedOnly; no current-epoch " +
                    "acceleration evidence exceeds the safety threshold.",
                    "Physics");
            }
            return observed;
        }

        // The retained V0.39 completion trace accelerated from 15.6 to 22.8
        // immediately after a wall lip. Treat recent accepted positive dVX/dt
        // as a bounded half-flight average-speed envelope. This is deliberately
        // restricted to post-quota traversal. Pre-quota Spirit routes retain
        // their established decay-only planning. Do not impose the old
        // synthetic +4 floor on a normal run; use only the measured
        // current-epoch ceiling and an actually observed dVX/dt envelope.
        float projectedGain = accelerationEvidence
            ? Mathf.Clamp(
                positiveHorizontalAccelerationEnvelope * 0.25f,
                0f,
                12f)
            : 0f;
        float planningSpeed = Mathf.Clamp(
            Mathf.Max(
                Mathf.Max(
                    observed + projectedGain,
                    observedCompletionCeiling),
                spiritResumeFloor),
            observed,
            80f);
        accelerationEnvelopeApplied = planningSpeed > observed + 0.25f;
        BonusRunnerLog.Debug(
            $"CompletionWallExitSpeedProjection Observed={rawObserved:F3}, " +
            $"ResumeFloor={verifiedResumeFloor:F3}, Resolved=" +
            $"{observed:F3}, " +
            $"Ceiling={observedCompletionCeiling:F3}, Envelope=" +
            $"{positiveHorizontalAccelerationEnvelope:F3}, ProjectedGain=" +
            $"{projectedGain:F3}, SpiritResumeFloor=" +
            $"{spiritResumeFloor:F3}, Planning={planningSpeed:F3}, Spirit=" +
            $"{state.SpiritBoostEnabled}, ProjectionMode=" +
            $"{(state.SpiritBoostEnabled && spiritResumeFloor > observed + 0.25f ? "SpiritResumeBound" : accelerationEvidence && ceilingEvidence ? "EnvelopeAndEpochCeiling" : accelerationEvidence ? "Envelope" : "EpochCeiling")}. " +
            "Only evidence collected in the current terrain epoch is used.",
            "Physics");
        return planningSpeed;
    }

    [HideFromIl2Cpp]
    private static JumpPhysicsSnapshot BuildPlanningPhysics(
        int sectionIndex,
        JumpPhysicsSnapshot observed,
        float liveRunSpeed,
        bool useLiveRunSpeedFloor,
        float verifiedSectionCruiseSpeed = 0f)
    {
        // Route calibration is reset at every section boundary, so these
        // profiles contain only same-section observations. Keep the helper at
        // every planning boundary to make that invariant explicit and to
        // prevent future callers from bypassing the section-scoped snapshot.
        _ = sectionIndex;
        if (!useLiveRunSpeedFloor ||
            liveRunSpeed <= 1f || liveRunSpeed >= 80f)
        {
            return observed;
        }

        // A verified grounded plateau remains constant through an ordinary
        // active-gameplay jump. A Spirit pickup, however, adds a transient
        // horizontal tier which visibly decays by BoostHorizontalDeceleration
        // back to that plateau. V0.55 incorrectly promoted every live sample
        // to the floor, so 20.3 was integrated as a constant even while the
        // rigidbody fell back toward the Section-3 16.9 plateau. Preserve the
        // live floor only when it is itself the verified cruise tier; otherwise
        // retain the established section floor and let the dynamic integral
        // model the decay. Pre-reward completion passes the same live floor
        // as ordinary active gameplay; only verified Spirit evidence supplies
        // the separate section-cruise override.
        float resolvedFlightFloor = liveRunSpeed;
        if (verifiedSectionCruiseSpeed > 1f &&
            verifiedSectionCruiseSpeed < 80f &&
            liveRunSpeed > verifiedSectionCruiseSpeed + 0.25f)
        {
            resolvedFlightFloor = verifiedSectionCruiseSpeed;
        }
        return observed with
        {
            BaseHorizontalSpeed = Mathf.Max(
                observed.BaseHorizontalSpeed,
                resolvedFlightFloor)
        };
    }

    [HideFromIl2Cpp]
    private static JumpPhysicsSnapshot BuildWallPlanningPhysics(
        JumpPhysicsSnapshot observed)
    {
        float wallJumpVelocity = Mathf.Clamp(observed.RawJumpForce, 5f, 50f);
        return observed with
        {
            JumpVelocity = wallJumpVelocity,
            VelocitySampleCount = 0,
            VelocitySource = "WallJumpForce",
            InputDelaySeconds = Mathf.Clamp(
                observed.FixedDeltaTime,
                0f,
                0.25f),
            DelaySampleCount = 0,
            DelaySource = "WallFixedStep",
            HorizontalTravelScale = 1f,
            LandingSampleCount = 0,
            TravelProfile = default,
            LandingErrorProfile = default
        };
    }

    [HideFromIl2Cpp]
    private static JumpPhysicsSnapshot BuildWallExitPlanningPhysics(
        JumpPhysicsSnapshot wallPhysics,
        float postLipHorizontalSpeed,
        float sectionCruiseSpeed,
        bool usePostLipAsCruiseFloor)
    {
        // Wall contact has two horizontal phases: VX is physically zero while
        // attached, then the pre-contact run speed resumes after lip clearance.
        // Wall flight timing is independent of the ground-jump flight-scale
        // calibration, and the normal Section-3 trace proves that resumed
        // 16.9 speed stays constant through landing. The retained V0.51 trace
        // proves the same invariant for active-gameplay Spirit tiers.
        // Completion abilities remain transient and retain the separate
        // deceleration/envelope model.
        float resolvedBaseSpeed = wallPhysics.BaseHorizontalSpeed;
        if (sectionCruiseSpeed > 1f && sectionCruiseSpeed < 80f)
        {
            resolvedBaseSpeed = Mathf.Max(
                resolvedBaseSpeed,
                sectionCruiseSpeed);
        }
        if (usePostLipAsCruiseFloor)
        {
            resolvedBaseSpeed = Mathf.Max(
                resolvedBaseSpeed,
                Mathf.Clamp(postLipHorizontalSpeed, 1f, 80f));
        }

        return wallPhysics with
        {
            BaseHorizontalSpeed = resolvedBaseSpeed,
            FlightTimeScale = 1f
        };
    }

    // A wall exit is accepted only when the same physical press is safe at
    // both ends of the current speed envelope. Enumerate the full native hold
    // set jointly: a locally optimal fast-only hold must not hide a different
    // hold that safely intersects both the slow and fast landing corridors.
    // Active routes with one effective speed keep the established single-
    // speed solver.
    [HideFromIl2Cpp]
    private bool TryChooseWallExitTransferHoldWithinSpeedEnvelope(
        bool requireEnvelope,
        float contactX,
        float contactFeetY,
        float physicalWallLipY,
        float requiredApexY,
        BonusBoardSegment downstreamTarget,
        float minimumHorizontalSpeed,
        float maximumHorizontalSpeed,
        JumpPhysicsSnapshot physics,
        float releaseTravelBias,
        float minimumHoldSeconds,
        float maximumHoldSeconds,
        float safeEdgeTolerance,
        bool allowRawBodyFitLanding,
        out float selectedHold,
        out float predictedFlightSeconds,
        out float predictedHorizontalTravel,
        out float predictedLandingX,
        out bool selectedRawBodyFit,
        out string summary)
    {
        float slowSpeed = Mathf.Min(
            minimumHorizontalSpeed,
            maximumHorizontalSpeed);
        float fastSpeed = Mathf.Max(
            minimumHorizontalSpeed,
            maximumHorizontalSpeed);
        if (!requireEnvelope || fastSpeed <= slowSpeed + 0.25f)
        {
            bool selected = jumpPlanner.TryChooseWallExitTransferHold(
                contactX,
                contactFeetY,
                physicalWallLipY,
                requiredApexY,
                downstreamTarget,
                fastSpeed,
                physics,
                releaseTravelBias,
                minimumHoldSeconds,
                maximumHoldSeconds,
                safeEdgeTolerance,
                allowRawBodyFitLanding,
                out selectedHold,
                out predictedFlightSeconds,
                out predictedHorizontalTravel,
                out predictedLandingX,
                out selectedRawBodyFit,
                out string singleSummary);
            summary =
                $"SpeedEnvelope[Slow={slowSpeed:F3},Fast={fastSpeed:F3}," +
                $"Validation=SingleSpeed,Selected={selected}] " +
                singleSummary;
            return selected;
        }

        selectedHold = 0f;
        predictedFlightSeconds = 0f;
        predictedHorizontalTravel = 0f;
        predictedLandingX = contactX;
        selectedRawBodyFit = false;
        bool found = false;
        int bestSafetyTier = int.MaxValue;
        float bestWorstMargin = float.NegativeInfinity;
        bool bestObjectiveComplete = false;
        float bestCentreError = float.PositiveInfinity;
        string evaluations = string.Empty;
        float targetCentre =
            (downstreamTarget.SafeLeft + downstreamTarget.SafeRight) * 0.5f;

        foreach (float hold in WallEnvelopeHoldCandidates)
        {
            if (hold + 0.001f < minimumHoldSeconds ||
                hold > maximumHoldSeconds + 0.001f)
            {
                continue;
            }

            bool slowSelected = jumpPlanner.TryChooseWallExitTransferHold(
                contactX,
                contactFeetY,
                physicalWallLipY,
                requiredApexY,
                downstreamTarget,
                slowSpeed,
                physics,
                releaseTravelBias,
                hold,
                hold,
                safeEdgeTolerance,
                allowRawBodyFitLanding,
                out _,
                out float slowFlight,
                out float slowTravel,
                out float slowLanding,
                out bool slowRaw,
                out string slowSummary);
            bool fastSelected = jumpPlanner.TryChooseWallExitTransferHold(
                contactX,
                contactFeetY,
                physicalWallLipY,
                requiredApexY,
                downstreamTarget,
                fastSpeed,
                physics,
                releaseTravelBias,
                hold,
                hold,
                safeEdgeTolerance,
                allowRawBodyFitLanding,
                out _,
                out float fastFlight,
                out float fastTravel,
                out float fastLanding,
                out bool fastRaw,
                out string fastSummary);
            evaluations +=
                $"H={hold:F3}[Slow={slowSelected}/" +
                $"X={slowLanding:F3},Fast={fastSelected}/" +
                $"X={fastLanding:F3},SlowProof=" +
                $"{CompactWallEnvelopeDiagnostic(slowSummary)}," +
                $"FastProof={CompactWallEnvelopeDiagnostic(fastSummary)}] | ";
            if (!slowSelected || !fastSelected)
                continue;

            int safetyTier = slowRaw || fastRaw ? 1 : 0;
            float worstMargin = Mathf.Min(
                Mathf.Min(
                    slowLanding - downstreamTarget.SafeLeft,
                    downstreamTarget.SafeRight - slowLanding),
                Mathf.Min(
                    fastLanding - downstreamTarget.SafeLeft,
                    downstreamTarget.SafeRight - fastLanding));
            bool objectiveComplete =
                slowSummary.Contains(
                    "ObjectiveApex=Complete",
                    StringComparison.Ordinal) &&
                fastSummary.Contains(
                    "ObjectiveApex=Complete",
                    StringComparison.Ordinal);
            float centreError = Mathf.Max(
                Mathf.Abs(slowLanding - targetCentre),
                Mathf.Abs(fastLanding - targetCentre));
            bool better;
            if (!found || safetyTier < bestSafetyTier)
                better = true;
            else if (safetyTier > bestSafetyTier)
                better = false;
            else if (worstMargin > bestWorstMargin + 0.08f)
                better = true;
            else if (worstMargin < bestWorstMargin - 0.08f)
                better = false;
            else if (objectiveComplete != bestObjectiveComplete)
                better = objectiveComplete;
            else
                better = centreError < bestCentreError - 0.001f;
            if (!better)
                continue;

            found = true;
            bestSafetyTier = safetyTier;
            bestWorstMargin = worstMargin;
            bestObjectiveComplete = objectiveComplete;
            bestCentreError = centreError;
            selectedHold = hold;
            predictedFlightSeconds = Mathf.Max(slowFlight, fastFlight);
            // The fast endpoint remains the command's published conservative
            // landing/travel; the complete pair is retained in the summary.
            predictedHorizontalTravel = fastTravel;
            predictedLandingX = fastLanding;
            selectedRawBodyFit = slowRaw || fastRaw;
        }

        summary =
            $"JointSpeedEnvelope[Slow={slowSpeed:F3},Fast={fastSpeed:F3}," +
            $"Found={found},SelectedHold={selectedHold:F3}," +
            $"SafetyTier={bestSafetyTier},WorstMargin=" +
            $"{bestWorstMargin:F3},ObjectiveApex=" +
            $"{(bestObjectiveComplete ? "Complete" : "Fallback")}," +
            $"CentreError={bestCentreError:F3},Candidates[{evaluations}]]";
        return found;
    }

    // Finite-face interception has two opposing failures: the slow endpoint
    // can arrive below the face while the fast endpoint can arrive too early
    // and high. Jointly enumerate every native hold and admit only the
    // intersection of both endpoint proofs.
    [HideFromIl2Cpp]
    private bool TryChooseWallFaceInterceptHoldWithinSpeedEnvelope(
        bool requireEnvelope,
        float contactX,
        float contactFeetY,
        BonusBoardSegment currentWall,
        float physicalWallLipY,
        float requiredApexY,
        BonusBoardSegment downstreamFace,
        float playerHalfWidth,
        float minimumHorizontalSpeed,
        float maximumHorizontalSpeed,
        float minimumContactFeetY,
        float maximumContactFeetY,
        float preferredContactFeetY,
        JumpPhysicsSnapshot physics,
        float minimumHoldSeconds,
        float maximumHoldSeconds,
        bool preserveHoldThroughLip,
        int horizontalTimingToleranceSteps,
        out float selectedHold,
        out float lipCrossingSeconds,
        out float topClearSeconds,
        out float targetContactSeconds,
        out float predictedTopClearFeetY,
        out float predictedContactFeetY,
        out float predictedContactVelocityY,
        out string summary)
    {
        float slowSpeed = Mathf.Min(
            minimumHorizontalSpeed,
            maximumHorizontalSpeed);
        float fastSpeed = Mathf.Max(
            minimumHorizontalSpeed,
            maximumHorizontalSpeed);
        if (!requireEnvelope || fastSpeed <= slowSpeed + 0.25f)
        {
            bool selected = jumpPlanner.TryChooseWallFaceInterceptHold(
                contactX,
                contactFeetY,
                currentWall,
                physicalWallLipY,
                requiredApexY,
                downstreamFace,
                playerHalfWidth,
                slowSpeed,
                minimumContactFeetY,
                maximumContactFeetY,
                preferredContactFeetY,
                physics,
                minimumHoldSeconds,
                maximumHoldSeconds,
                preserveHoldThroughLip,
                horizontalTimingToleranceSteps,
                out selectedHold,
                out lipCrossingSeconds,
                out topClearSeconds,
                out targetContactSeconds,
                out predictedTopClearFeetY,
                out predictedContactFeetY,
                out predictedContactVelocityY,
                out string singleSummary);
            summary =
                $"SpeedEnvelope[Slow={slowSpeed:F3},Fast={fastSpeed:F3}," +
                $"Validation=SingleSpeed,Selected={selected}] " +
                singleSummary;
            return selected;
        }

        selectedHold = 0f;
        lipCrossingSeconds = 0f;
        topClearSeconds = 0f;
        targetContactSeconds = 0f;
        predictedTopClearFeetY = contactFeetY;
        predictedContactFeetY = contactFeetY;
        predictedContactVelocityY = 0f;
        bool found = false;
        float bestWorstSafety = float.NegativeInfinity;
        bool bestObjectiveComplete = false;
        float bestContactError = float.PositiveInfinity;
        string evaluations = string.Empty;

        foreach (float hold in WallEnvelopeHoldCandidates)
        {
            if (hold + 0.001f < minimumHoldSeconds ||
                hold > maximumHoldSeconds + 0.001f)
            {
                continue;
            }

            bool slowSelected = jumpPlanner.TryChooseWallFaceInterceptHold(
                contactX,
                contactFeetY,
                currentWall,
                physicalWallLipY,
                requiredApexY,
                downstreamFace,
                playerHalfWidth,
                slowSpeed,
                minimumContactFeetY,
                maximumContactFeetY,
                preferredContactFeetY,
                physics,
                hold,
                hold,
                preserveHoldThroughLip,
                horizontalTimingToleranceSteps,
                out _,
                out float slowLip,
                out float slowTopTime,
                out float slowContactTime,
                out float slowTopY,
                out float slowContactY,
                out float slowContactVelocity,
                out string slowSummary);
            bool fastSelected = jumpPlanner.TryChooseWallFaceInterceptHold(
                contactX,
                contactFeetY,
                currentWall,
                physicalWallLipY,
                requiredApexY,
                downstreamFace,
                playerHalfWidth,
                fastSpeed,
                minimumContactFeetY,
                maximumContactFeetY,
                preferredContactFeetY,
                physics,
                hold,
                hold,
                preserveHoldThroughLip,
                horizontalTimingToleranceSteps,
                out _,
                out float fastLip,
                out float fastTopTime,
                out float fastContactTime,
                out float fastTopY,
                out float fastContactY,
                out float fastContactVelocity,
                out string fastSummary);
            evaluations +=
                $"H={hold:F3}[Slow={slowSelected}/" +
                $"Y={slowContactY:F3},Fast={fastSelected}/" +
                $"Y={fastContactY:F3},SlowProof=" +
                $"{CompactWallEnvelopeDiagnostic(slowSummary)}," +
                $"FastProof={CompactWallEnvelopeDiagnostic(fastSummary)}] | ";
            if (!slowSelected || !fastSelected)
                continue;

            float slowFaceSafety = Mathf.Min(
                slowContactY - minimumContactFeetY,
                maximumContactFeetY - slowContactY);
            float fastFaceSafety = Mathf.Min(
                fastContactY - minimumContactFeetY,
                maximumContactFeetY - fastContactY);
            float worstSafety = Mathf.Min(
                Mathf.Min(
                    slowTopY - currentWall.Top,
                    fastTopY - currentWall.Top),
                Mathf.Min(slowFaceSafety, fastFaceSafety));
            bool objectiveComplete =
                slowSummary.Contains(
                    "ObjectiveApex=Complete",
                    StringComparison.Ordinal) &&
                fastSummary.Contains(
                    "ObjectiveApex=Complete",
                    StringComparison.Ordinal);
            float contactError = Mathf.Max(
                Mathf.Abs(slowContactY - preferredContactFeetY),
                Mathf.Abs(fastContactY - preferredContactFeetY));
            bool better;
            if (!found || worstSafety > bestWorstSafety + 0.08f)
                better = true;
            else if (worstSafety < bestWorstSafety - 0.08f)
                better = false;
            else if (objectiveComplete != bestObjectiveComplete)
                better = objectiveComplete;
            else
                better = contactError < bestContactError - 0.001f;
            if (!better)
                continue;

            found = true;
            bestWorstSafety = worstSafety;
            bestObjectiveComplete = objectiveComplete;
            bestContactError = contactError;
            selectedHold = hold;
            lipCrossingSeconds = Mathf.Max(slowLip, fastLip);
            topClearSeconds = Mathf.Max(slowTopTime, fastTopTime);
            targetContactSeconds = Mathf.Max(
                slowContactTime,
                fastContactTime);
            predictedTopClearFeetY = Mathf.Min(slowTopY, fastTopY);
            // Preserve the slow-end publication used by the existing contact
            // watcher; both endpoints remain explicit in the joint summary.
            predictedContactFeetY = slowContactY;
            predictedContactVelocityY = slowContactVelocity;
        }

        summary =
            $"JointFaceSpeedEnvelope[Slow={slowSpeed:F3}," +
            $"Fast={fastSpeed:F3},Found={found},SelectedHold=" +
            $"{selectedHold:F3},WorstSafety={bestWorstSafety:F3}," +
            $"ObjectiveApex=" +
            $"{(bestObjectiveComplete ? "Complete" : "Fallback")}," +
            $"ContactError={bestContactError:F3},Candidates[" +
            $"{evaluations}]]";
        return found;
    }

    private static string CompactWallEnvelopeDiagnostic(string value)
    {
        const int maximumLength = 420;
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
            return value ?? string.Empty;
        return value[..maximumLength] + $"...(+{value.Length - maximumLength})";
    }

    [HideFromIl2Cpp]
    private static JumpPhysicsSnapshot BuildMandatoryFacePlanningPhysics(
        JumpPhysicsSnapshot observed)
    {
        JumpPhysicsSnapshot wallPhysics = BuildWallPlanningPhysics(observed);
        float fixedStep = Mathf.Clamp(
            wallPhysics.FixedDeltaTime,
            0.005f,
            0.05f);
        float heldStepVelocity = Mathf.Clamp(
            wallPhysics.RawJumpForce -
                wallPhysics.GravityMagnitude * fixedStep,
            5f,
            50f);
        return wallPhysics with
        {
            JumpVelocity = heldStepVelocity,
            VelocitySampleCount = 0,
            VelocitySource = "WallHeldFixedStep"
        };
    }

    public void LateUpdate()
    {
        try
        {
            if (BonusStageRetryBridge.BlocksTerrainControl)
            {
                SuspendTerrainControlForRetry(latestState);
                LogControlGateEvidence(latestState, "RetryModalOwnsControl");
                return;
            }

            if (retryModalControlSuspended)
            {
                retryModalControlSuspended = false;
                nextAutomaticAttemptTime = Math.Max(
                    nextAutomaticAttemptTime,
                    Time.unscaledTime + 0.10f);
                BonusRunnerLog.Debug(
                    $"RetryModalOwnershipReleased Frame={Time.frameCount}, " +
                    $"GameState={latestState.GameStateName}, IsBonus=" +
                    $"{latestState.IsBonusStage}. A fresh routing decision " +
                    "is required before terrain input resumes.",
                    "Retry");
            }

            if (!automationEnabled || !latestState.IsBonusStage)
            {
                LogControlGateEvidence(
                    latestState,
                    !automationEnabled
                        ? "AutomationDisabled"
                        : "OutsideBonusStage");
                jumpController.Release();
                completionRewardController.Reset(
                    !automationEnabled
                        ? "AutomationDisabled"
                        : "OutsideBonusStage");
                return;
            }

            if (Time.unscaledTime - lastManualInputTime < 0.40f)
            {
                LogControlGateEvidence(latestState, "ManualInputCooldown");
                jumpController.Release();
                return;
            }

            BonusStageState routingState = GetRoutingState(latestState);
            LogControlGateEvidence(routingState, "ControlEligible");
            UpdateAutomaticJump(routingState);
            // Lifecycle takeover can rearm the remembered terrain section
            // inside UpdateAutomaticJump. Recompute this diagnostic view so
            // the same-cycle action record names the map that actually drove
            // the decision instead of the controller's already-advanced one.
            LogActionEvidence(
                GetRoutingState(latestState),
                PlayerMovement.instance);
        }
        catch (System.Exception exception)
        {
            jumpController.Release();
            jumpPhysicsFeedback.ResetTransient("LateUpdateException");
            ResetAutomaticControlState();
            nextAutomaticAttemptTime = Time.unscaledTime + 0.50f;
            if (Time.unscaledTime >= nextErrorLogTime)
            {
                nextErrorLogTime = Time.unscaledTime + 5f;
                BonusRunnerLog.Exception(
                    "LateUpdate jump control",
                    exception);
            }
        }
    }

    private void LogControlGateEvidence(
        BonusStageState state,
        string gate)
    {
        if (!BonusRunnerLog.IsDebugMode)
            return;

        string signature =
            $"{gate}:{state.GameStateName}:{state.SectionIndex}:" +
            $"{state.RemainingRequiredSpheres}:{automationEnabled}:" +
            $"{state.RewardFlagsAvailable}:" +
            $"{state.WaitingForRewardZone}:" +
            $"{state.RewardZoneEntered}:{state.GivingRewards}:" +
            $"{rewardTargetDetector.IsLatched}:" +
            $"{AutomaticJumpingEnabled}:" +
            $"{completionRewardController.IsRewardPhaseActive}:" +
            $"{CompletionRewardActionsEnabled}:" +
            $"{CompletionWindDashEnabled}:" +
            $"{pitDescentGuardActive}:" +
            $"{pitRespawnImmediateTakeoverEligible}:" +
            $"{pitRespawnTakeoverEvidence}:" +
            $"{BonusStageRetryBridge.ControlGateSummary}";
        if (string.Equals(
                signature,
                lastControlGateSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        lastControlGateSignature = signature;
        BonusRunnerLog.Debug(
            $"ControlGate Gate={gate}, Frame={Time.frameCount}, " +
            $"FixedStep={JumpPhysicsFeedback.FixedStepSequence}, " +
            $"AutomationEnabled={automationEnabled}, " +
            $"AutomaticJumping={AutomaticJumpingEnabled}(fixed), " +
            $"NativeRewardPhase=" +
            $"{completionRewardController.IsRewardPhaseActive}, " +
            $"CompletionRewardActions={CompletionRewardActionsEnabled}(fixed), " +
            $"CompletionWindDash={CompletionWindDashEnabled}(fixed), " +
            $"GameState={state.GameStateName}, Map={state.MapName}, " +
            $"RoutingSection={state.SectionIndex}, ControllerSection=" +
            $"{latestState.SectionIndex}, LastActiveTerrainSection=" +
            $"{lastActiveTerrainSection}, HasPlayer={state.HasPlayer}, " +
            $"BonusGameplayStarted={bonusGameplayStarted}, " +
            $"Spheres={(state.HasSphereProgress ? $"{state.CollectedSpheres}/{Math.Ceiling(state.RequiredSpheres):F0}" : "Unavailable")}, " +
            $"Remaining={state.RemainingRequiredSpheres}, " +
            $"RewardFlags[Available={state.RewardFlagsAvailable},Wait=" +
            $"{state.WaitingForRewardZone}," +
            $"Trigger={state.RewardZoneEntered},Giving=" +
            $"{state.GivingRewards}], RewardTarget=" +
            $"[{rewardTargetDetector.Describe()}], " +
            $"FellOff={state.CharacterFellOff}, " +
            $"ManualCooldownRemaining={Mathf.Max(0f, 0.40f - (Time.unscaledTime - lastManualInputTime)):F3}s, " +
            $"Holding={jumpController.IsHoldingJump}, " +
            $"LearningActive={learningSampleActive}, " +
            $"LearningSource={learningSource ?? "None"}, " +
            $"PredictionActive={automaticPredictionActive}, " +
            $"WallPhase={wallActionPhase}, RespawnGuard[Active=" +
            $"{pitDescentGuardActive},Immediate=" +
            $"{pitRespawnImmediateTakeoverEligible},Evidence=" +
            $"{pitRespawnTakeoverEvidence},StableSteps=" +
            $"{pitRespawnStableFixedSteps}], RetryState=" +
            $"[{BonusStageRetryBridge.ControlGateSummary}].",
            "Evidence");
    }

    private void LogActionEvidence(
        BonusStageState state,
        PlayerMovement player)
    {
        if (!BonusRunnerLog.IsDebugMode || player == null || !state.HasPlayer)
            return;

        bool completionTraversal = IsSuccessfulCompletionTraversal(state);
        bool nativeRewardPhase = IsNativeRewardActionPhase(state);
        bool wallActionActive =
            wallActionPhase != WallActionPhase.None &&
            wallActionPhase != WallActionPhase.Completed &&
            wallActionPhase != WallActionPhase.Failed;
        bool actionActive =
            wallActionActive || completionTraversal || nativeRewardPhase ||
            automaticPredictionActive || jumpController.IsHoldingJump ||
            (learningSampleActive && learningSource == "Automatic");
        if (!actionActive)
            return;

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        BonusWallContact wall = wallDetector.Detect(
            player,
            GetWallRouteSpeed());
        string signature =
            $"{wallActionPhase}:{wallRecoveryAttempts}:" +
            $"{jumpController.IsHoldingJump}:{state.IsGrounded}:" +
            $"{wall.IsDetected}:{wall.IsTouching}:" +
            $"{wallRecoveryLipCrossed}:{wallResidualRiseWaitActive}:" +
            $"{wallExitFaceInterceptCommitted}:" +
            $"{wallExitCollectionFaceInterceptCommitted}:" +
            $"{wallExitFaceContactRequired}:" +
            $"{wallMandatoryFaceSetupActive}:" +
            $"{wallMandatoryFaceInterceptCommitted}:" +
            $"{automaticAttemptId}:" +
            $"{state.GameStateName}:{state.RemainingRequiredSpheres}:" +
            $"{state.RewardZoneEntered}:{state.GivingRewards}";
        bool eventChanged = !string.Equals(
            signature,
            lastEvidenceSignature,
            StringComparison.Ordinal);
        bool shouldLog = wallActionActive
            ? fixedStep != lastEvidenceFixedStep
            : eventChanged || Time.unscaledTime >= nextGeneralEvidenceTime;
        if (!shouldLog)
            return;

        lastEvidenceFixedStep = fixedStep;
        lastEvidenceSignature = signature;
        nextGeneralEvidenceTime = Time.unscaledTime + 0.25f;

        BonusBoardScanResult scan = default;
        string scanSummary;
        try
        {
            scan = platformScanner.Scan(
                state.PlayerPosition,
                player,
                Mathf.Max(
                    Mathf.Abs(state.PlayerVelocity.x),
                    lastReliableHorizontalSpeed));
            scanSummary = DescribeEvidenceScan(scan);
        }
        catch (System.Exception exception)
        {
            scanSummary = $"ScanFailed:{exception.GetType().Name}:{exception.Message}";
        }

        Collider2D playerCollider = player.playerCollider;
        string playerBounds = playerCollider != null
            ? $"Center=({playerCollider.bounds.center.x:F3},{playerCollider.bounds.center.y:F3})," +
              $"Min=({playerCollider.bounds.min.x:F3},{playerCollider.bounds.min.y:F3})," +
              $"Max=({playerCollider.bounds.max.x:F3},{playerCollider.bounds.max.y:F3})," +
              $"Extents=({playerCollider.bounds.extents.x:F3},{playerCollider.bounds.extents.y:F3})"
            : "Unavailable";
        string wallSummary = wall.IsDetected
            ? $"Detected=True,Touching={wall.IsTouching},Distance={wall.Distance:F3}," +
              $"BodyGap={wall.BodyGap:F3},FaceX={wall.FaceX:F3}," +
              $"Point=({wall.Point.x:F3},{wall.Point.y:F3})," +
              $"Normal=({wall.Normal.x:F3},{wall.Normal.y:F3})," +
              $"Collider={wall.ColliderInstanceId}:{wall.ColliderName},Reason={wall.Reason}"
            : $"Detected=False,Reason={wall.Reason}";
        string exitTarget = wallExitTargetActive
            ? DescribeEvidenceSegment(wallExitTarget)
            : "None";
        string target = automaticPredictionActive || wallActionActive
            ? $"Raw=[{automaticTargetLeft:F3},{automaticTargetRight:F3}]" +
              $" Safe=[{automaticTargetSafeLeft:F3},{automaticTargetSafeRight:F3}]" +
              $" Top={automaticTargetTop:F3} Collider=" +
              $"{automaticTargetColliderId}:{automaticTargetColliderName}" +
              $" Piece={automaticTargetMapPieceName}#" +
              $"{automaticTargetMapPieceInstanceId}/G" +
              $"{automaticTargetRegistryGeneration}/S" +
              $"{automaticTargetStaticSurfaceIndex}"
            : "None";
        float holdElapsed = jumpController.LastPressStartedAt >= 0f
            ? Mathf.Max(0f, Time.unscaledTime - jumpController.LastPressStartedAt)
            : -1f;
        float commitmentRemaining = Mathf.Max(
            0f,
            wallRecoveryCommitmentUntil - Time.unscaledTime);

        BonusRunnerLog.Debug(
            $"ActionPhysicsFrame EventChanged={eventChanged}, " +
            $"Frame={Time.frameCount}, FixedStep={fixedStep}, " +
            $"UnscaledTime={Time.unscaledTime:F3}, TimeScale={Time.timeScale:F3}, " +
            $"RouteId={activeRouteDecisionId}, AttemptId={automaticAttemptId}, " +
            $"LearningId={learningSampleId}, GameState={state.GameStateName}, " +
            $"Map={state.MapName}, Section={state.SectionIndex}, " +
            $"CompletionTraversal={completionTraversal}, " +
            $"NativeRewardPhase={nativeRewardPhase}, " +
            $"BonusGameplayStarted={bonusGameplayStarted}, " +
            $"Spheres={(state.HasSphereProgress ? $"{state.CollectedSpheres}/{Math.Ceiling(state.RequiredSpheres):F0}" : "Unavailable")}, " +
            $"Remaining={state.RemainingRequiredSpheres}, RewardFlags[Available=" +
            $"{state.RewardFlagsAvailable},Wait=" +
            $"{state.WaitingForRewardZone},Trigger=" +
            $"{state.RewardZoneEntered},Giving={state.GivingRewards}], " +
            $"FellOff={state.CharacterFellOff}, " +
            $"SpiritBoost={state.SpiritBoostEnabled}, Timer={state.CurrentTime:F3}/{state.MaximumTime:F3}, " +
            $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"Grounded={state.IsGrounded}, PlayerBounds[{playerBounds}], " +
            $"PlayerJump[IsJumping={player.isJumping},JumpTime={player.jumpTime:F3}," +
            $"Counter={player.jumpTimeCounter:F3},Force={player.jumpForce:F3}], " +
            $"Input[ControllerHolding={jumpController.IsHoldingJump}," +
            $"HoldElapsed={holdElapsed:F3},LastDown={jumpController.LastPressStartedAt:F3}," +
            $"LastUp={jumpController.LastReleaseAt:F3},PanelDown={JumpPanel.jumpDown}," +
            $"PanelPressed={JumpPanel.jumpPressed},PanelUp={JumpPanel.jumpUp}," +
            $"HeldPointers={(JumpPanel.instance != null ? JumpPanel.instance.heldPointerCount : -1)}], " +
            $"Plan[Maneuver={automaticManeuver},Reason={automaticPlanReason ?? "None"}," +
            $"Trigger=({automaticPlanTriggerPosition.x:F3},{automaticPlanTriggerPosition.y:F3})," +
            $"Launch={automaticPlannedLaunchX:F3}," +
            $"Window=[{automaticLaunchWindowLeft:F3},{automaticLaunchWindowRight:F3}]," +
            $"Hold={automaticPlannedHold:F3},PredictedLandingX={automaticPredictedLandingX:F3}," +
            $"PredictedFlight={automaticPredictedFlightSeconds:F3}," +
            $"Target={target}], " +
            $"Wall[Phase={wallActionPhase},Attempts={wallRecoveryAttempts}/" +
            $"{MaximumWallRecoveriesPerAirborneSequence},ContactLatched={wallRecoveryContactLatched}," +
            $"Contact=({wallRecoveryContactX:F3},{wallRecoveryContactY:F3})," +
            $"SawUpward={wallRecoverySawUpwardMotion},UpwardSteps={wallRecoveryUpwardPhysicsSteps}," +
            $"ImpulseConfirmed={wallRecoveryImpulseConfirmed},LipCrossed={wallRecoveryLipCrossed}," +
            $"PhysicalLipY={wallRecoveryPhysicalLipY:F3}," +
            $"ObjectiveReleaseY={wallRecoveryRequiredReleaseY:F3}," +
            $"CommitRemaining={commitmentRemaining:F3}," +
            $"ReleaseObservedStep={wallReleaseObservedFixedStep}," +
            $"DetachedLastStep={wallDetachedLastFixedStep}," +
            $"DetachedConfirmSteps={wallDetachedConfirmationSteps}," +
            $"TopSequenceCommitted={wallTopLandingSequenceCommitted}," +
            $"ExitContactWatch={wallExitContactWatchActive}," +
            $"ExitContactDeadlineFixedStep=" +
            $"{wallExitContactWatchDeadlineFixedStep}," +
            $"ExitFaceInterceptCommitted=" +
            $"{wallExitFaceInterceptCommitted}," +
            $"ExitCollectionFaceInterceptCommitted=" +
            $"{wallExitCollectionFaceInterceptCommitted}," +
            $"ResidualRiseWait={wallResidualRiseWaitActive}," +
            $"ExitFaceContactRequired={wallExitFaceContactRequired}," +
            $"ExitObjectiveCount={wallExitObjectiveCountAtCapture}," +
            $"ExitObjectiveY=[{wallExitObjectiveMinimumY:F3}," +
            $"{wallExitObjectiveMaximumY:F3}]," +
            $"MandatoryFaceSetupActive=" +
            $"{wallMandatoryFaceSetupActive}," +
            $"MandatoryFaceSetupRemainingFixedSteps=" +
            $"{(wallMandatoryFaceSetupActive ? Math.Max(0L, wallMandatoryFaceSetupDeadlineFixedStep - JumpPhysicsFeedback.FixedStepSequence) : 0L)}," +
            $"MandatoryFaceInterceptCommitted=" +
            $"{wallMandatoryFaceInterceptCommitted}," +
            $"MandatoryFacePrediction[TargetX=" +
            $"{wallMandatoryFaceTargetContactX:F3},TopClearY=" +
            $"{wallMandatoryFacePredictedTopClearFeetY:F3},ContactY=" +
            $"{wallMandatoryFacePredictedContactFeetY:F3},ContactVY=" +
            $"{wallMandatoryFacePredictedContactVelocityY:F3},ContactT=" +
            $"{wallMandatoryFacePredictedContactSeconds:F3}]," +
            $"ExitTransferCommitted={wallExitTransferCommitted}," +
            $"LandingFlightCommitted={wallLandingFlightCommitted}," +
            $"ExitTarget={exitTarget},Probe={wallSummary}], " +
            $"Scan[{scanSummary}], Physics[{latestPhysicsSnapshot.Summary}]",
            "Evidence");
    }

    private static string DescribeEvidenceScan(BonusBoardScanResult scan)
    {
        if (!scan.IsValid)
            return $"Valid=False,Reason={scan.Reason}";

        string next = scan.HasNext
            ? DescribeEvidenceSegment(scan.Next)
            : "None";
        string intermediate = scan.HasIntermediate
            ? DescribeEvidenceSegment(scan.Intermediate)
            : "None";
        string alternatives = scan.Alternatives == null || scan.Alternatives.Length == 0
            ? "None"
            : string.Join("|", scan.Alternatives.Select(DescribeEvidenceSegment));
        return $"Valid=True,Reason={scan.Reason},Current={DescribeEvidenceSegment(scan.Current)}," +
               $"Next={next},Intermediate={intermediate}," +
               $"EdgeDistance={scan.DistanceToCurrentEdge:F3},Gap={scan.Gap:F3}," +
               $"DeltaY={scan.HeightDelta:F3},Alternatives={alternatives}";
    }

    private static string DescribeEvidenceSegment(BonusBoardSegment segment) =>
        $"[{segment.Left:F3},{segment.Right:F3}]" +
        $"Safe[{segment.SafeLeft:F3},{segment.SafeRight:F3}]" +
        $"@{segment.Top:F3}:C{segment.ColliderInstanceId}:{segment.ColliderName}:" +
        $"P{segment.MapPieceName}#{segment.MapPieceInstanceId}/" +
        $"G{segment.RegistryGeneration}/S{segment.StaticSurfaceIndex}/" +
        $"WF{(float.IsFinite(segment.WallFaceX)
            ? segment.WallFaceX.ToString("F3")
            : "None")}";

    private void LogMovementEvents(BonusStageState state)
    {
        if (!state.HasPlayer)
            return;

        bool observedMouseInput = Input.GetMouseButton(0);
        bool observedJumpInput = Input.GetKey(KeyCode.Space) || observedMouseInput;
        if (observedJumpInput)
            lastManualInputTime = Time.unscaledTime;

        if (observedJumpInput != previousObservedJumpInput)
        {
            if (observedJumpInput)
            {
                observedJumpInputStartedAt = Time.unscaledTime;
                if (jumpController.IsHoldingJump)
                {
                    jumpController.Release();
                    BonusRunnerLog.Debug(
                        "Physical jump input overrode the active automatic hold.",
                        "Input");
                }
                if (learningSampleActive && learningSource == "Automatic")
                    FinishLearningSample(state, "ManualOverride");
                ResetAutomaticControlState();
                ObserveManualWallJumpDown(state);
                if (state.IsGrounded && !learningSampleActive)
                    StartLearningSample(
                        state,
                        "Manual",
                        IsManualGroundLearningEligible(state));
                if (state.IsGrounded)
                    LogManualJumpContext(state);
                BonusRunnerLog.Debug(
                    $"Observed jump input DOWN: Frame={Time.frameCount}, X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
                    $"VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}, Grounded={state.IsGrounded}",
                    "Input");
            }
            else
            {
                if (learningSampleActive && learningSource == "Manual" &&
                    !learningInputReleased)
                {
                    learningInputUpTime = Time.unscaledTime;
                    learningInputReleased = true;
                }
                ObserveManualWallJumpUp(state);
                BonusRunnerLog.Debug(
                    $"Observed jump input UP: Frame={Time.frameCount}, Held={Time.unscaledTime - observedJumpInputStartedAt:F3}s, " +
                    $"X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}",
                    "Input");
            }
            previousObservedJumpInput = observedJumpInput;
        }

        if (observedMouseInput != previousObservedMouseInput)
        {
            if (observedMouseInput && !automationEnabled)
            {
                BeginManualDemonstration(state);
                manualDemonstrationMousePresses++;
                LogManualDemonstrationEvidence(
                    state,
                    "MouseDown",
                    force: true);
            }
            else if (!observedMouseInput && manualDemonstrationActive)
            {
                LogManualDemonstrationEvidence(
                    state,
                    "MouseUp",
                    force: true);
            }
            previousObservedMouseInput = observedMouseInput;
        }

        if (manualDemonstrationActive)
        {
            PlayerMovement manualPlayer = PlayerMovement.instance;
            bool lowDescending =
                !state.IsGrounded &&
                state.PlayerPosition.y < PitDescentYThreshold &&
                state.PlayerVelocity.y <= PitDescentVelocityThreshold;
            bool recoverableWall = HasRecoverableWallAhead(
                state,
                manualPlayer,
                requirePlannedTarget: false,
                out BonusWallContact manualWall,
                out string manualWallEvidence);
            bool manualPitCandidate =
                lowDescending &&
                !observedMouseInput &&
                !recoverableWall;
            bool confirmedManualPit = UpdateManualPitConfirmation(
                manualPitCandidate);

            if (lowDescending && recoverableWall &&
                Time.unscaledTime >= nextManualPitEvidenceTime)
            {
                nextManualPitEvidenceTime = Time.unscaledTime + 0.25f;
                BonusRunnerLog.Debug(
                    $"ManualPitThresholdSuppressed Position=" +
                    $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"Velocity=({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), Wall=" +
                    $"{manualWallEvidence}, FaceX={manualWall.FaceX:F3}, " +
                    $"Distance={manualWall.Distance:F3}, Touching=" +
                    $"{manualWall.IsTouching}. The low position is a " +
                    "recoverable authored trench, not a demonstrated death.",
                    "Demonstration");
            }

            if (confirmedManualPit && !manualDemonstrationPitLogged)
            {
                manualDemonstrationPitLogged = true;
                LogManualDemonstrationEvidence(
                    state,
                    "PitDescent",
                    force: true);
                if (learningSampleActive && learningSource == "Manual")
                    FinishLearningSample(state, "ManualPitDescent");
                EndManualDemonstration(state, "PitDescent");
            }
            else
            {
                LogManualDemonstrationEvidence(
                    state,
                    observedMouseInput ? "MouseHeld" : "Trajectory",
                    force: false);
            }
        }

        if (!movementInitialized)
        {
            movementInitialized = true;
            previousMovementPosition = state.PlayerPosition;
            previousMovementObservedAt = Time.unscaledTime;
            previousMovementObservedFixedStep =
                JumpPhysicsFeedback.FixedStepSequence;
            previousGrounded = state.IsGrounded;
            previousVelocityY = state.PlayerVelocity.y;
            return;
        }

        float movementDeltaSeconds = Mathf.Max(
            0.001f,
            Time.unscaledTime - previousMovementObservedAt);
        Vector3 movementDelta =
            state.PlayerPosition - previousMovementPosition;
        long currentMovementFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
        long elapsedMovementFixedSteps =
            previousMovementObservedFixedStep >= 0
                ? Math.Max(
                    0L,
                    currentMovementFixedStep -
                    previousMovementObservedFixedStep)
                : 0L;
        float movementFixedDelta = Mathf.Clamp(
            latestPhysicsSnapshot.FixedDeltaTime > 0f
                ? latestPhysicsSnapshot.FixedDeltaTime
                : Time.fixedDeltaTime,
            0.005f,
            0.05f);
        float movementPhysicsDelta =
            elapsedMovementFixedSteps > 0
                ? elapsedMovementFixedSteps * movementFixedDelta
                : Mathf.Min(movementDeltaSeconds, movementFixedDelta);
        float expectedHorizontalDelta = Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.x),
                lastReliableHorizontalSpeed) *
            movementPhysicsDelta + 1.50f;
        float movementVerticalVelocityEnvelope = Mathf.Max(
            Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.y),
                Mathf.Abs(previousVelocityY)),
            Mathf.Max(
                5f,
                Mathf.Abs(latestPhysicsSnapshot.JumpVelocity)));
        float movementGravityEnvelope = Mathf.Clamp(
            Mathf.Abs(latestPhysicsSnapshot.GravityMagnitude),
            1f,
            120f);
        float expectedVerticalDelta =
            movementVerticalVelocityEnvelope * movementPhysicsDelta +
            0.5f * movementGravityEnvelope *
                movementPhysicsDelta * movementPhysicsDelta +
            1.25f;
        bool globalPositionDiscontinuity =
            automationEnabled &&
            bonusGameplayStarted &&
            (Mathf.Abs(movementDelta.x) > expectedHorizontalDelta ||
             Mathf.Abs(movementDelta.y) > Mathf.Max(1.25f, expectedVerticalDelta));
        previousMovementPosition = state.PlayerPosition;
        previousMovementObservedAt = Time.unscaledTime;
        previousMovementObservedFixedStep =
            currentMovementFixedStep;
        if (globalPositionDiscontinuity)
        {
            float priorObservedY =
                state.PlayerPosition.y - movementDelta.y;
            bool verifiedUpwardRespawnTeleport =
                movementDelta.y >= 2.0f &&
                state.CharacterFellOff &&
                priorObservedY < PitDescentYThreshold;
            BonusRunnerLog.Warning(
                $"GlobalPositionDiscontinuity Delta=" +
                $"({movementDelta.x:F3},{movementDelta.y:F3}), " +
                $"WallDt={movementDeltaSeconds:F3}s, PhysicsDt=" +
                $"{movementPhysicsDelta:F3}s, FixedSteps=" +
                $"{elapsedMovementFixedSteps}, Limits=" +
                $"[X={expectedHorizontalDelta:F3}," +
                $"Y={expectedVerticalDelta:F3}], Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), LearningActive=" +
                $"{learningSampleActive}, PriorObservedY=" +
                $"{priorObservedY:F3}, VerifiedRespawnTeleport=" +
                $"{verifiedUpwardRespawnTeleport}. FailureDomain=Lifecycle; all " +
                "automatic input is blocked until a stable respawn.");
            jumpController.Release();
            if (learningSampleActive && learningSource == "Automatic")
                FinishLearningSample(state, "PositionDiscontinuity");
            ResetAutomaticControlState();
            ResetCompletionTraversalSpeedEvidence(
                "GlobalPositionDiscontinuity");
            pitDescentGuardActive = true;
            pitRespawnImmediateTakeoverEligible =
                verifiedUpwardRespawnTeleport;
            pitRespawnTakeoverEvidence =
                verifiedUpwardRespawnTeleport
                    ? "VerifiedUpwardRespawnTeleport"
                    : "GlobalPositionDiscontinuity";
            terrainContinuationEpochBlocked = true;
            bonusGameplayStarted = false;
            rewardTargetDetector.BeginEpoch(
                state,
                "PositionDiscontinuity");
            rewardTargetObservation =
                rewardTargetDetector.LastObservation;
            lastRewardTargetObservationSignature = string.Empty;
            rewardLatchObservedDuringInactiveFrame = false;
            rewardTargetEmptyBaselineRequired = true;
            rewardTargetRearmDeferralLogged = false;
            rewardTargetConsecutiveEmptyScans = 0;
            rewardTargetLastEmptyScanFrame = -1;
            completionRewardController.Reset(
                "PositionDiscontinuity");
            pitRespawnLastFixedStep = -1;
            pitRespawnStableFixedSteps = 0;
            nextAutomaticAttemptTime = Time.unscaledTime + 0.45f;
            previousGrounded = state.IsGrounded;
            previousVelocityY = state.PlayerVelocity.y;
            return;
        }

        if (previousGrounded && !state.IsGrounded)
        {
            BonusRunnerLog.Debug(
                $"Takeoff: Frame={Time.frameCount}, Time={Time.time:F3}, X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
                $"VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}, InputHeld={observedJumpInput}",
                "Movement");
        }
        else if (!previousGrounded && state.IsGrounded)
        {
            BonusRunnerLog.Debug(
                $"Landing: Frame={Time.frameCount}, Time={Time.time:F3}, X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
                $"VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}",
                "Movement");
            LogManualDemonstrationEvidence(
                state,
                "Landing",
                force: true);
        }

        if (previousVelocityY > 0f && state.PlayerVelocity.y <= 0f && !state.IsGrounded)
        {
            BonusRunnerLog.Debug(
                $"Apex: Frame={Time.frameCount}, Time={Time.time:F3}, X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
                $"VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}",
                "Movement");
            LogManualDemonstrationEvidence(
                state,
                "Apex",
                force: true);
        }

        previousGrounded = state.IsGrounded;
        previousVelocityY = state.PlayerVelocity.y;
    }

    private void LogManualJumpContext(BonusStageState state)
    {
        PlayerMovement player = PlayerMovement.instance;
        if (player == null)
            return;

        float observedHorizontalSpeed = Mathf.Abs(state.PlayerVelocity.x);
        bool observedPlanningSpeedUsable =
            observedHorizontalSpeed > 1f && observedHorizontalSpeed < 80f;
        float planningHorizontalSpeed = observedPlanningSpeedUsable
            ? observedHorizontalSpeed
            : Mathf.Max(1f, lastReliableHorizontalSpeed);
        string planningSpeedSource = observedPlanningSpeedUsable
            ? "LiveRigidbody"
            : "ReliableRunFallback";

        BonusBoardScanResult scan = platformScanner.Scan(
            state.PlayerPosition,
            player,
            planningHorizontalSpeed);
        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        latestPhysicsSnapshot = observedPhysics;
        JumpPhysicsSnapshot physics = BuildPlanningPhysics(
            state.SectionIndex,
            observedPhysics,
            planningHorizontalSpeed,
            state.IsActiveGameplay ||
                IsSuccessfulCompletionTraversal(state),
            state.SpiritBoostEnabled
                ? sectionCruiseHorizontalSpeed
                : 0f);
        SpiritBoostRouteContext spiritBoostRouteContext =
            CaptureRouteSpeedContext(
                state,
                player,
                state.PlayerPosition.x - 2.0f,
                state.PlayerPosition.x + 80f,
                sectionCruiseHorizontalSpeed > 1f
                    ? sectionCruiseHorizontalSpeed
                    : 0f);
        BonusJumpPlan plan = jumpPlanner.Plan(
            scan,
            state.PlayerPosition,
            new Vector2(planningHorizontalSpeed, state.PlayerVelocity.y),
            physics,
            hazardScanner.FindNearest(state.PlayerPosition),
            sectionIndex: state.SectionIndex,
            allowRecoverableLowerFaceCatch:
                !state.UsesStage3AuthoredRouting &&
                state.SectionIndex >= 2,
            useFixedStepAlignedHolds:
                !state.UsesStage3AuthoredRouting &&
                state.SectionIndex >= 2,
            spiritBoost: spiritBoostRouteContext);

        if (!scan.IsValid)
        {
            BonusRunnerLog.Debug(
                $"ManualJumpContext AttemptId={learningSampleId}, X={state.PlayerPosition.x:F3}, " +
                $"AutomationEnabled={automationEnabled}, Scan={scan.Reason}",
                "Learning");
            return;
        }

        string next = scan.HasNext
            ? $"Next=[{scan.Next.Left:F3},{scan.Next.Right:F3}] " +
              $"Safe=[{scan.Next.SafeLeft:F3},{scan.Next.SafeRight:F3}]@{scan.Next.Top:F3}"
            : "Next=Unavailable";
        string intermediate = scan.HasIntermediate
            ? $", Intermediate=[{scan.Intermediate.Left:F3}," +
              $"{scan.Intermediate.Right:F3}] " +
              $"Safe=[{scan.Intermediate.SafeLeft:F3}," +
              $"{scan.Intermediate.SafeRight:F3}]@{scan.Intermediate.Top:F3}"
            : string.Empty;
        BonusRunnerLog.Debug(
            $"ManualJumpContext AttemptId={learningSampleId}, " +
            $"X={state.PlayerPosition.x:F3}, VX={lastReliableHorizontalSpeed:F3}, " +
            $"AutomationEnabled={automationEnabled}, " +
            $"Current=[{scan.Current.Left:F3},{scan.Current.Right:F3}] " +
            $"Safe=[{scan.Current.SafeLeft:F3},{scan.Current.SafeRight:F3}]@{scan.Current.Top:F3}, " +
            $"{next}{intermediate}, Gap={scan.Gap:F3}, " +
            $"DeltaY={scan.HeightDelta:F3}, ScanReason={scan.Reason}, " +
            $"SuggestedHold={plan.HoldSeconds:F3}s, PlannedLaunch={plan.PlannedLaunchX:F3}, " +
            $"Reason={plan.Reason}; SpiritBoostRoute[" +
            $"{spiritBoostRouteContext.Summary}]; Physics[{physics.Summary}]; " +
            $"Candidates: {plan.CandidateSummary}",
            "Learning");
    }

    private void BeginManualDemonstration(BonusStageState state)
    {
        if (manualDemonstrationActive || automationEnabled ||
            !state.IsBonusStage)
        {
            return;
        }

        manualDemonstrationActive = true;
        manualDemonstrationId = ++nextManualDemonstrationId;
        manualDemonstrationMousePresses = 0;
        manualDemonstrationStartedAt = Time.unscaledTime;
        manualDemonstrationStartPosition = state.PlayerPosition;
        manualDemonstrationStartSection = state.SectionIndex;
        manualDemonstrationStartSpheres =
            BonusStageInspector.TryGetBonusSphereCount(out int sphereCount)
                ? sphereCount
                : -1;
        manualDemonstrationPitLogged = false;
        ResetManualPitConfirmation();
        nextManualDemonstrationFrameTime = 0f;
        lastManualDemonstrationSignature = string.Empty;
        BonusRunnerLog.Debug(
            $"ManualDemonstrationStart SessionId=" +
            $"{manualDemonstrationId}, Map={state.MapName}, " +
            $"Section={state.SectionIndex}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), Spheres=" +
            $"{manualDemonstrationStartSpheres}, " +
            "AutomationEnabled=False. Map perception and shadow planning " +
            "remain active, but no automatic input will be sent.",
            "Demonstration");
    }

    private void EndManualDemonstration(
        BonusStageState state,
        string outcome)
    {
        if (!manualDemonstrationActive)
            return;

        LogManualDemonstrationEvidence(
            state,
            $"SessionEnd:{outcome}",
            force: true);
        int endSpheres =
            BonusStageInspector.TryGetBonusSphereCount(out int sphereCount)
                ? sphereCount
                : -1;
        string sphereDelta =
            manualDemonstrationStartSpheres >= 0 && endSpheres >= 0
                ? $"{manualDemonstrationStartSpheres}->{endSpheres} " +
                  $"Delta={endSpheres - manualDemonstrationStartSpheres}"
                : $"Unavailable(Start={manualDemonstrationStartSpheres}," +
                  $"End={endSpheres})";
        BonusRunnerLog.Debug(
            $"ManualDemonstrationEnd SessionId=" +
            $"{manualDemonstrationId}, Outcome={outcome}, " +
            $"Duration={Mathf.Max(0f, Time.unscaledTime - manualDemonstrationStartedAt):F3}s, " +
            $"StartSection={manualDemonstrationStartSection}, " +
            $"EndSection={state.SectionIndex}, MousePresses=" +
            $"{manualDemonstrationMousePresses}, Start=" +
            $"({manualDemonstrationStartPosition.x:F3}," +
            $"{manualDemonstrationStartPosition.y:F3}), End=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"SphereProgress={sphereDelta}.",
            "Demonstration");
        manualDemonstrationActive = false;
        manualDemonstrationId = 0;
        manualDemonstrationMousePresses = 0;
        manualDemonstrationPitLogged = false;
        ResetManualPitConfirmation();
        nextManualDemonstrationFrameTime = 0f;
        lastManualDemonstrationSignature = string.Empty;
    }

    private void LogManualDemonstrationEvidence(
        BonusStageState state,
        string eventName,
        bool force)
    {
        if (!manualDemonstrationActive || !BonusRunnerLog.IsDebugMode ||
            !state.IsBonusStage || !state.HasPlayer)
        {
            return;
        }

        PlayerMovement player = PlayerMovement.instance;
        if (player == null)
            return;

        BonusWallContact wall = wallDetector.Detect(
            player,
            lastReliableHorizontalSpeed);
        string signature =
            $"{eventName}:{state.SectionIndex}:{state.IsGrounded}:" +
            $"{Input.GetMouseButton(0)}:{wall.IsDetected}:" +
            $"{wall.IsTouching}:{state.CharacterFellOff}";
        bool eventChanged = !string.Equals(
            signature,
            lastManualDemonstrationSignature,
            StringComparison.Ordinal);
        float interval = state.IsGrounded && !Input.GetMouseButton(0)
            ? 0.20f
            : 0.08f;
        if (!force && !eventChanged &&
            Time.unscaledTime < nextManualDemonstrationFrameTime)
        {
            return;
        }

        lastManualDemonstrationSignature = signature;
        nextManualDemonstrationFrameTime = Time.unscaledTime + interval;

        BonusBoardScanResult scan = default;
        BonusHazard hazard = default;
        BonusJumpPlan shadowPlan = default;
        string scanSummary;
        string shadowSummary;
        try
        {
            scan = platformScanner.Scan(
                state.PlayerPosition,
                player,
                Mathf.Max(
                    Mathf.Abs(state.PlayerVelocity.x),
                    lastReliableHorizontalSpeed));
            scanSummary = DescribeEvidenceScan(scan);
            hazard = hazardScanner.FindNearest(state.PlayerPosition);
            if (scan.IsValid)
            {
                JumpPhysicsSnapshot physics =
                    jumpPhysicsFeedback.CaptureSnapshot(player);
                latestPhysicsSnapshot = physics;
                SpiritBoostRouteContext spiritBoostRouteContext =
                    CaptureRouteSpeedContext(
                        state,
                        player,
                        state.PlayerPosition.x - 2.0f,
                        state.PlayerPosition.x + 80f,
                        sectionCruiseHorizontalSpeed > 1f
                            ? sectionCruiseHorizontalSpeed
                            : 0f);
                shadowPlan = jumpPlanner.Plan(
                    scan,
                    state.PlayerPosition,
                    state.PlayerVelocity,
                    physics,
                    hazard,
                    sectionIndex: state.SectionIndex,
                    allowRecoverableLowerFaceCatch:
                        !state.UsesStage3AuthoredRouting &&
                        state.SectionIndex >= 2,
                    useFixedStepAlignedHolds:
                        !state.UsesStage3AuthoredRouting &&
                        state.SectionIndex >= 2,
                    spiritBoost: spiritBoostRouteContext);
                shadowSummary =
                    $"Valid={shadowPlan.IsValid}," +
                    $"ShouldJumpNow={shadowPlan.ShouldJumpNow}," +
                    $"Maneuver={shadowPlan.Maneuver}," +
                    $"Reason={shadowPlan.Reason}," +
                    $"Hold={shadowPlan.HoldSeconds:F3}," +
                    $"Launch={shadowPlan.PlannedLaunchX:F3}," +
                    $"Window=[{shadowPlan.LaunchWindowLeft:F3}," +
                    $"{shadowPlan.LaunchWindowRight:F3}]," +
                    $"PredictedLandingX=" +
                    $"{shadowPlan.PredictedLandingX:F3}," +
                    $"PredictedFlight=" +
                    $"{shadowPlan.PredictedFlightSeconds:F3}," +
                    $"SpiritBoostRoute=[" +
                    $"{spiritBoostRouteContext.Summary}]" +
                    (force
                        ? $",Candidates={shadowPlan.CandidateSummary}"
                        : string.Empty);
            }
            else
            {
                shadowSummary = "Unavailable:InvalidScan";
            }
        }
        catch (System.Exception exception)
        {
            scanSummary =
                $"ScanFailed:{exception.GetType().Name}:" +
                $"{exception.Message}";
            shadowSummary = "Unavailable:Exception";
        }

        string hazardSummary = hazard.IsValid
            ? $"[{hazard.Left:F3},{hazard.Right:F3}]" +
              $"@{hazard.Top:F3}:" +
              $"{hazard.InstanceId}:{hazard.Name}:" +
              $"{hazard.ComponentPath}"
            : "None";
        string wallSummary = wall.IsDetected
            ? $"Detected=True,Touching={wall.IsTouching}," +
              $"Distance={wall.Distance:F3},BodyGap=" +
              $"{wall.BodyGap:F3},FaceX={wall.FaceX:F3}," +
              $"Point=({wall.Point.x:F3},{wall.Point.y:F3})," +
              $"Normal=({wall.Normal.x:F3},{wall.Normal.y:F3})," +
              $"Collider={wall.ColliderInstanceId}:" +
              $"{wall.ColliderName},Reason={wall.Reason}"
            : $"Detected=False,Reason={wall.Reason}";
        Collider2D collider = player.playerCollider;
        string bounds = collider != null
            ? $"Min=({collider.bounds.min.x:F3}," +
              $"{collider.bounds.min.y:F3}),Max=" +
              $"({collider.bounds.max.x:F3}," +
              $"{collider.bounds.max.y:F3}),Extents=" +
              $"({collider.bounds.extents.x:F3}," +
              $"{collider.bounds.extents.y:F3})"
            : "Unavailable";

        BonusRunnerLog.Debug(
            $"ManualDemonstrationFrame SessionId=" +
            $"{manualDemonstrationId}, Event={eventName}, " +
            $"MousePress={manualDemonstrationMousePresses}, " +
            $"Elapsed={Time.unscaledTime - manualDemonstrationStartedAt:F3}s, " +
            $"Frame={Time.frameCount}, FixedStep=" +
            $"{JumpPhysicsFeedback.FixedStepSequence}, " +
            $"Map={state.MapName}, Section={state.SectionIndex}, " +
            $"GameState={state.GameStateName}, " +
            $"SpiritBoost={state.SpiritBoostEnabled}, " +
            $"Timer={state.CurrentTime:F3}/{state.MaximumTime:F3}, " +
            $"Spheres={(state.HasSphereProgress ? $"{state.CollectedSpheres}/{Math.Ceiling(state.RequiredSpheres):F0}" : "Unavailable")}, " +
            $"FellOff={state.CharacterFellOff}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), Grounded={state.IsGrounded}, " +
            $"MouseHeld={Input.GetMouseButton(0)}, " +
            $"PlayerJump[IsJumping={player.isJumping}," +
            $"JumpTime={player.jumpTime:F3},Counter=" +
            $"{player.jumpTimeCounter:F3},Force={player.jumpForce:F3}], " +
            $"JumpPanel[Down={JumpPanel.jumpDown}," +
            $"Pressed={JumpPanel.jumpPressed},Up={JumpPanel.jumpUp}," +
            $"HeldPointers={(JumpPanel.instance != null ? JumpPanel.instance.heldPointerCount : -1)}], " +
            $"PlayerBounds[{bounds}], Wall[{wallSummary}], " +
            $"Hazard[{hazardSummary}], Scan[{scanSummary}], " +
            $"ShadowPlan[{shadowSummary}], " +
            $"ManualLearning[Active={learningSampleActive}," +
            $"AttemptId={(learningSampleActive && learningSource == "Manual" ? learningSampleId : 0)}," +
            $"WallSequenceId={manualWallSequenceId}," +
            $"WallPulse={manualWallJumpCount}], " +
            $"Physics[{latestPhysicsSnapshot.Summary}]",
            "Demonstration");
    }

    private void UpdateAutomaticJump(BonusStageState state)
    {
        PlayerMovement player = PlayerMovement.instance;
        bool successfulCompletionTraversal =
            IsSuccessfulCompletionTraversal(state);
        if (UpdateCompletionStallGuard(state, successfulCompletionTraversal))
            return;
        bool nativeRewardActionPhase =
            IsNativeRewardActionPhase(state);
        bool lifecycleRecoveryPhase =
            terrainContinuationEpochBlocked ||
            pitDescentGuardActive;
        bool completionControlPhase =
            successfulCompletionTraversal ||
            nativeRewardActionPhase ||
            lifecycleRecoveryPhase;
        bool automaticJumpingEnabled = AutomaticJumpingEnabled;
        bool nativeRewardPhaseStarting =
            automaticJumpingEnabled &&
            nativeRewardActionPhase &&
            !completionRewardController.IsRewardPhaseActive;
        if (nativeRewardPhaseStarting)
        {
            bool priorTerrainInputOwned =
                jumpController.IsHoldingJump ||
                automaticPredictionActive ||
                passiveWallApproachActive ||
                wallExitContactWatchActive ||
                wallActionPhase != WallActionPhase.None;
            jumpController.Release();
            if (learningSampleActive && learningSource == "Automatic")
            {
                FinishLearningSample(
                    state,
                    "RewardObjectPhaseOwnershipHandoff");
            }
            ResetAutomaticControlState();
            BonusRunnerLog.Debug(
                $"RewardObjectOwnershipHandoff Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), PriorTerrainOwner=" +
                $"{priorTerrainInputOwned}, RewardFlags[Available=" +
                $"{state.RewardFlagsAvailable},Wait=" +
                $"{state.WaitingForRewardZone},Trigger=" +
                $"{state.RewardZoneEntered},Giving=" +
                $"{state.GivingRewards}], Target=" +
                $"[{rewardTargetDetector.Describe()}]. Existing terrain/wall input was " +
                "released atomically before reward control began.",
                "Completion");
        }
        if (player != null &&
            jumpController.IsHoldingJump &&
            automaticPredictionActive &&
            learningSampleActive &&
            learningSource == "Automatic" &&
            state.HasPlayer &&
            (state.IsActiveGameplay ||
             completionControlPhase))
        {
            MonitorCommittedJump(state, player);
        }
        jumpController.Update(player);
        completionRewardController.ObserveTraversal(
            automaticJumpingEnabled && nativeRewardActionPhase,
            state);

        if (!automaticJumpingEnabled ||
            !state.HasPlayer || player == null ||
            (!state.IsActiveGameplay &&
              !completionControlPhase) ||
            !state.IsSupportedBonusMap)
        {
            bool completingCommittedTrajectory =
                AutomaticJumpingEnabled &&
                state.HasPlayer && player != null &&
                state.IsSupportedBonusMap &&
                learningSampleActive &&
                learningSource == "Automatic" &&
                learningTookOff &&
                automaticPredictionActive &&
                !state.IsGrounded;
            if (completingCommittedTrajectory)
            {
                if (!automaticControlSuspended)
                {
                    automaticControlSuspended = true;
                    BonusRunnerLog.Debug(
                        $"CommittedJumpCarryThrough AttemptId={automaticAttemptId}, " +
                        $"GameState={state.GameStateName}, " +
                        $"Elapsed={Time.unscaledTime - learningInputDownTime:F3}s, " +
                        $"PlannedHold={automaticPlannedHold:F3}s, " +
                        $"ControllerHolding={jumpController.IsHoldingJump}. " +
                        "No new route will be started, but the accepted jump will not be truncated.",
                        "Control");
                }
                return;
            }

            if (!automaticControlSuspended)
            {
                automaticControlSuspended = true;
                BonusRunnerLog.Debug(
                    $"AutomaticControlSuspended GameState={state.GameStateName}, " +
                    $"Map={state.MapName}, HasPlayer={state.HasPlayer}. " +
                    "All jump input has been released; death/transition movement is game-controlled.",
                    "Control");
            }
            jumpController.Release();
            ResetAutomaticControlState();
            return;
        }
        if (automaticControlSuspended)
        {
            automaticControlSuspended = false;
            BonusRunnerLog.Debug(
                $"AutomaticControlResumed GameState={state.GameStateName}, " +
                $"Map={state.MapName}, Section={state.SectionIndex}.",
                "Control");
        }

        // A typed reward object owns input only while the player is alive.
        // Validate the same sustained, unrecoverable pit evidence before the
        // reward early-return so a persistent pooled target cannot keep
        // jump/arrow/dash control through death and respawn.
        if (nativeRewardActionPhase)
        {
            bool rewardPhaseLowDescending =
                !state.IsGrounded &&
                state.PlayerPosition.y < PitDescentYThreshold &&
                state.PlayerVelocity.y <= PitDescentVelocityThreshold;
            bool rewardPhaseRecoverableWall = HasRecoverableWallAhead(
                state,
                player,
                requirePlannedTarget: true,
                out _,
                out string rewardPhaseWallEvidence);
            bool rewardPhasePitConfirmed =
                UpdateAutomaticPitConfirmation(
                    rewardPhaseLowDescending &&
                    !rewardPhaseRecoverableWall);
            if (rewardPhaseLowDescending &&
                !rewardPhaseRecoverableWall &&
                !rewardPhasePitConfirmed)
            {
                jumpController.Release();
                if (BonusRunnerLog.IsDebugMode &&
                    Time.unscaledTime >= nextPitDescentEvidenceTime)
                {
                    nextPitDescentEvidenceTime =
                        Time.unscaledTime + 0.25f;
                    BonusRunnerLog.Debug(
                        $"RewardObjectPitConfirmationPending Position=" +
                        $"({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), Velocity=" +
                        $"({state.PlayerVelocity.x:F3}," +
                        $"{state.PlayerVelocity.y:F3}), ConfirmedSteps=" +
                        $"{pitDescentCandidateFixedSteps}/" +
                        $"{PitDescentConfirmationFixedSteps}, Wall=" +
                        $"{rewardPhaseWallEvidence}. Reward actions are " +
                        "withheld while lifecycle evidence is unresolved.",
                        "Lifecycle");
                }
                return;
            }

            if (rewardPhasePitConfirmed || pitDescentGuardActive)
            {
                string revocationReason = rewardPhasePitConfirmed
                    ? "ConfirmedPitDescent"
                    : "PitDescentGuardActive";
                BonusRunnerLog.Warning(
                    $"RewardObjectOwnershipRevoked Reason=" +
                    $"{revocationReason}, Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), Target=" +
                    $"[{rewardTargetDetector.Describe()}]. The typed " +
                    "target must be re-established by a distinct instance " +
                    "or after the retired instance is observed absent.");
                rewardTargetDetector.BeginEpoch(
                    state,
                    revocationReason);
                rewardTargetObservation =
                    rewardTargetDetector.LastObservation;
                lastRewardTargetObservationSignature = string.Empty;
                rewardLatchObservedDuringInactiveFrame = false;
                rewardTargetEmptyBaselineRequired = true;
                rewardTargetRearmDeferralLogged = false;
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
                bonusGameplayStarted = false;
                terrainContinuationEpochBlocked = true;
                ResetCompletionTraversalSpeedEvidence(
                    revocationReason);
                completionRewardController.Reset(revocationReason);
                nativeRewardActionPhase = false;
                successfulCompletionTraversal =
                    IsSuccessfulCompletionTraversal(state);
                completionControlPhase =
                    successfulCompletionTraversal;
            }
        }

        if (nativeRewardActionPhase)
        {
            bool dashControlIdle = IsCompletionDashControlIdle();
            if (!dashControlIdle &&
                CompletionWindDashEnabled &&
                BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextCompletionDashDeferralLogTime)
            {
                nextCompletionDashDeferralLogTime =
                    Time.unscaledTime + 0.25f;
                BonusRunnerLog.Debug(
                    $"CompletionWindDashDeferred Reason=OwnedTrajectory, " +
                    $"Holding={jumpController.IsHoldingJump}, " +
                    $"AutomaticArmed={automaticJumpArmed}, Prediction=" +
                    $"{automaticPredictionActive}, Learning=" +
                    $"{learningSampleActive}/{learningSource}, PassiveWall=" +
                    $"{passiveWallApproachActive}, WallPhase=" +
                    $"{wallActionPhase}, ExitWatch=" +
                    $"{wallExitContactWatchActive}. The existing route keeps " +
                    "input ownership until it finishes.",
                    "Completion");
            }
            bool windDashActivated = dashControlIdle &&
                completionRewardController.TickGroundWindDash(
                    state,
                    player,
                    CompletionWindDashEnabled);
            if (windDashActivated)
            {
                completionDashPlanningResumeFixedStep = Math.Max(
                    completionDashPlanningResumeFixedStep,
                    JumpPhysicsFeedback.FixedStepSequence + 2);
                ClearRoutePlanLock();
                BonusRunnerLog.Debug(
                    $"CompletionWindDashPlanningBarrier ActivatedAtFixedStep=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}, ResumeAt=" +
                    $"{completionDashPlanningResumeFixedStep}, " +
                    $"CapturedVX={Mathf.Abs(state.PlayerVelocity.x):F3}. " +
                    "The native reward phase owns control; no terrain route " +
                    "will be planned while the dash velocity settles.",
                    "Completion");
            }

            TryCompletionRewardActions(state, player);
            return;
        }

        // characterFellOff can remain true for the rest of a protected run,
        // and authored trenches can legitimately descend below Y=-5. A low
        // Y value is therefore not a death state by itself. Confirm sustained
        // downward motion only when no planned, reachable wall face exists.
        // This preserves a live trench/wall route while still closing the
        // protector-teleport window after a real fall.
        bool lowDescending =
            !state.IsGrounded &&
            state.PlayerPosition.y < PitDescentYThreshold &&
            state.PlayerVelocity.y <= PitDescentVelocityThreshold;
        // A severe but accepted partial jump remains an owned flight. Give
        // its exact mapped contact one recovery opportunity before the generic
        // pit confirmation can release that ownership.
        bool partialImpulseOwnerActive =
            partialAutomaticJumpImpulseOwnedFlight &&
            partialAutomaticJumpImpulseAttemptId == automaticAttemptId &&
            learningSampleActive &&
            learningSource == "Automatic" &&
            learningTookOff &&
            automaticPredictionActive;
        if (lowDescending &&
            partialImpulseOwnerActive &&
            TryPromoteUnexpectedFlightWallContact(state, player))
        {
            return;
        }
        // Exact authored Stage-3 collision is also evaluated before the pit
        // guard. A nominal landing sample may have closed only a few physics
        // steps earlier; releasing input merely because that stale prediction
        // no longer owns a wall reproduces the Ground 6/S3 death.
        if (TryPromoteRecentOrAuthoredStage3WallContact(
                state,
                player))
        {
            return;
        }
        // A Spirit Boost flight can arrive slightly earlier than its slow/fast
        // envelope predicted and physically meet the next face after the
        // second-stage landing preview has already been rejected.  At that
        // point the nominal route no longer owns a wall, so the generic pit
        // guard would otherwise classify the live, climbable contact as a
        // death.  Rebase only this late-section Spirit contact from observed
        // geometry before asking whether the old plan still owns the wall.
        if (TryPromoteSpiritPitWallContact(state, player, lowDescending))
            return;
        bool recoverableWall = HasRecoverableWallAhead(
            state,
            player,
            requirePlannedTarget: true,
            out BonusWallContact pitWall,
            out string pitWallEvidence);
        bool confirmedPitDescent = UpdateAutomaticPitConfirmation(
            lowDescending && !recoverableWall);
        if (lowDescending && recoverableWall &&
            Time.unscaledTime >= nextPitDescentEvidenceTime)
        {
            nextPitDescentEvidenceTime = Time.unscaledTime + 0.25f;
            BonusRunnerLog.Debug(
                $"PitThresholdSuppressed Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), Wall={pitWallEvidence}, " +
                $"FaceX={pitWall.FaceX:F3}, Distance={pitWall.Distance:F3}, " +
                $"Touching={pitWall.IsTouching}, Maneuver=" +
                $"{automaticManeuver}, Phase={wallActionPhase}. " +
                "The planned wall route retains input authority.",
                "Lifecycle");
        }
        if (lowDescending && !recoverableWall && !confirmedPitDescent)
        {
            // Do not start a fresh route during the short confirmation
            // interval. Keep the existing wall target intact in case the
            // next physics step establishes valid face contact.
            jumpController.Release();
            return;
        }
        if (confirmedPitDescent)
        {
            if (!terrainContinuationEpochBlocked)
            {
                rewardTargetDetector.BeginEpoch(
                    state,
                    "ConfirmedPitDescent");
                rewardTargetObservation =
                    rewardTargetDetector.LastObservation;
                lastRewardTargetObservationSignature = string.Empty;
                rewardLatchObservedDuringInactiveFrame = false;
                rewardTargetEmptyBaselineRequired = true;
                rewardTargetRearmDeferralLogged = false;
                rewardTargetConsecutiveEmptyScans = 0;
                rewardTargetLastEmptyScanFrame = -1;
                completionRewardController.Reset(
                    "ConfirmedPitDescent");
            }
            // Death blocks both terrain and reward ownership until the
            // respawn is grounded and real forward gameplay has resumed.
            terrainContinuationEpochBlocked = true;
            bonusGameplayStarted = false;
            pitRespawnImmediateTakeoverEligible = true;
            pitRespawnTakeoverEvidence = "ConfirmedPitDescent";
            if (!pitDescentGuardActive)
            {
                pitDescentGuardActive = true;
                RecordRunDeath(state);
                pitRespawnLastFixedStep = -1;
                pitRespawnStableFixedSteps = 0;
                BonusBoardSegment failedTarget =
                    BuildAutomaticTargetSegment();
                BonusRunnerLog.Warning(
                    $"PitDescentDetected Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                    $"AttemptId={automaticAttemptId}, ConfirmedFixedSteps=" +
                    $"{pitDescentCandidateFixedSteps}, PartialImpulse[" +
                    $"Active={partialAutomaticJumpImpulseOwnedFlight}," +
                    $"Attempt={partialAutomaticJumpImpulseAttemptId}," +
                    $"ObservedVY={partialAutomaticJumpObservedVelocityY:F3}," +
                    $"ExpectedVY={partialAutomaticJumpExpectedVelocityY:F3}], " +
                    $"WallEvidence=" +
                    $"{pitWallEvidence}, SphereProgress=" +
                    $"{(state.HasSphereProgress
                        ? $"{state.CollectedSpheres}/" +
                          $"{state.RequiredSpheres}"
                        : "Unavailable")}, Remaining=" +
                    $"{state.RemainingRequiredSpheres}, ActiveGameplay=" +
                    $"{state.IsActiveGameplay}, CompletionTraversal=" +
                    $"{IsSuccessfulCompletionTraversal(state)}, " +
                    $"RewardTargetLatched=" +
                    $"{rewardTargetObservation.IsLatched}, Plan=" +
                    $"{automaticPlanReason}/{automaticManeuver}, " +
                    $"Holding={jumpController.IsHoldingJump}, Target=" +
                    $"{(failedTarget.Width > 0.05f
                        ? $"[{failedTarget.Left:F3}," +
                          $"{failedTarget.Right:F3}]@" +
                          $"{failedTarget.Top:F3}"
                        : "None")}, PredictedLandingX=" +
                    $"{automaticPredictedLandingX:F3}. " +
                    "FailureDomain=Lifecycle; " +
                    "all input is released until a stable respawn landing.");
            }
            jumpController.Release();
            if (learningSampleActive && learningSource == "Automatic")
                FinishLearningSample(state, "PitDescentDetected");
            ResetAutomaticControlState();
            ResetCompletionTraversalSpeedEvidence(
                "ConfirmedPitDescent");
            nextAutomaticAttemptTime = Time.unscaledTime + 0.45f;
            return;
        }
        if (pitDescentGuardActive)
        {
            BonusWallContact respawnWallContact =
                state.IsSupportedBonusMap &&
                state.HasPlayer &&
                state.IsGrounded &&
                Mathf.Abs(state.PlayerVelocity.y) <= 2.50f &&
                Mathf.Abs(state.PlayerVelocity.x) < 1.0f &&
                lastActiveTerrainSection >= 0
                    ? wallDetector.Detect(
                        player,
                        Mathf.Max(1f, lastReliableHorizontalSpeed))
                    : default;
            float respawnFeetY = player.playerCollider != null
                ? player.playerCollider.bounds.min.y
                : state.PlayerPosition.y - 0.27f;
            float respawnHalfWidth = player.playerCollider != null
                ? Mathf.Max(
                    0.15f,
                    player.playerCollider.bounds.extents.x)
                : 0.60f;
            BonusBoardSegment respawnWallSurface = default;
            bool mappedWallRespawnReady =
                respawnWallContact.IsDetected &&
                respawnWallContact.IsTouching &&
                platformScanner.TryFindWallSurfaceAtFace(
                    respawnWallContact.FaceX,
                    respawnFeetY,
                    respawnHalfWidth,
                    out respawnWallSurface);
            bool forwardGameplayResumed =
                state.IsActiveGameplay ||
                Mathf.Abs(state.PlayerVelocity.x) >= 1.0f ||
                mappedWallRespawnReady;
            bool stableRespawnFrame =
                state.IsGrounded &&
                Mathf.Abs(state.PlayerVelocity.y) <= 2.50f &&
                forwardGameplayResumed;
            if (!stableRespawnFrame)
            {
                pitRespawnLastFixedStep = -1;
                pitRespawnStableFixedSteps = 0;
                jumpController.Release();
                return;
            }

            long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
            if (fixedStep != pitRespawnLastFixedStep)
            {
                pitRespawnLastFixedStep = fixedStep;
                pitRespawnStableFixedSteps++;
            }
            bool immediateTakeover =
                pitRespawnImmediateTakeoverEligible &&
                (Mathf.Abs(state.PlayerVelocity.x) >= 1.0f ||
                 mappedWallRespawnReady);
            int requiredStableFixedSteps = immediateTakeover ? 1 : 2;
            bool restartDelaySatisfied =
                immediateTakeover ||
                Time.unscaledTime >= nextAutomaticAttemptTime;
            if (pitRespawnStableFixedSteps < requiredStableFixedSteps ||
                !restartDelaySatisfied)
            {
                jumpController.Release();
                return;
            }

            string recoveryEvidence = pitRespawnTakeoverEvidence;
            pitDescentGuardActive = false;
            terrainContinuationEpochBlocked = false;
            bool terrainEpochRearmed =
                state.IsSupportedBonusMap &&
                state.HasPlayer &&
                lastActiveTerrainSection >= 0;
            bonusGameplayStarted = terrainEpochRearmed;
            if (terrainEpochRearmed && state.IsActiveGameplay)
                lastActiveTerrainSection = state.SectionIndex;
            ResetAutomaticPitConfirmation();
            bool sameFrameRoutingResumed =
                immediateTakeover && terrainEpochRearmed;
            if (sameFrameRoutingResumed)
            {
                // A confirmed pit or a verified upward protector teleport is
                // already a strong lifecycle boundary. On the first real
                // grounded fixed step, use the live Rigidbody speed and
                // refresh the remembered terrain section immediately. The
                // former extra fixed step, 0.45 s timer, and following render
                // frame allowed the accelerated revive to consume an entire
                // launch window before control returned.
                nextAutomaticAttemptTime = Time.unscaledTime;
                if (speedPlanningResumeFixedStep > fixedStep)
                    speedPlanningResumeFixedStep = fixedStep;
                RefreshMapRegistry(latestState);
                state = GetRoutingState(latestState);
                successfulCompletionTraversal =
                    IsSuccessfulCompletionTraversal(state);
                nativeRewardActionPhase =
                    IsNativeRewardActionPhase(state);
                lifecycleRecoveryPhase = false;
                completionControlPhase =
                    successfulCompletionTraversal ||
                    nativeRewardActionPhase;
            }
            BonusRunnerLog.Debug(
                $"PitDescentGuardReleased Position=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                $"StableFixedSteps={pitRespawnStableFixedSteps}, " +
                 $"ForwardGameplayResumed={forwardGameplayResumed}, " +
                 $"WallRespawnReady={mappedWallRespawnReady}, " +
                 $"WallFaceX={respawnWallContact.FaceX:F3}, " +
                 $"WallTarget={(mappedWallRespawnReady ? $"[{respawnWallSurface.Left:F3},{respawnWallSurface.Right:F3}]@{respawnWallSurface.Top:F3}" : "None")}, " +
                 $"TerrainEpochRearmed={terrainEpochRearmed}, " +
                 $"ActiveGameplay={state.IsActiveGameplay}, RecoveryEvidence=" +
                 $"{recoveryEvidence}, ImmediateTakeover=" +
                 $"{immediateTakeover}, RequiredStableFixedSteps=" +
                 $"{requiredStableFixedSteps}, SameFrameRouting=" +
                 $"{sameFrameRoutingResumed}, ControllerSection=" +
                 $"{latestState.SectionIndex}, RoutingSection=" +
                 $"{state.SectionIndex}. A stable " +
                 "grounded respawn with forward motion or authoritative " +
                 "physical wall contact was observed; " +
                 (sameFrameRoutingResumed
                    ? "the verified accelerated revive resumes route planning " +
                      "in this LateUpdate from refreshed terrain identity."
                    : "the conservative lifecycle path resumes on the next " +
                      "frame after map identity is refreshed."),
                "Lifecycle");
            pitRespawnLastFixedStep = -1;
            pitRespawnStableFixedSteps = 0;
            pitRespawnImmediateTakeoverEligible = false;
            pitRespawnTakeoverEvidence = string.Empty;
            if (!sameFrameRoutingResumed)
            {
                // An unverified lifecycle transition keeps the older
                // conservative handoff and never plans from stale identity.
                jumpController.Release();
                return;
            }
        }

        if (successfulCompletionTraversal &&
            TryArmCompletionTraversalWallContact(state, player))
        {
            return;
        }

        // A fourth-section collection jump can clear the trench without
        // clearing the following face.  Exact physical face contact is a
        // continuation of that airborne action, not a platform landing that
        // should be handed back to the ground planner.  Promote it before the
        // generic wall gate or stable-landing confirmation can discard the
        // live contact.
        if (TryPromoteLateSectionFlightWallContact(state, player))
            return;

        if (TryPromoteUnexpectedFlightWallContact(state, player))
            return;

        // Stage2UnmappedWallIntercept is a route intention only. Older builds
        // promoted it into a second input owner that could remain active while
        // issuing no action, permanently blocking both the ground planner and
        // the shared wall executor. Any stale instance is relinquished here;
        // exact physical contact below is rebased onto the observed wall top
        // and handled by the same bounded wall action as every other route.
        if (stage2UnmappedWallTraverseActive)
            ResetStage2UnmappedWallTraverse();

        if (state.UsesStage2LiveRouting &&
            state.SectionIndex == 1 &&
            (state.IsActiveGameplay ||
             successfulCompletionTraversal))
        {
            float stage2PlayerHalfWidth = player.playerCollider != null
                ? Mathf.Max(
                    0.15f,
                    player.playerCollider.bounds.extents.x)
                : 0.60f;
            BonusWallContact stage2Wall = wallDetector.Detect(
                player,
                Mathf.Max(
                    1f,
                    Mathf.Max(
                        lastReliableHorizontalSpeed,
                        sectionCruiseHorizontalSpeed)));
            if (TryAdoptStage2UnmappedPhysicalWallContact(
                    state,
                    player,
                    stage2Wall,
                    stage2PlayerHalfWidth))
            {
                return;
            }
        }

        if (TryWallRecoveryJump(state, player))
            return;

        // A rising body can receive one Grounded pulse while its lower edge
        // crosses a narrow pillar lip.  During a promoted completion wall
        // chain there is intentionally no active learning sample between the
        // old lip and the next attached press, so IsCommittedWallClimbActive
        // alone cannot protect this frame.  V0.47 handed the captured
        // X=698.548 / VY=12.976 pulse to the ground planner, which armed a
        // future launch window while the body was already airborne and then
        // fell below X=714.  Preserve wall-route ownership until a real apex,
        // a new wall press, or a stable landing resolves it.
        bool completionRisingWallGroundPulse =
            successfulCompletionTraversal &&
            state.IsGrounded &&
            state.PlayerVelocity.y > 2.50f &&
            passiveWallApproachActive &&
            wallResidualRiseWaitActive &&
            wallActionPhase == WallActionPhase.AwaitingWallContact;
        if (completionRisingWallGroundPulse)
        {
            jumpController.Release();
            if (BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextWallProbeLogTime)
            {
                nextWallProbeLogTime = Time.unscaledTime + 0.08f;
                BonusRunnerLog.Debug(
                    $"CompletionWallGroundPulsePreserved Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), Target=" +
                    $"[{automaticTargetLeft:F3}," +
                    $"{automaticTargetRight:F3}]@" +
                    $"{automaticTargetTop:F3}, Phase={wallActionPhase}. " +
                    "The rising Grounded pulse is collision evidence, not " +
                    "a stable top landing; ground routing remains blocked.",
                    "Recovery");
            }
            return;
        }

        // An unresolved downstream-face watch or objective descent owns the
        // frame even when a render-frame ray briefly misses. Letting grounded
        // routing continue here can erase the watch/speed latch immediately
        // before the next physics step confirms the wall contact.
        if (wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted)
        {
            if (attachedObjectiveDescentActive)
                jumpController.Release();
            return;
        }

        // Unity reports a short Grounded pulse while the player is still
        // sliding/climbing against the vertical face. It is not a platform
        // landing. Keep the wall sequence alive until UpdateLearningSample
        // verifies the target top or the independent climb deadline expires.
        if (IsCommittedWallClimbActive() && state.IsGrounded)
        {
            if (BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextWallProbeLogTime)
            {
                nextWallProbeLogTime = Time.unscaledTime + 0.10f;
                BonusRunnerLog.Debug(
                    $"WallClimbGroundPulseIgnored AttemptId={automaticAttemptId}, " +
                    $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                    $"ControllerHolding={jumpController.IsHoldingJump}, " +
                    $"Attempts={wallRecoveryAttempts}/{MaximumWallRecoveriesPerAirborneSequence}, " +
                    $"CommitRemaining={wallRecoveryCommitmentUntil - Time.unscaledTime:F3}s. " +
                    "Normal landing/reset logic is intentionally bypassed.",
                    "Recovery");
            }
            return;
        }

        if (!state.IsGrounded)
        {
            if (!automaticJumpArmed)
                airborneAfterAutomaticJump = true;
            MonitorAutomaticTrajectory(state);
            RefreshSecondStagePreview(state, player);
            return;
        }

        // UpdateLearningSample owns the grounded action until it has observed
        // two stable physics steps on one support. Without this gate,
        // LateUpdate could arm a new jump during the one-frame confirmation
        // window and interrupt the sample that was introduced specifically to
        // reject wall-contact Grounded pulses.
        if (learningSampleActive)
            return;

        if (!passiveWallApproachActive &&
            !jumpController.IsHoldingJump)
        {
            ResetWallRecoveryAfterLanding();
        }

        if (airborneAfterAutomaticJump)
        {
            automaticJumpArmed = true;
            airborneAfterAutomaticJump = false;
        }

        BonusMapController controller = BonusMapController.instance;
        if (controller == null ||
            !automaticJumpArmed ||
            jumpController.IsHoldingJump ||
            Time.unscaledTime < nextAutomaticAttemptTime)
            return;

        // Rigidbody state is unchanged between render frames that share the
        // same native physics step. Re-running the full platform scan, route
        // search, and continuation proof on each of those frames creates CPU
        // spikes without adding new evidence. The pre-movement FixedUpdate
        // controller remains authoritative at native physics cadence; this
        // gate only removes duplicate render-side ground planning.
        long renderPlanningFixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (renderPlanningFixedStep == lastRenderGroundPlanningFixedStep)
            return;
        lastRenderGroundPlanningFixedStep = renderPlanningFixedStep;

        float observedHorizontalSpeed = Mathf.Abs(state.PlayerVelocity.x);
        bool observedPlanningSpeedUsable =
            observedHorizontalSpeed > 1f && observedHorizontalSpeed < 80f;
        float planningHorizontalSpeed = observedPlanningSpeedUsable
            ? observedHorizontalSpeed
            : Mathf.Max(1f, lastReliableHorizontalSpeed);
        string planningSpeedSource = observedPlanningSpeedUsable
            ? "LiveRigidbody"
            : "ReliableRunFallback";

        BonusBoardScanResult scan = platformScanner.Scan(
            state.PlayerPosition,
            player,
            planningHorizontalSpeed);
        BonusHazard hazard = hazardScanner.FindNearest(state.PlayerPosition);
        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        latestPhysicsSnapshot = observedPhysics;
        JumpPhysicsSnapshot physics = BuildPlanningPhysics(
            state.SectionIndex,
            observedPhysics,
            planningHorizontalSpeed,
            state.IsActiveGameplay ||
                IsSuccessfulCompletionTraversal(state),
            state.SpiritBoostEnabled
                ? sectionCruiseHorizontalSpeed
                : 0f);
        Vector2 planningVelocity = new(
            planningHorizontalSpeed,
            state.PlayerVelocity.y);
        bool speedAdaptiveRoutingRequired =
            state.SpiritBoostEnabled ||
            planningHorizontalSpeed > physics.BaseHorizontalSpeed + 1.0f;
        float sphereLookAhead = Mathf.Clamp(
            30f + Mathf.Max(0f, planningHorizontalSpeed - 18f) * 1.4f,
            30f,
            80f);
        Vector2[] routeSphereObjectives =
            state.HasSphereProgress && state.RemainingRequiredSpheres > 0
                ? BonusStageInspector.GetActiveSpherePositions(
                    state.PlayerPosition.x - 1.0f,
                    state.PlayerPosition.x + SectionObjectiveHorizon)
                : Array.Empty<Vector2>();
        float verifiedSpiritBaseSpeed =
            sectionCruiseHorizontalSpeed > 1f
                ? sectionCruiseHorizontalSpeed
                : 0f;
        SpiritBoostRouteContext spiritBoostRouteContext =
            CaptureRouteSpeedContext(
                state,
                player,
                state.PlayerPosition.x - 2.0f,
                state.PlayerPosition.x + sphereLookAhead,
                verifiedSpiritBaseSpeed);
        BonusJumpPlan selectorPlan = default;
        bool selectorPlanAvailable = false;
        bool liveGeometryRouting =
            !state.UsesStage3AuthoredRouting;
        bool ordinaryStage1Section2CrossSphereDetected =
            TryGetOrdinaryStage1Section2CrossSphereRoute(
                state,
                scan,
                routeSphereObjectives,
                out Vector2 ordinaryStage1CrossSphereCenter);
        if (ordinaryStage1Section2CrossSphereDetected &&
            (!stage1CrossSpherePriorityLatched ||
             Mathf.Abs(
                 stage1CrossSpherePriorityCenter.x -
                 ordinaryStage1CrossSphereCenter.x) > 0.50f))
        {
            stage1CrossSpherePriorityLatched = true;
            stage1CrossSpherePriorityCenter =
                ordinaryStage1CrossSphereCenter;
            stage1CrossSpherePriorityTarget = scan.Next;
        }
        if (stage1CrossSpherePriorityLatched &&
            (state.SpiritBoostEnabled ||
             state.SectionIndex != 2 ||
             !string.Equals(
                 state.MapName,
                 "map_bonus_stage_1",
                 StringComparison.OrdinalIgnoreCase) ||
             state.PlayerPosition.x >
                 stage1CrossSpherePriorityTarget.Right + 2.0f))
        {
            stage1CrossSpherePriorityLatched = false;
        }
        if (liveGeometryRouting)
        {
            scan = jumpPlanner.SelectReachableRoute(
                scan,
                state.PlayerPosition,
                planningVelocity,
                physics,
                hazard,
                routeSphereObjectives,
                sectionIndex: state.SectionIndex,
                preferSphereCoverage:
                    routeSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch:
                    state.SectionIndex >= 2,
                useFixedStepAlignedHolds: state.SectionIndex >= 2,
                spiritBoost: spiritBoostRouteContext,
                selectionContext: "Live",
                selection: out string liveSelection,
                selectedPlan: out selectorPlan,
                selectedPlanAvailable: out selectorPlanAvailable,
                useStage2LiveTopologyProfile:
                    state.UsesStage2LiveRouting);
            int liveDetailStart = liveSelection.IndexOf('[');
            string liveSelectionClass = liveDetailStart > 0
                ? liveSelection.Substring(0, liveDetailStart)
                : liveSelection;
            string liveSelectionSignature =
                $"{liveSelectionClass}:{scan.Reason}:" +
                $"{(scan.HasNext ? scan.Next.Left : float.NaN):F2}:" +
                $"{(scan.HasNext ? scan.Next.Right : float.NaN):F2}:" +
                $"{(scan.HasNext ? scan.Next.Top : float.NaN):F2}";
            if (!string.Equals(
                    liveSelectionSignature,
                    lastLiveRouteSelection,
                    StringComparison.Ordinal))
            {
                lastLiveRouteSelection = liveSelectionSignature;
                BonusRunnerLog.Debug(
                    $"LiveReachableRoute X={state.PlayerPosition.x:F3}, " +
                    $"Speed={planningHorizontalSpeed:F3}, " +
                    $"SpeedSource={planningSpeedSource}, Selection=" +
                    $"{liveSelection}, ScanReason={scan.Reason}. " +
                    "An impossible nearest landing may be replaced only by " +
                    "a verified farther support whose trajectory clears " +
                    "every observed intermediate surface.",
                    "Routing");
            }
        }
        else
        {
            lastLiveRouteSelection = string.Empty;
        }

        if (speedAdaptiveRoutingRequired && !liveGeometryRouting)
        {
            scan = jumpPlanner.SelectReachableRoute(
                scan,
                state.PlayerPosition,
                planningVelocity,
                physics,
                hazard,
                routeSphereObjectives,
                sectionIndex: state.SectionIndex,
                preferSphereCoverage:
                    routeSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch: false,
                useFixedStepAlignedHolds: false,
                spiritBoost: spiritBoostRouteContext,
                selectionContext: "SpeedAdaptive",
                selection: out string boostSelection,
                selectedPlan: out selectorPlan,
                selectedPlanAvailable: out selectorPlanAvailable);
            if (boostSelection != lastBoostRouteSelection)
            {
                lastBoostRouteSelection = boostSelection;
                BonusRunnerLog.Debug(
                    $"SpeedAdaptiveRoute X={state.PlayerPosition.x:F3}, " +
                    $"Speed={planningHorizontalSpeed:F3}, " +
                    $"SpeedSource={planningSpeedSource}, " +
                    $"SpiritBoost={state.SpiritBoostEnabled}, " +
                    $"Selection={boostSelection}, ScanReason={scan.Reason}.",
                    "Routing");
            }
        }
        else if (!liveGeometryRouting)
        {
            // Authored terrain remains a source of candidate surfaces, not a
            // second planner. Normal and Spirit runs use the same reachable
            // route selector and the same safety-first soul utility.
            scan = jumpPlanner.SelectReachableRoute(
                scan,
                state.PlayerPosition,
                planningVelocity,
                physics,
                hazard,
                routeSphereObjectives,
                sectionIndex: state.SectionIndex,
                preferSphereCoverage:
                    routeSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch: false,
                useFixedStepAlignedHolds: false,
                spiritBoost: spiritBoostRouteContext,
                selectionContext: "Authored",
                selection: out string authoredSelection,
                selectedPlan: out selectorPlan,
                selectedPlanAvailable: out selectorPlanAvailable);
            if (authoredSelection != lastBoostRouteSelection)
            {
                lastBoostRouteSelection = authoredSelection;
                BonusRunnerLog.Debug(
                    $"UnifiedAuthoredRoute X={state.PlayerPosition.x:F3}, " +
                    $"Speed={planningHorizontalSpeed:F3}, " +
                    $"SpiritBoost={state.SpiritBoostEnabled}, " +
                    $"Selection={authoredSelection}, " +
                    $"ScanReason={scan.Reason}.",
                    "Routing");
            }
        }
        else
        {
            lastBoostRouteSelection = string.Empty;
        }
        ObserveNoSupportStall(state, scan);
        bool urgentNarrowLandingChain =
            scan.IsValid &&
            secondStagePreviewActive &&
            secondStageObservedAirborne &&
            secondStageProjectedScan.IsValid &&
            secondStageProjectedScan.HasNext &&
            secondStageProjectedPlan.IsValid &&
            secondStageExpectedSupport.Width <= 2.25f &&
            Mathf.Abs(
                scan.Current.Top -
                secondStageExpectedSupport.Top) <= 0.35f &&
            state.PlayerPosition.x >=
                secondStageExpectedSupport.Left - 0.15f &&
            state.PlayerPosition.x <=
                secondStageExpectedSupport.Right + 0.15f;
        ConfirmOrRejectSecondStagePreview(state, scan);
        BonusJumpPlan plan = GetStableRoutePlan(
            state,
            scan,
            planningVelocity,
            physics,
            hazard,
            routeSphereObjectives,
            spiritBoostRouteContext,
            selectorPlan,
            selectorPlanAvailable);
        bool ordinaryStage1Section2CrossSphereRoute =
            stage1CrossSpherePriorityLatched &&
            scan.IsValid &&
            scan.HasNext &&
            Stage1CrossSphereTargetMatchesLatchedTarget(
                scan.Next,
                stage1CrossSpherePriorityTarget);
        if (ordinaryStage1Section2CrossSphereRoute)
        {
            BonusJumpPlan originalPlan = plan;
            BonusJumpPlan crossSpherePriorityPlan = jumpPlanner.Plan(
                scan,
                state.PlayerPosition,
                planningVelocity,
                physics,
                hazard,
                routeSphereObjectives,
                sectionIndex: state.SectionIndex,
                preferSphereCoverage: true,
                allowRecoverableLowerFaceCatch: false,
                useFixedStepAlignedHolds: false,
                spiritBoost: spiritBoostRouteContext,
                routeTargetIsAuthoritative: true,
                useStage2LiveTopologyProfile:
                    state.UsesStage2LiveRouting,
                minimumSphereHits: Stage1CrossSphereMinimumHits);
            bool priorityApplied =
                crossSpherePriorityPlan.IsValid &&
                crossSpherePriorityPlan.ExpectedSphereHits >=
                    Stage1CrossSphereMinimumHits &&
                (crossSpherePriorityPlan.Maneuver ==
                     BonusManeuverKind.GroundJumpToLanding ||
                 crossSpherePriorityPlan.Maneuver ==
                     BonusManeuverKind.SphereSweepToLowerLanding);
            if (priorityApplied)
            {
                plan = crossSpherePriorityPlan;
            }

            if (!float.IsFinite(lastLoggedStage1CrossSphereCenterX) ||
                Mathf.Abs(
                    lastLoggedStage1CrossSphereCenterX -
                    stage1CrossSpherePriorityCenter.x) > 0.50f)
            {
                lastLoggedStage1CrossSphereCenterX =
                    stage1CrossSpherePriorityCenter.x;
                BonusRunnerLog.Debug(
                    $"Stage1Section2CrossSpherePriority CenterX=" +
                    $"{stage1CrossSpherePriorityCenter.x:F3}, PositionX=" +
                    $"{state.PlayerPosition.x:F3}, RequiredActualHits=1, " +
                    $"Policy=LatchedSafeSameTargetPriorityWithOriginalFallback, " +
                    $"Applied={priorityApplied}, OriginalPlan=" +
                    $"{originalPlan.Reason}/{originalPlan.Maneuver}/" +
                    $"Hits={originalPlan.ExpectedSphereHits}, PriorityPlan=" +
                    $"{crossSpherePriorityPlan.Reason}/" +
                    $"{crossSpherePriorityPlan.Maneuver}/Hits=" +
                    $"{crossSpherePriorityPlan.ExpectedSphereHits}, " +
                    $"SelectedTarget=[{scan.Next.Left:F3}," +
                    $"{scan.Next.Right:F3}]@{scan.Next.Top:F3}. " +
                    "The pickup preference cannot replace the selected " +
                    "support or invalidate the original survival route.",
                    "Routing");
            }
        }
        if (state.UsesStage3AuthoredRouting &&
            state.SectionIndex == 2 &&
            jumpPlanner.TryPlanAuthoredGround6EntryWallRoute(
                scan,
                state.PlayerPosition,
                planningHorizontalSpeed,
                physics,
                hazard,
                out BonusJumpPlan authoredGround6WallRoute) &&
            (plan.Maneuver !=
                 BonusManeuverKind.EnterTrenchThenWallJump ||
             !plan.IsValid))
        {
            BonusRunnerLog.Warning(
                $"AuthoredGround6EntryWallRouteRestored Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), PriorPlan={plan.Reason}/" +
                $"{plan.Maneuver}, Source=[{scan.Current.Left:F3}," +
                $"{scan.Current.Right:F3}]@{scan.Current.Top:F3}/" +
                $"{scan.Current.MapPieceName}#" +
                $"{scan.Current.MapPieceInstanceId}/S" +
                $"{scan.Current.StaticSurfaceIndex}, Target=" +
                $"[{scan.Next.Left:F3},{scan.Next.Right:F3}]@" +
                $"{scan.Next.Top:F3}/{scan.Next.MapPieceName}#" +
                $"{scan.Next.MapPieceInstanceId}/S" +
                $"{scan.Next.StaticSurfaceIndex}, RestoredPlan=" +
                $"{authoredGround6WallRoute.Reason}/" +
                $"{authoredGround6WallRoute.Maneuver}. " +
                "FailureDomain=RouteSelection; the authored wall-entry " +
                "contract overrides optional landing or pickup ranking.");
            ClearRoutePlanLock();
            plan = authoredGround6WallRoute;
        }
        BonusBoardSegment plannedTarget =
            IsStage2LowCorridorWallCatch(plan, scan)
                ? scan.Intermediate
                : plan.Maneuver == BonusManeuverKind.SphereCollectionJump
                ? scan.Current
                : scan.Next;

        bool urgentNarrowPlanPromoted = false;
        if (urgentNarrowLandingChain &&
            plan.IsValid &&
            !plan.FutureSpeedTransitionExpected &&
            !spiritBoostRouteContext.RequiresSpeedEnvelope &&
            !plan.ShouldJumpNow &&
            scan.HasNext &&
            plan.Maneuver != BonusManeuverKind.EnterTrenchThenWallJump)
        {
            bool genuinelyEarly =
                state.PlayerPosition.x <= plan.PlannedLaunchX + 0.001f;
            float earlyLaunchDistance =
                plan.PlannedLaunchX - state.PlayerPosition.x;
            float earlyLandingX =
                state.PlayerPosition.x + plan.HorizontalTravel;
            float landingTolerance = 0.20f;
            string translatedTrajectoryCheck = "LaunchAlreadyPassed";
            string translatedTargetFaceCheck = "LaunchAlreadyPassed";
            bool translatedHazardSafe =
                genuinelyEarly &&
                jumpPlanner.IsTrajectorySafe(
                    hazard,
                    state.PlayerPosition.x,
                    earlyLandingX,
                    scan.Current.Top,
                    planningHorizontalSpeed,
                    plan.HoldSeconds,
                    plan.PredictedFlightSeconds,
                    physics,
                    out translatedTrajectoryCheck);
            bool translatedTargetFaceSafe =
                genuinelyEarly &&
                jumpPlanner.IsRaisedTargetFaceClear(
                    scan.Current,
                    plannedTarget,
                    state.PlayerPosition.x,
                    planningHorizontalSpeed,
                    plan.HorizontalTravel,
                    plan.HoldSeconds,
                    plan.PredictedFlightSeconds,
                    physics,
                    out translatedTargetFaceCheck);
            bool translatedTrajectorySafe =
                translatedHazardSafe && translatedTargetFaceSafe;
            bool earlyLandingStillFits =
                genuinelyEarly &&
                earlyLaunchDistance <= Mathf.Max(
                    0.80f,
                    planningHorizontalSpeed * physics.FixedDeltaTime * 1.75f) &&
                earlyLandingX >= plannedTarget.SafeLeft - landingTolerance &&
                earlyLandingX <= plannedTarget.SafeRight + landingTolerance &&
                translatedTrajectorySafe;
            if (earlyLandingStillFits)
            {
                plan = plan with
                {
                    ShouldJumpNow = true,
                    PlannedLaunchX = state.PlayerPosition.x,
                    PredictedLandingX = earlyLandingX,
                    HorizontalTravel = Mathf.Max(
                        0f,
                        earlyLandingX - state.PlayerPosition.x),
                    LaunchWindowLeft = state.PlayerPosition.x,
                    LaunchWindowRight = state.PlayerPosition.x,
                    Reason = "UrgentNarrowLandingChain",
                    CandidateSummary =
                        $"PromotedEarlyBy={earlyLaunchDistance:F3}," +
                        $"AdjustedLanding={earlyLandingX:F3}," +
                        $"LiveTrajectory={translatedTrajectoryCheck};" +
                        $"{translatedTargetFaceCheck}; " +
                        plan.CandidateSummary
                };
                urgentNarrowPlanPromoted = true;
                BonusRunnerLog.Debug(
                    $"UrgentNarrowLandingPlanPromoted Position=" +
                    $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"EarlyBy={earlyLaunchDistance:F3}, AdjustedLanding=" +
                    $"{earlyLandingX:F3}, TargetSafe=" +
                    $"[{plannedTarget.SafeLeft:F3}," +
                    $"{plannedTarget.SafeRight:F3}], Hold=" +
                    $"{plan.HoldSeconds:F3}. The verified one-step landing " +
                    "is chained immediately because another fixed-step coast " +
                    "would leave the narrow source support.",
                    "Lookahead");
            }
            else
            {
                BonusRunnerLog.Debug(
                    $"UrgentNarrowLandingPromotionRejected Position=" +
                    $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"PlannedLaunch={plan.PlannedLaunchX:F3}, " +
                    $"GenuinelyEarly={genuinelyEarly}, EarlyBy=" +
                    $"{earlyLaunchDistance:F3}, LiveLanding=" +
                    $"{earlyLandingX:F3}, TargetSafe=" +
                    $"[{plannedTarget.SafeLeft:F3}," +
                    $"{plannedTarget.SafeRight:F3}], TrajectorySafe=" +
                    $"{translatedTrajectorySafe}, Check=" +
                    $"{translatedTrajectoryCheck};" +
                    $"{translatedTargetFaceCheck}. The stale planned launch " +
                    "is not promoted after it has already passed or without " +
                    "a fresh hazard check.",
                    "Lookahead");
            }
        }

        // A spike is allowed to create its own jump only when it is on the
        // current continuous support. A later spike must never replace the
        // route to the immediate next platform.
        bool sameSurfaceHazard = hazard.IsValid && scan.IsValid &&
            hazard.Left >= scan.Current.Left &&
            hazard.Right <= scan.Current.Right;
        if (!plan.IsValid && sameSurfaceHazard)
        {
            ClearRoutePlanLock();
            plan = jumpPlanner.PlanSameSurfaceHazard(
                scan,
                hazard,
                state.PlayerPosition,
                planningVelocity,
                physics,
                spiritBoostRouteContext);
            plannedTarget = scan.Current;
        }

        if (hazard.IsValid)
        {
            string hazardSignature =
                $"{hazard.InstanceId}:{plan.Reason}:{plan.PlannedLaunchX:F2}";
            if (hazardSignature != lastHazardSignature ||
                Time.unscaledTime >= nextHazardLogTime)
            {
                lastHazardSignature = hazardSignature;
                nextHazardLogTime = Time.unscaledTime + 0.25f;
                BonusRunnerLog.Debug(
                    $"HazardProbe X={state.PlayerPosition.x:F3}, " +
                    $"Hazard=[{hazard.Left:F3},{hazard.Right:F3}] Top={hazard.Top:F3}, " +
                    $"OnCurrentSurface={sameSurfaceHazard}, " +
                    $"Collider={hazard.InstanceId}:{hazard.Name}, Path={hazard.ComponentPath}, " +
                    $"Decision={plan.Reason}; {plan.CandidateSummary}",
                    "Hazard");
            }
        }

        if (!plan.IsValid && plan.Reason == "IntentionalDrop" && scan.HasNext)
        {
            float fallSeconds = plan.PredictedFlightSeconds;
            float expectedLandingX = plan.PredictedLandingX;
            secondStageObservedAirborne = false;
            automaticTrajectoryCompatible = true;
            if (plan.FutureSpeedTransitionExpected)
            {
                PrepareSecondStagePreview(
                    state,
                    scan.Next,
                    expectedLandingX,
                    physics,
                    "FutureSpeedIntentionalDrop");
                BonusRunnerLog.Debug(
                    $"IntentionalDropSupportIdentityPreserved Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), TravelEnvelope=" +
                    $"[{plan.MinimumHorizontalTravel:F3}," +
                    $"{plan.MaximumHorizontalTravel:F3}], " +
                    "Reason=FutureSpiritBoostTransition. The landing frame " +
                    "will rescan live support and speed before a second " +
                    "action is allowed, while retaining the expected support " +
                    "for one-step edge-contact recovery.",
                    "Lookahead");
            }
            else
            {
                PrepareSecondStagePreview(
                    state,
                    scan.Next,
                    expectedLandingX,
                    physics,
                    "IntentionalDrop");
            }
            string dropSignature =
                $"{state.SectionIndex}:{scan.Current.Left:F2}:" +
                $"{scan.Current.Right:F2}:{scan.Current.Top:F2}>" +
                $"{scan.Next.Left:F2}:{scan.Next.Right:F2}:" +
                $"{scan.Next.Top:F2}:{expectedLandingX:F2}:" +
                $"{plan.MinimumHorizontalTravel:F2}:" +
                $"{plan.MaximumHorizontalTravel:F2}";
            if (!string.Equals(
                    dropSignature,
                    lastIntentionalDropSignature,
                    StringComparison.Ordinal) ||
                Time.unscaledTime >= nextIntentionalDropLogTime)
            {
                lastIntentionalDropSignature = dropSignature;
                nextIntentionalDropLogTime = Time.unscaledTime + 0.50f;
                BonusRunnerLog.Debug(
                    $"IntentionalDropPlan From=[{scan.Current.Left:F3}," +
                    $"{scan.Current.Right:F3}]@{scan.Current.Top:F3}, " +
                    $"LandingSupport=[{scan.Next.Left:F3},{scan.Next.Right:F3}]" +
                    $"@{scan.Next.Top:F3}, Fall={fallSeconds:F3}s, " +
                    $"ExpectedLandingX={expectedLandingX:F3}. No jump input sent. " +
                    $"Calculation={plan.CandidateSummary}",
                    "Lookahead");
            }
        }
        LogRouteDiagnostics(
            state,
            scan,
            plan,
            planningHorizontalSpeed,
            physics);
        if (successfulCompletionTraversal &&
            Time.unscaledTime >= nextCompletionNavigationLogTime)
        {
            nextCompletionNavigationLogTime = Time.unscaledTime + 0.25f;
            BonusRunnerLog.Debug(
                $"CompletionTraversalNavigation Section=" +
                $"{state.SectionIndex}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), ScanValid=" +
                $"{scan.IsValid}, HasNext={scan.HasNext}, " +
                $"PlanValid={plan.IsValid}, ShouldJump=" +
                $"{plan.ShouldJumpNow}, Maneuver={plan.Maneuver}, " +
                $"Reason={plan.Reason}, TerrainPolicy=" +
                $"SharedPreRewardTerrain, SpeedContext=" +
                $"[{spiritBoostRouteContext.Evidence}]. The same terrain " +
                "selector used before quota continues through the inactive " +
                "transition; reward actions remain blocked until a typed box " +
                "or reward collectable is confirmed on two frames.",
                "Completion");
        }

        bool stationaryBeforeRoute =
            state.IsGrounded && observedHorizontalSpeed <= 1f && scan.HasNext;
        bool speedTransitionWouldBlock =
            plan.IsValid &&
            (plan.ShouldJumpNow || stationaryBeforeRoute) &&
            JumpPhysicsFeedback.FixedStepSequence <
                speedPlanningResumeFixedStep;
        bool speedTransitionBarrier =
            speedTransitionWouldBlock &&
            !urgentNarrowPlanPromoted;
        bool completionDashBarrier =
            successfulCompletionTraversal &&
            JumpPhysicsFeedback.FixedStepSequence <
                completionDashPlanningResumeFixedStep &&
            !urgentNarrowPlanPromoted;
        // A discontinuity or Wind Dash barrier always wins.  A physical
        // obstacle escape may use pre-contact speed, but it must not bypass a
        // still-pending physics observation from a genuine speed change.
        if (speedTransitionBarrier || completionDashBarrier)
        {
            if (Time.unscaledTime >= nextSpeedPlanningBarrierLogTime)
            {
                nextSpeedPlanningBarrierLogTime = Time.unscaledTime + 0.10f;
                BonusRunnerLog.Debug(
                    $"JumpPlanningDeferred Position=" +
                    $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"ObservedVX={observedHorizontalSpeed:F3}, " +
                    $"ReliableVX={lastReliableHorizontalSpeed:F3}, " +
                    $"PlanningVX={planningHorizontalSpeed:F3}, " +
                    $"SpeedSource={planningSpeedSource}, Grounded=" +
                    $"{state.IsGrounded}, HasNext={scan.HasNext}, " +
                    $"Plan={plan.Reason}/{plan.Maneuver}, " +
                    $"CurrentFixedStep=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}, " +
                    $"SpeedResumeFixedStep={speedPlanningResumeFixedStep}, " +
                    $"DashResumeFixedStep=" +
                    $"{completionDashPlanningResumeFixedStep}, Reason=" +
                    (completionDashBarrier
                            ? "CompletionWindDashVelocityPending"
                            : "VelocityDiscontinuityPendingPhysicsStep") +
                    ". No DOWN is sent from a stale horizontal model.",
                    "Physics");
            }
            return;
        }
        if (speedTransitionWouldBlock && urgentNarrowPlanPromoted)
        {
            BonusRunnerLog.Debug(
                $"UrgentNarrowLandingBarrierBypass Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"LiveVX={observedHorizontalSpeed:F3}, ReliableVX=" +
                $"{lastReliableHorizontalSpeed:F3}, Plan=" +
                $"{plan.Reason}/{plan.Maneuver}, CurrentFixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, NormalResume=" +
                $"{speedPlanningResumeFixedStep}. The plan was recomputed " +
                "from the current Rigidbody velocity and the verified narrow " +
                "support cannot survive an extra physics-step wait.",
                "Lookahead");
        }

        if (!stationaryBeforeRoute)
            ResetStage2GroundedWallContactEvidence();

        if (stationaryBeforeRoute)
        {
            BonusObstacleAssessment stationaryObstacle =
                BonusObstacleClassifier.Classify(scan);
            bool mappedGround6RaisedLip =
                string.Equals(
                    scan.Current.MapPieceName,
                    "Ground 6",
                    StringComparison.OrdinalIgnoreCase) &&
                scan.Current.StaticSurfaceIndex == 0 &&
                string.Equals(
                    scan.Next.MapPieceName,
                    "Ground 6",
                    StringComparison.OrdinalIgnoreCase) &&
                scan.Next.StaticSurfaceIndex == 3 &&
                scan.Current.MapPieceInstanceId != 0 &&
                scan.Current.MapPieceInstanceId ==
                    scan.Next.MapPieceInstanceId;
            bool plannedAdjacentWallObstacle =
                plan.IsValid &&
                plan.Maneuver ==
                    BonusManeuverKind.EnterTrenchThenWallJump &&
                stationaryObstacle.Kind == BonusObstacleKind.AdjacentWall &&
                scan.Gap <= 0.10f;
            bool plannedFlushAdjacentObstacle =
                plannedAdjacentWallObstacle &&
                state.PlayerPosition.x >= plan.PlannedLaunchX - 0.16f;
            BonusWallContact contact = plannedAdjacentWallObstacle
                ? wallDetector.Detect(player, planningHorizontalSpeed)
                : default;
            bool raycastTargetContact =
                plannedFlushAdjacentObstacle &&
                contact.IsDetected && contact.IsTouching &&
                Mathf.Abs(contact.FaceX - scan.Next.Left) <= 0.25f;
            Bounds playerBounds = player.playerCollider != null
                ? player.playerCollider.bounds
                : new Bounds(
                    state.PlayerPosition,
                    new Vector3(1.20f, 1.20f, 0f));
            float forwardBodyX = playerBounds.max.x;
            float groundedFeetY = playerBounds.min.y;
            float geometricFaceGap = scan.Next.Left - forwardBodyX;
            bool stage2GeometricContactCandidate =
                state.UsesStage2LiveRouting &&
                plannedAdjacentWallObstacle &&
                state.IsGrounded &&
                Mathf.Abs(state.PlayerVelocity.x) <= 0.50f &&
                Mathf.Abs(state.PlayerVelocity.y) <= 2.50f &&
                scan.HeightDelta >= 0.30f &&
                Mathf.Abs(groundedFeetY - scan.Current.Top) <= 0.25f &&
                state.PlayerPosition.x >= scan.Current.Left - 0.20f &&
                state.PlayerPosition.x <= scan.Current.Right + 0.20f &&
                geometricFaceGap >= -0.10f &&
                geometricFaceGap <= 0.10f;
            bool geometricTargetContact =
                UpdateStage2GroundedWallContactEvidence(
                    stage2GeometricContactCandidate,
                    scan.Next);
            bool confirmedTargetContact =
                raycastTargetContact || geometricTargetContact;
            bool stationarySpeedAboveCruise =
                sectionCruiseHorizontalSpeed > 1f &&
                planningHorizontalSpeed >
                    sectionCruiseHorizontalSpeed +
                    Mathf.Max(
                        1.0f,
                        sectionCruiseHorizontalSpeed * 0.10f);
            bool strictBoostLanding =
                state.SpiritBoostEnabled || stationarySpeedAboveCruise;
            BonusJumpPlan escapePlan = default;
            BonusBoardSegment escapeTarget = default;
            string escapeRejection =
                spiritBoostRouteContext.RequiresSpeedEnvelope
                    ? "FutureSpiritBoostUsesContactWallExecutor"
                    : confirmedTargetContact
                        ? string.Empty
                        : "PlannerNotRunWithoutConfirmedMappedContact";
            bool escapePlanned =
                confirmedTargetContact && mappedGround6RaisedLip &&
                !spiritBoostRouteContext.RequiresSpeedEnvelope &&
                jumpPlanner.TryPlanGroundedContactEscape(
                    scan,
                    state.PlayerPosition,
                    planningHorizontalSpeed,
                    physics,
                    hazard,
                    strictBoostLanding,
                    out escapePlan,
                    out escapeTarget,
                    out escapeRejection);
            if (escapePlanned)
            {
                BonusRunnerLog.Debug(
                    $"GroundedContactEscapeDecision Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), LiveVX=" +
                    $"{observedHorizontalSpeed:F3}, PreContactVX=" +
                    $"{planningHorizontalSpeed:F3}, SpeedSource=" +
                    $"{planningSpeedSource}, SpiritBoost=" +
                    $"{state.SpiritBoostEnabled}, SpeedAboveCruise=" +
                    $"{stationarySpeedAboveCruise}, StrictSafe=" +
                    $"{strictBoostLanding}, WallFaceX=" +
                    $"{contact.FaceX:F3}, Gap={scan.Gap:F3}, Rise=" +
                    $"{scan.HeightDelta:F3}, Hold=" +
                    $"{escapePlan.HoldSeconds:F3}s, Target=" +
                    $"[{escapeTarget.Left:F3}," +
                    $"{escapeTarget.Right:F3}] Safe=" +
                    $"[{escapeTarget.SafeLeft:F3}," +
                    $"{escapeTarget.SafeRight:F3}]@" +
                    $"{escapeTarget.Top:F3}, PredictedLanding=" +
                    $"{escapePlan.PredictedLandingX:F3}, Reason=" +
                    $"{escapePlan.Reason}, RouteIdentity=" +
                    $"{scan.Current.MapPieceName}#" +
                    $"{scan.Current.MapPieceInstanceId}/S" +
                    $"{scan.Current.StaticSurfaceIndex}->S" +
                    $"{scan.Next.StaticSurfaceIndex}. Collision VX=0 is contact " +
                    "evidence, not the post-clear horizontal model. " +
                    $"Candidates[{escapePlan.CandidateSummary}]",
                    "Routing");
                jumpController.Press(
                    player,
                    escapePlan.HoldSeconds,
                    $"GroundedContactEscape: LiveVX=0, " +
                    $"PreContactVX={planningHorizontalSpeed:F2}, " +
                    $"Landing={escapePlan.PredictedLandingX:F2}",
                    GetGroundPlanFixedStepHoldLimit(
                        state,
                        escapePlan,
                        physics));
                MarkAutomaticJumpRequested(
                    state,
                    escapePlan,
                    escapeTarget,
                    scan,
                    hazard,
                    physics,
                    planningHorizontalSpeed,
                    spiritBoostRouteContext);
                if (!automaticJumpArmed)
                    ClearRoutePlanLock();
                return;
            }

            if (confirmedTargetContact)
            {
                string contactEvidence = geometricTargetContact
                    ? "TwoFixedStepGeometricContact"
                    : "NativeRaycastContact";
                if (geometricTargetContact)
                {
                    BonusRunnerLog.Warning(
                        $"Stage2GroundedWallStallRecovered Position=" +
                        $"({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), Velocity=" +
                        $"({state.PlayerVelocity.x:F3}," +
                        $"{state.PlayerVelocity.y:F3}), PlayerBoundsX=" +
                        $"[{playerBounds.min.x:F3},{playerBounds.max.x:F3}], " +
                        $"Source=[{scan.Current.Left:F3}," +
                        $"{scan.Current.Right:F3}]@{scan.Current.Top:F3}, " +
                        $"Target=[{scan.Next.Left:F3}," +
                        $"{scan.Next.Right:F3}]@{scan.Next.Top:F3}, " +
                        $"FaceGap={geometricFaceGap:F3}, StableFixedSteps=" +
                        $"{stage2GroundedWallContactFixedSteps}, " +
                        $"Raycast={contact.IsDetected}/{contact.IsTouching}/" +
                        $"{contact.Reason}, Plan={plan.Reason}/" +
                        $"{plan.Maneuver}. FailureDomain=Perception; the " +
                        "verified adjacent-wall route now uses stable body " +
                        "bounds as contact authority instead of waiting for " +
                        "an unreachable ArmX.");
                }
                BonusRunnerLog.Debug(
                    $"StationaryWallContactExecutorArmed Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), PreContactVX=" +
                    $"{planningHorizontalSpeed:F3}, WallFaceX=" +
                    $"{(contact.IsDetected ? contact.FaceX : scan.Next.Left):F3}, " +
                    $"ContactEvidence={contactEvidence}, Target=" +
                    $"{scan.Next.MapPieceName}#" +
                    $"{scan.Next.MapPieceInstanceId}/S" +
                    $"{scan.Next.StaticSurfaceIndex}@" +
                    $"[{scan.Next.Left:F3},{scan.Next.Right:F3}]/" +
                    $"Y{scan.Next.Top:F3}, DirectEscapeAttempted=" +
                    $"{mappedGround6RaisedLip}, DirectEscapeResult=" +
                    $"{escapeRejection}. The mapped contact state, not a " +
                    $"blind downstream jump or an indefinite zero-VX wait, " +
                    $"now owns recovery.",
                    "Recovery");
                ResetStage2GroundedWallContactEvidence();
                BeginPassiveWallApproach(
                    state,
                    plan,
                    scan,
                    scan.Next,
                    hazard,
                    physics);
                return;
            }

            if (!stage2GeometricContactCandidate)
                ResetStage2GroundedWallContactEvidence();

            if (Time.unscaledTime >= nextSpeedPlanningBarrierLogTime)
            {
                nextSpeedPlanningBarrierLogTime =
                    Time.unscaledTime + 0.50f;
                BonusRunnerLog.Debug(
                    $"JumpPlanningDeferred Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), ObservedVX=" +
                    $"{observedHorizontalSpeed:F3}, ReliableVX=" +
                    $"{lastReliableHorizontalSpeed:F3}, PlanningVX=" +
                    $"{planningHorizontalSpeed:F3}, SpeedSource=" +
                    $"{planningSpeedSource}, Grounded=" +
                    $"{state.IsGrounded}, HasNext={scan.HasNext}, Plan=" +
                    $"{plan.Reason}/{plan.Maneuver}, Obstacle=" +
                    $"{stationaryObstacle.Kind}, Contact=" +
                    $"{contact.IsDetected}/{contact.IsTouching}, " +
                    $"ContactFaceX={contact.FaceX:F3}, Reason=" +
                    "LiveHorizontalVelocityZeroWithoutVerifiedEscape. " +
                    $"EscapePlanner={escapeRejection}, GeometricContact=" +
                    $"{stage2GeometricContactCandidate}/" +
                    $"{stage2GroundedWallContactFixedSteps}Steps, " +
                    $"ForwardBodyX={forwardBodyX:F3}, FaceGap=" +
                    $"{geometricFaceGap:F3}, " +
                    "No DOWN is sent without both physical face contact " +
                    "and a speed-aware verified escape target.",
                    "Physics");
            }
            return;
        }

        bool plannedWallDropApproach =
            plan.IsValid &&
            plan.Maneuver == BonusManeuverKind.EnterTrenchThenWallJump &&
            scan.HasNext;
        float renderWallOwnershipLead =
            state.UsesStage2LiveRouting &&
            state.SectionIndex == 1
                ? Mathf.Max(
                    0f,
                    planningHorizontalSpeed * Mathf.Clamp(
                        physics.FixedDeltaTime,
                        0.005f,
                        0.05f))
                : 0f;
        bool wallDropReachedArm =
            plannedWallDropApproach &&
            state.PlayerPosition.x + renderWallOwnershipLead >=
                plan.PlannedLaunchX - 0.16f;
        float reboundWallTravel = plannedWallDropApproach
            ? Mathf.Max(0f, plan.PredictedLandingX - state.PlayerPosition.x)
            : float.PositiveInfinity;
        float reboundWallTravelSeconds = plannedWallDropApproach
            ? reboundWallTravel / Mathf.Max(1f, planningHorizontalSpeed)
            : float.PositiveInfinity;
        bool spiritSection3NativeReboundHandoff =
            plannedWallDropApproach &&
            !wallDropReachedArm &&
            state.SpiritBoostEnabled &&
            state.SectionIndex == 3 &&
            state.IsGrounded &&
            state.PlayerVelocity.y >=
                SpiritSection3ReboundMinimumVelocityY &&
            scan.Gap <= 0.10f &&
            scan.HeightDelta >= 4.0f &&
            reboundWallTravelSeconds <=
                SpiritSection3ReboundMaximumWallTravelSeconds;
        if (wallDropReachedArm || spiritSection3NativeReboundHandoff)
        {
            if (spiritSection3NativeReboundHandoff)
            {
                BonusRunnerLog.Warning(
                    $"SpiritSection3NativeReboundWallOwnershipArmed Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), Source=" +
                    $"[{scan.Current.Left:F3},{scan.Current.Right:F3}]@" +
                    $"{scan.Current.Top:F3}, Target=" +
                    $"[{scan.Next.Left:F3},{scan.Next.Right:F3}]@" +
                    $"{scan.Next.Top:F3}, Gap={scan.Gap:F3}, Rise=" +
                    $"{scan.HeightDelta:F3}, NominalArmX=" +
                    $"{plan.PlannedLaunchX:F3}, ExpectedFaceContactX=" +
                    $"{plan.PredictedLandingX:F3}, RemainingTravel=" +
                    $"{reboundWallTravel:F3}, EstimatedTravelTime=" +
                    $"{reboundWallTravelSeconds:F3}s. Grounded plus positive " +
                    "VY proves a native collision rebound; the exact planned " +
                    "wall now owns the flight, but DOWN remains gated by " +
                    "physical face contact.");
            }
            BeginPassiveWallApproach(
                state,
                plan,
                scan,
                scan.Next,
                hazard,
                physics);
            return;
        }

        if (!plan.IsValid || !plan.ShouldJumpNow)
        {
            // Missing/terminal geometry is never reward evidence. A confirmed
            // typed reward target is the sole authorization for the shared
            // jump/attack action; a route that is waiting or an
            // IntentionalDrop remains input-free here.
            return;
        }

        BonusRunnerLog.Debug(
            $"FixedStepRouteScheduled X={state.PlayerPosition.x:F3}, Hold={plan.HoldSeconds:F3}s, " +
            $"EffectiveHold={Mathf.Min(plan.HoldSeconds, physics.EffectiveHoldCapSeconds):F3}s, " +
            $"CandidateRange=[0.020,0.180]s, NativeCap={physics.EffectiveHoldCapSeconds:F3}s, " +
            $"Flight={plan.PredictedFlightSeconds:F3}s, Travel={plan.HorizontalTravel:F3}, " +
            $"Window=[{plan.LaunchWindowLeft:F3},{plan.LaunchWindowRight:F3}], " +
            $"TargetSafe=[{plannedTarget.SafeLeft:F3},{plannedTarget.SafeRight:F3}]@{plannedTarget.Top:F3}, " +
            $"PredictedLandingX={plan.PredictedLandingX:F3}, " +
            $"Maneuver={plan.Maneuver}, Reason={plan.Reason}, " +
            $"Physics[{physics.Summary}]. No render-frame DOWN is sent; " +
            "the next PlayerMovement.FixedUpdate must rebuild and commit " +
            "this route from its own live snapshot.",
            "Routing");
    }

    private bool UpdateStage2GroundedWallContactEvidence(
        bool candidate,
        BonusBoardSegment target)
    {
        if (!candidate ||
            target.Right <= target.Left + 0.05f)
        {
            ResetStage2GroundedWallContactEvidence();
            return false;
        }

        bool sameTarget =
            !float.IsNaN(stage2GroundedWallContactTargetLeft) &&
            !float.IsNaN(stage2GroundedWallContactTargetTop) &&
            Mathf.Abs(
                stage2GroundedWallContactTargetLeft -
                target.Left) <= 0.10f &&
            Mathf.Abs(
                stage2GroundedWallContactTargetTop -
                target.Top) <= 0.35f;
        if (!sameTarget)
        {
            ResetStage2GroundedWallContactEvidence();
            stage2GroundedWallContactTargetLeft = target.Left;
            stage2GroundedWallContactTargetTop = target.Top;
        }

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (fixedStep != stage2GroundedWallContactLastFixedStep)
        {
            stage2GroundedWallContactLastFixedStep = fixedStep;
            stage2GroundedWallContactFixedSteps++;
        }

        return stage2GroundedWallContactFixedSteps >= 2;
    }

    private void ResetStage2GroundedWallContactEvidence()
    {
        stage2GroundedWallContactLastFixedStep = -1;
        stage2GroundedWallContactFixedSteps = 0;
        stage2GroundedWallContactTargetLeft = float.NaN;
        stage2GroundedWallContactTargetTop = float.NaN;
    }

    [HideFromIl2Cpp]
    private bool TryArmSharedGroundedWallStallFixedStep(
        PlayerMovement player)
    {
        BonusStageState state = GetRoutingState(latestState);
        bool terrainControlActive =
            state.IsActiveGameplay ||
            IsSuccessfulCompletionTraversal(state);
        bool wallOwnerActive =
            passiveWallApproachActive ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            ground5HighestPillarSinkActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            wallActionPhase != WallActionPhase.None;
        bool manualLearningOwner =
            learningSampleActive &&
            !string.Equals(
                learningSource,
                "Automatic",
                System.StringComparison.Ordinal);
        if (!automationEnabled ||
            !AutomaticJumpingEnabled ||
            player == null ||
            !terrainControlActive ||
            !state.IsBonusStage ||
            !state.IsSupportedBonusMap ||
            BonusStageRetryBridge.BlocksTerrainControl ||
            terrainContinuationEpochBlocked ||
            pitDescentGuardActive ||
            rewardTargetDetector.IsLatched ||
            Time.unscaledTime - lastManualInputTime < 0.40f ||
            wallOwnerActive ||
            manualLearningOwner ||
            jumpController.IsHoldingJump)
        {
            ResetSharedGroundedWallStallEvidence();
            return false;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        Vector2 velocity = body != null
            ? body.velocity
            : state.PlayerVelocity;
        bool groundedStallCandidate =
            player.IsGrounded() &&
            Mathf.Abs(velocity.x) <= 0.50f &&
            Mathf.Abs(velocity.y) <= 2.50f;
        if (!groundedStallCandidate)
        {
            ResetSharedGroundedWallStallEvidence();
            return false;
        }

        float expectedSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                lastReliableHorizontalSpeed,
                sectionCruiseHorizontalSpeed));
        BonusWallContact wall = wallDetector.Detect(
            player,
            expectedSpeed);
        if (!wall.IsDetected || !wall.IsTouching)
        {
            ResetSharedGroundedWallStallEvidence();
            return false;
        }

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        bool sameFace =
            !float.IsNaN(sharedGroundedWallStallFaceX) &&
            Mathf.Abs(
                sharedGroundedWallStallFaceX -
                wall.FaceX) <= 0.20f;
        Vector3 observedPosition = player.transform.position;
        bool samePosition =
            sameFace &&
            Mathf.Abs(
                observedPosition.x -
                sharedGroundedWallStallStartPosition.x) <=
                SharedGroundedWallStallMaximumHorizontalDrift &&
            Mathf.Abs(
                observedPosition.y -
                sharedGroundedWallStallStartPosition.y) <=
                SharedGroundedWallStallMaximumVerticalDrift;
        if (!sameFace || !samePosition)
        {
            ResetSharedGroundedWallStallEvidence();
            sharedGroundedWallStallFaceX = wall.FaceX;
            sharedGroundedWallStallStartPosition =
                observedPosition;
        }
        if (fixedStep != sharedGroundedWallStallLastFixedStep)
        {
            sharedGroundedWallStallLastFixedStep = fixedStep;
            sharedGroundedWallStallFixedSteps++;
        }
        if (sharedGroundedWallStallFixedSteps <
            SharedGroundedWallStallConfirmationFixedSteps)
        {
            return false;
        }

        // Wall-top resolution performs vertical all-layer probes and boundary
        // refinement. Accumulate the cheap same-face physics proof first, then
        // pay that cost once for a confirmed stall instead of on every frame
        // of ordinary contact.
        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(
                0.15f,
                player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : player.transform.position.y - 0.27f;
        if (!TryResolveObservedWallSurface(
                wall,
                feetY,
                playerHalfWidth,
                out BonusBoardSegment target,
                out string targetAuthority) ||
            target.Top <= feetY + 0.20f)
        {
            ResetSharedGroundedWallStallEvidence();
            return false;
        }

        Vector3 currentPosition = player.transform.position;
        int confirmedSteps = sharedGroundedWallStallFixedSteps;
        Vector3 stallStart = sharedGroundedWallStallStartPosition;
        ResetSharedGroundedWallStallEvidence();
        if (!TryRebaseUnexpectedWallContact(
                state,
                player,
                wall,
                playerHalfWidth))
        {
            return false;
        }

        nextWallRecoveryTime = 0f;
        bool pulseIssued = TryWallRecoveryJump(state, player);
        BonusRunnerLog.Warning(
            $"MovementStallDetected Position=({currentPosition.x:F3}," +
            $"{currentPosition.y:F3}), Start=({stallStart.x:F3}," +
            $"{stallStart.y:F3}), Velocity=({velocity.x:F3}," +
            $"{velocity.y:F3}), StableFixedSteps={confirmedSteps}, " +
            $"Map={state.MapName}, Section={state.SectionIndex}, " +
            $"ActiveGameplay={state.IsActiveGameplay}, " +
            $"CompletionTraversal=" +
            $"{IsSuccessfulCompletionTraversal(state)}, FaceX=" +
            $"{wall.FaceX:F3}, Wall={wall.Reason}, Target=" +
            $"[{target.Left:F3},{target.Right:F3}]@{target.Top:F3}, " +
            $"TargetAuthority={targetAuthority}, PulseIssued=" +
            $"{pulseIssued}. FailureDomain=ControlOwnership; a shared " +
            "physics-confirmed wall route replaces the unavailable or stale " +
            "terrain plan. This recovery is map-independent but cannot run " +
            "during ordinary forward motion or after a reward target latch.");
        return true;
    }

    private void ResetSharedGroundedWallStallEvidence()
    {
        sharedGroundedWallStallLastFixedStep = -1;
        sharedGroundedWallStallFixedSteps = 0;
        sharedGroundedWallStallFaceX = float.NaN;
        sharedGroundedWallStallStartPosition = default;
    }

    private static int GetGroundPlanFixedStepHoldLimit(
        BonusStageState state,
        BonusJumpPlan plan,
        JumpPhysicsSnapshot physics)
    {
        if (!plan.IsValid ||
            plan.HoldSeconds <= 0f)
        {
            return 0;
        }

        float fixedDelta = Mathf.Clamp(
            physics.FixedDeltaTime,
            0.005f,
            0.05f);
        int fixedSteps = Mathf.Clamp(
            Mathf.RoundToInt(plan.HoldSeconds / fixedDelta),
            1,
            64);
        float deliveredHold = fixedSteps * fixedDelta;
        // Planner candidates are quantized to native ticks. Keep this small
        // tolerance as a defensive contract check; never silently fall back
        // to render-time release for a ground route.
        return Mathf.Abs(deliveredHold - plan.HoldSeconds) <=
               Mathf.Max(0.001f, fixedDelta * 0.06f)
            ? fixedSteps
            : Mathf.Clamp(
                Mathf.CeilToInt(plan.HoldSeconds / fixedDelta - 0.0001f),
                1,
                64);
    }

    // The game freezes the stage timer once a section's spheres are collected,
    // so nothing ends the stage during this crossing. A hero wedged under an
    // overhang gains almost no height per wall jump and is only released by
    // falling, which the wall recovery prevents by pressing again immediately.
    private bool UpdateCompletionStallGuard(
        BonusStageState state,
        bool completionTraversal)
    {
        if (!completionTraversal || !state.HasPlayer)
        {
            EndCompletionStallEpisode("the crossing ended", state);
            completionStallTracking = false;
            completionStallPauseUntil = 0f;
            return false;
        }

        float now = Time.unscaledTime;
        float x = state.PlayerPosition.x;
        if (!completionStallTracking || Time.timeScale <= 0f)
        {
            completionStallTracking = true;
            completionStallX = x;
            completionStallSince = now;
            return false;
        }

        float moved = Mathf.Abs(x - completionStallX);
        if (moved >= CompletionStallFreedDistance)
        {
            EndCompletionStallEpisode("the hero moved forward", state);
            completionStallX = x;
            completionStallSince = now;
            completionStallPauseUntil = 0f;
            return false;
        }

        if (now < completionStallPauseUntil)
        {
            jumpController.Release();
            return true;
        }

        if (moved > CompletionStallPositionTolerance)
        {
            completionStallX = x;
            completionStallSince = now;
            return false;
        }

        if (now - completionStallSince < CompletionStallSeconds)
            return false;

        if (completionStallPauses == 0)
            completionStallEpisodeStart = completionStallSince;
        float pauseSeconds = Mathf.Min(
            Mathf.Pow(2f, completionStallPauses),
            CompletionStallMaximumPauseSeconds);
        completionStallPauses++;
        completionStallPauseUntil = now + pauseSeconds;
        completionStallSince = completionStallPauseUntil;
        jumpController.Release();
        if (learningSampleActive && learningSource == "Automatic")
            FinishLearningSample(state, "CompletionStallGuard");
        ResetAutomaticControlState();
        BonusRunnerLog.User(
            $"Completion stall guard: hero stuck, jumps paused for " +
            $"{pauseSeconds:0.#}s. Pause={completionStallPauses}, Map=" +
            $"{state.MapName}, Section={state.SectionIndex}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"SpiritBoost={state.SpiritBoostEnabled}.");
        return true;
    }

    private void EndCompletionStallEpisode(
        string reason,
        BonusStageState state)
    {
        if (completionStallPauses == 0)
            return;
        BonusRunnerLog.User(
            $"Completion stall guard: released, {reason}, after " +
            $"{completionStallPauses} pause(s) and " +
            $"{Time.unscaledTime - completionStallEpisodeStart:F1}s. Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}).");
        completionStallPauses = 0;
    }

    private bool IsSuccessfulCompletionTraversal(
        BonusStageState state)
    {
        return state.IsBonusStage &&
            state.IsSupportedBonusMap &&
            state.HasPlayer &&
            bonusGameplayStarted &&
            lastActiveTerrainSection >= 0 &&
            !terrainContinuationEpochBlocked &&
            !state.IsActiveGameplay &&
            !rewardTargetDetector.IsLatched;
    }

    private bool IsNativeRewardActionPhase(BonusStageState state) =>
        state.IsBonusStage &&
        state.IsSupportedBonusMap &&
        state.HasPlayer &&
        rewardTargetDetector.IsLatched;

    private void UpdateRunTracking(BonusStageState state)
    {
        if (!runTrackingActive)
        {
            if (!state.IsSupportedBonusMap || !state.IsActiveGameplay)
                return;

            runTrackingActive = true;
            runStartedAtRealtime = Time.realtimeSinceStartup;
            runDeaths = 0;
            runHighestSection = Mathf.Clamp(state.SectionIndex, 0, 4);
            runSpiritBoostObserved = state.SpiritBoostEnabled;
            runFinalCompletionReached = false;
            BonusRunnerLog.Debug(
                $"Run tracking started: Map={state.MapName}, " +
                $"Section={state.SectionIndex}.",
                "Statistics");
        }

        runHighestSection = Mathf.Max(
            runHighestSection,
            Mathf.Clamp(state.SectionIndex, 0, 4));
        runSpiritBoostObserved |= state.SpiritBoostEnabled;
        if (state.SectionIndex >= 4 &&
            state.HasSphereProgress &&
            state.CollectedSpheres >=
                (int)Math.Ceiling(state.RequiredSpheres))
        {
            runFinalCompletionReached = true;
        }
    }

    private void RecordRunDeath(BonusStageState state)
    {
        if (!runTrackingActive)
            return;

        runDeaths++;
        BonusRunnerLog.Debug(
            $"Run death recorded: Death={runDeaths}, " +
            $"Attempt={runDeaths + 1}, Section={state.SectionIndex}, " +
            $"Position=({state.PlayerPosition.x:F2}," +
            $"{state.PlayerPosition.y:F2}).",
            "Statistics");
    }

    private void FinishRunTracking()
    {
        if (!runTrackingActive)
            return;

        float duration = Mathf.Max(
            0f,
            Time.realtimeSinceStartup - runStartedAtRealtime);
        bool success = runFinalCompletionReached;
        sessionRunCount++;
        sessionDeathCount += runDeaths;
        if (success)
        {
            sessionSuccessCount++;
            sessionSuccessfulDurationTotal += duration;
            sessionBestSuccessfulDuration = Mathf.Min(
                sessionBestSuccessfulDuration,
                duration);
            if (runDeaths == 0)
                sessionDeathlessSuccessCount++;
        }
        else
        {
            sessionFailureCount++;
        }

        float passRate = sessionRunCount > 0
            ? sessionSuccessCount * 100f / sessionRunCount
            : 0f;
        string averageSuccessTime = sessionSuccessCount > 0
            ? $"{sessionSuccessfulDurationTotal / sessionSuccessCount:F2}s"
            : "N/A";
        string bestTime = sessionSuccessCount > 0
            ? $"{sessionBestSuccessfulDuration:F2}s"
            : "N/A";
        string endReason = success
            ? "CompletedAllSections"
            : "StageExitedBeforeCompletion";

        BonusRunnerLog.User(
            $"Run count: Total={sessionRunCount}, " +
            $"Success={sessionSuccessCount}, Failure={sessionFailureCount}, " +
            $"PassRate={passRate:F1}%, " +
            $"Deathless={sessionDeathlessSuccessCount}, " +
            $"SessionDeaths={sessionDeathCount}, " +
            $"LastResult={(success ? "Success" : "Failure")}, " +
            $"Duration={duration:F2}s, " +
            $"AverageSuccessTime={averageSuccessTime}, " +
            $"BestTime={bestTime}, Deaths={runDeaths}, " +
            $"Attempts={runDeaths + 1}, " +
            $"SectionsCleared={runHighestSection}/4, " +
            $"SpiritBoost={runSpiritBoostObserved}, " +
            $"EndReason={endReason}.");

        runTrackingActive = false;
        runStartedAtRealtime = 0f;
        runDeaths = 0;
        runHighestSection = 0;
        runSpiritBoostObserved = false;
        runFinalCompletionReached = false;
    }

    private bool IsCompletionDashControlIdle() =>
        automaticJumpArmed &&
        !jumpController.IsHoldingJump &&
        !automaticPredictionActive &&
        !(learningSampleActive && learningSource == "Automatic") &&
        !passiveWallApproachActive &&
        !wallExitContactWatchActive &&
        !attachedObjectiveDescentActive &&
        !wallMandatoryFaceSetupActive &&
        !wallMandatoryFaceInterceptCommitted &&
        wallActionPhase == WallActionPhase.None;

    private bool TryCompletionRewardActions(
        BonusStageState state,
        PlayerMovement player)
    {
        if (!AutomaticJumpingEnabled ||
            !state.HasPlayer ||
            player == null ||
            !state.IsSupportedBonusMap)
        {
            return false;
        }

        if (!IsNativeRewardActionPhase(state))
            return false;

        return completionRewardController.TryRewardActions(
            state,
            player,
            jumpController,
            CompletionRewardActionsEnabled);
    }

    private void ObserveNoSupportStall(
        BonusStageState state,
        BonusBoardScanResult scan)
    {
        bool invalidSupport = !scan.IsValid;
        bool terminalRouteEnd =
            scan.IsValid && !scan.HasNext;
        if ((!invalidSupport && !terminalRouteEnd) ||
            !state.IsGrounded ||
            Mathf.Abs(state.PlayerVelocity.x) > 0.50f ||
            !state.IsActiveGameplay)
        {
            noSupportStallStartedAt = -1f;
            return;
        }

        if (noSupportStallStartedAt < 0f)
        {
            noSupportStallStartedAt = Time.unscaledTime;
            nextNoSupportStallLogTime = Time.unscaledTime + 0.40f;
            return;
        }

        float stalledFor = Time.unscaledTime - noSupportStallStartedAt;
        if (stalledFor < 0.40f ||
            Time.unscaledTime < nextNoSupportStallLogTime)
        {
            return;
        }

        nextNoSupportStallLogTime = Time.unscaledTime + 1.0f;
        BonusMapPieceRegistry registry = platformScanner.MapRegistry;
        Vector2[] sectionActive = BonusStageInspector.GetActiveSpherePositions(
            -100000f,
            100000f,
            500);
        int activeBehind = sectionActive.Count(
            sphere => sphere.x < state.PlayerPosition.x - 1.0f);
        int activeAhead = sectionActive.Length - activeBehind;
        string classification =
            state.HasSphereProgress && state.RemainingRequiredSpheres > 0
                ? "TerminalOrRouteEndWithSphereDeficit"
                : "PerceptionOrTerminalWait";
        string failureDomain = registry.State !=
            BonusMapPieceRegistryState.Ready
                ? "Perception"
                : state.HasSphereProgress &&
                  state.RemainingRequiredSpheres > activeAhead
                    ? "Route:QuotaDeficit"
                    : invalidSupport
                        ? "PerceptionOrTerminal"
                        : "TerminalWait";
        BonusRunnerLog.Warning(
            $"NoSupportStall Classification={classification}, " +
            $"Duration={stalledFor:F3}s, Section={state.SectionIndex + 1}, " +
            $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"Scan={scan.Reason}, StaticMap={registry.State}/" +
            $"{registry.StatusReason}/G{registry.Generation}, " +
            $"SphereProgress={(state.HasSphereProgress ? $"{state.CollectedSpheres}/{Math.Ceiling(state.RequiredSpheres):F0}" : "Unavailable")}, " +
            $"RequiredRaw={(state.HasSphereProgress ? state.RequiredSpheres.ToString("F3") : "Unavailable")}, " +
            $"RemainingRequired={state.RemainingRequiredSpheres}, " +
            $"ActiveSpheres[Total={sectionActive.Length}," +
            $"Behind={activeBehind},Ahead={activeAhead}], " +
            $"Timer={state.CurrentTime:F3}/{state.MaximumTime:F3}. " +
            $"FailureDomain={failureDomain}, Action=None, " +
            "SafetyPolicy=DoNotBlindJump. " +
            "The input is released while the route is reacquired or the " +
            "game resolves the terminal state.");
    }

    private void MonitorCommittedJump(
        BonusStageState state,
        PlayerMovement player)
    {
        float elapsed = Mathf.Max(
            0f,
            Time.unscaledTime - jumpController.LastPressStartedAt);

        // During the first half of a wall route, touching the intended face is
        // more authoritative than the precomputed hold deadline. Release this
        // press immediately so the next frame can issue the independent climb
        // press with a newly calculated duration.
        if (automaticManeuver ==
            BonusManeuverKind.ApproachJumpThenWallJump)
        {
            BonusWallContact wall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            bool intendedWall = wall.IsDetected &&
                wall.IsTouching &&
                wall.Point.x >= automaticTargetLeft - 0.80f &&
                wall.Point.x <= automaticTargetRight + 0.80f;
            if (intendedWall)
            {
                BonusRunnerLog.Debug(
                    $"FramePlan WallContactEarlyRelease Frame={Time.frameCount}, " +
                    $"Elapsed={elapsed:F3}s, Position=" +
                    $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                    $"WallPoint=({wall.Point.x:F3},{wall.Point.y:F3}), " +
                    $"Collider={wall.ColliderInstanceId}:{wall.ColliderName}.",
                    "FramePlan");
                jumpController.Release();
                wallReleaseObservedFixedStep =
                    JumpPhysicsFeedback.FixedStepSequence;
                wallActionPhase = WallActionPhase.AwaitingWallContact;
                learningInputUpTime = Time.unscaledTime;
                learningInputReleased = true;
            }
            return;
        }

        // Mandatory Ground 3 face interception is solved against the exact
        // fixed-step hold delivered by JumpController. Horizontal resume is
        // useful release-flight evidence, but it must not shorten that solved
        // press; doing so changes the trajectory the face solver validated.
        if (automaticManeuver == BonusManeuverKind.WallJumpClimb &&
            wallMandatoryFaceInterceptCommitted &&
            wallExitFaceContactRequired &&
            wallExitTargetActive)
        {
            float currentFeetY = player.playerCollider != null
                ? player.playerCollider.bounds.min.y
                : state.PlayerPosition.y - 0.27f;
            float currentHalfWidth = player.playerCollider != null
                ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
                : 0.60f;
            BonusWallContact releaseWall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            float movedFromContact =
                state.PlayerPosition.x - wallRecoveryContactX;
            bool horizontalResumed =
                state.PlayerVelocity.x >=
                    MandatoryWallFaceHorizontalResumeVelocity &&
                movedFromContact >= 0.030f &&
                currentFeetY >= wallRecoveryPhysicalLipY - 0.15f;
            // A single composite-ray miss is not detachment evidence. The
            // old fallback released valid pulses below the lip while VX was
            // still zero. The fixed-step actuator is the safe ceiling when a
            // render frame does not observe horizontal resume in time.
            if (horizontalResumed && jumpController.IsHoldingJump)
            {
                BonusRunnerLog.Debug(
                    $"MandatoryFaceInterceptHoldPreserved Trigger=" +
                    $"HorizontalResume, Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY={currentFeetY:F3}, " +
                    $"Velocity=({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), MovedFromContact=" +
                    $"{movedFromContact:F3}, Elapsed={elapsed:F3}, " +
                    $"PlannedHold={automaticPlannedHold:F3}. " +
                    "Action=keep DOWN until the fixed-step deadline; the " +
                    "validated face trajectory, not lip observation, owns " +
                    "release timing.",
                    "Recovery");
                return;
            }
            if (horizontalResumed)
            {
                float observedResumeSpeed = Mathf.Clamp(
                    Mathf.Abs(state.PlayerVelocity.x),
                    MandatoryWallFaceHorizontalResumeVelocity,
                    80f);
                float targetContactX = float.IsNaN(
                    wallMandatoryFaceTargetContactX)
                    ? wallExitTarget.Left - currentHalfWidth
                    : wallMandatoryFaceTargetContactX;
                JumpPhysicsSnapshot facePhysics =
                    BuildMandatoryFacePlanningPhysics(
                        jumpPhysicsFeedback.CaptureSnapshot(player));
                bool hasReleasePrediction =
                    jumpPlanner.TryPredictReleasedWallFaceContact(
                        state.PlayerPosition.x,
                        currentFeetY,
                        state.PlayerVelocity.y,
                        targetContactX,
                        observedResumeSpeed,
                        facePhysics,
                        out int predictionSteps,
                        out float predictionSeconds,
                        out float predictionFeetY,
                        out float predictionVelocityY);
                wallMandatoryFaceReleaseFeetY = currentFeetY;
                wallMandatoryFaceReleaseVelocityY = state.PlayerVelocity.y;
                wallMandatoryFaceReleasePredictedContactFeetY =
                    hasReleasePrediction ? predictionFeetY : float.NaN;
                wallMandatoryFaceReleasePredictedContactVelocityY =
                    hasReleasePrediction ? predictionVelocityY : float.NaN;
                float releaseTravel = Mathf.Max(
                    0f,
                    targetContactX - state.PlayerPosition.x);
                SpiritBoostRouteContext releaseSpiritContext =
                    CaptureRouteSpeedContext(
                        state,
                        player,
                        state.PlayerPosition.x - 1.0f,
                        targetContactX + 2.0f,
                        sectionCruiseHorizontalSpeed > 1f
                            ? sectionCruiseHorizontalSpeed
                            : 0f);
                // Horizontal resume is measured after the wall has released
                // the Rigidbody. It supersedes the entry/contact speed for
                // every remaining field of this same attempt.
                wallRouteHorizontalSpeed = observedResumeSpeed;
                wallRouteSpeedLatched = true;
                automaticTriggerSpeed = observedResumeSpeed;
                automaticPredictedHorizontalTravel = releaseTravel;
                automaticMinimumPredictedHorizontalTravel = releaseTravel;
                automaticMaximumPredictedHorizontalTravel = releaseTravel;
                automaticPredictedLandingX = targetContactX;
                automaticPredictedFlightSeconds = hasReleasePrediction
                    ? predictionSeconds
                    : automaticPredictedFlightSeconds;
                automaticFutureSpeedTransitionExpected =
                    releaseSpiritContext.RequiresSpeedEnvelope;
                automaticSpiritBoostRouteEvidence =
                    $"WallHorizontalResumeVX={observedResumeSpeed:F3};" +
                    releaseSpiritContext.Summary;
                BonusRunnerLog.Debug(
                    $"MandatoryFaceInterceptPlannedRelease Trigger=" +
                    $"HoldDeadlineAfterHorizontalResume, " +
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY={currentFeetY:F3}, " +
                    $"Velocity=({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), " +
                    $"ResumeVX={observedResumeSpeed:F3}, " +
                    $"MovedFromContact={movedFromContact:F3}, " +
                    $"WallTouching={releaseWall.IsTouching}, " +
                    $"TargetContactX={targetContactX:F3}, " +
                    $"ReleasePrediction=" +
                    $"{(hasReleasePrediction ? $"Steps={predictionSteps},T={predictionSeconds:F3},FeetY={predictionFeetY:F3},VY={predictionVelocityY:F3}" : "Unavailable")}, " +
                    $"PlanPrediction[FeetY=" +
                    $"{wallMandatoryFacePredictedContactFeetY:F3},VY=" +
                    $"{wallMandatoryFacePredictedContactVelocityY:F3},T=" +
                    $"{wallMandatoryFacePredictedContactSeconds:F3}], " +
                    $"SpiritResume[{releaseSpiritContext.Summary}]. " +
                    "Action=confirm UP after the planned deadline and lock " +
                    "all further DOWN until physical " +
                    "contact with the mapped S3 face.",
                    "Recovery");
                jumpController.Release();
                learningInputUpTime = Time.unscaledTime;
                learningInputReleased = true;
                wallRecoveryLipCrossed = true;
                wallRecoveryContactLatched = false;
                wallRecoverySawUpwardMotion = false;
                wallReleaseObservedFixedStep =
                    JumpPhysicsFeedback.FixedStepSequence;
                wallActionPhase = WallActionPhase.ExitFlight;
                ResetWallResidualRiseWait();
                bool watchArmed = TryArmWallExitContactWatch(
                    state,
                    "MandatoryFaceHorizontalResume");
                if (!watchArmed)
                {
                    BonusRunnerLog.Warning(
                        $"MandatoryFaceInterceptWatchArmFailed Position=" +
                        $"({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), Target=" +
                        $"[{wallExitTarget.Left:F3}," +
                        $"{wallExitTarget.Right:F3}]@" +
                        $"{wallExitTarget.Top:F3}. No additional DOWN will " +
                        "be sent for the stale face-intercept plan. " +
                        "FailureDomain=RouteState; the commitment is reset " +
                        "atomically.");
                    automaticTrajectoryCompatible = false;
                    if (learningSampleActive)
                    {
                        FinishLearningSample(
                            state,
                            "MandatoryFaceWatchArmFailed");
                    }
                    ResetAutomaticControlState();
                    nextAutomaticAttemptTime =
                        Time.unscaledTime + 0.20f;
                }
                return;
            }
        }

        // Input duration is now committed once DOWN is sent. Render-frame
        // velocity samples can briefly report VX=0 and vertical position only
        // changes on physics frames; using either to shorten the active press
        // caused severe oscillation and 0.02s accidental jumps. Continue to
        // rescan the target for diagnostics, but apply corrections to the next
        // action rather than destabilising the current one.
        BonusBoardSegment expectedTarget = new(
            automaticTargetLeft,
            automaticTargetRight,
            automaticTargetTop,
            automaticTargetSafeLeft,
            automaticTargetSafeRight,
            automaticTargetColliderId,
            automaticTargetColliderName,
            automaticTargetMapPieceName,
            automaticTargetMapPieceOriginX,
            automaticTargetMapPieceInstanceId,
            automaticTargetRegistryGeneration,
            automaticTargetStaticSurfaceIndex);
        bool refreshed = platformScanner.TryRefreshExpectedSurface(
            expectedTarget,
            automaticPredictedLandingX,
            player,
            out BonusBoardSegment liveTarget,
            out string refreshReason);
        if (!refreshed)
            liveTarget = expectedTarget;
        else
        {
            bool expectedHasMappedIdentity =
                expectedTarget.MapPieceInstanceId != 0 &&
                expectedTarget.StaticSurfaceIndex >= 0;
            bool liveHasMappedIdentity =
                liveTarget.MapPieceInstanceId != 0 &&
                liveTarget.StaticSurfaceIndex >= 0;
            bool contradictoryMappedIdentity =
                expectedHasMappedIdentity &&
                liveHasMappedIdentity &&
                (liveTarget.MapPieceInstanceId !=
                    expectedTarget.MapPieceInstanceId ||
                 liveTarget.StaticSurfaceIndex !=
                    expectedTarget.StaticSurfaceIndex);
            if (contradictoryMappedIdentity &&
                wallExitFaceContactRequired)
            {
                FailMandatoryFacePlan(
                    state,
                    "CommittedCurrentSurfaceIdentityChanged",
                    $"Expected={expectedTarget.MapPieceName}#" +
                    $"{expectedTarget.MapPieceInstanceId}/S" +
                    $"{expectedTarget.StaticSurfaceIndex}/G" +
                    $"{expectedTarget.RegistryGeneration}, Live=" +
                    $"{liveTarget.MapPieceName}#" +
                    $"{liveTarget.MapPieceInstanceId}/S" +
                    $"{liveTarget.StaticSurfaceIndex}/G" +
                    $"{liveTarget.RegistryGeneration}, Refresh=" +
                    refreshReason);
                return;
            }
            if (contradictoryMappedIdentity)
            {
                refreshed = false;
                liveTarget = expectedTarget;
            }

            automaticTargetLeft = liveTarget.Left;
            automaticTargetRight = liveTarget.Right;
            automaticTargetTop = liveTarget.Top;
            automaticTargetSafeLeft = liveTarget.SafeLeft;
            automaticTargetSafeRight = liveTarget.SafeRight;
            if (liveHasMappedIdentity)
            {
                automaticTargetMapPieceName = liveTarget.MapPieceName;
                automaticTargetMapPieceOriginX = liveTarget.MapPieceOriginX;
                automaticTargetMapPieceInstanceId =
                    liveTarget.MapPieceInstanceId;
                automaticTargetRegistryGeneration =
                    liveTarget.RegistryGeneration;
                automaticTargetStaticSurfaceIndex =
                    liveTarget.StaticSurfaceIndex;
            }
            else
            {
                // A composite-collider probe can verify live bounds while its
                // static annotation is temporarily Unknown#0/S-1. Preserve
                // the already verified Ground3 identity; otherwise the next
                // frame silently drops the dedicated solver and re-enters the
                // old generic DOWN path.
                automaticTargetMapPieceName = expectedTarget.MapPieceName;
                automaticTargetMapPieceOriginX =
                    expectedTarget.MapPieceOriginX;
                automaticTargetMapPieceInstanceId =
                    expectedTarget.MapPieceInstanceId;
                automaticTargetRegistryGeneration =
                    expectedTarget.RegistryGeneration;
                automaticTargetStaticSurfaceIndex =
                    expectedTarget.StaticSurfaceIndex;
                liveTarget = liveTarget with
                {
                    MapPieceName = expectedTarget.MapPieceName,
                    MapPieceOriginX = expectedTarget.MapPieceOriginX,
                    MapPieceInstanceId = expectedTarget.MapPieceInstanceId,
                    RegistryGeneration = expectedTarget.RegistryGeneration,
                    StaticSurfaceIndex = expectedTarget.StaticSurfaceIndex
                };
            }
        }
        if (Time.unscaledTime >= nextDynamicPlanLogTime)
        {
            nextDynamicPlanLogTime = Time.unscaledTime + 0.08f;
            BonusRunnerLog.Debug(
                $"CommittedJumpFrame Frame={Time.frameCount}, " +
                $"AttemptId={automaticAttemptId}, " +
                $"Elapsed={elapsed:F3}s, Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=" +
                $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                $"Target=[{liveTarget.SafeLeft:F3},{liveTarget.SafeRight:F3}]" +
                $"@{liveTarget.Top:F3}, Refreshed={refreshed}, " +
                $"Refresh={refreshReason}, CommittedHold=" +
                $"{automaticPlannedHold:F3}s, " +
                $"RemainingHold={Mathf.Max(0f, automaticPlannedHold - elapsed):F3}s. " +
                "Policy=NeverShortenAfterInputDown",
                "FramePlan");
        }
    }

    [HideFromIl2Cpp]
    private BonusJumpPlan GetStableRoutePlan(
        BonusStageState state,
        BonusBoardScanResult scan,
        Vector2 planningVelocity,
        JumpPhysicsSnapshot physics,
        BonusHazard hazard,
        IReadOnlyList<Vector2> sphereObjectives,
        SpiritBoostRouteContext spiritBoost,
        BonusJumpPlan selectorPlan,
        bool selectorPlanAvailable)
    {
        bool unifiedLiveSoulPlan =
            sphereObjectives != null &&
            sphereObjectives.Count > 0;
        BonusJumpPlan terrainPlan = selectorPlanAvailable
            ? selectorPlan
            : jumpPlanner.Plan(
                scan,
                state.PlayerPosition,
                planningVelocity,
                physics,
                hazard,
                unifiedLiveSoulPlan
                    ? sphereObjectives
                    : Array.Empty<Vector2>(),
                sectionIndex: state.SectionIndex,
                preferSphereCoverage: unifiedLiveSoulPlan,
                allowRecoverableLowerFaceCatch:
                    !state.UsesStage3AuthoredRouting &&
                    state.SectionIndex >= 2,
                useFixedStepAlignedHolds:
                    !state.UsesStage3AuthoredRouting &&
                    state.SectionIndex >= 2,
                spiritBoost: spiritBoost,
                useStage2LiveTopologyProfile:
                    state.UsesStage2LiveRouting);
        BonusJumpPlan sphereAwarePlan =
            unifiedLiveSoulPlan
                ? terrainPlan
                : sphereObjectives != null && sphereObjectives.Count > 0
                ? jumpPlanner.Plan(
                    scan,
                    state.PlayerPosition,
                    planningVelocity,
                    physics,
                    hazard,
                    sphereObjectives,
                    sectionIndex: state.SectionIndex,
                    preferSphereCoverage:
                        !state.UsesStage3AuthoredRouting,
                    allowRecoverableLowerFaceCatch:
                        !state.UsesStage3AuthoredRouting &&
                        state.SectionIndex >= 2,
                    useFixedStepAlignedHolds:
                        !state.UsesStage3AuthoredRouting &&
                        state.SectionIndex >= 2,
                    spiritBoost: spiritBoost,
                    useStage2LiveTopologyProfile:
                        state.UsesStage2LiveRouting)
                : terrainPlan;

        // Terrain geometry owns every ordinary crossing decision. Sphere data
        // may create either a deadline-safe same-surface opportunity or a
        // sphere-scored replacement for an already verified natural drop.
        // Both retain an explicit safe landing contract.
        bool sphereControlledRoute =
            !state.SpiritBoostEnabled &&
            (!scan.HasIntermediate ||
             TerrainCommandMatches(terrainPlan, sphereAwarePlan)) &&
            (sphereAwarePlan.Maneuver ==
                 BonusManeuverKind.SphereCollectionJump ||
             sphereAwarePlan.Maneuver ==
                 BonusManeuverKind.SphereSweepToLowerLanding ||
             !state.UsesStage3AuthoredRouting &&
             sphereAwarePlan.IsValid &&
             sphereAwarePlan.Maneuver ==
                 BonusManeuverKind.GroundJumpToLanding &&
             sphereAwarePlan.ExpectedSphereHits > 0);
        BonusJumpPlan plan =
            sphereControlledRoute
                ? sphereAwarePlan
                : terrainPlan;
        if (!sphereControlledRoute &&
            !state.SpiritBoostEnabled &&
            scan.HasIntermediate &&
            sphereAwarePlan.IsValid &&
            !TerrainCommandMatches(terrainPlan, sphereAwarePlan))
        {
            plan = plan with
            {
                CandidateSummary = plan.CandidateSummary +
                    " | SphereCommandRejected[The selected downstream " +
                    "route was proved with the terrain command; a " +
                    "different pickup-scored hold cannot inherit that " +
                    "intermediate-surface proof.]"
            };
        }
        if (!sphereControlledRoute &&
            sphereAwarePlan.ExpectedSphereHits > 0 &&
            TerrainCommandMatches(plan, sphereAwarePlan))
        {
            plan = plan with
            {
                ExpectedSphereHits = sphereAwarePlan.ExpectedSphereHits,
                CandidateSummary = plan.CandidateSummary +
                    $" | SphereMetadataOnly[ExpectedHits=" +
                    $"{sphereAwarePlan.ExpectedSphereHits}]"
            };
        }
        if (plan.Maneuver == BonusManeuverKind.SphereCollectionJump &&
            !SphereOpportunityPreservesTerrainDeadline(
                scan,
                terrainPlan,
                plan,
                planningVelocity.x,
                out string sphereDeadlineCheck))
        {
            plan = terrainPlan with
            {
                CandidateSummary = terrainPlan.CandidateSummary +
                    $" | SphereOpportunityRejected[{sphereDeadlineCheck}]"
            };
        }
        return plan;
    }

    private static bool TerrainCommandMatches(
        BonusJumpPlan terrainPlan,
        BonusJumpPlan sphereAwarePlan)
    {
        return terrainPlan.IsValid == sphereAwarePlan.IsValid &&
            terrainPlan.ShouldJumpNow == sphereAwarePlan.ShouldJumpNow &&
            terrainPlan.Maneuver == sphereAwarePlan.Maneuver &&
            Mathf.Abs(terrainPlan.HoldSeconds - sphereAwarePlan.HoldSeconds) <=
                0.0001f &&
            Mathf.Abs(
                terrainPlan.PlannedLaunchX -
                sphereAwarePlan.PlannedLaunchX) <= 0.001f &&
            Mathf.Abs(
                terrainPlan.PredictedLandingX -
                sphereAwarePlan.PredictedLandingX) <= 0.001f;
    }

    private static bool IsStage2LowCorridorWallCatch(
        BonusJumpPlan plan,
        BonusBoardScanResult scan) =>
        plan.IsValid &&
        plan.Maneuver == BonusManeuverKind.ApproachJumpThenWallJump &&
        plan.Reason.EndsWith(
            "Stage2LowCorridorWallCatch",
            StringComparison.Ordinal) &&
        !scan.HasIntermediate &&
        scan.Intermediate.Width >= 0.75f &&
        scan.Intermediate.Top - scan.Current.Top >= 5.35f;

    private static bool SphereOpportunityPreservesTerrainDeadline(
        BonusBoardScanResult scan,
        BonusJumpPlan terrainPlan,
        BonusJumpPlan spherePlan,
        float horizontalSpeed,
        out string check)
    {
        if (!scan.HasNext)
        {
            check = "NoImmediateTerrainTransition";
            return true;
        }

        bool hasTerrainDeadline = terrainPlan.IsValid ||
            terrainPlan.Reason == "IntentionalDrop";
        if (!hasTerrainDeadline)
        {
            bool continuous = terrainPlan.Reason == "ContinuousSurface";
            check = continuous
                ? "ContinuousSurfaceUsesReservedRunway"
                : $"BaseTerrainPlanUnavailable:{terrainPlan.Reason}";
            return continuous;
        }

        float reactionMargin = Mathf.Clamp(
            Mathf.Abs(horizontalSpeed) * 0.04f,
            0.45f,
            2.00f);
        float latestSafeLanding =
            terrainPlan.LaunchWindowRight - reactionMargin;
        bool preserves =
            spherePlan.PredictedLandingX <= latestSafeLanding;
        check =
            $"SphereLanding={spherePlan.PredictedLandingX:F3}," +
            $"TerrainWindowRight={terrainPlan.LaunchWindowRight:F3}," +
            $"ReactionMargin={reactionMargin:F3}," +
            $"LatestSafeLanding={latestSafeLanding:F3}," +
            $"BaseReason={terrainPlan.Reason},Preserves={preserves}";
        return preserves;
    }

    private void LogRouteDiagnostics(
        BonusStageState state,
        BonusBoardScanResult scan,
        BonusJumpPlan plan,
        float horizontalSpeed,
        JumpPhysicsSnapshot physics)
    {
        if (!BonusRunnerLog.IsDebugMode)
            return;

        string signature = scan.IsValid && scan.HasNext
            ? $"{state.SectionIndex}:{scan.Current.Top:F1}:" +
              $"{scan.Next.Left:F1}:{scan.Next.Right:F1}:{scan.Next.Top:F1}:" +
              $"{plan.HoldSeconds:F2}:{plan.Reason}:{plan.ShouldJumpNow}:" +
              $"S{state.CollectedSpheres}:P{physics.ModelRevision}"
            : scan.IsValid
                ? $"{state.SectionIndex}:NoNext:{scan.Current.Top:F1}:{scan.Reason}"
                : $"{state.SectionIndex}:Invalid:{scan.Reason}";
        bool changed = signature != lastRouteSignature;
        if (!changed && Time.unscaledTime < nextRouteLogTime)
            return;

        lastRouteSignature = signature;
        nextRouteLogTime = Time.unscaledTime + 0.75f;

        if (!scan.IsValid)
        {
            BonusRunnerLog.Debug(
                $"SurfaceMap invalid at X={state.PlayerPosition.x:F3}: {scan.Reason}",
                "Routing");
            return;
        }

        string nextBoard = scan.HasNext
            ? $"Next=[{scan.Next.Left:F3},{scan.Next.Right:F3}] " +
              $"Safe=[{scan.Next.SafeLeft:F3},{scan.Next.SafeRight:F3}] " +
              $"Top={scan.Next.Top:F3} Collider={scan.Next.ColliderInstanceId}:{scan.Next.ColliderName} " +
              $"MapPiece={scan.Next.MapPieceName}#{scan.Next.MapPieceInstanceId} " +
              $"Generation={scan.Next.RegistryGeneration} Surface={scan.Next.StaticSurfaceIndex} " +
              $"Local=[{scan.Next.LocalLeft:F2},{scan.Next.LocalRight:F2}]"
            : "Next=Unavailable";
        string intermediateBoard = scan.HasIntermediate
            ? $"; Intermediate=[{scan.Intermediate.Left:F3}," +
              $"{scan.Intermediate.Right:F3}] " +
              $"Safe=[{scan.Intermediate.SafeLeft:F3}," +
              $"{scan.Intermediate.SafeRight:F3}] " +
              $"Top={scan.Intermediate.Top:F3} Collider=" +
              $"{scan.Intermediate.ColliderInstanceId}:" +
              $"{scan.Intermediate.ColliderName}"
            : string.Empty;
        string alternativeBoards =
            scan.Alternatives != null && scan.Alternatives.Length > 0
                ? "; Alternatives=" + string.Join(
                    "|",
                    scan.Alternatives.Take(12).Select(segment =>
                        $"[{segment.Left:F2},{segment.Right:F2}]" +
                        $"@{segment.Top:F2}:{segment.MapPieceName}"))
                : string.Empty;
        Vector2[] sectionActiveSpheres =
            BonusStageInspector.GetActiveSpherePositions(
                -100000f,
                100000f,
                500);
        int activeBehind = sectionActiveSpheres.Count(
            sphere => sphere.x < state.PlayerPosition.x - 1.0f);
        int activeAhead = sectionActiveSpheres.Length - activeBehind;
        string quotaStatus = !state.HasSphereProgress
            ? "Unavailable"
            : state.RemainingRequiredSpheres <= 0
                ? "Met"
                : activeAhead >= state.RemainingRequiredSpheres
                    ? "VisibleAheadUpperBoundSufficient"
                    : "VisibleAheadDeficitRisk";
        BonusRunnerLog.Debug(
            $"SurfaceMap X={state.PlayerPosition.x:F3}, Scanner=AllLayersStandable; " +
            $"Current=[{scan.Current.Left:F3},{scan.Current.Right:F3}] " +
            $"Safe=[{scan.Current.SafeLeft:F3},{scan.Current.SafeRight:F3}] " +
            $"Top={scan.Current.Top:F3} Collider={scan.Current.ColliderInstanceId}:{scan.Current.ColliderName} " +
            $"MapPiece={scan.Current.MapPieceName}#{scan.Current.MapPieceInstanceId} " +
            $"Generation={scan.Current.RegistryGeneration} Surface={scan.Current.StaticSurfaceIndex} " +
            $"Local=[{scan.Current.LocalLeft:F2},{scan.Current.LocalRight:F2}]; " +
            $"{nextBoard}{intermediateBoard}{alternativeBoards}; " +
            $"SafeEdgeDistance={scan.DistanceToCurrentEdge:F3}, " +
            $"Gap={scan.Gap:F3}, DeltaY={scan.HeightDelta:F3}, Reason={scan.Reason}, " +
            $"SphereProgress={(state.HasSphereProgress ? $"{state.CollectedSpheres}/{Math.Ceiling(state.RequiredSpheres):F0}" : "Unavailable")}, " +
            $"RequiredRaw={(state.HasSphereProgress ? state.RequiredSpheres.ToString("F3") : "Unavailable")}, " +
            $"Remaining={state.RemainingRequiredSpheres}, " +
            $"SphereInventory[Active={sectionActiveSpheres.Length}," +
            $"Behind={activeBehind},Ahead={activeAhead},Status={quotaStatus}]",
            "Routing");

        BonusRunnerLog.Debug(
            $"RouteEval X={state.PlayerPosition.x:F3}, VX={horizontalSpeed:F3}, " +
            $"Valid={plan.IsValid}, Action={(plan.ShouldJumpNow ? "Jump" : "Wait")}, " +
            $"ChosenHold={plan.HoldSeconds:F3}s, Flight={plan.PredictedFlightSeconds:F3}s, " +
            $"Travel={plan.HorizontalTravel:F3}, PlannedLaunch={plan.PlannedLaunchX:F3}, " +
            $"Window=[{plan.LaunchWindowLeft:F3},{plan.LaunchWindowRight:F3}], " +
            $"PredictedLanding={plan.PredictedLandingX:F3}, " +
            $"Maneuver={plan.Maneuver}, ExpectedSphereHits={plan.ExpectedSphereHits}, " +
            $"ExpectedSpeedBoostHits={plan.ExpectedSpeedBoostHits}, " +
            $"Reason={plan.Reason}; " +
            $"DecisionClass={ClassifyRouteDecision(plan, scan)}, " +
            $"FailureDomain={ClassifyRouteFailureDomain(plan, scan)}; " +
            $"Physics[{physics.Summary}]; " +
            $"Candidates: {plan.CandidateSummary}",
            "Routing");
    }

    private static string ClassifyRouteDecision(
        BonusJumpPlan plan,
        BonusBoardScanResult scan)
    {
        if (plan.IsValid)
            return plan.ShouldJumpNow ? "ExecutableNow" : "ApproachOrSafetyWait";
        if (plan.Reason == "IntentionalDrop")
            return "IntentionalCoast";
        if (plan.Reason == "ContinuousSurface")
            return "WalkForward";
        if (plan.Reason == "NextBoardUnavailable")
            return "ObserveRouteEnd";
        if (plan.Reason == "HorizontalSpeedTooLow")
            return "AwaitHorizontalMotion";
        return scan.IsValid ? "NoExecutableRoute" : "PerceptionPending";
    }

    private static string ClassifyRouteFailureDomain(
        BonusJumpPlan plan,
        BonusBoardScanResult scan)
    {
        if (plan.IsValid ||
            plan.Reason == "IntentionalDrop" ||
            plan.Reason == "ContinuousSurface" ||
            plan.Reason == "NextBoardUnavailable" ||
            plan.Reason == "HorizontalSpeedTooLow")
        {
            return "None";
        }

        return scan.IsValid ? "Route" : "Perception";
    }

    [HideFromIl2Cpp]
    private int GetPhysicsStepBudget(float seconds)
    {
        float fixedStep = latestPhysicsSnapshot.FixedDeltaTime;
        if (float.IsNaN(fixedStep) ||
            float.IsInfinity(fixedStep) ||
            fixedStep <= 0f)
        {
            fixedStep = Time.fixedDeltaTime;
        }
        fixedStep = Mathf.Clamp(fixedStep, 0.005f, 0.05f);
        return Mathf.Max(
            1,
            Mathf.CeilToInt(Mathf.Max(0.02f, seconds) / fixedStep));
    }

    [HideFromIl2Cpp]
    private static bool IsVerifiedGround3MandatoryFaceGeometry(
        BonusStageState state,
        BonusBoardSegment currentWall,
        BonusBoardSegment targetFace,
        out string reason)
    {
        float gap = targetFace.Left - currentWall.Right;
        float topDelta = targetFace.Top - currentWall.Top;
        bool sectionMatch = state.SectionIndex == 1;
        bool currentRole = string.Equals(
                currentWall.MapPieceName,
                "Ground 3",
                StringComparison.OrdinalIgnoreCase) &&
            currentWall.StaticSurfaceIndex == 2;
        bool targetRole = string.Equals(
                targetFace.MapPieceName,
                "Ground 3",
                StringComparison.OrdinalIgnoreCase) &&
            targetFace.StaticSurfaceIndex == 3;
        bool sameMappedInstance =
            currentWall.MapPieceInstanceId != 0 &&
            currentWall.MapPieceInstanceId ==
                targetFace.MapPieceInstanceId;
        bool sameCapturedGeneration =
            currentWall.RegistryGeneration <= 0 ||
            targetFace.RegistryGeneration <= 0 ||
            currentWall.RegistryGeneration ==
                targetFace.RegistryGeneration;
        bool originCompatible =
            !float.IsNaN(currentWall.MapPieceOriginX) &&
            !float.IsNaN(targetFace.MapPieceOriginX) &&
            Mathf.Abs(
                currentWall.MapPieceOriginX -
                targetFace.MapPieceOriginX) <= 0.15f;
        bool geometryMatch =
            gap >= 2.50f && gap <= 3.50f &&
            Mathf.Abs(topDelta) <= 0.15f;
        bool valid =
            sectionMatch &&
            currentRole &&
            targetRole &&
            sameMappedInstance &&
            originCompatible &&
            geometryMatch;
        reason =
            $"Section={state.SectionIndex + 1}/Expected2," +
            $"Current={currentWall.MapPieceName}#" +
            $"{currentWall.MapPieceInstanceId}/G" +
            $"{currentWall.RegistryGeneration}/S" +
            $"{currentWall.StaticSurfaceIndex}@" +
            $"[{currentWall.Left:F3},{currentWall.Right:F3}]/" +
            $"Y{currentWall.Top:F3}/O{currentWall.MapPieceOriginX:F3}," +
            $"Target={targetFace.MapPieceName}#" +
            $"{targetFace.MapPieceInstanceId}/G" +
            $"{targetFace.RegistryGeneration}/S" +
            $"{targetFace.StaticSurfaceIndex}@" +
            $"[{targetFace.Left:F3},{targetFace.Right:F3}]/" +
            $"Y{targetFace.Top:F3}/O{targetFace.MapPieceOriginX:F3}," +
            $"Gap={gap:F3},TopDelta={topDelta:F3},Checks[" +
            $"Section={sectionMatch},CurrentRole={currentRole}," +
            $"TargetRole={targetRole},SameInstance=" +
            $"{sameMappedInstance},SameCapturedGeneration=" +
            $"{sameCapturedGeneration}(DiagnosticOnly),Origin=" +
            $"{originCompatible}," +
            $"Geometry={geometryMatch}]";
        return valid;
    }

    [HideFromIl2Cpp]
    private bool TryValidateMandatoryFaceRouteIdentity(
        BonusStageState state,
        PlayerMovement player,
        out string reason)
    {
        if (!wallExitFaceContactRequired || !wallExitTargetActive)
        {
            reason =
                $"ContractRequired={wallExitFaceContactRequired}," +
                $"TargetActive={wallExitTargetActive}";
            return false;
        }

        BonusBoardSegment currentWall = BuildAutomaticTargetSegment();
        if (!IsVerifiedGround3MandatoryFaceGeometry(
                state,
                currentWall,
                wallExitTarget,
                out reason))
        {
            return false;
        }

        float halfWidth = player?.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float expectedContactX = wallExitTarget.Left - halfWidth;
        bool contactXCompatible =
            !wallMandatoryFaceInterceptCommitted ||
            float.IsNaN(wallMandatoryFaceTargetContactX) ||
            Mathf.Abs(
                wallMandatoryFaceTargetContactX - expectedContactX) <= 0.20f;
        reason +=
            $",TargetContactX[Stored=" +
            $"{wallMandatoryFaceTargetContactX:F3},Expected=" +
            $"{expectedContactX:F3},Compatible={contactXCompatible}]";
        return contactXCompatible;
    }

    [HideFromIl2Cpp]
    private bool FailMandatoryFacePlan(
        BonusStageState state,
        string reason,
        string details)
    {
        jumpController.Release();
        BonusRunnerLog.Warning(
            $"MandatoryFacePlanFailed Reason={reason}, " +
            $"Position=({state.PlayerPosition.x:F3}," +
            $"{state.PlayerPosition.y:F3}), Velocity=" +
            $"({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), ActionPhase=" +
            $"{wallActionPhase}, FixedStep=" +
            $"{JumpPhysicsFeedback.FixedStepSequence}, Details[{details}]. " +
            "FailureDomain=RouteExecution; Action=UP and all mandatory " +
            "setup/intercept/watch state is cleared atomically. No generic " +
            "fallback DOWN is allowed for this failed contract.");
        automaticTrajectoryCompatible = false;
        if (learningSampleActive && learningSource == "Automatic")
            FinishLearningSample(state, $"MandatoryFace:{reason}");
        ResetAutomaticControlState();
        nextAutomaticAttemptTime = Time.unscaledTime + 0.20f;
        return true;
    }

    [HideFromIl2Cpp]
    private bool HandleMandatoryFaceContactWatch(
        BonusStageState state,
        PlayerMovement player)
    {
        if (!wallExitTargetActive ||
            !wallExitFaceContactRequired ||
            !wallExitContactWatchActive)
        {
            return FailMandatoryFacePlan(
                state,
                "ContactWatchNotArmed",
                $"TargetActive={wallExitTargetActive}," +
                $"ContractRequired={wallExitFaceContactRequired}," +
                $"WatchActive={wallExitContactWatchActive}");
        }

        long currentFixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (wallMandatoryFaceContactWatchDeadlineFixedStep < 0)
        {
            wallMandatoryFaceContactWatchDeadlineFixedStep =
                currentFixedStep +
                GetPhysicsStepBudget(
                    MandatoryWallFaceContactWatchSeconds);
        }

        BonusBoardSegment target = wallExitTarget;
        BonusWallContact wall = wallDetector.Detect(
            player,
            GetWallRouteSpeed());
        float halfWidth = player.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        float bodyRight = state.PlayerPosition.x + halfWidth;
        float targetContactCenterX = target.Left - halfWidth;
        float faceBottomY = target.Top - Ground3ObjectiveFaceHeight;
        float physicalMinimumFeetY =
            faceBottomY + MandatoryWallFacePhysicalBottomClearance;
        float physicalMaximumFeetY =
            target.Top - MandatoryWallFacePhysicalTopClearance;
        bool insideFiniteFace =
            feetY >= physicalMinimumFeetY &&
            feetY <= physicalMaximumFeetY;
        bool nativeWallEvidence =
            wall.Reason?.IndexOf(
                "NativeIsWalled=True",
                StringComparison.Ordinal) >= 0;
        bool sideNormal = wall.Normal.x <= -0.35f;
        bool bodyAtTargetFace =
            Mathf.Abs(bodyRight - target.Left) <=
                MandatoryWallFaceBodyXMatchTolerance;
        bool rayAtTargetFace =
            wall.ColliderInstanceId != 0 &&
            Mathf.Abs(wall.FaceX - target.Left) <=
                MandatoryWallFaceRayXMatchTolerance &&
            wall.Point.y >= faceBottomY - 0.15f &&
            wall.Point.y <= target.Top + 0.15f;
        bool exactTargetFaceContact =
            wall.IsDetected &&
            wall.IsTouching &&
            insideFiniteFace &&
            sideNormal &&
            (rayAtTargetFace ||
             nativeWallEvidence && bodyAtTargetFace);
        bool kinematicPersistence =
            wallMandatoryFaceContactObservedFixedStep >= 0 &&
            insideFiniteFace &&
            Mathf.Abs(state.PlayerVelocity.x) <=
                WallAttachmentVelocityTolerance &&
            Mathf.Abs(
                state.PlayerPosition.x - targetContactCenterX) <=
                MandatoryWallFaceBodyXMatchTolerance &&
            bodyRight >= target.Left - 0.20f &&
            bodyRight <= target.Left + 0.12f;
        bool hasCurrentContactEvidence =
            exactTargetFaceContact || kinematicPersistence;

        if (wallMandatoryFaceContactObservedFixedStep >= 0 &&
            currentFixedStep >
                wallMandatoryFaceContactObservedFixedStep)
        {
            if (hasCurrentContactEvidence)
            {
                long firstContactStep =
                    wallMandatoryFaceContactObservedFixedStep;
                bool usedKinematicPersistence =
                    !exactTargetFaceContact && kinematicPersistence;
                jumpController.Release();
                BonusRunnerLog.Debug(
                    $"MandatoryFaceInterceptObserved Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY={feetY:F3}, " +
                    $"Velocity=({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), TargetFace=" +
                    $"X={target.Left:F3},Y=[{faceBottomY:F3}," +
                    $"{target.Top:F3}], Evidence=" +
                    $"{(usedKinematicPersistence ? "KinematicPersistenceAfterExactContact" : "ExactRayOrNative")}, " +
                    $"FixedSteps[First={firstContactStep},Current=" +
                    $"{currentFixedStep},Delta=" +
                    $"{currentFixedStep - firstContactStep}], " +
                    $"Normal=({wall.Normal.x:F3},{wall.Normal.y:F3}), " +
                    $"WallReason={wall.Reason}, ElapsedFromDown=" +
                    $"{Time.unscaledTime - wallMandatoryFaceInterceptStartedAt:F3}s, " +
                    $"PlanPrediction[FeetY=" +
                    $"{wallMandatoryFacePredictedContactFeetY:F3},VY=" +
                    $"{wallMandatoryFacePredictedContactVelocityY:F3},T=" +
                    $"{wallMandatoryFacePredictedContactSeconds:F3}," +
                    $"ErrorY={feetY - wallMandatoryFacePredictedContactFeetY:F3}], " +
                    $"ReleasePrediction[ReleaseFeetY=" +
                    $"{wallMandatoryFaceReleaseFeetY:F3},ReleaseVY=" +
                    $"{wallMandatoryFaceReleaseVelocityY:F3},ContactFeetY=" +
                    $"{wallMandatoryFaceReleasePredictedContactFeetY:F3}," +
                    $"ContactVY=" +
                    $"{wallMandatoryFaceReleasePredictedContactVelocityY:F3}," +
                    $"ErrorY=" +
                    $"{feetY - wallMandatoryFaceReleasePredictedContactFeetY:F3}]. " +
                    "Result=ConfirmedFiniteTargetSideFaceContact.",
                    "Recovery");

                BonusWallContact confirmedWall = exactTargetFaceContact
                    ? wall
                    : new BonusWallContact(
                        true,
                        true,
                        halfWidth,
                        0f,
                        target.Left,
                        new Vector2(target.Left, feetY),
                        Vector2.left,
                        0,
                        "ConfirmedMappedGround3S3Face",
                        "TwoFixedStepContact:ExactThenKinematicPersistence");
                int capturedObjectiveCount =
                    wallExitObjectiveCountAtCapture;
                float capturedObjectiveMinimumY =
                    wallExitObjectiveMinimumY;
                float capturedObjectiveMaximumY =
                    wallExitObjectiveMaximumY;
                if (!TryPromoteChainedWallTarget(
                        state,
                        allowLevelFace: true,
                        observedFaceX: confirmedWall.FaceX,
                        observedFeetY: feetY))
                {
                    return FailMandatoryFacePlan(
                        state,
                        "ConfirmedContactPromotionFailed",
                        $"Target=[{target.Left:F3}," +
                        $"{target.Right:F3}]@{target.Top:F3}, " +
                        $"FeetY={feetY:F3}, Evidence=" +
                        $"{confirmedWall.Reason}");
                }

                BonusRunnerLog.Debug(
                    $"WallExitContactIntercepted Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY={feetY:F3}, " +
                    $"Evidence={confirmedWall.Reason}. The finite S3 side " +
                    "face is now authoritative; objective descent is tried " +
                    "before any fresh climb input.",
                    "Recovery");
                if (TryManageAttachedObjectiveDescent(
                        state,
                        confirmedWall,
                        feetY,
                        capturedObjectiveCount,
                        capturedObjectiveMinimumY,
                        capturedObjectiveMaximumY))
                {
                    return true;
                }

                // A confirmed mandatory-face handoff may coincide with a
                // transient sphere-registry refresh. Never recurse into the
                // generic wall executor on that same physics observation: a
                // fresh DOWN here would undo the finite-face contact we just
                // proved. The next update can re-evaluate the promoted face.
                jumpController.Release();
                wallActionPhase = WallActionPhase.AwaitingWallContact;
                wallStallStartedAt = -1f;
                nextWallRecoveryTime = 0f;
                passiveWallApproachActive = true;
                BonusRunnerLog.Debug(
                    "MandatoryFaceHandoffBarrier Action=UP; objective " +
                    "descent was not armed on the confirmation frame, so " +
                    "generic wall input is deferred until a later update.",
                    "Recovery");
                return true;
            }

            BonusRunnerLog.Debug(
                $"MandatoryFaceContactConfirmationLost FirstStep=" +
                $"{wallMandatoryFaceContactObservedFixedStep}, " +
                $"CurrentStep={currentFixedStep}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY={feetY:F3}, " +
                $"WallDetected={wall.IsDetected}, " +
                $"Touching={wall.IsTouching}, Exact=" +
                $"{exactTargetFaceContact}, Persistence=" +
                $"{kinematicPersistence}. The observed physics step did not " +
                "retain target-face contact; a later exact contact must start " +
                "a new confirmation.",
                "Recovery");
            wallMandatoryFaceContactObservedFixedStep = -1;
        }

        bool physicsBudgetExpired =
            currentFixedStep >
                wallMandatoryFaceContactWatchDeadlineFixedStep;
        if (wallMandatoryFaceContactObservedFixedStep < 0 &&
            physicsBudgetExpired)
        {
            return FailMandatoryFacePlan(
                state,
                "ContactWatchPhysicsBudgetExpired",
                $"FixedStep={currentFixedStep}, DeadlineStep=" +
                $"{wallMandatoryFaceContactWatchDeadlineFixedStep}, " +
                $"FirstExactContact={exactTargetFaceContact}. A first " +
                "contact after the fixed-step budget cannot revive the " +
                "expired mandatory route.");
        }

        if (wallMandatoryFaceContactObservedFixedStep < 0 &&
            exactTargetFaceContact)
        {
            wallMandatoryFaceContactObservedFixedStep = currentFixedStep;
            wallMandatoryFaceContactWatchDeadlineFixedStep = Math.Max(
                wallMandatoryFaceContactWatchDeadlineFixedStep,
                currentFixedStep + 2);
            jumpController.Release();
            BonusRunnerLog.Debug(
                $"MandatoryFaceContactConfirmationStarted FixedStep=" +
                $"{currentFixedStep}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY={feetY:F3}, " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), Face=" +
                $"({wall.Point.x:F3},{wall.Point.y:F3}), FaceBand=" +
                $"[{physicalMinimumFeetY:F3}," +
                $"{physicalMaximumFeetY:F3}], BodyRight=" +
                $"{bodyRight:F3}, TargetX={target.Left:F3}, " +
                $"Normal=({wall.Normal.x:F3},{wall.Normal.y:F3}), " +
                $"Evidence={wall.Reason}. Action=UP; require a later " +
                "physics step on the same finite side face. A top/corner " +
                "landing cannot satisfy this contract.",
                "Recovery");
            return true;
        }

        if (wallMandatoryFaceContactObservedFixedStep >= 0 &&
            currentFixedStep ==
                wallMandatoryFaceContactObservedFixedStep)
        {
            jumpController.Release();
            return true;
        }

        bool passedTargetFace =
            state.PlayerPosition.x > target.Left + 0.35f;
        bool fellBelowFiniteFace =
            feetY < faceBottomY - 0.35f;
        if (physicsBudgetExpired ||
            passedTargetFace ||
            fellBelowFiniteFace)
        {
            return FailMandatoryFacePlan(
                state,
                physicsBudgetExpired
                    ? "ContactWatchPhysicsBudgetExpired"
                    : passedTargetFace
                        ? "PassedTargetFaceWithoutContact"
                        : "FellBelowTargetFace",
                $"FixedStep={currentFixedStep}, DeadlineStep=" +
                $"{wallMandatoryFaceContactWatchDeadlineFixedStep}, " +
                $"Target=[{target.Left:F3},{target.Right:F3}]@" +
                $"{target.Top:F3}, FeetY={feetY:F3}, " +
                $"Wall[Detected={wall.IsDetected},Touching=" +
                $"{wall.IsTouching},FaceX={wall.FaceX:F3}," +
                $"PointY={wall.Point.y:F3},NormalX=" +
                $"{wall.Normal.x:F3},Reason={wall.Reason}], Checks[" +
                $"FiniteY={insideFiniteFace},BodyAtFace=" +
                $"{bodyAtTargetFace},RayAtFace={rayAtTargetFace}," +
                $"SideNormal={sideNormal},Native=" +
                $"{nativeWallEvidence}]");
        }

        if (BonusRunnerLog.IsDebugMode &&
            Time.unscaledTime >= nextMandatoryFaceWindowLogTime)
        {
            nextMandatoryFaceWindowLogTime = Time.unscaledTime + 0.08f;
            BonusRunnerLog.Debug(
                $"MandatoryFaceInterceptContactWatch Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY={feetY:F3}, " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), TargetFace=" +
                $"X={target.Left:F3},Y=[{faceBottomY:F3}," +
                $"{target.Top:F3}], PhysicalFeetBand=" +
                $"[{physicalMinimumFeetY:F3}," +
                $"{physicalMaximumFeetY:F3}], FixedSteps[Current=" +
                $"{currentFixedStep},Deadline=" +
                $"{wallMandatoryFaceContactWatchDeadlineFixedStep}," +
                $"Remaining={Math.Max(0L, wallMandatoryFaceContactWatchDeadlineFixedStep - currentFixedStep)}], " +
                $"Wall[Detected={wall.IsDetected},Touching=" +
                $"{wall.IsTouching},Face=({wall.Point.x:F3}," +
                $"{wall.Point.y:F3}),Normal=({wall.Normal.x:F3}," +
                $"{wall.Normal.y:F3}),Reason={wall.Reason}], Checks[" +
                $"FiniteY={insideFiniteFace},BodyAtFace=" +
                $"{bodyAtTargetFace},RayAtFace={rayAtTargetFace}," +
                $"SideNormal={sideNormal},Exact=" +
                $"{exactTargetFaceContact},Persistence=" +
                $"{kinematicPersistence}]. Action=UP; no DOWN is legal " +
                "until confirmed finite S3 side contact.",
                "Recovery");
        }
        jumpController.Release();
        return true;
    }

    private bool TryArmCompletionTraversalWallContact(
        BonusStageState state,
        PlayerMovement player)
    {
        bool activeCompletionFlight =
            automaticPredictionActive &&
            learningSampleActive &&
            learningSource == "Automatic" &&
            learningTookOff &&
            automaticManeuver != BonusManeuverKind.EnterTrenchThenWallJump &&
            automaticManeuver != BonusManeuverKind.ApproachJumpThenWallJump &&
            automaticManeuver != BonusManeuverKind.WallJumpClimb;
        bool routeAlreadyOwnsWall =
            passiveWallApproachActive ||
            automaticPredictionActive && !activeCompletionFlight ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted;
        if (routeAlreadyOwnsWall ||
            state.IsGrounded ||
            state.PlayerVelocity.y > -1.0f)
        {
            return false;
        }

        float routeSpeed = Mathf.Max(1f, lastReliableHorizontalSpeed);
        BonusWallContact wall = wallDetector.Detect(player, routeSpeed);
        if (!wall.IsDetected || !wall.IsTouching)
            return false;

        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        bool staticWallTargetResolved =
            platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out BonusBoardSegment target);
        bool liveWallTargetResolved =
            !staticWallTargetResolved &&
            platformScanner.TryFindLiveWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out target);
        bool projectedWallTargetResolved = false;
        if (!staticWallTargetResolved &&
            !liveWallTargetResolved &&
            state.UsesStage2LiveRouting &&
            state.SectionIndex == 1 &&
            secondStagePreviewActive &&
            secondStageProjectedScan.IsValid &&
            secondStageProjectedScan.HasNext)
        {
            BonusBoardSegment projectedTarget =
                secondStageProjectedScan.Next;
            projectedWallTargetResolved =
                projectedTarget.Width >= 0.75f &&
                Mathf.Abs(projectedTarget.Left - wall.FaceX) <= 0.35f &&
                projectedTarget.Top >= feetY + 0.35f &&
                projectedTarget.Top <= feetY + 12.0f;
            if (projectedWallTargetResolved)
                target = projectedTarget;
        }
        if (!staticWallTargetResolved &&
            !liveWallTargetResolved &&
            !projectedWallTargetResolved)
        {
            return false;
        }

        float sourceRight = wall.FaceX - 0.01f;
        BonusBoardSegment source = new(
            state.PlayerPosition.x - playerHalfWidth,
            sourceRight,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "CompletionAirborneContact");
        BonusBoardScanResult contactScan = new(
            true,
            source,
            true,
            target,
            0f,
            Mathf.Max(0f, target.Left - source.Right),
            target.Top - feetY,
            "CompletionTraversalWallContact");
        float contactCenterX = target.Left - playerHalfWidth;
        BonusJumpPlan contactPlan = new(
            true,
            false,
            0f,
            0.01f,
            0f,
            state.PlayerPosition.x,
            contactCenterX,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            "CompletionTraversalWallContact",
            $"PhysicalFaceX={wall.FaceX:F3},TargetTop={target.Top:F3}",
            BonusManeuverKind.EnterTrenchThenWallJump);
        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            lastActiveTerrainSection >= 0
                ? lastActiveTerrainSection
                : state.SectionIndex,
            observedPhysics,
            routeSpeed,
            false);

        long priorRouteId = activeRouteDecisionId;
        long priorAttemptId = automaticAttemptId;
        string priorPlan = automaticPlanReason;
        BonusManeuverKind priorManeuver = automaticManeuver;
        float priorTargetLeft = automaticTargetLeft;
        float priorTargetRight = automaticTargetRight;
        float priorTargetTop = automaticTargetTop;
        bool interruptedCompletionHold = jumpController.IsHoldingJump;
        if (interruptedCompletionHold)
            jumpController.Release();
        if (activeCompletionFlight)
        {
            FinishLearningSample(
                state,
                "CompletionAirborneWallContactHandoff");
        }
        BeginPassiveWallApproach(
            state,
            contactPlan,
            contactScan,
            target,
            default,
            planningPhysics);
        nextWallRecoveryTime = 0f;
        wallInputSeparationReleaseFixedStep = interruptedCompletionHold
            ? JumpPhysicsFeedback.FixedStepSequence
            : -1;
        bool immediatePulseIssued =
            !interruptedCompletionHold &&
            TryWallRecoveryJump(state, player);
        BonusRunnerLog.Warning(
            $"CompletionTraversalWallContactRecovered Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), FaceX={wall.FaceX:F3}, " +
            $"FeetY={feetY:F3}, Target=[{target.Left:F3}," +
            $"{target.Right:F3}]@{target.Top:F3}, ActiveFlightHandoff=" +
            $"{activeCompletionFlight}, PriorRouteId={priorRouteId}, " +
            $"PriorAttemptId={priorAttemptId}, PriorPlan={priorPlan}, " +
            $"PriorManeuver={priorManeuver}, PriorTarget=" +
            $"[{priorTargetLeft:F3},{priorTargetRight:F3}]@" +
            $"{priorTargetTop:F3}. Exact physical contact matches a new " +
            $"{(staticWallTargetResolved
                ? "static"
                : liveWallTargetResolved
                    ? "live collider"
                    : "projected live")} " +
            "wall face; it overrides a stale boosted landing target " +
            $"and bounded wall control is re-armed. InterruptedPriorHold=" +
            $"{interruptedCompletionHold}, ImmediatePulseIssued=" +
            $"{immediatePulseIssued}, PulseDisposition=" +
            $"{(interruptedCompletionHold
                ? "DeferredUntilSeparatedFixedStep"
                : immediatePulseIssued
                    ? "IssuedNow"
                    : "ControllerDeferred")}, ReleaseFixedStep=" +
            $"{wallInputSeparationReleaseFixedStep}.");
        // Contact promotion owns the frame even when DOWN is deliberately
        // deferred until a later physics step.
        return true;
    }

    /// <summary>
    /// Last-resort closed-loop correction for Bonus Stage 1 section four while
    /// Spirit Boost is active.  This route is deliberately unavailable to
    /// ordinary play and to authored Stage 3 routing.  It requires either an
    /// exact physical face touch or a near-zero-X-speed contact immediately in
    /// front of a mapped, climbable face; a merely visible wall cannot create
    /// input ownership.
    /// </summary>
    private bool TryPromoteSpiritPitWallContact(
        BonusStageState state,
        PlayerMovement player,
        bool lowDescending)
    {
        bool routeAlreadyOwnsWall =
            passiveWallApproachActive ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            wallActionPhase != WallActionPhase.None;
        if (!lowDescending ||
            !state.SpiritBoostEnabled ||
            state.UsesStage3AuthoredRouting ||
            state.SectionIndex != 3 ||
            !state.IsActiveGameplay ||
            routeAlreadyOwnsWall)
        {
            return false;
        }

        float routeSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.x),
                lastReliableHorizontalSpeed));
        BonusWallContact wall = wallDetector.Detect(player, routeSpeed);
        bool stoppedAtFace =
            wall.IsDetected &&
            wall.Distance <= 0.35f &&
            Mathf.Abs(state.PlayerVelocity.x) <= 0.75f;
        if (!wall.IsDetected ||
            wall.Distance > RecoverableWallProbeDistance ||
            (!wall.IsTouching && !stoppedAtFace))
        {
            return false;
        }

        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        if (!platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out BonusBoardSegment target) ||
            target.Top <= feetY + 0.20f)
        {
            return false;
        }

        float sourceRight = wall.FaceX - 0.01f;
        BonusBoardSegment source = new(
            state.PlayerPosition.x - playerHalfWidth,
            sourceRight,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "SpiritPitWallContact");
        BonusBoardScanResult contactScan = new(
            true,
            source,
            true,
            target,
            0f,
            Mathf.Max(0f, target.Left - source.Right),
            target.Top - feetY,
            "SpiritPitWallContactRecovery");
        float contactCenterX = target.Left - playerHalfWidth;
        BonusJumpPlan contactPlan = new(
            true,
            false,
            0f,
            0.01f,
            0f,
            state.PlayerPosition.x,
            contactCenterX,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            "SpiritPitWallContactRecovery",
            $"PhysicalFaceX={wall.FaceX:F3},TargetTop={target.Top:F3}",
            BonusManeuverKind.EnterTrenchThenWallJump);
        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            observedPhysics,
            routeSpeed,
            false);

        long priorRouteId = activeRouteDecisionId;
        long priorAttemptId = automaticAttemptId;
        string priorPlan = automaticPlanReason;
        BonusManeuverKind priorManeuver = automaticManeuver;
        bool interruptedPriorHold = jumpController.IsHoldingJump;
        if (interruptedPriorHold)
            jumpController.Release();
        if (learningSampleActive && learningSource == "Automatic")
            FinishLearningSample(state, "SpiritPitWallContactHandoff");
        BeginPassiveWallApproach(
            state,
            contactPlan,
            contactScan,
            target,
            default,
            planningPhysics);
        nextWallRecoveryTime = 0f;
        wallInputSeparationReleaseFixedStep = interruptedPriorHold
            ? JumpPhysicsFeedback.FixedStepSequence
            : -1;
        bool immediatePulseIssued =
            !interruptedPriorHold &&
            TryWallRecoveryJump(state, player);
        ResetAutomaticPitConfirmation();
        BonusRunnerLog.Warning(
            $"SpiritPitWallContactRecovered PriorRouteId={priorRouteId}, " +
            $"PriorAttemptId={priorAttemptId}, PriorPlan={priorPlan}, " +
            $"PriorManeuver={priorManeuver}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), FaceX={wall.FaceX:F3}, " +
            $"Distance={wall.Distance:F3}, Touching={wall.IsTouching}, " +
            $"StoppedAtFace={stoppedAtFace}, FeetY={feetY:F3}, Target=" +
            $"[{target.Left:F3},{target.Right:F3}]@{target.Top:F3}, " +
            $"InterruptedPriorHold={interruptedPriorHold}, " +
            $"ImmediatePulseIssued={immediatePulseIssued}, " +
            $"ReleaseFixedStep={wallInputSeparationReleaseFixedStep}. " +
            "Observed climbable contact overrides the rejected Spirit " +
            "landing prediction and transfers control directly to the " +
            "bounded wall sequence.");
        return true;
    }

    /// <summary>
    /// Converts an exact owned-target collision during a late live section (or
    /// the authored fourth section) into the first frame of the wall sequence.
    /// Ordinary top landings and unrelated pit walls cannot manufacture a new
    /// route. A transient Grounded pulse is accepted because that pulse is the
    /// collision symptom this handoff prevents from becoming a ground re-plan.
    /// </summary>
    private bool TryPromoteLateSectionFlightWallContact(
        BonusStageState state,
        PlayerMovement player)
    {
        bool eligibleFlightManeuver =
            automaticManeuver == BonusManeuverKind.GroundJumpToLanding ||
            automaticManeuver == BonusManeuverKind.SphereCollectionJump ||
            automaticManeuver == BonusManeuverKind.SphereSweepToLowerLanding ||
            automaticManeuver == BonusManeuverKind.HazardClearanceJump;
        bool activeLearningFlight =
            eligibleFlightManeuver &&
            automaticPredictionActive &&
            learningSampleActive &&
            learningSource == "Automatic" &&
            learningTookOff;
        bool passiveDropFlight =
            (state.SpiritBoostEnabled ||
             !state.UsesStage3AuthoredRouting) &&
            !learningSampleActive &&
            secondStagePreviewActive &&
            secondStageObservedAirborne &&
            string.Equals(
                secondStageSource,
                "IntentionalDrop",
                StringComparison.Ordinal) &&
            secondStageExpectedSupport.Width > 0.05f;
        bool wallRouteAlreadyOwnsContact =
            passiveWallApproachActive ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            wallActionPhase != WallActionPhase.None;
        bool contactRecoverySection =
            !state.UsesStage3AuthoredRouting
                ? state.SectionIndex >= 2
                : state.SectionIndex == 3;
        if (!contactRecoverySection ||
            !state.IsActiveGameplay ||
            wallRouteAlreadyOwnsContact ||
            (!activeLearningFlight && !passiveDropFlight))
        {
            return false;
        }

        float routeSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.x),
                lastReliableHorizontalSpeed));
        BonusWallContact wall = wallDetector.Detect(player, routeSpeed);
        if (!wall.IsDetected || !wall.IsTouching)
            return false;

        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        // Stage 1/2 deliberately run without the authored map registry. Their
        // current automatic target (or the target of an IntentionalDrop) is
        // already a live-collider contract, so exact contact with that face is
        // sufficient authority. Stage 3 retains the stricter static lookup.
        BonusBoardSegment liveExpectedTarget = passiveDropFlight
            ? secondStageExpectedSupport
            : BuildAutomaticTargetSegment();
        bool liveExpectedFace =
            !state.UsesStage3AuthoredRouting &&
            liveExpectedTarget.Width > 0.05f &&
            Mathf.Abs(wall.FaceX - liveExpectedTarget.Left) <= 0.45f &&
            liveExpectedTarget.Top >= feetY - OwnedTargetFaceLipTolerance &&
            liveExpectedTarget.Top <= feetY + 12.0f;
        float ordinaryMaximumContactVelocityY =
            passiveDropFlight ? 3.0f : 2.0f;
        if (!liveExpectedFace &&
            state.PlayerVelocity.y > ordinaryMaximumContactVelocityY)
        {
            return false;
        }
        BonusBoardSegment target;
        bool targetResolved;
        if (liveExpectedFace)
        {
            target = liveExpectedTarget;
            targetResolved = true;
        }
        else
        {
            targetResolved = platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out target);
        }
        if (!targetResolved ||
            target.Top < feetY - OwnedTargetFaceLipTolerance)
        {
            return false;
        }

        bool passiveDropTargetMatch =
            !passiveDropFlight ||
            Mathf.Abs(target.Left - secondStageExpectedSupport.Left) <= 0.35f &&
            Mathf.Abs(target.Right - secondStageExpectedSupport.Right) <= 0.35f &&
            Mathf.Abs(target.Top - secondStageExpectedSupport.Top) <= 0.35f &&
            StaticOrColliderIdentityMatches(
                target,
                secondStageExpectedSupport);
        if (!passiveDropTargetMatch)
            return false;

        string priorPlan = automaticPlanReason;
        BonusManeuverKind priorManeuver = automaticManeuver;
        long priorRouteId = activeRouteDecisionId;
        long priorAttemptId = automaticAttemptId;
        float sourceRight = wall.FaceX - 0.01f;
        BonusBoardSegment source = new(
            state.PlayerPosition.x - playerHalfWidth,
            sourceRight,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "LateSectionAirborneContact");
        BonusBoardScanResult contactScan = new(
            true,
            source,
            true,
            target,
            0f,
            Mathf.Max(0f, target.Left - source.Right),
            target.Top - feetY,
            "LateSectionFlightWallContact");
        float contactCenterX = target.Left - playerHalfWidth;
        BonusJumpPlan contactPlan = new(
            true,
            false,
            0f,
            0.01f,
            0f,
            state.PlayerPosition.x,
            contactCenterX,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            "LateSectionFlightWallContact",
            $"PhysicalFaceX={wall.FaceX:F3},TargetTop={target.Top:F3}",
            BonusManeuverKind.EnterTrenchThenWallJump);
        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            observedPhysics,
            routeSpeed,
            false);

        bool interruptedPriorHold = jumpController.IsHoldingJump;
        if (interruptedPriorHold)
            jumpController.Release();
        if (activeLearningFlight)
            FinishLearningSample(state, "AirborneWallContactHandoff");
        BeginPassiveWallApproach(
            state,
            contactPlan,
            contactScan,
            target,
            default,
            planningPhysics);
        // This is a new, contact-confirmed second press. If the ballistic jump
        // was still held, the native controller must consume one complete
        // physics step with UP before another DOWN is legal. A wall-clock
        // half-tick was not sufficient at high render rates: two Updates
        // could run before any FixedUpdate and merge both actions again.
        nextWallRecoveryTime = 0f;
        wallInputSeparationReleaseFixedStep = interruptedPriorHold
            ? JumpPhysicsFeedback.FixedStepSequence
            : -1;
        bool immediatePulseIssued =
            !interruptedPriorHold &&
            TryWallRecoveryJump(state, player);
        BonusRunnerLog.Warning(
            $"LateSectionFlightWallContactPromoted PriorRouteId={priorRouteId}, " +
            $"PriorAttemptId={priorAttemptId}, PriorPlan={priorPlan}, " +
            $"PriorManeuver={priorManeuver}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), GroundedPulse=" +
            $"{state.IsGrounded}, FaceX={wall.FaceX:F3}, FeetY={feetY:F3}, " +
            $"Target=[{target.Left:F3},{target.Right:F3}]@" +
            $"{target.Top:F3}, HandoffSource=" +
            $"{(passiveDropFlight
                ? "IntentionalDropFaceCatch"
                : "ActiveJumpFaceCatch")}, InterruptedPriorHold=" +
            $"{interruptedPriorHold}, ImmediatePulseIssued=" +
            $"{immediatePulseIssued}, PulseDisposition=" +
            $"{(interruptedPriorHold
                ? "DeferredUntilSeparatedFixedStep"
                : immediatePulseIssued
                    ? "IssuedNow"
                    : "ControllerDeferred")}. " +
            $"ReleaseFixedStep=" +
            $"{wallInputSeparationReleaseFixedStep}, CurrentFixedStep=" +
            $"{JumpPhysicsFeedback.FixedStepSequence}. " +
            $"TargetAuthority={(liveExpectedFace ? "LiveExpectedFace" : "AuthoredStaticFace")}. " +
            "Action=wall-route ownership transfer; the collision remains one " +
            "continuous airborne/climb route instead of falling through to a " +
            "new ground jump.");
        // Promotion itself is the ownership handoff. A pulse that is deferred
        // for one physics step must not let the old ground/pit pipeline run.
        return true;
    }

    /// <summary>
    /// Closed-loop fallback for an owned non-wall flight that physically meets
    /// an unexpected mapped face. Nominal target coordinates are abandoned;
    /// the observed face, feet height and current route speed become the new
    /// authoritative state. Section 3 and completion traversal retain their
    /// more specific handoffs and therefore reach this method first.
    /// </summary>
    private bool TryPromoteUnexpectedFlightWallContact(
        BonusStageState state,
        PlayerMovement player)
    {
        bool nonWallAutomaticFlight =
            automaticManeuver != BonusManeuverKind.None &&
            automaticManeuver != BonusManeuverKind.EnterTrenchThenWallJump &&
            automaticManeuver != BonusManeuverKind.ApproachJumpThenWallJump &&
            automaticManeuver != BonusManeuverKind.WallJumpClimb;
        bool wallRouteAlreadyOwnsContact =
            passiveWallApproachActive ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            wallActionPhase != WallActionPhase.None;
        if (!state.IsActiveGameplay ||
            state.SectionIndex == 3 ||
            wallRouteAlreadyOwnsContact ||
            !nonWallAutomaticFlight ||
            !automaticPredictionActive ||
            !learningSampleActive ||
            learningSource != "Automatic" ||
            !learningTookOff ||
            state.PlayerVelocity.y > 2.0f)
        {
            return false;
        }

        float routeSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.x),
                lastReliableHorizontalSpeed));
        BonusWallContact wall = wallDetector.Detect(player, routeSpeed);
        if (!wall.IsDetected || !wall.IsTouching)
            return false;

        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        if (!platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out BonusBoardSegment target) ||
            target.Top <= feetY + 0.20f)
        {
            return false;
        }

        long priorRouteId = activeRouteDecisionId;
        long priorAttemptId = automaticAttemptId;
        string priorPlan = automaticPlanReason;
        BonusManeuverKind priorManeuver = automaticManeuver;
        float priorPredictedLandingX = automaticPredictedLandingX;
        BonusBoardSegment priorTarget = BuildAutomaticTargetSegment();
        bool priorPartialImpulse =
            partialAutomaticJumpImpulseOwnedFlight &&
            partialAutomaticJumpImpulseAttemptId == priorAttemptId;
        float priorPartialObservedVelocityY =
            partialAutomaticJumpObservedVelocityY;
        float priorPartialExpectedVelocityY =
            partialAutomaticJumpExpectedVelocityY;
        float elapsedFlight = Mathf.Max(
            0f,
            Time.unscaledTime - automaticJumpRequestedAt);
        float flightProgress = Mathf.Clamp01(
            elapsedFlight /
            Mathf.Max(0.05f, automaticPredictedFlightSeconds));
        float expectedContactX = Mathf.Lerp(
            automaticPlanTriggerPosition.x,
            priorPredictedLandingX,
            flightProgress);
        bool predictedLowLandingRemainsSafe =
            priorTarget.SafeRight > priorTarget.SafeLeft + 0.05f &&
            priorPredictedLandingX >= priorTarget.SafeLeft - 0.05f &&
            priorPredictedLandingX <= priorTarget.SafeRight + 0.05f;
        bool horizontalContractRemainsValid =
            Mathf.Abs(state.PlayerPosition.x - expectedContactX) <= 0.65f;
        bool contactFaceWithinLowTargetBand =
            wall.FaceX >= priorTarget.Left - 0.20f &&
            wall.FaceX <= priorTarget.Right + 0.20f;
        bool overheadGenerationRebased = false;
        bool ordinaryStage2Ground3OverheadContact =
            state.UsesStage2LiveRouting &&
            state.SectionIndex == 1 &&
            !state.SpiritBoostEnabled &&
            priorManeuver == BonusManeuverKind.GroundJumpToLanding &&
            state.PlayerVelocity.y < -2.0f &&
            !jumpController.IsHoldingJump &&
            !partialAutomaticJumpImpulseOwnedFlight &&
            predictedLowLandingRemainsSafe &&
            horizontalContractRemainsValid &&
            contactFaceWithinLowTargetBand &&
            IsStage2Ground3OverheadForCommittedLowRoute(
                priorTarget,
                target,
                out overheadGenerationRebased);
        if (ordinaryStage2Ground3OverheadContact)
        {
            if (stage2LowRouteOverheadPreservedAttemptId !=
                automaticAttemptId)
            {
                stage2LowRouteOverheadPreservedAttemptId =
                    automaticAttemptId;
                BonusRunnerLog.Warning(
                    $"Stage2LowRouteOverheadContactPreserved RouteId=" +
                    $"{priorRouteId}, AttemptId={automaticAttemptId}, " +
                    $"Plan={priorPlan}, Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), FaceX=" +
                    $"{wall.FaceX:F3}, FeetY={feetY:F3}, LowTarget=" +
                    $"[{priorTarget.Left:F3},{priorTarget.Right:F3}]@" +
                    $"{priorTarget.Top:F3}/S" +
                    $"{priorTarget.StaticSurfaceIndex}/G" +
                    $"{priorTarget.RegistryGeneration}, Overhead=" +
                    $"[{target.Left:F3},{target.Right:F3}]@" +
                    $"{target.Top:F3}/S{target.StaticSurfaceIndex}/G" +
                    $"{target.RegistryGeneration}, GenerationRebased=" +
                    $"{overheadGenerationRebased}, " +
                    $"PredictedLandingX={priorPredictedLandingX:F3}, " +
                    $"ExpectedContactX={expectedContactX:F3}, " +
                    $"HorizontalError=" +
                    $"{state.PlayerPosition.x - expectedContactX:F3}. " +
                    "The planner already classified this same-instance " +
                    "Ground 3 surface as an underpass ceiling. No input or " +
                    "ownership state is changed; the original low landing " +
                    "remains authoritative.");
            }

            // Returning false would immediately hand the same physical face
            // to TryAdoptStage2UnmappedPhysicalWallContact below. Consume this
            // observation while retaining the original ground-flight sample.
            return true;
        }
        float sourceRight = wall.FaceX - 0.01f;
        BonusBoardSegment source = new(
            state.PlayerPosition.x - playerHalfWidth,
            sourceRight,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "ReactiveAirborneContact");
        BonusBoardScanResult contactScan = new(
            true,
            source,
            true,
            target,
            0f,
            Mathf.Max(0f, target.Left - source.Right),
            target.Top - feetY,
            "ReactiveFlightWallContact");
        float contactCenterX = target.Left - playerHalfWidth;
        BonusJumpPlan contactPlan = new(
            true,
            false,
            0f,
            0.01f,
            0f,
            state.PlayerPosition.x,
            contactCenterX,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            "ReactiveFlightWallContact",
            $"PhysicalFaceX={wall.FaceX:F3},TargetTop={target.Top:F3}",
            BonusManeuverKind.EnterTrenchThenWallJump);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            jumpPhysicsFeedback.CaptureSnapshot(player),
            routeSpeed,
            false);

        bool interruptedReactiveHold = jumpController.IsHoldingJump;
        if (interruptedReactiveHold)
            jumpController.Release();
        FinishLearningSample(state, "UnexpectedMappedWallContactHandoff");
        BeginPassiveWallApproach(
            state,
            contactPlan,
            contactScan,
            target,
            default,
            planningPhysics);
        nextWallRecoveryTime = 0f;
        wallInputSeparationReleaseFixedStep = interruptedReactiveHold
            ? JumpPhysicsFeedback.FixedStepSequence
            : -1;
        bool immediatePulseIssued =
            !interruptedReactiveHold &&
            TryWallRecoveryJump(state, player);
        BonusRunnerLog.Warning(
            $"ReactiveFlightWallContactHandoff PriorRouteId={priorRouteId}, " +
            $"PriorAttemptId={priorAttemptId}, PriorPlan={priorPlan}, " +
            $"PriorManeuver={priorManeuver}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), GroundedPulse=" +
            $"{state.IsGrounded}, FaceX={wall.FaceX:F3}, FeetY={feetY:F3}, " +
            $"ObservedTarget=[{target.Left:F3},{target.Right:F3}]@" +
            $"{target.Top:F3}, AbandonedTarget=[{priorTarget.Left:F3}," +
            $"{priorTarget.Right:F3}]@{priorTarget.Top:F3}, " +
            $"AbandonedPredictedLandingX={priorPredictedLandingX:F3}, " +
            $"PredictionErrorToContactX=" +
            $"{state.PlayerPosition.x - priorPredictedLandingX:F3}. " +
            $"PartialImpulse[Active={priorPartialImpulse}," +
            $"ObservedVY={priorPartialObservedVelocityY:F3}," +
            $"ExpectedVY={priorPartialExpectedVelocityY:F3}]. " +
            "RecoveryState=AwaitingObservedWallPulse; exact static-map " +
            $"contact overrides the stale nominal flight. " +
            $"InterruptedPriorHold={interruptedReactiveHold}, " +
            $"ImmediatePulseIssued={immediatePulseIssued}, " +
            $"PulseDisposition=" +
            $"{(interruptedReactiveHold
                ? "DeferredUntilSeparatedFixedStep"
                : immediatePulseIssued
                    ? "IssuedNow"
                    : "ControllerDeferred")}, ReleaseFixedStep=" +
            $"{wallInputSeparationReleaseFixedStep}.");
        return true;
    }

    /// <summary>
    /// Correlates a committed Stage-2 Ground-3 low landing with the overlapping
    /// S4/S5 overhead face seen later in that same flight. The Stage-2 registry
    /// can advance while pooled coverage changes, so a still-live committed
    /// target may carry the immediately older global generation. Rebase only
    /// this runtime correlation, and only after proving the same nonzero piece
    /// instance and unchanged prefab origin. The planner's snapshot-level
    /// generation contract remains strict.
    /// </summary>
    private static bool IsStage2Ground3OverheadForCommittedLowRoute(
        BonusBoardSegment committedTarget,
        BonusBoardSegment observedOverhead,
        out bool generationRebased)
    {
        generationRebased = false;
        if (BonusJumpPlanner.IsStage2Ground3OverheadForLowRoute(
                committedTarget,
                observedOverhead))
        {
            return true;
        }

        bool sameLivePieceAcrossNewerGeneration =
            committedTarget.RegistryGeneration > 0 &&
            observedOverhead.RegistryGeneration >
                committedTarget.RegistryGeneration &&
            committedTarget.MapPieceInstanceId != 0 &&
            committedTarget.MapPieceInstanceId ==
                observedOverhead.MapPieceInstanceId &&
            float.IsFinite(committedTarget.MapPieceOriginX) &&
            float.IsFinite(observedOverhead.MapPieceOriginX) &&
            Mathf.Abs(
                committedTarget.MapPieceOriginX -
                observedOverhead.MapPieceOriginX) <= 0.15f;
        if (!sameLivePieceAcrossNewerGeneration)
            return false;

        BonusBoardSegment rebasedTarget = committedTarget with
        {
            RegistryGeneration = observedOverhead.RegistryGeneration
        };
        if (!BonusJumpPlanner.IsStage2Ground3OverheadForLowRoute(
                rebasedTarget,
                observedOverhead))
        {
            return false;
        }

        generationRebased = true;
        return true;
    }

    private static bool TryGetOrdinaryStage1Section2CrossSphereRoute(
        BonusStageState state,
        BonusBoardScanResult scan,
        IReadOnlyList<Vector2> sphereObjectives,
        out Vector2 crossCenter)
    {
        crossCenter = default;
        if (state.SpiritBoostEnabled ||
            state.SectionIndex != 2 ||
            !string.Equals(
                state.MapName,
                "map_bonus_stage_1",
                StringComparison.OrdinalIgnoreCase) ||
            !scan.IsValid ||
            !scan.HasNext ||
            sphereObjectives == null ||
            sphereObjectives.Count < 5)
        {
            return false;
        }

        // Authored fixture, expressed by topology rather than world X:
        // after the walkable seam the runner reaches a roughly seven-unit
        // road, followed across a four-unit opening by a two-unit level
        // stone. Five live BonusSphere objects form a unit-spaced cross above
        // that first stone. The complete module repeats every 96 world units,
        // so the live shape/relative geometry -- never an absolute X -- owns
        // every occurrence.
        bool matchingRoadTransfer =
            scan.Current.Width >= 6.25f &&
            scan.Current.Width <= 7.75f &&
            scan.Next.Width >= 1.50f &&
            scan.Next.Width <= 2.50f &&
            Mathf.Abs(scan.HeightDelta) <= 0.10f &&
            scan.Gap >= 3.50f &&
            scan.Gap <= 4.50f;
        if (!matchingRoadTransfer)
            return false;

        const float coordinateTolerance = 0.18f;
        foreach (Vector2 center in sphereObjectives)
        {
            bool left = false;
            bool right = false;
            bool down = false;
            bool up = false;
            foreach (Vector2 sphere in sphereObjectives)
            {
                float dx = sphere.x - center.x;
                float dy = sphere.y - center.y;
                left |= Mathf.Abs(dx + 1f) <= coordinateTolerance &&
                        Mathf.Abs(dy) <= coordinateTolerance;
                right |= Mathf.Abs(dx - 1f) <= coordinateTolerance &&
                         Mathf.Abs(dy) <= coordinateTolerance;
                down |= Mathf.Abs(dx) <= coordinateTolerance &&
                        Mathf.Abs(dy + 1f) <= coordinateTolerance;
                up |= Mathf.Abs(dx) <= coordinateTolerance &&
                      Mathf.Abs(dy - 1f) <= coordinateTolerance;
            }

            if (left && right && down && up &&
                center.x >= scan.Next.Left + 0.50f &&
                center.x <= scan.Next.Right - 0.50f &&
                center.y >= scan.Current.Top + 3.0f &&
                center.y <= scan.Current.Top + 5.0f)
            {
                crossCenter = center;
                return true;
            }
        }

        return false;
    }

    private static bool Stage1CrossSphereTargetMatchesLatchedTarget(
        BonusBoardSegment candidate,
        BonusBoardSegment latchedTarget) =>
        candidate.Width > 0.05f &&
        latchedTarget.Width > 0.05f &&
        Mathf.Abs(candidate.Left - latchedTarget.Left) <= 0.35f &&
        Mathf.Abs(candidate.Right - latchedTarget.Right) <= 0.35f &&
        Mathf.Abs(candidate.Top - latchedTarget.Top) <= 0.15f;

    private void ResetStage1CrossSpherePriority()
    {
        stage1CrossSpherePriorityLatched = false;
        stage1CrossSpherePriorityCenter = default;
        stage1CrossSpherePriorityTarget = default;
        lastLoggedStage1CrossSphereCenterX = float.NaN;
        lastCommittedStage1CrossSphereCenterX = float.NaN;
    }

    private bool TryPromoteRecentOrAuthoredStage3WallContact(
        BonusStageState state,
        PlayerMovement player)
    {
        if (!state.UsesStage3AuthoredRouting ||
            !state.IsActiveGameplay ||
            state.SectionIndex < 0 ||
            state.SectionIndex > 2 ||
            passiveWallApproachActive ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            wallActionPhase != WallActionPhase.None)
        {
            return false;
        }

        long currentFixedStep = JumpPhysicsFeedback.FixedStepSequence;
        long recentFixedStepAge =
            recentAutomaticFlightEndedFixedStep >= 0
                ? currentFixedStep -
                  recentAutomaticFlightEndedFixedStep
                : long.MaxValue;
        float recentWallTimeAge =
            recentAutomaticFlightEndedAt >= 0f
                ? Time.unscaledTime -
                  recentAutomaticFlightEndedAt
                : float.PositiveInfinity;
        bool recentIdentityMatches =
            recentAutomaticFlightContactActive &&
            recentFixedStepAge >= 0 &&
            recentFixedStepAge <=
                RecentAutomaticFlightContactMaximumFixedSteps &&
            recentWallTimeAge >= 0f &&
            recentWallTimeAge <=
                RecentAutomaticFlightContactMaximumSeconds &&
            recentAutomaticFlightPlayerInstanceId ==
                state.PlayerInstanceId &&
            recentAutomaticFlightSection == state.SectionIndex &&
            string.Equals(
                recentAutomaticFlightMap,
                state.MapName,
                StringComparison.Ordinal);
        if (recentAutomaticFlightContactActive &&
            !recentIdentityMatches)
        {
            ClearRecentAutomaticFlightContact();
        }

        float routeSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                lastReliableHorizontalSpeed,
                recentIdentityMatches
                    ? Mathf.Abs(
                        recentAutomaticFlightTriggerVelocity.x)
                    : 0f));
        BonusWallContact wall =
            wallDetector.Detect(player, routeSpeed);
        if (!wall.IsDetected || !wall.IsTouching)
            return false;

        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(
                0.15f,
                player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        if (!platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out BonusBoardSegment target) ||
            target.Top <= feetY + 0.20f)
        {
            return false;
        }

        // Ground 6/S3 is the authored narrow pillar at the opening of the
        // third displayed section. Its static role, not its pooled world X,
        // proves that an exact face contact is a legal wall-entry route.
        bool authoredGround6EntryFace =
            state.SectionIndex == 2 &&
            string.Equals(
                target.MapPieceName,
                "Ground 6",
                StringComparison.OrdinalIgnoreCase) &&
            target.StaticSurfaceIndex == 3 &&
            target.MapPieceInstanceId != 0;
        bool recentTargetIdentityMatches =
            recentIdentityMatches &&
            (StaticOrColliderIdentityMatches(
                 target,
                 recentAutomaticFlightTarget) ||
             wall.FaceX >=
                 recentAutomaticFlightTarget.Left - 0.80f &&
             wall.FaceX <=
                 recentAutomaticFlightTarget.Right + 0.80f);
        bool recentIntermediateBlockingFace =
            recentIdentityMatches &&
            recentAutomaticFlightSource.Width > 0.05f &&
            wall.FaceX >=
                recentAutomaticFlightSource.Right - 0.35f &&
            wall.FaceX <=
                recentAutomaticFlightTarget.Right + 0.80f &&
            target.Left <=
                recentAutomaticFlightTarget.Right + 0.80f;
        if (!authoredGround6EntryFace &&
            !recentTargetIdentityMatches &&
            !recentIntermediateBlockingFace)
        {
            return false;
        }

        long priorAttemptId = automaticPredictionActive
            ? automaticAttemptId
            : recentAutomaticFlightAttemptId;
        long priorRouteId = automaticPredictionActive
            ? activeRouteDecisionId
            : recentAutomaticFlightRouteId;
        string priorPlan = automaticPredictionActive
            ? automaticPlanReason
            : recentAutomaticFlightPlan;
        BonusManeuverKind priorManeuver = automaticPredictionActive
            ? automaticManeuver
            : recentAutomaticFlightManeuver;
        string priorOutcome = recentIdentityMatches
            ? recentAutomaticFlightOutcome
            : "ActiveOrAuthoredContact";
        float priorPredictedLandingX = automaticPredictionActive
            ? automaticPredictedLandingX
            : recentAutomaticFlightPredictedLandingX;

        BonusBoardSegment source = new(
            state.PlayerPosition.x - playerHalfWidth,
            wall.FaceX - 0.01f,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "Stage3AuthoritativeWallContact");
        BonusBoardScanResult contactScan = new(
            true,
            source,
            true,
            target,
            0f,
            Mathf.Max(0f, target.Left - source.Right),
            target.Top - feetY,
            "Stage3RecentOrAuthoredWallContact");
        BonusJumpPlan contactPlan = new(
            true,
            false,
            0f,
            0.01f,
            0f,
            state.PlayerPosition.x,
            target.Left - playerHalfWidth,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            authoredGround6EntryFace
                ? "AuthoredGround6EntryWallContact"
                : "RecentAutomaticFlightWallContact",
            $"PhysicalFaceX={wall.FaceX:F3}," +
            $"Target={target.MapPieceName}#" +
            $"{target.MapPieceInstanceId}/S" +
            $"{target.StaticSurfaceIndex}@{target.Top:F3}",
            BonusManeuverKind.EnterTrenchThenWallJump);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            jumpPhysicsFeedback.CaptureSnapshot(player),
            routeSpeed,
            false);

        bool interruptedPriorHold = jumpController.IsHoldingJump;
        if (interruptedPriorHold)
            jumpController.Release();
        if (learningSampleActive &&
            learningSource == "Automatic")
        {
            FinishLearningSample(
                state,
                "Stage3AuthoritativeWallContactHandoff");
        }
        BeginPassiveWallApproach(
            state,
            contactPlan,
            contactScan,
            target,
            default,
            planningPhysics);
        nextWallRecoveryTime = 0f;
        wallInputSeparationReleaseFixedStep =
            interruptedPriorHold
                ? currentFixedStep
                : -1;
        bool immediatePulseIssued =
            !interruptedPriorHold &&
            TryWallRecoveryJump(state, player);
        ClearRecentAutomaticFlightContact();
        BonusRunnerLog.Warning(
            $"Stage3WallContactOwnershipRecovered Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), FaceX={wall.FaceX:F3}, " +
            $"FeetY={feetY:F3}, Target=[{target.Left:F3}," +
            $"{target.Right:F3}]@{target.Top:F3}/" +
            $"{target.MapPieceName}#{target.MapPieceInstanceId}/S" +
            $"{target.StaticSurfaceIndex}, Authority=" +
            $"{(authoredGround6EntryFace
                ? "AuthoredGround6EntryFace"
                : recentTargetIdentityMatches
                    ? "RecentTargetIdentity"
                    : "RecentIntermediateBlockingFace")}, " +
            $"PriorRouteId={priorRouteId}, PriorAttemptId=" +
            $"{priorAttemptId}, PriorPlan={priorPlan}, PriorManeuver=" +
            $"{priorManeuver}, PriorOutcome={priorOutcome}, " +
            $"PriorPredictedLandingX={priorPredictedLandingX:F3}, " +
            $"RecentAge={recentFixedStepAge}Steps/" +
            $"{recentWallTimeAge:F3}s, InterruptedPriorHold=" +
            $"{interruptedPriorHold}, ImmediatePulseIssued=" +
            $"{immediatePulseIssued}, PulseDisposition=" +
            $"{(interruptedPriorHold
                ? "DeferredUntilSeparatedFixedStep"
                : immediatePulseIssued
                    ? "IssuedNow"
                    : "ControllerDeferred")}. FailureDomain=" +
            "ControlOwnership; exact authored collision restores the " +
            "bounded wall route before pit confirmation.");
        return true;
    }

    private bool TryRebaseUnexpectedWallContact(
        BonusStageState state,
        PlayerMovement player,
        BonusWallContact wall,
        float playerHalfWidth)
    {
        // Mandatory objective-face routes may only hand off to their exact
        // authored face. Rebinding those routes would turn a missed objective
        // lane into false success.
        if (!wall.IsDetected ||
            !wall.IsTouching ||
            wallExitFaceContactRequired ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            attachedObjectiveDescentActive)
        {
            return false;
        }

        // "Already owned" is a handled contact, not a failed rebase. Direct
        // callers such as the passive-route timeout must keep the existing
        // shared executor alive and request its next bounded pulse instead of
        // interpreting this observation as proof that the route should abort.
        if (state.UsesStage2LiveRouting &&
            state.SectionIndex == 1 &&
            SharedWallOwnsPhysicalFace(wall.FaceX))
        {
            if (BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextStage2UnmappedWallLogTime)
            {
                nextStage2UnmappedWallLogTime =
                    Time.unscaledTime + 0.25f;
                BonusRunnerLog.Debug(
                    $"SharedWallPhysicalFaceAlreadyOwned FaceX=" +
                    $"{wall.FaceX:F3}, OwnedFaceX=" +
                    $"{sharedWallOwnedFaceX:F3}, Target=[" +
                    $"{automaticTargetLeft:F3}," +
                    $"{automaticTargetRight:F3}]@" +
                    $"{automaticTargetTop:F3}, Phase={wallActionPhase}. " +
                    "The caller may continue the current bounded wall " +
                    "executor; no hold, phase, or route identity is reset.",
                    "Recovery");
            }
            return true;
        }

        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        if (!TryResolveObservedWallSurface(
                wall,
                feetY,
                playerHalfWidth,
                out BonusBoardSegment observedTarget,
                out string targetAuthority) ||
            observedTarget.Top <= feetY + 0.20f)
        {
            return false;
        }

        BonusBoardSegment abandonedTarget = BuildAutomaticTargetSegment();
        long abandonedRouteId = activeRouteDecisionId;
        long abandonedAttemptId = automaticAttemptId;
        string abandonedPlan = automaticPlanReason;
        float abandonedPredictionX = automaticPredictedLandingX;
        float routeSpeed = Mathf.Max(1f, GetWallRouteSpeed());
        BonusBoardSegment source = new(
            state.PlayerPosition.x - playerHalfWidth,
            wall.FaceX - 0.01f,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "ReactiveWallRouteContact");
        BonusBoardScanResult contactScan = new(
            true,
            source,
            true,
            observedTarget,
            0f,
            0f,
            observedTarget.Top - feetY,
            "ReactiveWallRouteRebase");
        BonusJumpPlan contactPlan = new(
            true,
            false,
            0f,
            0.01f,
            0f,
            state.PlayerPosition.x,
            observedTarget.Left - playerHalfWidth,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            "ReactiveWallRouteRebase",
            $"PhysicalFaceX={wall.FaceX:F3},TargetTop={observedTarget.Top:F3}",
            BonusManeuverKind.EnterTrenchThenWallJump);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            jumpPhysicsFeedback.CaptureSnapshot(player),
            routeSpeed,
            false);

        if (learningSampleActive && learningSource == "Automatic")
            FinishLearningSample(state, "UnexpectedWallRouteContactReplan");
        BeginPassiveWallApproach(
            state,
            contactPlan,
            contactScan,
            observedTarget,
            default,
            planningPhysics);
        // The resolved target can be a merged live surface whose bounds span
        // several authored steps. Preserve the measured collision face as
        // identity instead of substituting that broad surface's left edge.
        sharedWallOwnedFaceX = wall.FaceX;
        BonusRunnerLog.Warning(
            $"ReactiveWallRouteRebased PriorRouteId={abandonedRouteId}, " +
            $"PriorAttemptId={abandonedAttemptId}, PriorPlan={abandonedPlan}, " +
            $"Position=({state.PlayerPosition.x:F3}," +
            $"{state.PlayerPosition.y:F3}), Velocity=" +
            $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"ObservedFaceX={wall.FaceX:F3}, FeetY={feetY:F3}, " +
            $"ObservedTarget=[{observedTarget.Left:F3}," +
            $"{observedTarget.Right:F3}]@{observedTarget.Top:F3}, " +
            $"TargetAuthority={targetAuthority}, " +
            $"AbandonedTarget=[{abandonedTarget.Left:F3}," +
            $"{abandonedTarget.Right:F3}]@{abandonedTarget.Top:F3}, " +
            $"AbandonedPredictedLandingX={abandonedPredictionX:F3}. " +
            "FailureDomain=Prediction; RecoveryState=ObservedWallAuthority. " +
            "The physically touched climbable face replaces the stale wall " +
            "target before any further DOWN is issued.");
        return true;
    }

    private bool SharedWallOwnsPhysicalFace(float faceX)
    {
        bool ownerActive =
            passiveWallApproachActive ||
            wallActionPhase != WallActionPhase.None &&
            wallActionPhase != WallActionPhase.Completed &&
            wallActionPhase != WallActionPhase.Failed;
        return ownerActive &&
               float.IsFinite(sharedWallOwnedFaceX) &&
               float.IsFinite(faceX) &&
               Mathf.Abs(faceX - sharedWallOwnedFaceX) <= 0.25f;
    }

    [HideFromIl2Cpp]
    private bool TryResolveObservedWallSurface(
        BonusWallContact wall,
        float feetY,
        float playerHalfWidth,
        out BonusBoardSegment target,
        out string authority)
    {
        if (platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out target))
        {
            authority = "AuthoredRegistry";
            return true;
        }

        if (platformScanner.TryFindLiveWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out target))
        {
            authority = "LiveColliderTop";
            return true;
        }

        authority = "Unavailable";
        target = default;
        return false;
    }

    private bool TryAdoptStage2UnmappedPhysicalWallContact(
        BonusStageState state,
        PlayerMovement player,
        BonusWallContact wall,
        float playerHalfWidth)
    {
        bool ownsStage2WallEntryFlight =
            learningSampleActive &&
            learningSource == "Automatic" &&
            automaticPredictionActive;
        bool reactiveStationaryContact =
            !learningSampleActive &&
            !automaticPredictionActive &&
            Mathf.Abs(state.PlayerVelocity.x) <= 0.50f &&
            (state.IsGrounded ||
             state.PlayerPosition.y > -3.20f &&
             state.PlayerVelocity.y > -14f);
        bool protectedWallContract =
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            attachedObjectiveDescentActive ||
            wallExitFaceContactRequired ||
            HasCommittedExitFaceFlight();
        if (!state.UsesStage2LiveRouting ||
            state.SectionIndex != 1 ||
            (!state.IsActiveGameplay &&
             !IsSuccessfulCompletionTraversal(state)) ||
            protectedWallContract ||
            (!ownsStage2WallEntryFlight &&
             !reactiveStationaryContact) ||
            !wall.IsDetected ||
            !wall.IsTouching ||
            Mathf.Abs(state.PlayerVelocity.x) > 1.25f)
        {
            return false;
        }

        // The first physical contact transfers this face to the shared wall
        // state machine. On the next frame its own climb pulse is still an
        // automatic learning sample, which previously satisfied the adoption
        // gate again: the active hold was released, wall phase was reset to
        // EnteringTrench, and the runner sailed past the lip before the reset
        // route could observe another contact. Ownership of the same physical
        // face is idempotent. Target extents are deliberately not used as
        // identity: a later Stage-2 stair face can lie inside the preceding
        // wide platform while still being a genuinely new climb.
        bool sharedWallOwnsSameFace =
            SharedWallOwnsPhysicalFace(wall.FaceX);
        if (sharedWallOwnsSameFace)
        {
            if (BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextStage2UnmappedWallLogTime)
            {
                nextStage2UnmappedWallLogTime =
                    Time.unscaledTime + 0.25f;
                BonusRunnerLog.Debug(
                    $"Stage2PhysicalWallAlreadyOwned FaceX=" +
                    $"{wall.FaceX:F3}, OwnedFaceX=" +
                    $"{sharedWallOwnedFaceX:F3}, Target=[" +
                    $"{automaticTargetLeft:F3}," +
                    $"{automaticTargetRight:F3}]@" +
                    $"{automaticTargetTop:F3}, Phase=" +
                    $"{wallActionPhase}, Holding=" +
                    $"{jumpController.IsHoldingJump}. The existing shared " +
                    "wall action keeps control; no release or route reset " +
                    "is permitted for the same face.",
                    "Recovery");
            }
            return false;
        }

        // Stage2UnmappedWallIntercept may explain why this face was reached,
        // but it must never become a second input controller. Resolve the
        // physical wall that actually stopped the body and hand it to the
        // shared wall state machine. The old implementation fabricated a
        // thirty-unit-wide downstream support, issued fixed 0.08/0.12 pulses,
        // and could remain active forever after its pulse limit.
        long priorAttemptId = automaticAttemptId;
        string priorPlan = automaticPlanReason;
        bool interruptedPriorHold = jumpController.IsHoldingJump;
        if (interruptedPriorHold)
            jumpController.Release();
        ResetStage2UnmappedWallTraverse();
        if (!TryRebaseUnexpectedWallContact(
                state,
                player,
                wall,
                playerHalfWidth))
        {
            return false;
        }

        nextWallRecoveryTime = 0f;
        wallInputSeparationReleaseFixedStep =
            interruptedPriorHold
                ? JumpPhysicsFeedback.FixedStepSequence
                : -1;
        bool immediatePulseIssued =
            !interruptedPriorHold &&
            TryWallRecoveryJump(state, player);
        BonusRunnerLog.Warning(
            $"Stage2PhysicalWallUnified PriorAttempt={priorAttemptId}, " +
            $"PriorPlan={priorPlan}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), ObservedFaceX=" +
            $"{wall.FaceX:F3}, InterruptedPriorHold=" +
            $"{interruptedPriorHold}, ImmediatePulseIssued=" +
            $"{immediatePulseIssued}, NextState=" +
            $"{wallActionPhase}. FailureDomain=ControlOwnership; " +
            "the observed wall top is now owned by the shared bounded wall " +
            "executor and no Stage-2-specific pulse owner remains active.");
        return true;

#if false
        // Retained only as source-history context during the V1.12 rewrite.
        // This block is intentionally not compiled and can be removed after
        // gameplay validation of the unified physical wall executor.
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        float routeSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                lastReliableHorizontalSpeed,
                sectionCruiseHorizontalSpeed));
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            jumpPhysicsFeedback.CaptureSnapshot(player),
            routeSpeed,
            false);
        float fixedDelta = Mathf.Clamp(
            planningPhysics.FixedDeltaTime,
            0.005f,
            0.05f);
        int fixedStepHoldLimit = Mathf.Max(
            1,
            Mathf.RoundToInt(0.08f / fixedDelta));
        float hold = Mathf.Min(
            planningPhysics.EffectiveHoldCapSeconds,
            fixedStepHoldLimit * fixedDelta);
        fixedStepHoldLimit = Mathf.Max(
            1,
            Mathf.RoundToInt(hold / fixedDelta));

        // The low-corridor classifier correctly authorized entry, but this
        // composite staircase can expose its real blocking face farther ahead
        // than the retained raised bounds. Static-map registration is not
        // required once native collision proves the exact face. Keep a bounded
        // forward ownership window only until any real support is observed.
        float targetTop = Mathf.Max(
            feetY,
            automaticTargetTop);
        BonusBoardSegment physicalContinuation = new(
            wall.FaceX,
            wall.FaceX + 30f,
            targetTop,
            wall.FaceX + 0.75f,
            wall.FaceX + 29.25f,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "Stage2PhysicalWallContinuation",
            WallFaceX: wall.FaceX);
        BonusBoardSegment contactSupport = new(
            state.PlayerPosition.x - playerHalfWidth,
            state.PlayerPosition.x + playerHalfWidth,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "Stage2PhysicalWallContact");
        BonusBoardScanResult contactScan = new(
            true,
            contactSupport,
            true,
            physicalContinuation,
            0f,
            0f,
            targetTop - feetY,
            "Stage2PhysicalWallAuthority");
        float predictedFlight =
            jumpPlanner.PredictRawInputToLandingSeconds(
                hold,
                targetTop - feetY,
                planningPhysics);
        if (predictedFlight <= 0f)
            predictedFlight = 0.70f;
        float predictedLandingX =
            wall.FaceX + playerHalfWidth + 0.50f;
        BonusJumpPlan pulsePlan = new(
            true,
            true,
            hold,
            predictedFlight,
            Mathf.Max(
                0f,
                predictedLandingX - state.PlayerPosition.x),
            state.PlayerPosition.x,
            predictedLandingX,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            "Stage2UnmappedWallClimbPulse1",
            $"PhysicalStage2Face={wall.FaceX:F3}," +
            $"PriorPlan={automaticPlanReason}," +
            $"AbandonedTarget=[{automaticTargetLeft:F3}," +
            $"{automaticTargetRight:F3}]@{automaticTargetTop:F3}",
            BonusManeuverKind.GroundJumpToLanding);

        long priorAttemptId = automaticAttemptId;
        string priorPlan = automaticPlanReason;
        BonusBoardSegment abandonedTarget = BuildAutomaticTargetSegment();
        stage2UnmappedWallTraverseActive = true;
        stage2UnmappedWallTraverseTarget = physicalContinuation;
        stage2UnmappedWallTraversePulses = 0;
        stage2UnmappedWallLastPulsePosition = state.PlayerPosition;
        stage2UnmappedWallStallLastFixedStep = -1;
        stage2UnmappedWallStallFixedSteps = 0;
        nextStage2UnmappedWallLogTime = 0f;

        // A wall DOWN must be a distinct native edge.  If the approach press
        // is still held, retain ownership and let the continuation issue the
        // first pulse after the controller has observed UP plus two stable
        // contact physics steps.  Dropping ownership here was the source of
        // the generic-controller handoff in the retained Stage-2 trace.
        if (jumpController.IsHoldingJump)
        {
            automaticPlanReason =
                "Stage2UnmappedWallContactAwaitRelease";
            automaticManeuver =
                BonusManeuverKind.GroundJumpToLanding;
            passiveWallApproachActive = false;
            wallStallStartedAt = -1f;
            BonusRunnerLog.Warning(
                $"Stage2PhysicalWallOwnershipLatched Attempt=" +
                $"{priorAttemptId}, Plan={priorPlan}, Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), ObservedFaceX=" +
                $"{wall.FaceX:F3}, Target=[{physicalContinuation.Left:F3}," +
                $"{physicalContinuation.Right:F3}]@" +
                $"{physicalContinuation.Top:F3}. Exact native contact now " +
                "owns this staircase; the first separated climb pulse waits " +
                "for the current approach hold to release.");
            return true;
        }

        if (learningSampleActive &&
            learningSource == "Automatic")
        {
            FinishLearningSample(
                state,
                "Stage2PhysicalWallHandoff");
        }
        jumpController.Release();
        jumpController.Press(
            player,
            hold,
            $"Stage2 physical wall correction: face={wall.FaceX:F2}",
            fixedStepHoldLimit);
        MarkAutomaticJumpRequested(
            state,
            pulsePlan,
            physicalContinuation,
            contactScan,
            default,
            planningPhysics,
            routeSpeed);
        if (!automaticPredictionActive)
        {
            ResetStage2UnmappedWallTraverse();
            return false;
        }

        stage2UnmappedWallTraversePulses = 1;
        stage2UnmappedWallLastPulsePosition = state.PlayerPosition;
        stage2UnmappedWallStallLastFixedStep = -1;
        stage2UnmappedWallStallFixedSteps = 0;
        nextStage2UnmappedWallLogTime = 0f;
        BonusRunnerLog.Warning(
            $"Stage2PhysicalWallAdopted PriorAttempt=" +
            $"{priorAttemptId}, PriorPlan={priorPlan}, NewAttempt=" +
            $"{automaticAttemptId}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"ObservedFaceX={wall.FaceX:F3}, FeetY={feetY:F3}, " +
            $"AbandonedTarget=[{abandonedTarget.Left:F3}," +
            $"{abandonedTarget.Right:F3}]@{abandonedTarget.Top:F3}, " +
            $"Hold={hold:F3}s/{fixedStepHoldLimit}Steps. " +
            "FailureDomain=ControlOwnership; exact native collision replaces " +
            "the transient support coordinate and starts the single bounded " +
            "Stage-2 climb chain.");
        return true;
#endif
    }

    private bool HasCommittedExitFaceFlight()
    {
        return wallExitFaceInterceptCommitted ||
               wallExitCollectionFaceInterceptCommitted;
    }

    private bool HasSolvedWallFlightCommitment()
    {
        return wallExitTransferCommitted ||
               wallLandingFlightCommitted ||
               wallMandatoryFaceInterceptCommitted ||
               HasCommittedExitFaceFlight();
    }

    private bool IsAdoptedExitFaceAwaitingClimb()
    {
        return passiveWallApproachActive &&
            automaticPredictionActive &&
            wallExitContactWatchDeadlineFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence <=
                wallExitContactWatchDeadlineFixedStep &&
            (wallActionPhase == WallActionPhase.AwaitingWallContact ||
             wallActionPhase == WallActionPhase.AwaitingNextWallPress) &&
            (string.Equals(
                 automaticPlanReason,
                 "CommittedExitFaceInterceptContact",
                 StringComparison.Ordinal) ||
             string.Equals(
                 automaticPlanReason,
                 "CollectionFaceInterceptContact",
                 StringComparison.Ordinal));
    }

    [HideFromIl2Cpp]
    internal static void ObserveCommittedFaceFixedStep(
        PlayerMovement player)
    {
        AutoBonusRunnerRuntime runtime = activeRuntime;
        if (runtime == null || player == null)
            return;

        if (BonusStageRetryBridge.BlocksTerrainControl)
        {
            runtime.jumpController.Release();
            return;
        }

        try
        {
            runtime.TryHandleCommittedFaceFixedStep(player);
        }
        catch (System.Exception exception)
        {
            if (Time.unscaledTime < runtime.nextErrorLogTime)
                return;

            runtime.nextErrorLogTime = Time.unscaledTime + 5f;
            BonusRunnerLog.Exception(
                "Committed-face fixed-step observer",
                exception);
        }
    }

    [HideFromIl2Cpp]
    private void TryHandleCommittedFaceFixedStep(PlayerMovement player)
    {
        ObserveAutomaticFlightFixedStep(player);
        // A top-and-side corner can report both Grounded and wall contact in
        // the same physics step. The mapped top support is the stronger
        // continuation authority; only a body still below the lip should be
        // promoted to wall ownership.
        if (TryExecuteUrgentNarrowChainFixedStep(player))
            return;
        bool passiveWallTargetLandingHandedOff =
            TryCompletePassiveWallTargetLandingFixedStep(player);
        if (!passiveWallTargetLandingHandedOff &&
            TryPromoteOwnedFlightWallContactFixedStep(player))
        {
            return;
        }
        if (TryArmSharedGroundedWallStallFixedStep(player))
            return;

        TryLatchWatchedExitSupportFixedStep(player);
        if (wallExitSupportFixedStepLatched)
            return;

        // Ordinary routing must run on the same cadence that consumes jump
        // input. LateUpdate remains responsible for lifecycle and diagnostics,
        // but a grounded launch window is now selected and committed from one
        // live physics snapshot before PlayerMovement advances the body.
        if (TryExecuteLandingFirstGroundFixedStep(player))
            return;

        bool committedFaceFlightPending =
            HasCommittedExitFaceFlight() &&
            wallExitContactWatchActive &&
            wallExitTargetActive &&
            wallExitContactWatchDeadlineFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence <=
                wallExitContactWatchDeadlineFixedStep;
        bool adoptedFaceAwaitingClimb =
            IsAdoptedExitFaceAwaitingClimb();
        bool mandatoryFaceControlPending =
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted;
        bool fixedStepObjectiveDescentPending =
            attachedObjectiveDescentActive;
        bool genericWallControlPending =
            passiveWallApproachActive ||
            ground5HighestPillarSinkActive ||
            automaticManeuver == BonusManeuverKind.WallJumpClimb &&
                (wallRecoveryContactLatched ||
                 wallActionPhase == WallActionPhase.WallJumpPhaseOne ||
                 wallActionPhase == WallActionPhase.WallJumpPhaseTwo ||
                 wallActionPhase == WallActionPhase.AwaitingNextWallPress ||
                 wallActionPhase == WallActionPhase.AwaitingWallContact ||
                 wallActionPhase == WallActionPhase.ExitFlight);
        bool automaticLearningOwner =
            automaticPredictionActive &&
            learningSampleActive &&
            string.Equals(
                learningSource,
                "Automatic",
                StringComparison.Ordinal);
        // Passive wall approach and attached objective descent intentionally
        // exist between learning samples. They remain automatic route owners
        // even after FinishLearningSample clears prediction/sample flags.
        bool persistentWallRouteOwner =
            fixedStepObjectiveDescentPending ||
            passiveWallApproachActive ||
            ground5HighestPillarSinkActive;
        if (!automationEnabled ||
            (!committedFaceFlightPending &&
             !adoptedFaceAwaitingClimb &&
             !mandatoryFaceControlPending &&
             !fixedStepObjectiveDescentPending &&
             !genericWallControlPending) ||
            (!automaticLearningOwner && !persistentWallRouteOwner) ||
            !latestState.IsBonusStage ||
            !latestState.IsSupportedBonusMap)
        {
            return;
        }

        int playerInstanceId = player.GetInstanceID();
        if (latestState.PlayerInstanceId != 0 &&
            latestState.PlayerInstanceId != playerInstanceId)
        {
            return;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        BonusStageState fixedState = GetRoutingState(latestState) with
        {
            PlayerInstanceId = playerInstanceId,
            PlayerPosition = player.transform.position,
            PlayerVelocity = body != null ? body.velocity : Vector2.zero,
            IsGrounded = player.IsGrounded(),
            HasPlayer = true
        };
        if (fixedStepObjectiveDescentPending)
        {
            TryWallRecoveryJump(fixedState, player);
            return;
        }
        if (mandatoryFaceControlPending)
        {
            if (!TryValidateMandatoryFaceRouteIdentity(
                    fixedState,
                    player,
                    out string mandatoryIdentityReason))
            {
                FailMandatoryFacePlan(
                    fixedState,
                    "FixedStepRouteIdentityChanged",
                    mandatoryIdentityReason);
                return;
            }

            if (wallMandatoryFaceSetupActive)
            {
                TryWallRecoveryJump(fixedState, player);
                return;
            }
            if (wallActionPhase != WallActionPhase.ExitFlight)
                MonitorCommittedJump(fixedState, player);
            if (wallMandatoryFaceInterceptCommitted &&
                wallActionPhase == WallActionPhase.ExitFlight)
            {
                HandleMandatoryFaceContactWatch(fixedState, player);
            }
            return;
        }
        if (adoptedFaceAwaitingClimb)
        {
            TryWallRecoveryJump(fixedState, player);
            return;
        }
        if (genericWallControlPending && !committedFaceFlightPending)
        {
            // Release classification and the next separated wall DOWN must be
            // evaluated on the same physics cadence as the native movement.
            // Waiting for LateUpdate here loses multi-pulse climbs whenever
            // several FixedUpdates run under one background render frame.
            TryWallRecoveryJump(fixedState, player);
            return;
        }

        bool collectionFaceRoute =
            wallExitCollectionFaceInterceptCommitted;
        long contactFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
        if (!TryAdoptCommittedExitFaceContact(fixedState, player))
            return;

        BonusRunnerLog.Debug(
            $"CommittedExitFaceFixedStepHandoff FixedStep=" +
            $"{contactFixedStep}, Player={playerInstanceId}, Mode=" +
            $"{(collectionFaceRoute ? "CollectionFace" : "FaceOrTop")}, " +
            $"Position=({fixedState.PlayerPosition.x:F3}," +
            $"{fixedState.PlayerPosition.y:F3}), Velocity=" +
            $"({fixedState.PlayerVelocity.x:F3}," +
            $"{fixedState.PlayerVelocity.y:F3}). The mapped collision was " +
            "consumed before PlayerMovement.FixedUpdate so background " +
            "render throttling cannot skip the attached input window.",
            "Recovery");
        TryWallRecoveryJump(fixedState, player);
    }

    /// <summary>
    /// Completes a passive wall route whose learning sample has already
    /// closed when real physics proves that the player is standing on that
    /// route's target top. This runs before the wall-owner gate so the ground
    /// planner can consume the same fixed-step snapshot after the handoff.
    /// </summary>
    [HideFromIl2Cpp]
    private bool TryCompletePassiveWallTargetLandingFixedStep(
        PlayerMovement player)
    {
        BonusStageState routingState = GetRoutingState(latestState);
        bool passiveTargetOnly =
            automationEnabled &&
            AutomaticJumpingEnabled &&
            player != null &&
            routingState.IsBonusStage &&
            routingState.IsSupportedBonusMap &&
            routingState.UsesStage2LiveRouting &&
            routingState.SectionIndex == 1 &&
            !routingState.SpiritBoostEnabled &&
            (routingState.IsActiveGameplay ||
             IsSuccessfulCompletionTraversal(routingState)) &&
            !BonusStageRetryBridge.BlocksTerrainControl &&
            !terrainContinuationEpochBlocked &&
            !pitDescentGuardActive &&
            !rewardTargetDetector.IsLatched &&
            Time.unscaledTime - lastManualInputTime >= 0.40f &&
            passiveWallApproachActive &&
            wallActionPhase == WallActionPhase.EnteringTrench &&
            automaticPredictionActive &&
            !learningSampleActive &&
            automaticManeuver ==
                BonusManeuverKind.EnterTrenchThenWallJump &&
            automaticTargetRight > automaticTargetLeft + 0.05f &&
            string.Equals(
                automaticTargetMapPieceName,
                "Stage2 Ground 4",
                System.StringComparison.OrdinalIgnoreCase) &&
            automaticTargetStaticSurfaceIndex == 4 &&
            !jumpController.IsHoldingJump &&
            wallRecoveryAttempts == 0 &&
            !wallRecoveryContactLatched &&
            !wallRecoverySawUpwardMotion &&
            !wallExitTargetActive &&
            !wallExitContactWatchActive &&
            !wallExitTransferCommitted &&
            !wallLandingFlightCommitted &&
            !wallTopLandingSequenceCommitted &&
            !wallExitFaceInterceptCommitted &&
            !wallExitCollectionFaceInterceptCommitted &&
            !wallMandatoryFaceSetupActive &&
            !wallMandatoryFaceInterceptCommitted &&
            !attachedObjectiveDescentActive &&
            !ground5HighestPillarSinkActive &&
            !wallResidualRiseWaitActive;
        if (!passiveTargetOnly)
        {
            ResetPassiveWallTargetSupportEvidence();
            return false;
        }

        int playerInstanceId = player.GetInstanceID();
        if (routingState.PlayerInstanceId != 0 &&
            routingState.PlayerInstanceId != playerInstanceId)
        {
            ResetPassiveWallTargetSupportEvidence();
            return false;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        Vector2 velocity = body != null
            ? body.velocity
            : routingState.PlayerVelocity;
        if (!player.IsGrounded() ||
            Mathf.Abs(velocity.y) > 2.50f)
        {
            ResetPassiveWallTargetSupportEvidence();
            return false;
        }

        Vector3 position = player.transform.position;
        BonusBoardScanResult supportScan;
        try
        {
            supportScan = platformScanner.Scan(
                position,
                player,
                Mathf.Max(
                    1f,
                    Mathf.Max(
                        Mathf.Abs(velocity.x),
                        lastReliableHorizontalSpeed)));
        }
        catch
        {
            ResetPassiveWallTargetSupportEvidence();
            return false;
        }

        BonusBoardSegment target = BuildAutomaticTargetSegment();
        Bounds playerBounds = player.playerCollider != null
            ? player.playerCollider.bounds
            : new Bounds(
                position,
                new Vector3(1.20f, 1.20f, 0f));
        float feetY = playerBounds.min.y;
        bool bodyOverlapsTarget =
            playerBounds.max.x >= target.Left + 0.05f &&
            playerBounds.min.x <= target.Right - 0.05f;
        bool supportMatchesTarget =
            supportScan.IsValid &&
            bodyOverlapsTarget &&
            Mathf.Abs(feetY - supportScan.Current.Top) <= 0.35f &&
            Mathf.Abs(supportScan.Current.Top - target.Top) <= 0.35f &&
            supportScan.Current.Right > target.Left + 0.05f &&
            supportScan.Current.Left < target.Right - 0.05f &&
            StaticOrColliderIdentityMatches(
                supportScan.Current,
                target);
        if (!supportMatchesTarget)
        {
            ResetPassiveWallTargetSupportEvidence();
            return false;
        }

        BonusWallContact liveWall = wallDetector.Detect(
            player,
            Mathf.Max(
                1f,
                Mathf.Max(
                    Mathf.Abs(velocity.x),
                    lastReliableHorizontalSpeed)));
        if (liveWall.IsDetected && liveWall.IsTouching)
        {
            ResetPassiveWallTargetSupportEvidence();
            return false;
        }

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        bool samePlayer =
            passiveWallTargetSupportPlayerInstanceId ==
                playerInstanceId;
        bool sameTarget =
            passiveWallTargetSupportTarget.Width > 0.05f &&
            Mathf.Abs(
                passiveWallTargetSupportTarget.Left -
                target.Left) <= 0.12f &&
            Mathf.Abs(
                passiveWallTargetSupportTarget.Right -
                target.Right) <= 0.12f &&
            Mathf.Abs(
                passiveWallTargetSupportTarget.Top -
                target.Top) <= 0.35f &&
            StaticOrColliderIdentityMatches(
                passiveWallTargetSupportTarget,
                target);
        bool sameSupport =
            passiveWallTargetSupportSurface.Width > 0.05f &&
            Mathf.Abs(
                passiveWallTargetSupportSurface.Top -
                supportScan.Current.Top) <= 0.35f &&
            StaticOrColliderIdentityMatches(
                passiveWallTargetSupportSurface,
                supportScan.Current);
        bool consecutiveFixedStep =
            passiveWallTargetSupportLastFixedStep < 0 ||
            fixedStep ==
                passiveWallTargetSupportLastFixedStep + 1;
        if (!samePlayer ||
            !sameTarget ||
            !sameSupport ||
            !consecutiveFixedStep)
        {
            ResetPassiveWallTargetSupportEvidence();
            passiveWallTargetSupportPlayerInstanceId =
                playerInstanceId;
            passiveWallTargetSupportTarget = target;
            passiveWallTargetSupportSurface =
                supportScan.Current;
        }

        if (fixedStep != passiveWallTargetSupportLastFixedStep)
        {
            passiveWallTargetSupportLastFixedStep = fixedStep;
            passiveWallTargetSupportFixedSteps++;
        }
        if (passiveWallTargetSupportFixedSteps <
            PassiveWallTargetLandingConfirmationFixedSteps)
        {
            return false;
        }

        int confirmedFixedSteps =
            passiveWallTargetSupportFixedSteps;
        long routeId = activeRouteDecisionId;
        string completedPlan = automaticPlanReason;
        WallActionPhase completedPhase = wallActionPhase;
        BonusBoardSegment completedTarget = target;
        BonusBoardSegment actualSupport = supportScan.Current;
        ResetAutomaticControlState();
        nextAutomaticAttemptTime = 0f;
        BonusRunnerLog.Warning(
            $"PassiveWallTargetLandingHandoff RouteId={routeId}, " +
            $"Plan={completedPlan}, PriorPhase={completedPhase}, " +
            $"Map={routingState.MapName}, Section=" +
            $"{routingState.SectionIndex}, SpiritBoost=" +
            $"{routingState.SpiritBoostEnabled}, " +
            $"FixedStep={fixedStep}, StableFixedSteps=" +
            $"{confirmedFixedSteps}, Position=({position.x:F3}," +
            $"{position.y:F3}), FeetY={feetY:F3}, Velocity=" +
            $"({velocity.x:F3},{velocity.y:F3}), Support=" +
            $"[{actualSupport.Left:F3},{actualSupport.Right:F3}]@" +
            $"{actualSupport.Top:F3}/" +
            $"{actualSupport.MapPieceName}:S" +
            $"{actualSupport.StaticSurfaceIndex}, Target=" +
            $"[{completedTarget.Left:F3}," +
            $"{completedTarget.Right:F3}]@" +
            $"{completedTarget.Top:F3}/" +
            $"{completedTarget.MapPieceName}:S" +
            $"{completedTarget.StaticSurfaceIndex}. " +
            "FailureDomain=ControlOwnership; the completed passive wall " +
            "route released its stale prediction before the ground planner " +
            "evaluates this same physics step.");
        return true;
    }

    [HideFromIl2Cpp]
    private bool TryPromoteOwnedFlightWallContactFixedStep(
        PlayerMovement player)
    {
        BonusStageState routingState = GetRoutingState(latestState);
        if (!automationEnabled ||
            !AutomaticJumpingEnabled ||
            player == null ||
            BonusStageRetryBridge.BlocksTerrainControl ||
            terrainContinuationEpochBlocked ||
            pitDescentGuardActive ||
            rewardTargetDetector.IsLatched ||
            Time.unscaledTime - lastManualInputTime < 0.40f ||
            !routingState.IsBonusStage ||
            !routingState.IsSupportedBonusMap ||
            !routingState.IsActiveGameplay)
        {
            return false;
        }

        int playerInstanceId = player.GetInstanceID();
        if (routingState.PlayerInstanceId != 0 &&
            routingState.PlayerInstanceId != playerInstanceId)
        {
            return false;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        BonusStageState fixedState = routingState with
        {
            PlayerInstanceId = playerInstanceId,
            PlayerPosition = player.transform.position,
            PlayerVelocity = body != null ? body.velocity : Vector2.zero,
            IsGrounded = player.IsGrounded(),
            HasPlayer = true
        };
        return TryPromoteLateSectionFlightWallContact(fixedState, player);
    }

    [HideFromIl2Cpp]
    private void ObserveAutomaticFlightFixedStep(PlayerMovement player)
    {
        if (!automationEnabled || player == null)
            return;

        bool grounded = player.IsGrounded();
        if (!grounded &&
            secondStagePreviewActive &&
            string.Equals(
                secondStageSource,
                "IntentionalDrop",
                StringComparison.Ordinal))
        {
            secondStageObservedAirborne = true;
        }

        if (
            !learningSampleActive ||
            !string.Equals(
                learningSource,
                "Automatic",
                StringComparison.Ordinal) ||
            !automaticPredictionActive)
        {
            return;
        }

        int playerInstanceId = player.GetInstanceID();
        if (latestState.PlayerInstanceId != 0 &&
            latestState.PlayerInstanceId != playerInstanceId)
        {
            return;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        Vector2 velocity = body != null ? body.velocity : Vector2.zero;
        Vector3 position = player.transform.position;
        if (!grounded)
            secondStageObservedAirborne = true;

        bool validUpwardTakeoff =
            !learningTookOff &&
            !grounded &&
            velocity.y > 5f &&
            position.y >= learningTriggerPosition.y - 0.75f;
        if (validUpwardTakeoff)
        {
            learningTookOff = true;
            learningTakeoffTime = Time.unscaledTime;
            learningTakeoffPosition = position;
            learningTakeoffVelocity = velocity;
            learningPreviousVelocityY = velocity.y;
            if (position.y > learningMaximumY)
            {
                learningMaximumY = position.y;
                learningApexPosition = position;
            }
            jumpPhysicsFeedback.ObserveTakeoff(
                learningInputDownTime,
                learningTakeoffVelocity);
            BonusRunnerLog.Debug(
                $"JumpAttemptTakeoff AttemptId={learningSampleId}, " +
                $"Source={learningSource}, ObservationMode=" +
                $"FixedStepObserver, FixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, " +
                $"InputToTakeoff=" +
                $"{learningTakeoffTime - learningInputDownTime:F3}s, " +
                $"Position=({position.x:F3},{position.y:F3}), " +
                $"Velocity=({velocity.x:F3},{velocity.y:F3}). " +
                "Takeoff was captured even when no render Update separated " +
                "the powered physics steps.",
                "Attempt");
        }

        if (learningTookOff && !learningFirstApexCaptured)
        {
            if (position.y > learningMaximumY)
            {
                learningMaximumY = position.y;
                learningApexPosition = position;
            }
            if (learningPreviousVelocityY > 0f &&
                velocity.y <= 0f &&
                !grounded)
            {
                learningFirstApexCaptured = true;
                BonusRunnerLog.Debug(
                    $"JumpAttemptApex AttemptId={learningSampleId}, " +
                    $"Source={learningSource}, ObservationMode=" +
                    $"FixedStepObserver, FixedStep=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}, X=" +
                    $"{learningApexPosition.x:F3}, Y=" +
                    $"{learningApexPosition.y:F3}, CrossingVY=" +
                    $"{velocity.y:F3}.",
                    "Attempt");
            }
            learningPreviousVelocityY = velocity.y;
        }

        learningLastObservedPosition = position;
        learningLastObservedAt = Time.unscaledTime;
        learningLastObservedFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
    }

    /// <summary>
    /// Receding-horizon ground controller. It rebuilds perception, speed,
    /// physics, objectives, target selection and the executable action from a
    /// single pre-PlayerMovement FixedUpdate snapshot. Cached previews remain
    /// evidence only; they never authorize this DOWN.
    /// </summary>
    [HideFromIl2Cpp]
    private bool TryExecuteLandingFirstGroundFixedStep(
        PlayerMovement player)
    {
        BonusStageState routingState = GetRoutingState(latestState);
        bool terrainControlActive =
            routingState.IsActiveGameplay ||
            IsSuccessfulCompletionTraversal(routingState);
        bool wallOwnerActive =
            passiveWallApproachActive ||
            wallExitContactWatchActive ||
            attachedObjectiveDescentActive ||
            ground5HighestPillarSinkActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            wallActionPhase != WallActionPhase.None;
        if (!automationEnabled ||
            !AutomaticJumpingEnabled ||
            player == null ||
            !terrainControlActive ||
            !routingState.IsBonusStage ||
            !routingState.IsSupportedBonusMap ||
            BonusStageRetryBridge.BlocksTerrainControl ||
            terrainContinuationEpochBlocked ||
            pitDescentGuardActive ||
            rewardTargetDetector.IsLatched ||
            Time.unscaledTime - lastManualInputTime < 0.40f ||
            Time.unscaledTime < nextAutomaticAttemptTime ||
            wallOwnerActive ||
            learningSampleActive ||
            automaticPredictionActive ||
            !automaticJumpArmed ||
            jumpController.IsHoldingJump)
        {
            return false;
        }

        int playerInstanceId = player.GetInstanceID();
        if (routingState.PlayerInstanceId != 0 &&
            routingState.PlayerInstanceId != playerInstanceId)
        {
            return false;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        Vector3 livePosition = player.transform.position;
        Vector2 liveVelocity = body != null
            ? body.velocity
            : Vector2.zero;
        if (!player.IsGrounded() || liveVelocity.y > 2.50f)
            return false;

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (fixedStep < speedPlanningResumeFixedStep ||
            IsSuccessfulCompletionTraversal(routingState) &&
            fixedStep < completionDashPlanningResumeFixedStep)
        {
            return false;
        }

        float observedLiveSpeed = Mathf.Abs(liveVelocity.x);
        // Native retry can resume Bonus Stage 2 on the final narrow support
        // with Rigidbody VX exactly zero. The live topology can still prove
        // the bounded Stage2UnmappedWallIntercept from the retained free-run
        // speed, but V0.79 returned here before that proof was rebuilt. Keep
        // zero-VX forbidden for every other route; authorization is checked
        // again against the exact selected plan below.
        bool stationaryStage2InterceptCandidate =
            observedLiveSpeed <= 1f &&
            routingState.IsActiveGameplay &&
            routingState.UsesStage2LiveRouting &&
            routingState.SectionIndex == 1 &&
            lastReliableHorizontalSpeed > 1f &&
            lastReliableHorizontalSpeed < 80f;
        if (observedLiveSpeed >= 80f ||
            observedLiveSpeed <= 1f &&
            !stationaryStage2InterceptCandidate)
            return false;
        float liveSpeed = stationaryStage2InterceptCandidate
            ? lastReliableHorizontalSpeed
            : observedLiveSpeed;

        long spiritPlanningStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        BonusBoardScanResult scan;
        try
        {
            scan = platformScanner.Scan(
                livePosition,
                player,
                liveSpeed);
        }
        catch
        {
            return false;
        }
        if (!scan.IsValid)
            return false;
        long platformScanCompleted =
            System.Diagnostics.Stopwatch.GetTimestamp();

        BonusHazard hazard = hazardScanner.FindNearest(livePosition);
        long hazardScanCompleted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            routingState.SectionIndex,
            observedPhysics,
            liveSpeed,
            terrainControlActive,
            routingState.SpiritBoostEnabled
                ? sectionCruiseHorizontalSpeed
                : 0f);
        long physicsModelCompleted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        float lookAhead = Mathf.Clamp(
            30f + Mathf.Max(0f, liveSpeed - 18f) * 1.4f,
            30f,
            80f);
        Vector2[] objectives =
            routingState.HasSphereProgress &&
            routingState.RemainingRequiredSpheres > 0
                ? BonusStageInspector.GetActiveSpherePositions(
                    livePosition.x - 1.0f,
                    livePosition.x + SectionObjectiveHorizon)
                : Array.Empty<Vector2>();
        long objectiveScanCompleted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        SpiritBoostRouteContext spiritBoost =
            CaptureRouteSpeedContext(
                routingState,
                player,
                livePosition.x - 2.0f,
                livePosition.x + lookAhead,
                sectionCruiseHorizontalSpeed > 1f
                    ? sectionCruiseHorizontalSpeed
                    : 0f);
        long spiritContextCompleted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        Vector2 planningVelocity = new(liveSpeed, liveVelocity.y);
        bool fixedStepCrossNetGainRouteRequested =
            stage1CrossSpherePriorityLatched &&
            !routingState.SpiritBoostEnabled &&
            routingState.SectionIndex == 2 &&
            string.Equals(
                routingState.MapName,
                "map_bonus_stage_1",
                StringComparison.OrdinalIgnoreCase) &&
            scan.HasNext &&
            Stage1CrossSphereTargetMatchesLatchedTarget(
                scan.Next,
                stage1CrossSpherePriorityTarget);
        BonusBoardScanResult cachedScan = default;
        BonusJumpPlan cachedPlan = default;
        string cacheEvidence = string.Empty;
        bool reusedSpiritWaitPlan =
            !fixedStepCrossNetGainRouteRequested &&
            TryUseSpiritWaitPlanCache(
                routingState,
                scan,
                livePosition,
                liveSpeed,
                planningPhysics,
                hazard,
                objectives,
                spiritBoost,
                out cachedScan,
                out cachedPlan,
                out cacheEvidence);
        if (fixedStepCrossNetGainRouteRequested)
        {
            cachedScan = default;
            cachedPlan = default;
            cacheEvidence =
                "Stage1CrossObjectiveNetGainRequiresLiveProof";
        }
        string selection;
        BonusJumpPlan plan;
        if (reusedSpiritWaitPlan)
        {
            scan = cachedScan;
            plan = cachedPlan;
            selection = cacheEvidence;
        }
        else
        {
            scan = jumpPlanner.SelectReachableRoute(
                scan,
                livePosition,
                planningVelocity,
                planningPhysics,
                hazard,
                objectives,
                sectionIndex: routingState.SectionIndex,
                preferSphereCoverage: objectives.Length > 0,
                allowRecoverableLowerFaceCatch:
                    !routingState.UsesStage3AuthoredRouting &&
                    routingState.SectionIndex >= 2,
                useFixedStepAlignedHolds: true,
                spiritBoost: spiritBoost,
                selectionContext: "FixedStepLandingFirst",
                selection: out selection,
                selectedPlan: out BonusJumpPlan selectorPlan,
                selectedPlanAvailable: out bool selectorPlanAvailable,
                useStage2LiveTopologyProfile:
                    routingState.UsesStage2LiveRouting,
                preferVerifiedObjectiveNetGain:
                    fixedStepCrossNetGainRouteRequested);
            plan = selectorPlanAvailable
                ? selectorPlan
                : jumpPlanner.Plan(
                    scan,
                    livePosition,
                    planningVelocity,
                    planningPhysics,
                    hazard,
                    objectives,
                    sectionIndex: routingState.SectionIndex,
                    preferSphereCoverage: objectives.Length > 0,
                    allowRecoverableLowerFaceCatch:
                        !routingState.UsesStage3AuthoredRouting &&
                        routingState.SectionIndex >= 2,
                    useFixedStepAlignedHolds: true,
                    spiritBoost: spiritBoost,
                    useStage2LiveTopologyProfile:
                        routingState.UsesStage2LiveRouting);
        }

        bool fixedStepCrossSpherePriority =
            fixedStepCrossNetGainRouteRequested &&
            scan.HasNext &&
            Stage1CrossSphereTargetMatchesLatchedTarget(
                scan.Next,
                stage1CrossSpherePriorityTarget);
        bool fixedStepCrossNetGainRouteSelected =
            fixedStepCrossNetGainRouteRequested &&
            scan.HasNext &&
            !Stage1CrossSphereTargetMatchesLatchedTarget(
                scan.Next,
                stage1CrossSpherePriorityTarget);
        if (fixedStepCrossSpherePriority)
        {
            BonusJumpPlan fixedStepCrossSpherePlan = jumpPlanner.Plan(
                scan,
                livePosition,
                planningVelocity,
                planningPhysics,
                hazard,
                objectives,
                sectionIndex: routingState.SectionIndex,
                preferSphereCoverage: true,
                allowRecoverableLowerFaceCatch: false,
                useFixedStepAlignedHolds: true,
                spiritBoost: spiritBoost,
                routeTargetIsAuthoritative: true,
                useStage2LiveTopologyProfile:
                    routingState.UsesStage2LiveRouting,
                minimumSphereHits: Stage1CrossSphereMinimumHits);
            bool fixedStepPriorityValid =
                fixedStepCrossSpherePlan.IsValid &&
                fixedStepCrossSpherePlan.ExpectedSphereHits >=
                    Stage1CrossSphereMinimumHits &&
                fixedStepCrossSpherePlan.Maneuver ==
                    BonusManeuverKind.GroundJumpToLanding;
            if (fixedStepPriorityValid)
            {
                plan = fixedStepCrossSpherePlan;
                selection =
                    "LatchedStage1CrossSphereSameTargetPriority;" +
                    selection;
            }
            else if (BonusRunnerLog.IsDebugMode &&
                     Time.unscaledTime >= nextDynamicPlanLogTime)
            {
                nextDynamicPlanLogTime = Time.unscaledTime + 0.10f;
                BonusRunnerLog.Debug(
                    $"FixedStepCrossSpherePriorityFallback FixedStep=" +
                    $"{fixedStep}, CenterX=" +
                    $"{stage1CrossSpherePriorityCenter.x:F3}, PositionX=" +
                    $"{livePosition.x:F3}, OriginalPlan={plan.Reason}/" +
                    $"{plan.Maneuver}/Hits={plan.ExpectedSphereHits}, " +
                    $"PriorityPlan={fixedStepCrossSpherePlan.Reason}/" +
                    $"{fixedStepCrossSpherePlan.Maneuver}/Hits=" +
                    $"{fixedStepCrossSpherePlan.ExpectedSphereHits}. " +
                    "The original survival plan remains authoritative " +
                    "because no same-target pickup command is executable " +
                    "from this physics snapshot.",
                    "Routing");
            }
        }

        if (!reusedSpiritWaitPlan &&
            plan.Maneuver == BonusManeuverKind.SphereCollectionJump)
        {
            BonusJumpPlan terrainDeadlinePlan = jumpPlanner.Plan(
                scan,
                livePosition,
                planningVelocity,
                planningPhysics,
                hazard,
                Array.Empty<Vector2>(),
                sectionIndex: routingState.SectionIndex,
                preferSphereCoverage: false,
                allowRecoverableLowerFaceCatch:
                    !routingState.UsesStage3AuthoredRouting &&
                    routingState.SectionIndex >= 2,
                useFixedStepAlignedHolds: true,
                spiritBoost: spiritBoost,
                routeTargetIsAuthoritative: true);
            if (!SphereOpportunityPreservesTerrainDeadline(
                    scan,
                    terrainDeadlinePlan,
                    plan,
                    liveSpeed,
                    out string deadlineCheck))
            {
                plan = terrainDeadlinePlan with
                {
                    CandidateSummary =
                        terrainDeadlinePlan.CandidateSummary +
                        $" | FixedStepSphereRejected[{deadlineCheck}]"
                };
            }
        }

        if (!reusedSpiritWaitPlan)
        {
            TryStoreSpiritWaitPlanCache(
                routingState,
                scan,
                plan,
                livePosition,
                liveSpeed,
                planningPhysics,
                hazard,
                objectives,
                spiritBoost,
                fixedStep);
        }
        bool stationaryStage2InterceptAuthorized =
            stationaryStage2InterceptCandidate &&
            plan.IsValid &&
            plan.ShouldJumpNow &&
            plan.Maneuver == BonusManeuverKind.GroundJumpToLanding &&
            plan.Reason.StartsWith(
                "Stage2UnmappedWallIntercept",
                StringComparison.Ordinal) &&
            scan.HasNext &&
            scan.Current.Right - livePosition.x >= -0.20f &&
            scan.Current.Right - livePosition.x <= 6.25f &&
            scan.Next.Width >= 3.00f &&
            scan.Gap >= plan.HorizontalTravel + 2.00f;
        if (stationaryStage2InterceptCandidate &&
            !stationaryStage2InterceptAuthorized)
        {
            if (Time.unscaledTime >= nextSpeedPlanningBarrierLogTime)
            {
                nextSpeedPlanningBarrierLogTime =
                    Time.unscaledTime + 0.50f;
                BonusRunnerLog.Debug(
                    $"StationaryStage2InterceptRejected FixedStep=" +
                    $"{fixedStep}, Position=({livePosition.x:F3}," +
                    $"{livePosition.y:F3}), ObservedVX=" +
                    $"{observedLiveSpeed:F3}, ReliableVX=" +
                    $"{lastReliableHorizontalSpeed:F3}, Plan=" +
                    $"{plan.Reason}/{plan.Maneuver}, Valid=" +
                    $"{plan.IsValid}, ShouldJump={plan.ShouldJumpNow}, " +
                    $"CurrentWidth={scan.Current.Width:F3}, NextWidth=" +
                    $"{(scan.HasNext ? scan.Next.Width : 0f):F3}, Gap=" +
                    $"{scan.Gap:F3}, PlannedTravel=" +
                    $"{plan.HorizontalTravel:F3}. Zero-VX remains blocked " +
                    "because the exact bounded Stage-2 wall-intercept proof " +
                    "did not survive the fixed-step rebuild.",
                    "Physics");
            }
            return false;
        }
        // CaptureFixedStep runs in the PlayerMovement.FixedUpdate postfix and
        // advances the public sequence after this prefix returns. A moving
        // Stage-2 state has now completed the same authoritative perception,
        // route selection and action proof that LateUpdate would repeat, so
        // suppress only its post-step duplicate. A zero-VX Section-1
        // candidate that failed the exact intercept proof returned above
        // without setting this marker: the render controller must still
        // accumulate its two-frame body-bounds wall-contact fallback.
        if (routingState.UsesStage2LiveRouting)
            lastRenderGroundPlanningFixedStep = fixedStep + 1;
        long routePlanningCompleted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        LogGroundPlanningPhaseCost(
            routingState,
            livePosition,
            liveSpeed,
            plan,
            reusedSpiritWaitPlan,
            spiritPlanningStarted,
            platformScanCompleted,
            hazardScanCompleted,
            physicsModelCompleted,
            objectiveScanCompleted,
            spiritContextCompleted,
            routePlanningCompleted,
            fixedStep);
        LogSpiritGroundPlanningCost(
            routingState,
            livePosition,
            liveSpeed,
            plan,
            reusedSpiritWaitPlan,
            cacheEvidence,
            spiritPlanningStarted,
            fixedStep);

        bool sameSurfaceHazard =
            hazard.IsValid &&
            hazard.Left >= scan.Current.Left &&
            hazard.Right <= scan.Current.Right;
        if (!plan.IsValid && sameSurfaceHazard)
        {
            plan = jumpPlanner.PlanSameSurfaceHazard(
                scan,
                hazard,
                livePosition,
                planningVelocity,
                planningPhysics,
                spiritBoost);
        }

        BonusStageState fixedState = routingState with
        {
            PlayerInstanceId = playerInstanceId,
            PlayerPosition = livePosition,
            PlayerVelocity = liveVelocity,
            IsGrounded = true,
            HasPlayer = true
        };
        if (stationaryStage2InterceptAuthorized)
        {
            BonusRunnerLog.Debug(
                $"StationaryStage2UnmappedWallInterceptAuthorized " +
                $"FixedStep={fixedStep}, Position=({livePosition.x:F3}," +
                $"{livePosition.y:F3}), ObservedVX=" +
                $"{observedLiveSpeed:F3}, ReliableVX={liveSpeed:F3}, " +
                $"Source=[{scan.Current.Left:F3}," +
                $"{scan.Current.Right:F3}]@{scan.Current.Top:F3}, " +
                $"Downstream=[{scan.Next.Left:F3}," +
                $"{scan.Next.Right:F3}]@{scan.Next.Top:F3}, Gap=" +
                $"{scan.Gap:F3}, PlannedEntryTravel=" +
                $"{plan.HorizontalTravel:F3}, Hold=" +
                $"{plan.HoldSeconds:F3}s. Native retry supplied zero VX, " +
                "but the exact Stage-2 topology and retained free-run speed " +
                "authorize this one fixed-step wall-entry DOWN.",
                "Recovery");
        }
        if (!plan.IsValid &&
            string.Equals(
                plan.Reason,
                "IntentionalDrop",
                StringComparison.Ordinal) &&
            scan.HasNext)
        {
            automaticTrajectoryCompatible = true;
            secondStageObservedAirborne = false;
            PrepareSecondStagePreview(
                fixedState,
                scan.Next,
                plan.PredictedLandingX,
                planningPhysics,
                plan.FutureSpeedTransitionExpected
                    ? "FixedStepFutureSpeedDrop"
                    : "FixedStepIntentionalDrop");
            BonusRunnerLog.Debug(
                $"FixedStepRouteCommitted FixedStep={fixedStep}, " +
                $"Action=COAST, Position=({livePosition.x:F3}," +
                $"{livePosition.y:F3}), Velocity=({liveVelocity.x:F3}," +
                $"{liveVelocity.y:F3}), Source=[{scan.Current.Left:F3}," +
                $"{scan.Current.Right:F3}]@{scan.Current.Top:F3}, Target=" +
                $"[{scan.Next.Left:F3},{scan.Next.Right:F3}]@" +
                $"{scan.Next.Top:F3}, TargetSafe=[{scan.Next.SafeLeft:F3}," +
                $"{scan.Next.SafeRight:F3}], PredictedLanding=" +
                $"{plan.PredictedLandingX:F3}, TravelEnvelope=" +
                $"[{plan.MinimumHorizontalTravel:F3}," +
                $"{plan.MaximumHorizontalTravel:F3}], FutureSpeedTransition=" +
                $"{plan.FutureSpeedTransitionExpected}, BaseVX=" +
                $"{planningPhysics.BaseHorizontalSpeed:F3}, Selection=" +
                $"{selection}, Plan={plan.Reason}.",
                "Routing");
            return false;
        }

        if (!plan.IsValid)
            return false;

        BonusBoardSegment target =
            IsStage2LowCorridorWallCatch(plan, scan)
                ? scan.Intermediate
                : plan.Maneuver == BonusManeuverKind.SphereCollectionJump ||
                  plan.Maneuver == BonusManeuverKind.HazardClearanceJump
                ? scan.Current
                : scan.Next;
        float stage2Section1OwnershipLead =
            routingState.UsesStage2LiveRouting &&
            routingState.SectionIndex == 1
                ? Mathf.Max(
                    0f,
                    liveSpeed * Mathf.Clamp(
                        planningPhysics.FixedDeltaTime,
                        0.005f,
                        0.05f))
                : 0f;
        if (plan.Maneuver ==
                BonusManeuverKind.EnterTrenchThenWallJump &&
            scan.HasNext &&
            livePosition.x + stage2Section1OwnershipLead >=
                plan.PlannedLaunchX - 0.16f)
        {
            BeginPassiveWallApproach(
                fixedState,
                plan,
                scan,
                scan.Next,
                hazard,
                planningPhysics);
            BonusRunnerLog.Debug(
                $"FixedStepRouteCommitted FixedStep={fixedStep}, " +
                $"Action=PassiveWallOwnership, X={livePosition.x:F3}, " +
                $"VX={liveSpeed:F3}, Selection={selection}, " +
                $"Plan={plan.Reason}, FixedStepOwnershipLead=" +
                $"{stage2Section1OwnershipLead:F3}.",
                "Routing");
            return true;
        }

        if (!plan.ShouldJumpNow || target.Width <= 0.05f)
            return false;

        float committedMinimumTravel = plan.MinimumHorizontalTravel > 0f
            ? plan.MinimumHorizontalTravel
            : plan.HorizontalTravel;
        float committedMaximumTravel = plan.MaximumHorizontalTravel > 0f
            ? plan.MaximumHorizontalTravel
            : plan.HorizontalTravel;
        float committedEnvelopeLeft = livePosition.x + Mathf.Min(
            committedMinimumTravel,
            committedMaximumTravel);
        float committedEnvelopeRight = livePosition.x + Mathf.Max(
            committedMinimumTravel,
            committedMaximumTravel);
        float committedLeftMargin =
            committedEnvelopeLeft - target.SafeLeft;
        float committedRightMargin =
            target.SafeRight - committedEnvelopeRight;
        float committedWorstMargin = Mathf.Min(
            committedLeftMargin,
            committedRightMargin);

        jumpController.Press(
            player,
            plan.HoldSeconds,
            $"FixedStepLandingFirst: Selection={selection}, " +
            $"Landing={plan.PredictedLandingX:F2}, {plan.Reason}",
            GetGroundPlanFixedStepHoldLimit(
                fixedState,
                plan,
                planningPhysics));
        if (!jumpController.IsHoldingJump)
            return false;

        if ((fixedStepCrossSpherePriority ||
             fixedStepCrossNetGainRouteSelected) &&
            plan.ExpectedSphereHits >= Stage1CrossSphereMinimumHits &&
            (!float.IsFinite(
                 lastCommittedStage1CrossSphereCenterX) ||
             Mathf.Abs(
                 lastCommittedStage1CrossSphereCenterX -
                 stage1CrossSpherePriorityCenter.x) > 0.50f))
        {
            lastCommittedStage1CrossSphereCenterX =
                stage1CrossSpherePriorityCenter.x;
            BonusRunnerLog.Debug(
                $"{(fixedStepCrossNetGainRouteSelected
                    ? "Stage1Section2CrossSphereNetGainFixedStepCommitted"
                    : "Stage1Section2CrossSpherePriorityFixedStepCommitted")} " +
                $"FixedStep={fixedStep}, CenterX=" +
                $"{stage1CrossSpherePriorityCenter.x:F3}, PositionX=" +
                $"{livePosition.x:F3}, Hold={plan.HoldSeconds:F3}, " +
                $"LaunchWindow=[{plan.LaunchWindowLeft:F3}," +
                $"{plan.LaunchWindowRight:F3}], PredictedHits=" +
                $"{plan.ExpectedSphereHits}, Target=" +
                $"[{target.Left:F3},{target.Right:F3}]@{target.Top:F3}. " +
                "The authoritative pre-movement controller accepted this " +
                "plan and issued the actual DOWN.",
                "Routing");
        }

        ClearRoutePlanLock();
        MarkAutomaticJumpRequested(
            fixedState,
            plan,
            target,
            scan,
            hazard,
            planningPhysics,
            liveSpeed,
            spiritBoost);
        BonusRunnerLog.Debug(
            $"FixedStepRouteCommitted FixedStep={fixedStep}, " +
            $"Action=DOWN, Position=({livePosition.x:F3}," +
            $"{livePosition.y:F3}), Velocity=({liveVelocity.x:F3}," +
            $"{liveVelocity.y:F3}), Source=[{scan.Current.Left:F3}," +
            $"{scan.Current.Right:F3}]@{scan.Current.Top:F3}, Target=" +
            $"[{target.Left:F3},{target.Right:F3}]@{target.Top:F3}, " +
            $"TargetSafe=[{target.SafeLeft:F3},{target.SafeRight:F3}], " +
            $"Hold={plan.HoldSeconds:F3}, Landing=" +
            $"{plan.PredictedLandingX:F3}, LandingEnvelope=" +
            $"[{committedEnvelopeLeft:F3},{committedEnvelopeRight:F3}], " +
            $"LandingMargins=[{committedLeftMargin:F3}," +
            $"{committedRightMargin:F3}], WorstMargin=" +
            $"{committedWorstMargin:F3}, FutureSpeedTransition=" +
            $"{plan.FutureSpeedTransitionExpected}, BaseVX=" +
            $"{planningPhysics.BaseHorizontalSpeed:F3}, Selection=" +
            $"{selection}, " +
            $"Plan={plan.Reason}. Perception, optimization and actuation " +
            "used the same pre-movement physics snapshot.",
            "Routing");
        return true;
    }

    [HideFromIl2Cpp]
    private bool TryExecuteUrgentNarrowChainFixedStep(
        PlayerMovement player)
    {
        BonusStageState routingState = GetRoutingState(latestState);
        bool terrainControlActive =
            routingState.IsActiveGameplay ||
            IsSuccessfulCompletionTraversal(routingState);
        bool automaticLearningFlight =
            learningSampleActive &&
            string.Equals(
                learningSource,
                "Automatic",
                StringComparison.Ordinal) &&
            learningTookOff &&
            automaticPredictionActive;
        bool passiveIntentionalDrop =
            !learningSampleActive &&
            string.Equals(
                secondStageSource,
                "IntentionalDrop",
                StringComparison.Ordinal);
        bool projectedContinuationAvailable =
            secondStageProjectedPlan.IsValid ||
            string.Equals(
                secondStageProjectedPlan.Reason,
                "IntentionalDrop",
                StringComparison.Ordinal);
        if (!automationEnabled ||
            !AutomaticJumpingEnabled ||
            player == null ||
            !terrainControlActive ||
            terrainContinuationEpochBlocked ||
            pitDescentGuardActive ||
            rewardTargetDetector.IsLatched ||
            Time.unscaledTime - lastManualInputTime < 0.40f ||
            (!automaticLearningFlight && !passiveIntentionalDrop) ||
            !secondStagePreviewActive ||
            !secondStageObservedAirborne ||
            !secondStageProjectedScan.IsValid ||
            !secondStageProjectedScan.HasNext ||
            secondStageExpectedSupport.Width > 4.25f ||
            !routingState.IsBonusStage ||
            !routingState.IsSupportedBonusMap)
        {
            return false;
        }

        int playerInstanceId = player.GetInstanceID();
        if (routingState.PlayerInstanceId != 0 &&
            routingState.PlayerInstanceId != playerInstanceId)
        {
            return false;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        Vector2 liveVelocity = body != null ? body.velocity : Vector2.zero;
        Vector3 livePosition = player.transform.position;
        bool fixedStepGrounded = player.IsGrounded();
        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
            : 0.60f;
        float playerFeetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : livePosition.y;
        float playerBodyLeft = player.playerCollider != null
            ? player.playerCollider.bounds.min.x
            : livePosition.x - playerHalfWidth;
        float playerBodyRight = player.playerCollider != null
            ? player.playerCollider.bounds.max.x
            : livePosition.x + playerHalfWidth;
        float expectedSupportBodyOverlap = Mathf.Max(
            0f,
            Mathf.Min(
                playerBodyRight,
                secondStageExpectedSupport.Right) -
            Mathf.Max(
                playerBodyLeft,
                secondStageExpectedSupport.Left));
        bool expectedSupportEdgeContact =
            automaticLearningFlight &&
            fixedStepGrounded &&
            expectedSupportBodyOverlap >= 0.15f &&
            Mathf.Abs(
                playerFeetY - secondStageExpectedSupport.Top) <= 0.35f &&
            livePosition.x >=
                secondStageExpectedSupport.Left - playerHalfWidth - 0.05f &&
            livePosition.x <=
                secondStageExpectedSupport.Right + playerHalfWidth + 0.05f &&
            (livePosition.x < secondStageExpectedSupport.SafeLeft ||
             livePosition.x > secondStageExpectedSupport.SafeRight);
        if (!projectedContinuationAvailable &&
            !expectedSupportEdgeContact)
        {
            return false;
        }
        if (secondStageExpectedSupport.Width > 2.25f &&
            !expectedSupportEdgeContact)
        {
            return false;
        }
        if (!fixedStepGrounded ||
            (!automaticTrajectoryCompatible &&
             !expectedSupportEdgeContact) ||
            (liveVelocity.y > 2.50f &&
             !expectedSupportEdgeContact))
            return false;

        float liveSpeed = Mathf.Abs(liveVelocity.x);
        float planningSpeed = liveSpeed > 1f && liveSpeed < 80f
            ? liveSpeed
            : Mathf.Max(1f, lastReliableHorizontalSpeed);
        if (expectedSupportEdgeContact)
        {
            // The collision step temporarily damps VX (the two retained
            // failures report 2.59 and 4.45) before native motion restores
            // roughly 13.5..18.6 on the rebound. Solving the continuation
            // from that damped value would choose an artificially long jump.
            // The last reliable in-flight speed is the correct pre-impact
            // state for a DOWN issued before this FixedUpdate.
            planningSpeed = Mathf.Max(
                planningSpeed,
                Mathf.Max(1f, lastReliableHorizontalSpeed));
        }
        BonusBoardScanResult scan;
        try
        {
            scan = platformScanner.Scan(
                livePosition,
                player,
                planningSpeed);
        }
        catch
        {
            return false;
        }
        string liveScanReason = scan.Reason;
        bool liveSourceMatch =
            scan.IsValid &&
            Mathf.Abs(
                scan.Current.Top -
                secondStageExpectedSupport.Top) <= 0.35f &&
            livePosition.x >= secondStageExpectedSupport.Left - 0.15f &&
            livePosition.x <= secondStageExpectedSupport.Right + 0.15f;
        bool projectedSourceMatchesExpected =
            secondStageProjectedScan.IsValid &&
            secondStageProjectedScan.HasNext &&
            Mathf.Abs(
                secondStageProjectedScan.Current.Left -
                secondStageExpectedSupport.Left) <= 0.25f &&
            Mathf.Abs(
                secondStageProjectedScan.Current.Right -
                secondStageExpectedSupport.Right) <= 0.25f &&
            Mathf.Abs(
                secondStageProjectedScan.Current.Top -
                secondStageExpectedSupport.Top) <= 0.35f;
        bool usingExpectedSupportEdgeFallback =
            !liveSourceMatch &&
            expectedSupportEdgeContact &&
            projectedSourceMatchesExpected;
        if (usingExpectedSupportEdgeFallback)
        {
            // A fast descending jump can touch the raw left edge before the
            // player centre enters the scanner's safe source interval. Unity
            // then reports one Grounded step followed by its native upward
            // edge impulse. The projected contract was prepared from this
            // exact target before takeoff; body overlap plus matching feet Y
            // is stronger evidence than a centre-only live scan on this one
            // physics step, so retain that static source/next pair.
            scan = secondStageProjectedScan;
        }
        if (!liveSourceMatch && !usingExpectedSupportEdgeFallback)
            return false;

        if (expectedSupportEdgeContact)
        {
            BonusRunnerLog.Debug(
                $"NarrowEdgeContactTakeover FixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, Position=" +
                $"({livePosition.x:F3},{livePosition.y:F3}), FeetY=" +
                $"{playerFeetY:F3}, Velocity=({liveVelocity.x:F3}," +
                $"{liveVelocity.y:F3}), BodyOverlap=" +
                $"{expectedSupportBodyOverlap:F3}, ExpectedSupport=" +
                $"[{secondStageExpectedSupport.Left:F3}," +
                $"{secondStageExpectedSupport.Right:F3}]@" +
                $"{secondStageExpectedSupport.Top:F3}, LiveScan=" +
                $"{(liveSourceMatch ? "Matched:" : "FallbackFrom:")}" +
                $"{liveScanReason}, " +
                $"PlanningSpeed={planningSpeed:F3}, " +
                $"TrajectoryCompatible={automaticTrajectoryCompatible}. " +
                "The physical edge contact owns the prepared continuation " +
                "before Unity's rebound can leave the narrow top.",
                "Lookahead");
        }

        JumpPhysicsSnapshot observedPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        BonusHazard hazard = hazardScanner.FindNearest(livePosition);
        Vector2[] routeSphereObjectives =
            routingState.HasSphereProgress &&
            routingState.RemainingRequiredSpheres > 0
                ? BonusStageInspector.GetActiveSpherePositions(
                    livePosition.x - 1.0f,
                    livePosition.x + SectionObjectiveHorizon)
                : Array.Empty<Vector2>();
        SpiritBoostRouteContext spiritBoostRouteContext =
            CaptureRouteSpeedContext(
                routingState,
                player,
                livePosition.x - 2.0f,
                livePosition.x + Mathf.Clamp(
                    30f + Mathf.Max(0f, planningSpeed - 18f) * 1.4f,
                    30f,
                    80f),
                observedPhysics.BaseHorizontalSpeed > 1f &&
                observedPhysics.BaseHorizontalSpeed < 80f
                    ? observedPhysics.BaseHorizontalSpeed
                    : sectionCruiseHorizontalSpeed > 1f
                        ? sectionCruiseHorizontalSpeed
                        : 0f);
        bool pendingBoostResetAtEdgeContact =
            expectedSupportEdgeContact &&
            automaticFutureSpeedTransitionExpected &&
            automaticExpectedSpeedBoostHits > 0 &&
            spiritBoostRouteContext.Enabled &&
            spiritBoostRouteContext.KinematicsAvailable &&
            spiritBoostRouteContext.CurrentBoostComponent > 0.50f &&
            spiritBoostRouteContext.MaximumBoostComponent > 0.50f;
        if (pendingBoostResetAtEdgeContact)
        {
            // The pickup collider has retired by this callback, while native
            // Rigidbody speed still describes the contact step. The retained
            // V0.89 failures then changed 16.9 -> 26.9 in the same FixedUpdate
            // that consumed DOWN. Reconstruct that one bounded transition
            // from three independent facts: the prior route intersected the
            // typed trigger, the body is on that exact expected support, and
            // the typed boost component is active. Marking the trigger scan
            // unknown asks the existing slow/reset solver to prove both
            // current speed and base+maximum speed; it does not invent a
            // persistent trigger or bypass landing/wall safety.
            spiritBoostRouteContext = spiritBoostRouteContext with
            {
                TriggerScanSucceeded = false,
                Evidence = spiritBoostRouteContext.Evidence +
                    ";EdgeContactPendingBoostReset[" +
                    $"PriorExpectedHits={automaticExpectedSpeedBoostHits}," +
                    $"ObservedVX={planningSpeed:F3}," +
                    $"PhysicsBase=" +
                    $"{observedPhysics.BaseHorizontalSpeed:F3}]"
            };
        }
        float verifiedPlanningCruise =
            pendingBoostResetAtEdgeContact &&
            observedPhysics.BaseHorizontalSpeed > 1f &&
            observedPhysics.BaseHorizontalSpeed < 80f
                ? observedPhysics.BaseHorizontalSpeed
                : routingState.SpiritBoostEnabled
                    ? sectionCruiseHorizontalSpeed
                    : 0f;
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            routingState.SectionIndex,
            observedPhysics,
            planningSpeed,
            terrainControlActive,
            verifiedPlanningCruise);
        Vector2 planningVelocity = new(
            planningSpeed,
            liveVelocity.y);
        if (pendingBoostResetAtEdgeContact)
        {
            BonusRunnerLog.Debug(
                $"SpiritEdgeResetEnvelope FixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, Position=" +
                $"({livePosition.x:F3},{livePosition.y:F3}), " +
                $"ObservedVX={planningSpeed:F3}, PhysicsBase=" +
                $"{observedPhysics.BaseHorizontalSpeed:F3}, " +
                $"CurrentBoost=" +
                $"{spiritBoostRouteContext.CurrentBoostComponent:F3}, " +
                $"MaximumBoost=" +
                $"{spiritBoostRouteContext.MaximumBoostComponent:F3}, " +
                $"ResetVX=" +
                $"{spiritBoostRouteContext.BaseHorizontalSpeed + spiritBoostRouteContext.MaximumBoostComponent:F3}, " +
                $"PriorExpectedBoostHits=" +
                $"{automaticExpectedSpeedBoostHits}. The live command must " +
                "be safe at both the contact-step speed and the pending " +
                "native reset speed.",
                "Physics");
        }
        BonusJumpPlan fixedSelectorPlan = default;
        bool fixedSelectorPlanAvailable = false;
        if (!routingState.UsesStage3AuthoredRouting)
        {
            scan = jumpPlanner.SelectReachableRoute(
                scan,
                livePosition,
                planningVelocity,
                planningPhysics,
                hazard,
                routeSphereObjectives,
                sectionIndex: routingState.SectionIndex,
                preferSphereCoverage:
                    routeSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch:
                    routingState.SectionIndex >= 2,
                useFixedStepAlignedHolds:
                    routingState.SectionIndex >= 2,
                spiritBoost: spiritBoostRouteContext,
                selectionContext: "LiveFixedStep",
                selection: out _,
                selectedPlan: out fixedSelectorPlan,
                selectedPlanAvailable: out fixedSelectorPlanAvailable,
                useStage2LiveTopologyProfile:
                    routingState.UsesStage2LiveRouting);
        }
        if (routingState.UsesStage3AuthoredRouting)
        {
            scan = jumpPlanner.SelectReachableRoute(
                scan,
                livePosition,
                planningVelocity,
                planningPhysics,
                hazard,
                routeSphereObjectives,
                sectionIndex: routingState.SectionIndex,
                preferSphereCoverage:
                    routeSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch: false,
                useFixedStepAlignedHolds: false,
                spiritBoost: spiritBoostRouteContext,
                selectionContext: "SpeedAdaptiveFixedStep",
                selection: out _,
                selectedPlan: out fixedSelectorPlan,
                selectedPlanAvailable: out fixedSelectorPlanAvailable);
        }

        bool nextGeometryMatch =
            scan.HasNext &&
            Mathf.Abs(
                scan.Next.Left -
                secondStageProjectedScan.Next.Left) <= 0.25f &&
            Mathf.Abs(
                scan.Next.Right -
                secondStageProjectedScan.Next.Right) <= 0.25f &&
            Mathf.Abs(
                scan.Next.Top -
                secondStageProjectedScan.Next.Top) <= 0.35f;
        if (!nextGeometryMatch && !expectedSupportEdgeContact)
            return false;

        BonusJumpPlan plan = fixedSelectorPlanAvailable
            ? fixedSelectorPlan
            : jumpPlanner.Plan(
                scan,
                livePosition,
                planningVelocity,
                planningPhysics,
                hazard,
                routeSphereObjectives,
                routingState.SectionIndex,
                preferSphereCoverage:
                    routeSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch:
                    !routingState.UsesStage3AuthoredRouting &&
                    routingState.SectionIndex >= 2,
                useFixedStepAlignedHolds:
                    !routingState.UsesStage3AuthoredRouting &&
                    routingState.SectionIndex >= 2,
                spiritBoost: spiritBoostRouteContext,
                useStage2LiveTopologyProfile:
                    routingState.UsesStage2LiveRouting);
        if (!plan.IsValid || !scan.HasNext)
        {
            return false;
        }

        if (plan.Maneuver ==
                BonusManeuverKind.EnterTrenchThenWallJump)
        {
            if (!expectedSupportEdgeContact)
                return false;

            BonusStageState passiveFixedState = routingState with
            {
                PlayerInstanceId = playerInstanceId,
                PlayerPosition = livePosition,
                PlayerVelocity = liveVelocity,
                IsGrounded = true,
                HasPlayer = true
            };
            long passiveLandingStep =
                JumpPhysicsFeedback.FixedStepSequence;
            bool passiveReleasedPriorHold =
                jumpController.IsHoldingJump;
            if (passiveReleasedPriorHold)
            {
                jumpController.Release();
                if (automaticLearningFlight)
                {
                    learningInputUpTime = Time.unscaledTime;
                    learningInputReleased = true;
                }
            }
            if (automaticLearningFlight)
            {
                authoritativeLandingEvidenceActive = true;
                authoritativeLandingEvidenceHistorical = false;
                authoritativeLandingEvidenceSurface =
                    secondStageExpectedSupport;
                authoritativeLandingEvidencePlayerHalfWidth =
                    playerHalfWidth;
                authoritativeLandingEvidenceAt = Time.unscaledTime;
                authoritativeLandingEvidenceFixedStep =
                    passiveLandingStep;
                FinishLearningSample(passiveFixedState, "Landed");
            }

            nextAutomaticAttemptTime = 0f;
            ClearRoutePlanLock();
            BeginPassiveWallApproach(
                passiveFixedState,
                plan,
                scan,
                scan.Next,
                hazard,
                planningPhysics);
            BonusRunnerLog.Debug(
                $"UrgentNarrowPassiveWallOwnership FixedStep=" +
                $"{passiveLandingStep}, Position=" +
                $"({livePosition.x:F3},{livePosition.y:F3}), Velocity=" +
                $"({liveVelocity.x:F3},{liveVelocity.y:F3}), Support=" +
                $"[{scan.Current.Left:F3},{scan.Current.Right:F3}]@" +
                $"{scan.Current.Top:F3}, WallTarget=" +
                $"[{scan.Next.Left:F3},{scan.Next.Right:F3}]@" +
                $"{scan.Next.Top:F3}, ArmX={plan.PlannedLaunchX:F3}, " +
                $"ContactX={plan.PredictedLandingX:F3}, Plan=" +
                $"{plan.Reason}, ReleasedPriorHold=" +
                $"{passiveReleasedPriorHold}. " +
                "Every direct jump overshoots the nearby successor, so the " +
                "one physical edge-contact step transfers to passive wall " +
                "ownership before native motion enters the gap.",
                "Lookahead");
            return true;
        }

        BonusBoardSegment target =
            IsStage2LowCorridorWallCatch(plan, scan)
                ? scan.Intermediate
                : plan.Maneuver == BonusManeuverKind.SphereCollectionJump
                ? scan.Current
                : scan.Next;
        bool promotedEarly = false;
        if (!plan.ShouldJumpNow &&
            !plan.FutureSpeedTransitionExpected &&
            !spiritBoostRouteContext.RequiresSpeedEnvelope)
        {
            bool genuinelyEarly =
                livePosition.x <= plan.PlannedLaunchX + 0.001f;
            float earlyLaunchDistance =
                plan.PlannedLaunchX - livePosition.x;
            float earlyLandingX =
                livePosition.x + plan.HorizontalTravel;
            string translatedTrajectoryCheck = "LaunchAlreadyPassed";
            string translatedTargetFaceCheck = "LaunchAlreadyPassed";
            bool translatedHazardSafe =
                genuinelyEarly &&
                jumpPlanner.IsTrajectorySafe(
                    hazard,
                    livePosition.x,
                    earlyLandingX,
                    scan.Current.Top,
                    planningSpeed,
                    plan.HoldSeconds,
                    plan.PredictedFlightSeconds,
                    planningPhysics,
                    out translatedTrajectoryCheck);
            bool translatedTargetFaceSafe =
                genuinelyEarly &&
                jumpPlanner.IsRaisedTargetFaceClear(
                    scan.Current,
                    target,
                    livePosition.x,
                    planningSpeed,
                    plan.HorizontalTravel,
                    plan.HoldSeconds,
                    plan.PredictedFlightSeconds,
                    planningPhysics,
                    out translatedTargetFaceCheck);
            bool translatedTrajectorySafe =
                translatedHazardSafe && translatedTargetFaceSafe;
            float ordinaryEarlyLimit = Mathf.Max(
                0.80f,
                planningSpeed * planningPhysics.FixedDeltaTime * 1.75f);
            float edgeContactEarlyLimit = Mathf.Max(
                ordinaryEarlyLimit,
                playerHalfWidth * 2f +
                planningSpeed * planningPhysics.FixedDeltaTime);
            float allowedEarlyDistance = expectedSupportEdgeContact
                ? edgeContactEarlyLimit
                : ordinaryEarlyLimit;
            bool earlyLandingStillFits =
                genuinelyEarly &&
                earlyLaunchDistance <= allowedEarlyDistance &&
                earlyLandingX >= target.SafeLeft - 0.20f &&
                earlyLandingX <= target.SafeRight + 0.20f &&
                translatedTrajectorySafe;
            if (!earlyLandingStillFits)
            {
                BonusRunnerLog.Debug(
                    $"UrgentNarrowLandingFixedStepRejected FixedStep=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}, Position=" +
                    $"({livePosition.x:F3},{livePosition.y:F3}), " +
                    $"PlannedLaunch={plan.PlannedLaunchX:F3}, " +
                    $"GenuinelyEarly={genuinelyEarly}, EarlyBy=" +
                    $"{earlyLaunchDistance:F3}, AllowedEarly=" +
                    $"{allowedEarlyDistance:F3}, EdgeContact=" +
                    $"{expectedSupportEdgeContact}, LiveLanding=" +
                    $"{earlyLandingX:F3}, TargetSafe=" +
                    $"[{target.SafeLeft:F3},{target.SafeRight:F3}], " +
                    $"TrajectorySafe={translatedTrajectorySafe}, Check=" +
                    $"{translatedTrajectoryCheck};" +
                    $"{translatedTargetFaceCheck}.",
                    "Lookahead");
                return false;
            }

            plan = plan with
            {
                ShouldJumpNow = true,
                PlannedLaunchX = livePosition.x,
                PredictedLandingX = earlyLandingX,
                HorizontalTravel = Mathf.Max(
                    0f,
                    earlyLandingX - livePosition.x),
                LaunchWindowLeft = livePosition.x,
                LaunchWindowRight = livePosition.x,
                Reason = "UrgentNarrowLandingChainFixedStep",
                CandidateSummary =
                    $"PromotedEarlyBy={earlyLaunchDistance:F3}," +
                    $"AdjustedLanding={earlyLandingX:F3}," +
                    $"LiveTrajectory={translatedTrajectoryCheck};" +
                    $"{translatedTargetFaceCheck}; " +
                    plan.CandidateSummary
            };
            promotedEarly = true;
        }

        // A one-step narrow-support handoff is only a continuation, not proof
        // that the support's objectives were completed. The V0.62 trace
        // touched the left corner of a +3 pillar and immediately launched the
        // downstream jump while one or two souls were still active. Preserve
        // every still-ahead objective attached to this support in the actual
        // fixed-step trajectory, or let native ground motion consume another
        // step and re-evaluate from the resulting physical state.
        float attachedObjectiveForwardReach = Mathf.Max(
            0.75f,
            playerHalfWidth * 3.0f);
        Vector2[] attachedPendingObjectives = routeSphereObjectives
            .Where(sphere =>
                sphere.x >= livePosition.x - 0.35f &&
                sphere.x <= secondStageExpectedSupport.Right +
                    attachedObjectiveForwardReach &&
                sphere.y >= secondStageExpectedSupport.Top - 0.45f &&
                sphere.y <= secondStageExpectedSupport.Top + 4.25f)
            .ToArray();
        int attachedObjectiveHits =
            BonusJumpPlanner.CountTrajectoryObjectiveHits(
                attachedPendingObjectives,
                livePosition.x,
                plan.PredictedLandingX,
                scan.Current.Top,
                planningSpeed,
                plan.HoldSeconds,
                plan.PredictedFlightSeconds,
                planningPhysics);
        float remainingSupportResidence =
            (secondStageExpectedSupport.Right + playerHalfWidth -
             livePosition.x) /
            Mathf.Max(1f, planningSpeed);
        float deferResidenceThreshold = Mathf.Max(
            0.06f,
            planningPhysics.FixedDeltaTime * 2.5f);
        bool objectiveShortfall =
            attachedObjectiveHits < attachedPendingObjectives.Length;
        bool canSafelyDeferForObjectives =
            objectiveShortfall &&
            !expectedSupportEdgeContact &&
            remainingSupportResidence >= deferResidenceThreshold;
        if (canSafelyDeferForObjectives)
        {
            BonusRunnerLog.Debug(
                $"UrgentNarrowObjectiveHandoffDeferred FixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, Position=" +
                $"({livePosition.x:F3},{livePosition.y:F3}), Support=" +
                $"[{secondStageExpectedSupport.Left:F3}," +
                $"{secondStageExpectedSupport.Right:F3}]@" +
                $"{secondStageExpectedSupport.Top:F3}, PendingAhead=" +
                $"{attachedPendingObjectives.Length}, PredictedHits=" +
                $"{attachedObjectiveHits}, ForwardReach=" +
                $"{attachedObjectiveForwardReach:F3}, Residence=" +
                $"{remainingSupportResidence:F3}s/" +
                $"{deferResidenceThreshold:F3}s, Plan={plan.Reason}, Hold=" +
                $"{plan.HoldSeconds:F3}, Landing=" +
                $"{plan.PredictedLandingX:F3}. Action=NoneForThisStep; " +
                "native motion may collect the support objectives before the " +
                "same unified solver evaluates the next fixed step.",
                "Lookahead");
            return false;
        }
        if (objectiveShortfall)
        {
            BonusRunnerLog.Debug(
                $"UrgentNarrowObjectiveSafetyOverride FixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, Position=" +
                $"({livePosition.x:F3},{livePosition.y:F3}), Support=" +
                $"[{secondStageExpectedSupport.Left:F3}," +
                $"{secondStageExpectedSupport.Right:F3}]@" +
                $"{secondStageExpectedSupport.Top:F3}, PendingAhead=" +
                $"{attachedPendingObjectives.Length}, PredictedHits=" +
                $"{attachedObjectiveHits}, EdgeContact=" +
                $"{expectedSupportEdgeContact}, Residence=" +
                $"{remainingSupportResidence:F3}s/" +
                $"{deferResidenceThreshold:F3}s. Action=execute the safest " +
                "verified continuation now; objective optimization cannot " +
                "turn the only physical support step into a fall.",
                "Lookahead");
        }

        BonusStageState fixedState = routingState with
        {
            PlayerInstanceId = playerInstanceId,
            PlayerPosition = livePosition,
            PlayerVelocity = liveVelocity,
            IsGrounded = true,
            HasPlayer = true
        };
        long completedAttemptId = automaticAttemptId;
        long landingFixedStep = JumpPhysicsFeedback.FixedStepSequence;
        string previewSource = secondStageSource;
        bool releasedPriorHold = jumpController.IsHoldingJump;
        if (releasedPriorHold)
        {
            // The observer runs before MaintainHeldJump. On the boundary tick
            // the old pointer may therefore still be logically held even
            // though this verified support must launch the next action now.
            // Close the old edge explicitly before issuing the new DOWN.
            jumpController.Release();
            if (automaticLearningFlight)
            {
                learningInputUpTime = Time.unscaledTime;
                learningInputReleased = true;
            }
        }
        if (automaticLearningFlight)
        {
            if (expectedSupportEdgeContact)
            {
                authoritativeLandingEvidenceActive = true;
                authoritativeLandingEvidenceHistorical = false;
                authoritativeLandingEvidenceSurface =
                    secondStageExpectedSupport;
                authoritativeLandingEvidencePlayerHalfWidth =
                    playerHalfWidth;
                authoritativeLandingEvidenceAt = Time.unscaledTime;
                authoritativeLandingEvidenceFixedStep = landingFixedStep;
            }
            FinishLearningSample(fixedState, "Landed");
        }
        ResetWallRecoveryAfterLanding();
        automaticJumpArmed = true;
        airborneAfterAutomaticJump = false;
        nextAutomaticAttemptTime = 0f;
        ClearRoutePlanLock();
        jumpController.Press(
            player,
            plan.HoldSeconds,
            $"UrgentNarrowFixedStep: Source={previewSource}, " +
            $"LandingStep={landingFixedStep}, " +
            $"Target={plan.PredictedLandingX:F2}",
            GetGroundPlanFixedStepHoldLimit(
                fixedState,
                plan,
                planningPhysics));
        if (!jumpController.IsHoldingJump)
        {
            ResetAutomaticControlState();
            nextAutomaticAttemptTime = Time.unscaledTime + 0.20f;
            return false;
        }

        MarkAutomaticJumpRequested(
            fixedState,
            plan,
            target,
            scan,
            hazard,
            planningPhysics,
            planningSpeed,
            spiritBoostRouteContext);
        BonusRunnerLog.Debug(
            $"UrgentNarrowLandingFixedStepChained PriorAttemptId=" +
            $"{completedAttemptId}, NewAttemptId={automaticAttemptId}, " +
            $"FixedStep={landingFixedStep}, Player={playerInstanceId}, " +
            $"Position=({livePosition.x:F3},{livePosition.y:F3}), " +
            $"Velocity=({liveVelocity.x:F3},{liveVelocity.y:F3}), " +
            $"Source=[{scan.Current.Left:F3},{scan.Current.Right:F3}]@" +
            $"{scan.Current.Top:F3}, Target=" +
            $"[{target.Left:F3},{target.Right:F3}]@{target.Top:F3}, " +
            $"Hold={plan.HoldSeconds:F3}, PromotedEarly={promotedEarly}, " +
            $"EdgeContactTakeover={expectedSupportEdgeContact}, " +
            $"BodyOverlap={expectedSupportBodyOverlap:F3}, " +
            $"ReleasedPriorHold={releasedPriorHold}, " +
            $"Plan={plan.Reason}. DOWN was issued before the original " +
            "PlayerMovement.FixedUpdate consumed the only narrow-support " +
            "physics step.",
            "Lookahead");
        return true;
    }

    [HideFromIl2Cpp]
    private void TryLatchWatchedExitSupportFixedStep(
        PlayerMovement player)
    {
        BonusStageState routingState = GetRoutingState(latestState);
        long currentFixedStep = JumpPhysicsFeedback.FixedStepSequence;
        bool faceOrTopRoute = wallExitFaceInterceptCommitted;
        bool collectionFaceRoute =
            wallExitCollectionFaceInterceptCommitted;
        bool completionNarrowRoute =
            !faceOrTopRoute &&
             !collectionFaceRoute &&
             IsSuccessfulCompletionTraversal(routingState) &&
             wallExitTargetActive &&
             wallExitTarget.Width <= 2.25f;
        bool automaticWallTargetRoute =
            !faceOrTopRoute &&
            !collectionFaceRoute &&
            !completionNarrowRoute &&
            !wallExitTargetActive &&
            !wallExitContactWatchActive &&
            !HasCommittedExitFaceFlight() &&
            automaticPredictionActive &&
            learningSampleActive &&
            string.Equals(
                learningSource,
                "Automatic",
                StringComparison.Ordinal) &&
            automaticManeuver == BonusManeuverKind.WallJumpClimb &&
            automaticTargetRight > automaticTargetLeft + 0.05f;
        bool watchedExitPending =
            wallExitContactWatchActive ||
            HasCommittedExitFaceFlight() ||
            automaticWallTargetRoute;
        BonusBoardSegment watchedTarget = automaticWallTargetRoute
            ? BuildAutomaticTargetSegment()
            : wallExitTarget;
        bool committedWatchWithinDeadline =
            (faceOrTopRoute || collectionFaceRoute) &&
            wallExitContactWatchDeadlineFixedStep >= 0 &&
            currentFixedStep <= wallExitContactWatchDeadlineFixedStep;
        bool completionWatchWithinDeadline =
            completionNarrowRoute &&
            Time.unscaledTime <= wallRecoveryCommitmentUntil;
        bool automaticTargetWithinDeadline =
            automaticWallTargetRoute &&
            Time.unscaledTime <= wallRecoveryCommitmentUntil;
        if (wallExitSupportFixedStepLatched ||
            !automationEnabled ||
            !watchedExitPending ||
            (!wallExitTargetActive && !automaticWallTargetRoute) ||
            wallExitFaceContactRequired ||
            (!faceOrTopRoute &&
             !collectionFaceRoute &&
             !completionNarrowRoute &&
             !automaticWallTargetRoute) ||
            (!committedWatchWithinDeadline &&
             !completionWatchWithinDeadline &&
             !automaticTargetWithinDeadline) ||
            !automaticPredictionActive ||
            !learningSampleActive ||
            !string.Equals(
                learningSource,
                "Automatic",
                StringComparison.Ordinal) ||
            player == null ||
            !latestState.IsBonusStage ||
            !latestState.IsSupportedBonusMap)
        {
            return;
        }

        int playerInstanceId = player.GetInstanceID();
        if (latestState.PlayerInstanceId != 0 &&
            latestState.PlayerInstanceId != playerInstanceId)
        {
            return;
        }

        Rigidbody2D body = player.GetComponent<Rigidbody2D>();
        Vector2 velocity = body != null ? body.velocity : Vector2.zero;
        Vector3 position = player.transform.position;
        if (!player.IsGrounded() || velocity.y > 2.50f)
            return;

        BonusBoardScanResult supportScan;
        try
        {
            supportScan = platformScanner.Scan(
                position,
                player,
                Mathf.Max(1f, Mathf.Abs(velocity.x)));
        }
        catch
        {
            return;
        }

        bool supportConfirmed =
            supportScan.IsValid &&
            position.x >= supportScan.Current.Left - 0.20f &&
            position.x <= supportScan.Current.Right + 0.20f &&
            Mathf.Abs(position.y - supportScan.Current.Top) <= 0.60f;
        bool matchesWatchedExit =
            supportConfirmed &&
            Mathf.Abs(
                supportScan.Current.Top - watchedTarget.Top) <= 0.35f &&
            position.x >= watchedTarget.Left - 0.20f &&
            position.x <= watchedTarget.Right + 0.20f;
        if (!matchesWatchedExit)
            return;

        bool forcedRelease = jumpController.IsHoldingJump;
        if (forcedRelease)
        {
            // This prefix runs before the controller's normal fixed-step
            // maintenance. Exact non-rising support is stronger evidence than
            // the stale held flag, so close the old edge here rather than miss
            // a one-tick top on the release boundary.
            jumpController.Release();
            learningInputUpTime = Time.unscaledTime;
            learningInputReleased = true;
            BonusRunnerLog.Debug(
                $"WatchedExitSupportForcedRelease AttemptId=" +
                $"{automaticAttemptId}, FixedStep={currentFixedStep}, " +
                $"Position=({position.x:F3},{position.y:F3}), Support=" +
                $"[{supportScan.Current.Left:F3}," +
                $"{supportScan.Current.Right:F3}]@" +
                $"{supportScan.Current.Top:F3}. Exact support closed the " +
                "previous held edge before normal fixed-step maintenance.",
                "Control");
        }

        wallExitSupportFixedStepLatched = true;
        wallExitSupportLatchedAttemptId = automaticAttemptId;
        wallExitSupportLatchedFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
        wallExitSupportLatchedPlayerInstanceId = playerInstanceId;
        wallExitSupportLatchedTarget = watchedTarget;
        wallExitSupportLatchedSurface = supportScan.Current;
        wallExitSupportLatchedPosition = position;
        wallExitSupportLatchedVelocity = velocity;
        wallExitSupportLatchedPlayerHalfWidth =
            player.playerCollider != null
                ? Mathf.Max(
                    0.15f,
                    player.playerCollider.bounds.extents.x)
                : 0.60f;
        wallExitSupportLatchedAt = Time.unscaledTime;
        wallExitSupportLatchedFaceOrTop = faceOrTopRoute;
        wallExitSupportLatchedCollectionFace = collectionFaceRoute;
        wallExitSupportLatchedCompletionNarrow = completionNarrowRoute;
        wallExitSupportLatchedAutomaticTarget = automaticWallTargetRoute;
        BonusRunnerLog.Debug(
            $"WatchedExitSupportFixedStepLatched AttemptId=" +
            $"{automaticAttemptId}, FixedStep=" +
            $"{wallExitSupportLatchedFixedStep}, Player=" +
            $"{playerInstanceId}, Mode=" +
            $"{(collectionFaceRoute ? "CollectionFaceTopMiss" : faceOrTopRoute ? "FaceOrTop" : completionNarrowRoute ? "CompletionNarrow" : "AutomaticWallTarget")}, " +
            $"Position=({position.x:F3},{position.y:F3}), Velocity=" +
            $"({velocity.x:F3},{velocity.y:F3}), Support=" +
            $"[{supportScan.Current.Left:F3}," +
            $"{supportScan.Current.Right:F3}]@" +
            $"{supportScan.Current.Top:F3}/" +
            $"{supportScan.Current.MapPieceName}:S" +
            $"{supportScan.Current.StaticSurfaceIndex}, Target=" +
            $"[{watchedTarget.Left:F3},{watchedTarget.Right:F3}]@" +
            $"{watchedTarget.Top:F3}, ForcedRelease={forcedRelease}, " +
            $"DeadlineStep={wallExitContactWatchDeadlineFixedStep}, " +
            $"CommitmentRemaining=" +
            $"{wallRecoveryCommitmentUntil - Time.unscaledTime:F3}s. " +
            "The render loop may consume this " +
            "one-step support after additional physics ticks without " +
            "reclassifying it as a missed landing.",
            "Recovery");
    }

    private bool TryAdoptCommittedExitFaceContact(
        BonusStageState state,
        PlayerMovement player)
    {
        if (!HasCommittedExitFaceFlight() ||
            !wallExitTargetActive ||
            wallExitFaceContactRequired ||
            player == null)
        {
            return false;
        }

        BonusWallContact wall = wallDetector.Detect(
            player,
            GetWallRouteSpeed());
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        BonusBoardSegment exit = wallExitTarget;
        bool collectionFaceRoute =
            wallExitCollectionFaceInterceptCommitted;
        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(
                0.15f,
                player.playerCollider.bounds.extents.x)
            : 0.60f;
        BonusBoardSegment observedSurface = default;
        bool mappedSurfaceFound =
            wall.IsDetected &&
            platformScanner.TryFindWallSurfaceAtFace(
                wall.FaceX,
                feetY,
                playerHalfWidth,
                out observedSurface);
        bool mappedGeometryMatches =
            mappedSurfaceFound &&
            Mathf.Abs(observedSurface.Left - exit.Left) <= 0.45f &&
            Mathf.Abs(observedSurface.Top - exit.Top) <= 0.35f;
        bool mappedIdentityMatches =
            mappedGeometryMatches &&
            StaticOrColliderIdentityMatches(observedSurface, exit);
        float minimumFaceFeetY =
            exit.Top - CommittedExitFaceDepth +
            CommittedExitFaceBottomClearance;
        float maximumFaceFeetY =
            exit.Top -
            (collectionFaceRoute
                ? 0.45f
                : CommittedExitFaceTopClearance);
        bool exactLeftFaceContact =
            wall.IsDetected &&
            wall.IsTouching &&
            wall.Normal.x <= -0.35f &&
            Mathf.Abs(wall.FaceX - exit.Left) <= 0.45f &&
            mappedIdentityMatches &&
            state.PlayerPosition.x < exit.Left + 0.20f &&
            feetY <= maximumFaceFeetY &&
            feetY >= minimumFaceFeetY;
        if (!exactLeftFaceContact)
        {
            bool apparentTargetFace =
                wall.IsDetected &&
                wall.IsTouching &&
                Mathf.Abs(wall.FaceX - exit.Left) <= 0.45f;
            if (apparentTargetFace &&
                BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextWallProbeLogTime)
            {
                nextWallProbeLogTime = Time.unscaledTime + 0.08f;
                BonusRunnerLog.Debug(
                    $"CommittedExitFaceContactRejected Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY={feetY:F3}, " +
                    $"ObservedFaceX={wall.FaceX:F3}, Expected=" +
                    $"[{exit.Left:F3},{exit.Right:F3}]@{exit.Top:F3}/" +
                    $"{exit.MapPieceName}:S{exit.StaticSurfaceIndex}, " +
                    $"MappedFound={mappedSurfaceFound}, " +
                    $"MappedGeometryMatch={mappedGeometryMatches}, " +
                    $"MappedIdentityMatch={mappedIdentityMatches}, " +
                    $"MappedObserved=[{observedSurface.Left:F3}," +
                    $"{observedSurface.Right:F3}]@" +
                    $"{observedSurface.Top:F3}/" +
                    $"{observedSurface.MapPieceName}:S" +
                    $"{observedSurface.StaticSurfaceIndex}, FaceWindow=" +
                    $"[{minimumFaceFeetY:F3}," +
                    $"{maximumFaceFeetY:F3}], RouteMode=" +
                    $"{(collectionFaceRoute ? "CollectionFace" : "FaceOrTop")}. " +
                    "A ray hit alone cannot replace mapped finite-face " +
                    "identity or the solver's vertical contact window.",
                    "Recovery");
            }
            return false;
        }

        float predictedContactX = wallExitPredictedLandingX;
        float predictionErrorX =
            state.PlayerPosition.x - predictedContactX;
        wallExitContactWatchActive = false;
        // The flight watch is now resolved, but retain a separate bounded
        // fixed-step budget in the same field until the contact-triggered
        // climb actually issues its discrete DOWN. This lets a positive-VY
        // contact wait through real physics steps without being erased by a
        // background wall-clock timeout.
        wallExitContactWatchDeadlineFixedStep =
            JumpPhysicsFeedback.FixedStepSequence +
            GetPhysicsStepBudget(1.50f);
        wallExitTargetActive = false;
        wallExitTarget = default;
        ClearWallExitPreparedContract();
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        wallExitTransferCommitted = false;
        wallLandingFlightCommitted = false;
        wallTopLandingSequenceCommitted = false;
        wallExitFaceContactRequired = false;
        wallExitObjectiveCountAtCapture = 0;
        wallExitObjectiveMinimumY = float.NaN;
        wallExitObjectiveMaximumY = float.NaN;

        automaticTargetSafeLeft = exit.SafeLeft;
        automaticTargetSafeRight = exit.SafeRight;
        automaticLandingAllowsRawBodyFit = false;
        automaticLandingSafeTolerance = 0f;
        automaticTargetLeft = exit.Left;
        automaticTargetRight = exit.Right;
        automaticTargetTop = exit.Top;
        automaticTargetColliderId = exit.ColliderInstanceId;
        automaticTargetColliderName = exit.ColliderName;
        automaticTargetMapPieceName = exit.MapPieceName;
        automaticTargetMapPieceOriginX = exit.MapPieceOriginX;
        automaticTargetMapPieceInstanceId = exit.MapPieceInstanceId;
        automaticTargetRegistryGeneration = exit.RegistryGeneration;
        automaticTargetStaticSurfaceIndex = exit.StaticSurfaceIndex;
        automaticTargetHeightDelta = exit.Top - state.PlayerPosition.y;
        automaticPredictedLandingX = exit.Left;
        automaticPredictedHorizontalTravel = Mathf.Max(
            0f,
            exit.Left - automaticPlanTriggerPosition.x);

        wallRecoveryContactLatched = false;
        wallRecoverySawUpwardMotion = false;
        wallRecoveryLipCrossed = false;
        wallRecoveryPhysicalLipY = exit.Top - 0.20f;
        wallRecoveryPhysicalLeft = exit.Left;
        wallRecoveryPhysicalRight = exit.Right;
        wallRecoveryPhysicalSafeLeft = exit.SafeLeft;
        wallRecoveryPhysicalSafeRight = exit.SafeRight;
        wallRecoveryPhysicalLipFrozen = true;
        wallRecoveryRequiredReleaseY = exit.Top - 0.20f;
        wallRecoveryCommitmentUntil = Time.unscaledTime + 1.50f;
        wallReleaseObservedFixedStep = -1;
        wallDetachedLastFixedStep = -1;
        wallDetachedConfirmationSteps = 0;
        wallStallStartedAt =
            Time.unscaledTime - WallStallConfirmationSeconds;
        nextWallRecoveryTime = 0f;
        passiveWallApproachActive = true;
        wallActionPhase = WallActionPhase.AwaitingWallContact;
        automaticJumpArmed = false;
        airborneAfterAutomaticJump = true;
        automaticJumpRequestedAt = Time.unscaledTime;
        automaticJumpVelocityConfirmed = true;
        automaticPredictionActive = true;
        automaticTrajectoryCompatible = true;
        automaticPlanReason = collectionFaceRoute
            ? "CollectionFaceInterceptContact"
            : "CommittedExitFaceInterceptContact";
        automaticManeuver =
            BonusManeuverKind.ApproachJumpThenWallJump;

        BonusRunnerLog.Debug(
            $"CommittedExitFaceContactHandoff Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"FeetY={feetY:F3}, Velocity=" +
            $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"ObservedFaceX={wall.FaceX:F3}, Normal=" +
            $"({wall.Normal.x:F3},{wall.Normal.y:F3}), Target=" +
            $"[{exit.Left:F3},{exit.Right:F3}]@{exit.Top:F3}/" +
            $"{exit.MapPieceName}:S{exit.StaticSurfaceIndex}, " +
            $"PredictedContactX={predictedContactX:F3}, " +
            $"PredictionErrorX={predictionErrorX:F3}, Collider=" +
            $"{wall.ColliderInstanceId}:{wall.ColliderName}, MappedSurface=" +
            $"{observedSurface.MapPieceName}:S" +
            $"{observedSurface.StaticSurfaceIndex}/G" +
            $"{observedSurface.RegistryGeneration}, RouteMode=" +
            $"{(collectionFaceRoute ? "CollectionFace" : "FaceOrTop")}. " +
            "Result=ExactMappedLeftFace; NextState=AwaitingWallContact. " +
            "The physical face, not the rejected landing model, now owns " +
            "the bounded climb.",
            "Recovery");
        return true;
    }

    private bool TryWallRecoveryJump(
        BonusStageState state,
        PlayerMovement player)
    {
        if (Time.unscaledTime < completionStallPauseUntil)
            return false;

        // A wall jump is valid only while an automatic board trajectory is
        // actively trying to reach its recorded target. Pit/death walls must
        // never manufacture a new route by themselves.
        bool activeAutomaticJumpRoute =
            automaticPredictionActive &&
            learningSampleActive &&
            learningSource == "Automatic";
        // A completed pulse can finish its learning sample before the body
        // reaches the next physical face. Preserve only a probe-only slice of
        // ownership while that released flight is still airborne. It cannot
        // issue input by itself; the later gate still requires an exact,
        // touching, different mapped face before rebasing the climb.
        bool chainedExitFlightProbe =
            wallActionPhase == WallActionPhase.ExitFlight &&
            wallRecoveryAttempts > 0 &&
            airborneAfterAutomaticJump;
        bool unresolvedWallRoute =
            wallExitContactWatchActive ||
            HasCommittedExitFaceFlight() ||
            attachedObjectiveDescentActive ||
            wallMandatoryFaceSetupActive ||
            wallMandatoryFaceInterceptCommitted ||
            chainedExitFlightProbe;
        if (!passiveWallApproachActive &&
            !activeAutomaticJumpRoute &&
            !unresolvedWallRoute)
        {
            wallStallStartedAt = -1f;
            return false;
        }

        if ((wallMandatoryFaceSetupActive ||
             wallMandatoryFaceInterceptCommitted) &&
            !TryValidateMandatoryFaceRouteIdentity(
                state,
                player,
                out string mandatoryIdentityReason))
        {
            return FailMandatoryFacePlan(
                state,
                "RouteIdentityChanged",
                mandatoryIdentityReason);
        }

        bool mandatoryFaceSetupPhysicsExpired =
            wallMandatoryFaceSetupActive &&
            wallMandatoryFaceSetupDeadlineFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence >
                wallMandatoryFaceSetupDeadlineFixedStep;
        if (mandatoryFaceSetupPhysicsExpired)
        {
            BonusWallContact expiryWall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            float expiryHalfWidth = player.playerCollider != null
                ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
                : 0.60f;
            float expectedCurrentFaceCenterX =
                automaticTargetLeft - expiryHalfWidth;
            bool exactCurrentFace =
                expiryWall.IsDetected &&
                expiryWall.IsTouching &&
                Mathf.Abs(expiryWall.FaceX - automaticTargetLeft) <= 0.45f;
            bool kinematicCurrentFace =
                Mathf.Abs(state.PlayerVelocity.x) <=
                    WallAttachmentVelocityTolerance &&
                Mathf.Abs(
                    state.PlayerPosition.x - expectedCurrentFaceCenterX) <=
                    WallAttachmentPositionTolerance;
            if (!exactCurrentFace && !kinematicCurrentFace)
            {
                return FailMandatoryFacePlan(
                    state,
                    "SetupPhysicsBudgetExpiredWithoutOldFaceContact",
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), FixedStep=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}, " +
                    $"DeadlineStep=" +
                    $"{wallMandatoryFaceSetupDeadlineFixedStep}, " +
                    $"Wall[Detected={expiryWall.IsDetected}," +
                    $"Touching={expiryWall.IsTouching},FaceX=" +
                    $"{expiryWall.FaceX:F3},Reason={expiryWall.Reason}]");
            }
        }

        if (wallMandatoryFaceInterceptCommitted &&
            wallActionPhase != WallActionPhase.ExitFlight &&
            wallMandatoryFaceContactWatchDeadlineFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence >
                wallMandatoryFaceContactWatchDeadlineFixedStep)
        {
            return FailMandatoryFacePlan(
                state,
                "InterceptNeverEnteredContactWatch",
                $"StartedStep=" +
                $"{wallMandatoryFaceInterceptStartedFixedStep}, " +
                $"CurrentStep={JumpPhysicsFeedback.FixedStepSequence}, " +
                $"DeadlineStep=" +
                $"{wallMandatoryFaceContactWatchDeadlineFixedStep}, " +
                $"Holding={jumpController.IsHoldingJump}, Phase=" +
                $"{wallActionPhase}");
        }

        // Mandatory S3 contact owns the frame before any generic timeout,
        // speed, phase, stall or ray-miss gate. A valid contact on the final
        // deadline frame must be observed before timeout is considered.
        if (wallMandatoryFaceInterceptCommitted &&
            wallActionPhase == WallActionPhase.ExitFlight)
        {
            return HandleMandatoryFaceContactWatch(state, player);
        }

        // A committed Ground 7 face intercept owns contact before the generic
        // watch timeout. This matters on the deadline/background catch-up
        // frame: real collision is authoritative even if render time has just
        // advanced beyond the nominal watch duration.
        if (TryAdoptCommittedExitFaceContact(state, player))
            return TryWallRecoveryJump(state, player);

        bool committedExitFaceFlight = HasCommittedExitFaceFlight();
        bool committedFacePhysicsBudgetExpired =
            committedExitFaceFlight &&
            (wallExitContactWatchDeadlineFixedStep >= 0
                ? JumpPhysicsFeedback.FixedStepSequence >
                    wallExitContactWatchDeadlineFixedStep
                : Time.unscaledTime > wallRecoveryCommitmentUntil);
        bool ordinaryWatchTimeExpired =
            !committedExitFaceFlight &&
            Time.unscaledTime > wallRecoveryCommitmentUntil;
        bool watchedExitOverflown =
            state.PlayerPosition.x > wallExitTarget.Right + 1.0f;
        if (wallExitContactWatchActive &&
            !wallMandatoryFaceInterceptCommitted &&
            (committedFacePhysicsBudgetExpired ||
             ordinaryWatchTimeExpired ||
             watchedExitOverflown))
        {
            bool mandatoryContactExpired =
                wallExitFaceContactRequired;
            bool committedFaceInterceptExpired =
                wallExitFaceInterceptCommitted;
            bool collectionFaceInterceptExpired =
                wallExitCollectionFaceInterceptCommitted;
            BonusRunnerLog.Warning(
                $"WallExitContactWatchExpired Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), ExitTarget=" +
                $"[{wallExitTarget.Left:F3}," +
                $"{wallExitTarget.Right:F3}]@" +
                $"{wallExitTarget.Top:F3}, TimeRemaining=" +
                $"{wallRecoveryCommitmentUntil - Time.unscaledTime:F3}s, " +
                $"ContactRequired={mandatoryContactExpired}, " +
                $"CommittedFaceIntercept=" +
                $"{committedFaceInterceptExpired}, " +
                $"CollectionFaceIntercept=" +
                $"{collectionFaceInterceptExpired}, FixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}, " +
                $"DeadlineFixedStep=" +
                $"{wallExitContactWatchDeadlineFixedStep}, Overflown=" +
                $"{watchedExitOverflown}, " +
                $"ObjectiveCount={wallExitObjectiveCountAtCapture}. " +
                (mandatoryContactExpired
                    ? "No mandatory downstream face contact was observed; " +
                      "FailureDomain=RouteExecution and the stale wall route " +
                      "is reset."
                    : committedFaceInterceptExpired ||
                      collectionFaceInterceptExpired
                        ? "The committed Ground 7 face outcome was not " +
                          "observed. FailureDomain=RouteExecution; the exact " +
                          "contract is cleared before generic recovery."
                        : "No downstream contact was observed; the exit target " +
                          "and all related commitments are cleared atomically."));
            if (mandatoryContactExpired)
            {
                automaticTrajectoryCompatible = false;
                if (learningSampleActive &&
                    learningSource == "Automatic")
                {
                    FinishLearningSample(
                        state,
                        "MandatoryWallFaceContactWatchExpired");
                }
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime + 0.20f;
                return true;
            }

            if (committedFaceInterceptExpired ||
                collectionFaceInterceptExpired)
            {
                automaticTrajectoryCompatible = false;
                if (learningSampleActive &&
                    learningSource == "Automatic")
                {
                    FinishLearningSample(
                        state,
                        collectionFaceInterceptExpired
                            ? "CollectionFaceContactWatchExpired"
                            : "CommittedExitFaceContactWatchExpired");
                }
                ResetAutomaticControlState();
                nextAutomaticAttemptTime =
                    Time.unscaledTime + 0.20f;
                return true;
            }

            wallExitContactWatchActive = false;
            wallExitContactWatchDeadlineFixedStep = -1;
            wallExitTargetActive = false;
            wallExitTarget = default;
            ClearWallExitPreparedContract();
            wallExitFaceContactRequired = false;
            wallExitObjectiveCountAtCapture = 0;
            wallExitObjectiveMinimumY = float.NaN;
            wallExitObjectiveMaximumY = float.NaN;
            ResetMandatoryFacePlanState();
            wallExitTransferCommitted = false;
            wallLandingFlightCommitted = false;
            wallExitFaceInterceptCommitted = false;
            wallExitCollectionFaceInterceptCommitted = false;
            wallTopLandingSequenceCommitted = false;
            wallExitPredictedLandingX = 0f;
            wallExitPredictedTravel = 0f;
            wallExitPredictedFlightSeconds = 0f;
            wallExitPlanSummary = string.Empty;
        }

        if (!attachedObjectiveDescentActive &&
            passiveWallApproachActive &&
            (Time.unscaledTime - automaticJumpRequestedAt > 1.50f ||
             state.PlayerPosition.x > automaticTargetRight + 1.0f))
        {
            float abortPlayerHalfWidth = player.playerCollider != null
                ? Mathf.Max(0.15f, player.playerCollider.bounds.extents.x)
                : 0.60f;
            BonusWallContact abortWall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            if (TryRebaseUnexpectedWallContact(
                    state,
                    player,
                    abortWall,
                    abortPlayerHalfWidth))
            {
                BonusRunnerLog.Debug(
                    $"WallDropAbortSuppressed Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FaceX=" +
                    $"{abortWall.FaceX:F3}. Exact mapped contact was " +
                    "resolved before the stale target timeout/overflight " +
                    "gate, so reactive wall authority remains active.",
                    "Recovery");
                return TryWallRecoveryJump(state, player);
            }

            BonusRunnerLog.Warning(
                $"WallDropRouteAborted Position=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Elapsed=" +
                $"{Time.unscaledTime - automaticJumpRequestedAt:F3}s, " +
                $"Target=[{automaticTargetLeft:F3},{automaticTargetRight:F3}]" +
                $"@{automaticTargetTop:F3}. No confirmed wall contact was " +
                "observed; the passive route is cleared instead of jumping " +
                "against an unrelated death wall.");
            ResetAutomaticControlState();
            return false;
        }

        if (attachedObjectiveDescentActive)
        {
            BonusWallContact descentWall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            float descentFeetY = player.playerCollider != null
                ? player.playerCollider.bounds.min.y
                : state.PlayerPosition.y - 0.27f;
            if (TryManageAttachedObjectiveDescent(
                    state,
                    descentWall,
                    descentFeetY))
            {
                return true;
            }
        }

        float horizontalSpeed = Mathf.Abs(state.PlayerVelocity.x);
        bool committedWallClimb = IsCommittedWallClimbActive();
        bool belowPlannedWallTop =
            committedWallClimb &&
            state.PlayerPosition.y < automaticTargetTop + 0.35f;
        bool transientWallGrounding =
            state.IsGrounded &&
            (state.PlayerVelocity.y < -2f || belowPlannedWallTop);
        float inferredPlayerHalfWidth = Mathf.Max(
            0.15f,
            automaticTargetSafeLeft - automaticTargetLeft - 0.15f);
        float plannedWallContactCenterX =
            automaticTargetLeft - inferredPlayerHalfWidth;
        Bounds wallPlayerBounds = player.playerCollider != null
            ? player.playerCollider.bounds
            : new Bounds(
                state.PlayerPosition,
                new Vector3(1.20f, 1.20f, 0f));
        float plannedFaceBodyGap =
            automaticTargetLeft - wallPlayerBounds.max.x;
        bool hasPlannedTargetGeometry =
            automaticTargetRight > automaticTargetLeft + 0.05f &&
            automaticTargetTop > state.PlayerPosition.y - 0.50f;
        bool plannedWallApproach =
            passiveWallApproachActive ||
            automaticManeuver == BonusManeuverKind.ApproachJumpThenWallJump &&
                hasPlannedTargetGeometry &&
                state.PlayerPosition.y < automaticTargetTop - 0.10f;
        // The wider ray is useful for seeing the upcoming face, but it is not
        // proof that the game will accept a wall press. In the latest run the
        // first press was sent 0.31 units before body contact and produced no
        // upward impulse. Only bypass stall confirmation once the player
        // centre is at the physical contact centre (within one frame).
        bool plannedBodyContactReady =
            plannedWallApproach &&
            state.PlayerPosition.x >= plannedWallContactCenterX - 0.06f &&
            state.PlayerPosition.x <= plannedWallContactCenterX + 0.14f &&
            horizontalSpeed <= 1.25f;
        bool geometricPlannedWallContact =
            passiveWallApproachActive &&
            plannedWallApproach &&
            state.UsesStage2LiveRouting &&
            state.IsGrounded &&
            horizontalSpeed <= 1.25f &&
            Mathf.Abs(state.PlayerVelocity.y) <= 2.50f &&
            plannedFaceBodyGap >= -0.10f &&
            plannedFaceBodyGap <= 0.10f &&
            wallPlayerBounds.min.y <
                automaticTargetTop - 0.10f;
        plannedBodyContactReady |= geometricPlannedWallContact;

        // A landing-targeted press normally owns the action through landing.
        // Keep one evidence-based escape hatch: if its upward motion has
        // actually exhausted while the body is still attached below the lip,
        // the model was optimistic and another separated wall press is legal.
        // Merely being attached for one post-UP physics step is not enough.
        if (wallLandingFlightCommitted &&
            wallActionPhase == WallActionPhase.ExitFlight &&
            !wallRecoveryLipCrossed)
        {
            BonusWallContact committedWall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            bool stillAttachedBelowLip =
                committedWall.IsDetected &&
                committedWall.IsTouching &&
                committedWall.Point.x >= automaticTargetLeft - 0.80f &&
                committedWall.Point.x <= automaticTargetRight + 0.80f &&
                horizontalSpeed <= WallAttachmentVelocityTolerance &&
                state.PlayerPosition.y < automaticTargetTop - 0.20f;
            bool riseExhausted =
                state.PlayerVelocity.y <= -1.0f;
            if (!stillAttachedBelowLip || !riseExhausted)
                return false;

            if (wallRecoveryAttempts >=
                MaximumWallRecoveriesPerAirborneSequence)
            {
                BonusRunnerLog.Warning(
                    $"CommittedWallLandingFailed Reason=RiseExhaustedBelowLip, " +
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                    $"Attempts={wallRecoveryAttempts}/" +
                    $"{MaximumWallRecoveriesPerAirborneSequence}. " +
                    "FailureDomain=Execution; retry limit reached.");
                FinishLearningSample(
                    state,
                    "CommittedWallLandingRetryLimitReached");
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime + 0.25f;
                return false;
            }

            wallLandingFlightCommitted = false;
            wallExitTransferCommitted = false;
            wallRecoveryContactLatched = false;
            wallRecoverySawUpwardMotion = false;
            wallReleaseObservedFixedStep = -1;
            wallDetachedLastFixedStep = -1;
            wallDetachedConfirmationSteps = 0;
            wallActionPhase = WallActionPhase.AwaitingNextWallPress;
            wallStallStartedAt =
                Time.unscaledTime - WallStallConfirmationSeconds;
            nextWallRecoveryTime = 0f;
            BonusRunnerLog.Warning(
                $"CommittedWallLandingFallback Reason=RiseExhaustedBelowLip, " +
                $"Position=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                $"Attempts={wallRecoveryAttempts}/" +
                $"{MaximumWallRecoveriesPerAirborneSequence}, " +
                $"Collider={committedWall.ColliderInstanceId}:" +
                $"{committedWall.ColliderName}. The solved landing did not " +
                "reach the lip; a separated recovery press is re-authorized.");
        }
        if (wallRecoveryContactLatched &&
            state.PlayerVelocity.y > 2f &&
            state.PlayerPosition.y >= wallRecoveryContactY + 0.30f)
        {
            wallRecoverySawUpwardMotion = true;
        }
        // A wall press is one discrete launch. Only after its UP event and at
        // least one physics frame can we classify the result. Continued
        // contact authorizes another pulse eventually; it does not authorize
        // one while the prior pulse is still rising. The latest manual trace
        // stayed attached through the apex and successfully pressed again on
        // descent, while immediate +17.253-VY re-presses caused every repeated
        // Ground 3 overshoot.
        bool wallPressReleased =
            wallRecoveryContactLatched &&
            wallRecoverySawUpwardMotion &&
            !jumpController.IsHoldingJump &&
            jumpController.LastReleaseAt >= 0f;
        if (wallPressReleased && wallReleaseObservedFixedStep < 0)
        {
            wallReleaseObservedFixedStep =
                JumpPhysicsFeedback.FixedStepSequence;
            wallDetachedLastFixedStep = -1;
            wallDetachedConfirmationSteps = 0;
            return false;
        }
        bool releasedAfterWallPress =
            wallPressReleased &&
            JumpPhysicsFeedback.FixedStepSequence >
                wallReleaseObservedFixedStep;
        if (releasedAfterWallPress)
        {
            BonusWallContact releaseWall = wallDetector.Detect(
                player,
                GetWallRouteSpeed());
            bool sameWallGeometry = releaseWall.IsDetected &&
                releaseWall.Point.x >= automaticTargetLeft - 0.80f &&
                releaseWall.Point.x <= automaticTargetRight + 0.80f;
            bool raycastAttached =
                horizontalSpeed <= WallAttachmentVelocityTolerance &&
                Mathf.Abs(state.PlayerPosition.x - wallRecoveryContactX) <=
                    WallAttachmentPositionTolerance &&
                releaseWall.IsTouching &&
                sameWallGeometry;
            // Physics2D can miss the wall for one frame when the player's
            // collider overlaps the face after a wall-jump impulse.  The
            // failed run showed exactly that at X=1022.495: VX remained zero
            // and the body moved only 0.294 units, but the forward ray
            // returned no hit and the controller incorrectly entered
            // ExitFlight.  Treat the same immobile, near-face motion as
            // continuous attachment while still below the planned lip.
            bool bodyStillNearPlannedFace =
                state.PlayerPosition.x >= automaticTargetLeft - 1.00f &&
                state.PlayerPosition.x <= automaticTargetLeft + 0.25f;
            bool kinematicallyAttached =
                horizontalSpeed <= WallAttachmentVelocityTolerance &&
                Mathf.Abs(state.PlayerPosition.x - wallRecoveryContactX) <=
                    WallAttachmentPositionTolerance &&
                bodyStillNearPlannedFace &&
                !wallRecoveryLipCrossed &&
                state.PlayerPosition.y < automaticTargetTop - 0.20f;
            bool stillAttached =
                raycastAttached || kinematicallyAttached;
            if (!stillAttached &&
                wallDetachedLastFixedStep !=
                    JumpPhysicsFeedback.FixedStepSequence)
            {
                wallDetachedLastFixedStep =
                    JumpPhysicsFeedback.FixedStepSequence;
                wallDetachedConfirmationSteps++;
            }
            if (!stillAttached && wallDetachedConfirmationSteps < 2)
            {
                return false;
            }
            float inputGap =
                Time.unscaledTime - jumpController.LastReleaseAt;
            float executedPulseHold = Mathf.Max(
                0f,
                jumpController.LastReleaseAt -
                    jumpController.LastPressStartedAt);
            bool stillBelowWallLip =
                !wallRecoveryLipCrossed &&
                state.PlayerPosition.y < automaticTargetTop - 0.20f;
            bool needsAnotherWallPress =
                stillAttached &&
                stillBelowWallLip;

            BonusRunnerLog.Debug(
                $"WallPulseResult Pulse={wallRecoveryAttempts}/" +
                $"{MaximumWallRecoveriesPerAirborneSequence}, Hold=" +
                $"{executedPulseHold:F3}s, " +
                $"HeldFixedSteps={jumpController.LastReleaseHeldFixedSteps}, " +
                $"Start=({wallRecoveryContactX:F3}," +
                $"{wallRecoveryContactY:F3}), " +
                $"End=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Delta=" +
                $"({state.PlayerPosition.x - wallRecoveryContactX:F3}," +
                $"{state.PlayerPosition.y - wallRecoveryContactY:F3}), " +
                $"EndVelocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), StillAttached=" +
                $"{stillAttached}, BelowLip={stillBelowWallLip}, " +
                $"Target=[{automaticTargetLeft:F3}," +
                $"{automaticTargetRight:F3}]@{automaticTargetTop:F3}, " +
                $"WallDetected={releaseWall.IsDetected}, " +
                $"Touching={releaseWall.IsTouching}, " +
                $"SameWall={sameWallGeometry}, BodyGap=" +
                $"{releaseWall.BodyGap:F3}.",
                "Recovery");

            bool completedOldWall = !stillBelowWallLip;
            float classifiedFeetY = player.playerCollider != null
                ? player.playerCollider.bounds.min.y
                : state.PlayerPosition.y - 0.27f;
            if (wallMandatoryFaceSetupActive &&
                (!stillAttached || completedOldWall))
            {
                return FailMandatoryFacePlan(
                    state,
                    "SetupEnvelopeMiss",
                    $"FeetY={classifiedFeetY:F3},Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}),StillAttached=" +
                    $"{stillAttached},CompletedOldWall=" +
                    $"{completedOldWall},ReleaseY=" +
                    $"{wallRecoveryRequiredReleaseY:F3},Wall=" +
                    $"{releaseWall.Reason}");
            }
            if (!stillAttached || completedOldWall)
            {
                // Residual rise belongs to the wall that produced it. Once
                // the body leaves that face OR reaches its release height,
                // downstream contact/watch logic must run immediately.
                // Raycasts can keep reporting the old face for one or more
                // frames after the feet have crossed the lip; carrying the
                // wait through that state can hide the next mandatory face.
                ResetWallResidualRiseWait();
            }
            else if (needsAnotherWallPress)
            {
                float releaseFeetY = player.playerCollider != null
                    ? player.playerCollider.bounds.min.y
                    : state.PlayerPosition.y - 0.27f;
                JumpPhysicsSnapshot releasePhysics =
                    BuildWallPlanningPhysics(
                        jumpPhysicsFeedback.CaptureSnapshot(player));
                if (TryDeferWallPressForResidualRise(
                        state,
                        releaseFeetY,
                        releasePhysics,
                        "ReleasedSameFacePulse"))
                {
                    return true;
                }
            }

            if (wallLandingFlightCommitted && !needsAnotherWallPress)
            {
                wallActionPhase = WallActionPhase.ExitFlight;
                wallRecoveryContactLatched = false;
                wallRecoverySawUpwardMotion = false;
                wallReleaseObservedFixedStep = -1;
                wallDetachedLastFixedStep = -1;
                wallDetachedConfirmationSteps = 0;
                passiveWallApproachActive = false;
                BonusRunnerLog.Debug(
                    $"WallPostBounceClassified Result=" +
                    $"{(stillAttached ? "CommittedLandingStillAttached" : "CommittedLandingDetached")}, " +
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                    $"Attempt={wallRecoveryAttempts}/" +
                    $"{MaximumWallRecoveriesPerAirborneSequence}, " +
                    $"InputGap={inputGap:F3}s, ExitTransfer=" +
                    $"{wallExitTransferCommitted}, BodyGap=" +
                    $"{releaseWall.BodyGap:F3}. NextState=CommittedLandingFlight; " +
                    "no additional wall press may alter the solved hold or landing.",
                    "Recovery");
                return false;
            }
            if (wallLandingFlightCommitted && needsAnotherWallPress)
            {
                // A predicted wall-top/exit landing must not suppress the
                // game's actual wall-jump sequence.  If the released press
                // leaves the body attached below the lip, it was an
                // intermediate climb press.  Re-authorize a fresh DOWN after
                // this real UP/fixed-step boundary and solve the exit again
                // from the new observed height.
                wallLandingFlightCommitted = false;
                wallExitTransferCommitted = false;
                BonusRunnerLog.Debug(
                    $"WallLandingCommitReopened Result=StillAttachedBelowLip, " +
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), TargetTop=" +
                    $"{automaticTargetTop:F3}, Attempt=" +
                    $"{wallRecoveryAttempts}/" +
                    $"{MaximumWallRecoveriesPerAirborneSequence}. " +
                    "The prior press is treated as an intermediate wall " +
                    "climb; another discrete click is allowed.",
                    "Recovery");
            }
            if (needsAnotherWallPress &&
                wallRecoveryAttempts >=
                    MaximumWallRecoveriesPerAirborneSequence)
            {
                wallActionPhase = WallActionPhase.Failed;
                BonusRunnerLog.Warning(
                    $"WallPostBounceClassified Result=BounceLimitWhileAttached, " +
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Attempts=" +
                    $"{wallRecoveryAttempts}/" +
                    $"{MaximumWallRecoveriesPerAirborneSequence}, " +
                    $"BodyGap={releaseWall.BodyGap:F3}, " +
                    $"InputGap={inputGap:F3}s. ActionStatus=Failed, " +
                    "FailureDomain=Execution; no blind extra press is sent.");
                FinishLearningSample(
                    state,
                    "WallBounceLimitReachedWhileAttached");
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime + 0.20f;
                return false;
            }
            if (needsAnotherWallPress &&
                wallRecoveryAttempts <
                    MaximumWallRecoveriesPerAirborneSequence)
            {
                wallRecoveryContactLatched = false;
                wallReleaseObservedFixedStep = -1;
                wallDetachedLastFixedStep = -1;
                wallDetachedConfirmationSteps = 0;
                wallActionPhase = WallActionPhase.AwaitingNextWallPress;
                BonusRunnerLog.Debug(
                    $"WallPostBounceClassified Result=StillAttached, " +
                    $"X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
                    $"Attempt={wallRecoveryAttempts}/" +
                    $"{MaximumWallRecoveriesPerAirborneSequence}, " +
                    $"RiseSinceContact=" +
                    $"{state.PlayerPosition.y - wallRecoveryContactY:F3}, " +
                    $"InputGap={inputGap:F3}s, " +
                    $"WallDeltaX={state.PlayerPosition.x - wallRecoveryContactX:F3}, " +
                    $"Face=({releaseWall.Point.x:F3},{releaseWall.Point.y:F3}). " +
                    (wallMandatoryFaceSetupActive
                        ? "ActionStatus=Success; MandatoryFaceSetup remains " +
                          "released. The next DOWN is not authorized until a " +
                          "live descent-frame face-intercept candidate is safe."
                        : "ActionStatus=Success; the next wall press is " +
                          "authorized by observed continuous contact."),
                    "Recovery");
            }
            else
            {
                if (!stillAttached && TryPromoteChainedWallTarget(state))
                    return false;

                // "Still attached" is not the same as "still climbing this
                // wall".  At the release height the body ray can still touch
                // the old face while horizontal motion has already begun.
                // Arm the mapped downstream face from that observed boundary
                // so Ground 3 S2 -> S3 cannot silently lose its mandatory
                // contact contract.
                bool existingCommittedFaceWatch =
                    HasCommittedExitFaceFlight() &&
                    wallExitContactWatchActive &&
                    wallExitTargetActive;
                if (existingCommittedFaceWatch)
                {
                    wallExitContactWatchDeadlineFixedStep =
                        Math.Max(
                            wallExitContactWatchDeadlineFixedStep,
                            JumpPhysicsFeedback.FixedStepSequence +
                            GetPhysicsStepBudget(1.25f));
                    wallRecoveryCommitmentUntil = Mathf.Max(
                        wallRecoveryCommitmentUntil,
                        Time.unscaledTime + 1.25f);
                }
                bool watchingDownstreamWall =
                    (!stillAttached || completedOldWall) &&
                    (existingCommittedFaceWatch ||
                     TryArmWallExitContactWatch(
                         state,
                         completedOldWall && stillAttached
                             ? "PostBounceReleaseHeightWhileTouchingOldFace"
                             : "PostBounceExit"));

                wallActionPhase = WallActionPhase.ExitFlight;
                wallRecoveryContactLatched = false;
                wallRecoverySawUpwardMotion = false;
                wallReleaseObservedFixedStep = -1;
                wallDetachedLastFixedStep = -1;
                wallDetachedConfirmationSteps = 0;
                if (!watchingDownstreamWall)
                    wallRecoveryCommitmentUntil = 0f;
                passiveWallApproachActive = false;
                BonusRunnerLog.Debug(
                    "WallPostBounceClassified Result=ExitFlight, " +
                    $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                    $"Attempt={wallRecoveryAttempts}/" +
                    $"{MaximumWallRecoveriesPerAirborneSequence}, " +
                    $"InputGap={inputGap:F3}s, WallDetected={releaseWall.IsDetected}, " +
                    $"SameWall={sameWallGeometry}. ActionStatus=Success, " +
                    $"NextState={(watchingDownstreamWall ? "ExitTargetContactWatch" : "ExitFlight")}; " +
                    (watchingDownstreamWall
                        ? "the mapped downstream face remains armed until " +
                          "physical contact or a safe landing."
                        : "downstream landing will be replanned from " +
                          "observed position and velocity."),
                    "Recovery");
                if (!watchingDownstreamWall)
                    FinishLearningSample(state, "WallBounceExitFlight");
                // A bounce can put the player onto a lower ledge of the same
                // authored wall. If that frame is grounded, permit a fresh
                // route decision; otherwise re-arm on the observed landing.
                automaticJumpArmed = state.IsGrounded;
                airborneAfterAutomaticJump = !state.IsGrounded;
                nextAutomaticAttemptTime = Time.unscaledTime + 0.10f;
                return false;
            }
        }
        if (!wallRecoveryContactLatched &&
            wallReleaseObservedFixedStep >= 0)
        {
            if (JumpPhysicsFeedback.FixedStepSequence <=
                wallReleaseObservedFixedStep)
            {
                return false;
            }
            wallReleaseObservedFixedStep = -1;
        }
        // Probe before applying the horizontal-speed gate.  Physics reports
        // the incoming run velocity on the exact render frame where the body
        // first touches a wall, then collapses VX on the next physics step.
        // Waiting for that collapse lost the one-frame ray/native contact in
        // the retained Section 2 traces.  Only authoritative current-frame
        // contact with the already planned face may bypass the speed/stall
        // gates; predicted position alone remains insufficient.
        BonusWallContact wall = wallDetector.Detect(
            player,
            GetWallRouteSpeed());
        float priorExitFlightPulseContactX = wallRecoveryContactX;
        bool authoritativeNewExitFlightFace =
            wallRecoveryAttempts > 0 &&
            wallActionPhase == WallActionPhase.ExitFlight &&
            !wallExitFaceContactRequired &&
            !wallMandatoryFaceSetupActive &&
            !wallMandatoryFaceInterceptCommitted &&
            !attachedObjectiveDescentActive &&
            wall.IsDetected &&
            wall.IsTouching &&
            wall.FaceX > priorExitFlightPulseContactX +
                inferredPlayerHalfWidth + 0.55f;
        if (authoritativeNewExitFlightFace &&
            TryRebaseUnexpectedWallContact(
                state,
                player,
                wall,
                inferredPlayerHalfWidth))
        {
            BonusRunnerLog.Warning(
                $"ChainedExitFlightWallContact Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), NewFaceX=" +
                $"{wall.FaceX:F3}, PriorPulseContactX=" +
                $"{priorExitFlightPulseContactX:F3}. A different forward " +
                "face was " +
                "touched during ExitFlight, so it becomes the next bounded " +
                "climb target immediately instead of waiting for a ground " +
                "re-plan.");
            return TryWallRecoveryJump(state, player);
        }
        bool ground5AuthoredNarrowPillar =
            state.SectionIndex == 1 &&
            string.Equals(
                automaticTargetMapPieceName,
                "Ground 5",
                StringComparison.OrdinalIgnoreCase) &&
            automaticTargetStaticSurfaceIndex >= 2 &&
            automaticTargetStaticSurfaceIndex <= 4;
        bool authoritativePlannedContact =
            plannedWallApproach &&
            !ground5AuthoredNarrowPillar &&
            wall.IsDetected &&
            wall.IsTouching &&
            wall.Point.x >= automaticTargetLeft - 0.80f &&
            wall.Point.x <= automaticTargetRight + 0.80f;
        bool authoritativeWatchedExitContact =
            wallExitContactWatchActive &&
            wallExitTargetActive &&
            !HasCommittedExitFaceFlight() &&
            (wallActionPhase == WallActionPhase.ExitFlight ||
             wallActionPhase == WallActionPhase.AttachedObjectiveDescent) &&
            wall.IsDetected &&
            wall.IsTouching &&
            wall.Point.x >= wallExitTarget.Left - 0.80f &&
            wall.Point.x <= wallExitTarget.Right + 0.80f;
        bool wallContactReady =
            plannedBodyContactReady ||
            authoritativePlannedContact ||
            authoritativeWatchedExitContact;

        if (horizontalSpeed > WallAttachmentVelocityTolerance &&
            !transientWallGrounding &&
            !wallContactReady)
        {
            wallStallStartedAt = -1f;
            // Keep the completed press pending until its synthetic UP has
            // crossed the fixed-frame release barrier. Horizontal separation
            // is itself the evidence for ExitFlight; clearing the latch here
            // made post-bounce classification unreachable.
            return false;
        }

        bool separatedInputPhysicsStepPending =
            wallInputSeparationReleaseFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence <=
                wallInputSeparationReleaseFixedStep;
        if (wallInputSeparationReleaseFixedStep >= 0 &&
            !separatedInputPhysicsStepPending)
        {
            BonusRunnerLog.Debug(
                $"WallInputSeparationBarrierReleased UPFixedStep=" +
                $"{wallInputSeparationReleaseFixedStep}, CurrentFixedStep=" +
                $"{JumpPhysicsFeedback.FixedStepSequence}. A native physics " +
                "step has consumed the prior UP; a distinct wall DOWN may " +
                "now be evaluated.",
                "Recovery");
            wallInputSeparationReleaseFixedStep = -1;
        }

        if (jumpController.IsHoldingJump ||
            separatedInputPhysicsStepPending ||
            Time.unscaledTime < nextWallRecoveryTime ||
            wallRecoveryContactLatched ||
            wallRecoveryAttempts >= MaximumWallRecoveriesPerAirborneSequence)
        {
            return false;
        }

        bool downstreamWallContactWatch =
            wallExitContactWatchActive &&
            (wallActionPhase == WallActionPhase.ExitFlight ||
             wallActionPhase == WallActionPhase.AttachedObjectiveDescent);
        if (wallRecoveryAttempts > 0 &&
            wallActionPhase != WallActionPhase.AwaitingNextWallPress &&
            wallActionPhase != WallActionPhase.AwaitingWallContact &&
            wallActionPhase != WallActionPhase.AttachedObjectiveDescent &&
            !downstreamWallContactWatch)
        {
            return false;
        }

        float stalledFor = 0f;
        if (!transientWallGrounding && !wallContactReady)
        {
            if (wallStallStartedAt < 0f)
            {
                wallStallStartedAt = Time.unscaledTime;
                return false;
            }

            stalledFor = Time.unscaledTime - wallStallStartedAt;
            if (stalledFor < WallStallConfirmationSeconds)
                return false;
        }

        // After one confirmed wall press, the player's collider can remain
        // overlapped with the face and make Physics2D.RaycastAll return no
        // hit for several consecutive frames.  Post-release classification
        // already accepts this kinematic evidence; the next-press gate must
        // accept the same evidence or it authorizes phase two and then waits
        // forever for a ray that cannot see out of the overlap.
        bool inferredContinuousWallContact =
            wallRecoveryAttempts > 0 &&
            wallActionPhase == WallActionPhase.AwaitingNextWallPress &&
            horizontalSpeed <= WallAttachmentVelocityTolerance &&
            Mathf.Abs(state.PlayerPosition.x - wallRecoveryContactX) <=
                WallAttachmentPositionTolerance &&
            state.PlayerPosition.x >= automaticTargetLeft - 1.00f &&
            state.PlayerPosition.x <= automaticTargetLeft + 0.25f &&
            !wallRecoveryLipCrossed &&
            state.PlayerPosition.y < automaticTargetTop - 0.20f;
        if (BonusRunnerLog.IsDebugMode &&
            Time.unscaledTime >= nextWallProbeLogTime)
        {
            nextWallProbeLogTime = Time.unscaledTime + 0.50f;
            BonusRunnerLog.Debug(
                $"WallRecoveryProbe X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
                $"VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}, " +
                $"Grounded={state.IsGrounded}, TransientGrounding={transientWallGrounding}, " +
                $"PlannedApproach={plannedWallApproach}, " +
                $"Ground5NarrowPillarBodyGate=" +
                $"{ground5AuthoredNarrowPillar}, " +
                $"BodyContactReady={plannedBodyContactReady}, " +
                $"GeometricPlannedContact=" +
                $"{geometricPlannedWallContact}, PlannedFaceBodyGap=" +
                $"{plannedFaceBodyGap:F3}, PlayerBoundsX=" +
                $"[{wallPlayerBounds.min.x:F3}," +
                $"{wallPlayerBounds.max.x:F3}], " +
                $"AuthoritativePlannedContact=" +
                $"{authoritativePlannedContact}, " +
                $"AuthoritativeWatchedExitContact=" +
                $"{authoritativeWatchedExitContact}, " +
                $"ActionPhase={wallActionPhase}, " +
                $"PlannedContactX={plannedWallContactCenterX:F3}, " +
                $"StalledFor={stalledFor:F3}s, Attempt={wallRecoveryAttempts + 1}/" +
                $"{MaximumWallRecoveriesPerAirborneSequence}, Detected={wall.IsDetected}, " +
                $"Distance={wall.Distance:F3}, Point=({wall.Point.x:F3},{wall.Point.y:F3}), " +
                $"Touching={wall.IsTouching}, BodyGap={wall.BodyGap:F3}, FaceX={wall.FaceX:F3}, " +
                $"Normal=({wall.Normal.x:F3},{wall.Normal.y:F3}), " +
                $"Collider={wall.ColliderInstanceId}:{wall.ColliderName}, Reason={wall.Reason}",
                "Recovery");
        }
        if (!wall.IsDetected &&
            !inferredContinuousWallContact &&
            !geometricPlannedWallContact)
            return false;

        if (!wall.IsTouching &&
            !inferredContinuousWallContact &&
            !geometricPlannedWallContact)
        {
            // Predicted body position is only a cue to probe more closely.
            // It is not permission to consume a DOWN before the game has
            // established contact. A pre-contact DOWN can be accepted as an
            // ordinary airborne jump and leave no fresh edge for wall climb.
            return false;
        }

        bool touchingWatchedExitFace =
            downstreamWallContactWatch &&
            wallExitTargetActive &&
            wall.IsDetected &&
            wall.IsTouching &&
            wall.Point.x >= wallExitTarget.Left - 0.80f &&
            wall.Point.x <= wallExitTarget.Right + 0.80f;
        bool watchedExitIsAlreadyActiveTarget =
            touchingWatchedExitFace &&
            !wallExitFaceContactRequired &&
            !HasCommittedExitFaceFlight() &&
            Mathf.Abs(wallExitTarget.Left - automaticTargetLeft) <= 0.20f &&
            Mathf.Abs(wallExitTarget.Right - automaticTargetRight) <= 0.20f &&
            Mathf.Abs(wallExitTarget.Top - automaticTargetTop) <= 0.35f;
        if (watchedExitIsAlreadyActiveTarget)
        {
            BonusBoardSegment contactedExit = wallExitTarget;
            bool satisfiedMandatoryContact =
                wallExitFaceContactRequired;
            float abandonedPredictedLandingX =
                wallExitPredictedLandingX;
            float predictionErrorToContact =
                state.PlayerPosition.x - abandonedPredictedLandingX;
            wallExitContactWatchActive = false;
            wallExitContactWatchDeadlineFixedStep = -1;
            wallExitTargetActive = false;
            wallExitTarget = default;
            ClearWallExitPreparedContract();
            wallExitFaceContactRequired = false;
            wallExitObjectiveCountAtCapture = 0;
            wallExitObjectiveMinimumY = float.NaN;
            wallExitObjectiveMaximumY = float.NaN;
            ResetMandatoryFacePlanState();
            wallExitTransferCommitted = false;
            wallLandingFlightCommitted = false;
            wallExitFaceInterceptCommitted = false;
            wallExitCollectionFaceInterceptCommitted = false;
            wallTopLandingSequenceCommitted = false;
            wallRecoveryContactLatched = false;
            wallRecoverySawUpwardMotion = false;
            wallRecoveryLipCrossed = false;
            wallReleaseObservedFixedStep = -1;
            wallDetachedLastFixedStep = -1;
            wallDetachedConfirmationSteps = 0;
            wallRecoveryPhysicalLipY = automaticTargetTop - 0.20f;
            wallRecoveryPhysicalLeft = automaticTargetLeft;
            wallRecoveryPhysicalRight = automaticTargetRight;
            wallRecoveryPhysicalSafeLeft = automaticTargetSafeLeft;
            wallRecoveryPhysicalSafeRight = automaticTargetSafeRight;
            wallRecoveryPhysicalLipFrozen = true;
            wallRecoveryRequiredReleaseY = wallRecoveryPhysicalLipY;
            wallStallStartedAt =
                Time.unscaledTime - WallStallConfirmationSeconds;
            nextWallRecoveryTime = 0f;
            passiveWallApproachActive = true;
            automaticJumpRequestedAt = Time.unscaledTime;
            wallActionPhase = WallActionPhase.AwaitingWallContact;
            automaticPlanReason = "CommittedExitFaceContactFallback";
            automaticManeuver =
                BonusManeuverKind.ApproachJumpThenWallJump;
            BonusRunnerLog.Warning(
                $"CommittedExitContactFallback Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), Face=" +
                $"({wall.Point.x:F3},{wall.Point.y:F3}), Target=" +
                $"[{contactedExit.Left:F3},{contactedExit.Right:F3}]" +
                $"@{contactedExit.Top:F3}, Collider=" +
                $"{wall.ColliderInstanceId}:{wall.ColliderName}, " +
                $"AbandonedPredictedLandingX=" +
                $"{abandonedPredictedLandingX:F3}, " +
                $"PredictionErrorToContactX=" +
                $"{predictionErrorToContact:F3}. " +
                "ExpectedResult=direct landing; ActualResult=physical face " +
                "contact. The observed collision overrides the optimistic " +
                "landing prediction and re-enters bounded wall control." +
                (satisfiedMandatoryContact
                    ? " MandatoryFaceContact=Satisfied."
                    : string.Empty));
        }
        else if (touchingWatchedExitFace &&
                 TryPromoteChainedWallTarget(
                     state,
                     allowLevelFace: true,
                     observedFaceX: wall.FaceX,
                     observedFeetY: player.playerCollider != null
                         ? player.playerCollider.bounds.min.y
                         : state.PlayerPosition.y - 0.27f))
        {
            BonusRunnerLog.Debug(
                $"WallExitContactIntercepted Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Face=" +
                $"({wall.Point.x:F3},{wall.Point.y:F3}), " +
                $"Collider={wall.ColliderInstanceId}:" +
                $"{wall.ColliderName}. The downstream wall is now the " +
                "active climb target; input is solved from this contact.",
                "Recovery");
            return TryWallRecoveryJump(state, player);
        }

        bool wallBelongsToPlannedTarget =
            inferredContinuousWallContact ||
            geometricPlannedWallContact ||
            wall.Point.x >= automaticTargetLeft - 0.80f &&
            wall.Point.x <= automaticTargetRight + 0.80f;
        if (!wallBelongsToPlannedTarget)
        {
            if (TryRebaseUnexpectedWallContact(
                    state,
                    player,
                    wall,
                    inferredPlayerHalfWidth))
            {
                return TryWallRecoveryJump(state, player);
            }
            if (TryAdoptStage2UnmappedPhysicalWallContact(
                    state,
                    player,
                    wall,
                    inferredPlayerHalfWidth))
            {
                return true;
            }

            BonusRunnerLog.Debug(
                $"WallRecoveryRejected Reason=OutsidePlannedTarget, " +
                $"WallX={wall.Point.x:F3}, TargetRaw=" +
                $"[{automaticTargetLeft:F3},{automaticTargetRight:F3}], " +
                $"AttemptId={automaticAttemptId}.",
                "Recovery");
            wallStallStartedAt = -1f;
            return false;
        }

        float objectiveContactFeetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        if (TryManageAttachedObjectiveDescent(
                state,
                wall,
                objectiveContactFeetY))
        {
            return true;
        }

        if (learningSampleActive && learningSource != "Automatic")
            return false;

        CaptureWallExitTargetFromPreview(state);
        CaptureChainedWallTargetFromStaticMap(inferredPlayerHalfWidth);
        CaptureGround7WallExitTargetFromStaticMap(
            inferredPlayerHalfWidth);

        JumpPhysicsSnapshot observedWallPhysics =
            jumpPhysicsFeedback.CaptureSnapshot(player);
        JumpPhysicsSnapshot wallPhysics = BuildWallPlanningPhysics(
            observedWallPhysics);
        float postLipHorizontalSpeed = GetWallRouteSpeed();
        float wallExitPlanningSpeed = GetWallExitPlanningSpeed(
            state,
            postLipHorizontalSpeed,
            out bool wallExitAccelerationEnvelopeApplied);
        bool completionWallExitTraversal =
            IsSuccessfulCompletionTraversal(state);
        JumpPhysicsSnapshot wallExitPhysics =
            BuildWallExitPlanningPhysics(
                wallPhysics,
                wallExitPlanningSpeed,
                sectionCruiseHorizontalSpeed,
                state.IsActiveGameplay &&
                    !state.SpiritBoostEnabled);
        // Contact speed is not the same as the slowest possible lip-resume
        // speed.  A transient boost decays while horizontal movement is
        // blocked against the wall.  The retained Section-2 failure planned
        // its finite face at 18.936 even though the first usable lip speed was
        // 17.2.  Reserve a quarter second of observed decay for the slow end;
        // this affects completion validation only.
        float wallExitMinimumPlanningSpeed =
            state.SpiritBoostEnabled &&
            sectionCruiseHorizontalSpeed > 1f
                ? Mathf.Clamp(
                    sectionCruiseHorizontalSpeed,
                    1f,
                    wallExitPlanningSpeed)
                : completionWallExitTraversal
                    ? Mathf.Clamp(
                        postLipHorizontalSpeed -
                            Mathf.Max(
                                0.10f,
                                wallExitPhysics.BoostHorizontalDeceleration) *
                            0.25f,
                        Mathf.Max(
                            1f,
                            wallExitPhysics.BaseHorizontalSpeed),
                        wallExitPlanningSpeed)
                    : wallExitPlanningSpeed;
        bool wallExitSpeedEnvelopeRequired =
            (completionWallExitTraversal || state.SpiritBoostEnabled) &&
            wallExitPlanningSpeed >
                wallExitMinimumPlanningSpeed + 0.25f;
        float physicalReleaseFaceX = wall.IsDetected
            ? wall.FaceX
            : automaticTargetLeft;
        float wallReleaseTravelBias = Mathf.Clamp(
            physicalReleaseFaceX - state.PlayerPosition.x,
            0.14f,
            1.0f);
        JumpPhysicsSnapshot mandatoryFacePhysics =
            BuildMandatoryFacePlanningPhysics(observedWallPhysics);
        float contactFeetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        float playerHeight = player.playerCollider != null
            ? Mathf.Clamp(player.playerCollider.bounds.size.y, 0.75f, 1.75f)
            : 1.18f;
        // Geometry identity and remaining climb are different questions. A
        // body near the lip can already be above TargetTop-0.10 while an
        // uncollected objective remains above the same physical wall. Keep
        // scanning/caching against the frozen wall in that final interval.
        float physicalWallLeft = wallRecoveryPhysicalLipFrozen
            ? wallRecoveryPhysicalLeft
            : automaticTargetLeft;
        float physicalWallRight = wallRecoveryPhysicalLipFrozen
            ? wallRecoveryPhysicalRight
            : automaticTargetRight;
        float physicalWallTop = wallRecoveryPhysicalLipFrozen
            ? wallRecoveryPhysicalLipY + 0.20f
            : automaticTargetTop;
        float physicalWallSafeLeft = wallRecoveryPhysicalLipFrozen
            ? wallRecoveryPhysicalSafeLeft
            : automaticTargetSafeLeft;
        float physicalWallSafeRight = wallRecoveryPhysicalLipFrozen
            ? wallRecoveryPhysicalSafeRight
            : automaticTargetSafeRight;
        bool hasKnownWallGeometry =
            physicalWallRight > physicalWallLeft + 0.05f;
        bool hasKnownWallTarget =
            hasKnownWallGeometry &&
            physicalWallTop > contactFeetY + 0.10f;
        bool isGround3ObjectiveFace =
            state.SectionIndex == 1 &&
            string.Equals(
                automaticTargetMapPieceName,
                "Ground 3",
                StringComparison.OrdinalIgnoreCase) &&
            (automaticTargetStaticSurfaceIndex == 3 ||
             automaticTargetStaticSurfaceIndex < 0 &&
             !float.IsNaN(automaticTargetMapPieceOriginX) &&
             Mathf.Abs(
                 automaticTargetLeft -
                  (automaticTargetMapPieceOriginX + 1f)) <= 0.30f);
        BonusBoardSegment mandatoryCurrentWall =
            BuildAutomaticTargetSegment() with
            {
                Left = physicalWallLeft,
                Right = physicalWallRight,
                Top = physicalWallTop,
                SafeLeft = physicalWallSafeLeft,
                SafeRight = physicalWallSafeRight
            };
        string mandatoryRouteIdentityReason = "ContractNotActive";
        bool isGround3MandatoryFaceRoute =
            wallExitFaceContactRequired &&
            wallExitTargetActive &&
            IsVerifiedGround3MandatoryFaceGeometry(
                state,
                mandatoryCurrentWall,
                wallExitTarget,
                out mandatoryRouteIdentityReason);
        float remainingRise = hasKnownWallTarget
            ? physicalWallTop - contactFeetY
            : 0f;
        int wallSphereCount = 0;
        float wallSphereMinimumY = float.PositiveInfinity;
        float wallSphereMaximumY = float.NegativeInfinity;
        bool wallSphereScanSucceeded = false;
        bool hasLiveWallSpheres = hasKnownWallGeometry &&
            BonusStageInspector.TryGetActiveSphereVerticalBounds(
                physicalWallLeft - 1.55f,
                physicalWallRight + 0.8f,
                out wallSphereCount,
                out wallSphereMinimumY,
                out wallSphereMaximumY,
                out wallSphereScanSucceeded);
        bool objectiveCacheMatchesTarget =
            wallObjectiveCacheActive &&
            Mathf.Abs(
                wallObjectiveCacheTargetLeft - physicalWallLeft) <= 0.20f &&
            Mathf.Abs(
                wallObjectiveCacheTargetRight - physicalWallRight) <= 0.20f &&
            Mathf.Abs(
                wallObjectiveCacheTargetTop - physicalWallTop) <= 0.35f;
        bool retainedWallObjectiveAfterScanFailure = false;
        if (hasLiveWallSpheres)
        {
            wallObjectiveCacheActive = true;
            wallObjectiveCacheTargetLeft = physicalWallLeft;
            wallObjectiveCacheTargetRight = physicalWallRight;
            wallObjectiveCacheTargetTop = physicalWallTop;
            wallObjectiveCacheCount = wallSphereCount;
            wallObjectiveCacheMinimumY = wallSphereMinimumY;
            wallObjectiveCacheMaximumY = wallSphereMaximumY;
        }
        else if (wallSphereScanSucceeded)
        {
            // A successful empty scan is authoritative: the objective was
            // collected or the target lane is genuinely empty.
            ResetWallObjectiveCache();
        }
        else if (hasKnownWallGeometry && objectiveCacheMatchesTarget)
        {
            // FindObjectsOfType can fail transiently during pooled object
            // churn. Do not turn one failed observation into a lower release
            // objective in the middle of an attached sequence.
            wallSphereCount = wallObjectiveCacheCount;
            wallSphereMinimumY = wallObjectiveCacheMinimumY;
            wallSphereMaximumY = wallObjectiveCacheMaximumY;
            retainedWallObjectiveAfterScanFailure = true;
        }
        bool hasWallSpheres =
            hasLiveWallSpheres || retainedWallObjectiveAfterScanFailure;
        // Spheres are route objectives, not merely diagnostics. On scoring
        // walls (notably section three) extend the climb only as far as the
        // highest active row requires. Use the same feet-to-pickup envelope
        // as trajectory scoring; the old 0.35 allowance demanded about 1.8
        // units of fictitious extra climb and rejected safe wall landings.
        float sphereRequiredRise = hasWallSpheres
            ? Mathf.Max(
                0f,
                wallSphereMaximumY -
                WallSpherePickupAboveFeet -
                contactFeetY)
            : 0f;
        if (hasKnownWallGeometry && sphereRequiredRise > 0.05f)
        {
            // Near the physical lip the top itself may no longer require an
            // ascent, but an uncollected cached objective still does. Keep
            // the target-aware solver active for that last press.
            hasKnownWallTarget = true;
        }
        float plannedWallRise = Mathf.Max(remainingRise, sphereRequiredRise);
        if (!wallRecoveryPhysicalLipFrozen)
        {
            // Freeze the physical release boundary when this contacted wall
            // first becomes authoritative. Later composite-collider refreshes
            // may update automaticTargetTop for planning diagnostics, but they
            // cannot move the lip of an action already in progress.
            wallRecoveryPhysicalLipY = automaticTargetTop - 0.20f;
            wallRecoveryPhysicalLeft = automaticTargetLeft;
            wallRecoveryPhysicalRight = automaticTargetRight;
            wallRecoveryPhysicalSafeLeft = automaticTargetSafeLeft;
            wallRecoveryPhysicalSafeRight = automaticTargetSafeRight;
            wallRecoveryPhysicalLipFrozen = true;
        }
        wallRecoveryRequiredReleaseY = hasWallSpheres
            ? Mathf.Max(
                wallRecoveryPhysicalLipY,
                wallSphereMaximumY - WallSpherePickupAboveFeet)
            : wallRecoveryPhysicalLipY;

        if (TryManageGround5HighestPillarSink(
                state,
                wall,
                contactFeetY,
                hasWallSpheres,
                wallSphereCount,
                wallSphereMinimumY,
                wallSphereMaximumY))
        {
            return true;
        }

        if ((wallMandatoryFaceSetupActive ||
             wallMandatoryFaceInterceptCommitted) &&
            !isGround3MandatoryFaceRoute)
        {
            return FailMandatoryFacePlan(
                state,
                "RouteIdentityChangedAfterTargetCapture",
                mandatoryRouteIdentityReason);
        }
        if (wallExitFaceContactRequired &&
            wallExitTargetActive &&
            !isGround3MandatoryFaceRoute)
        {
            return FailMandatoryFacePlan(
                state,
                "MandatoryContractGeometryInvalid",
                mandatoryRouteIdentityReason);
        }

        // Contact alone is not vertical stall.  V0.27 issued repeated wall
        // DOWN edges at VY=+17.253, even when passive rise would cross the lip
        // in roughly one physics step.  The manual reference keeps the input
        // released until the player reaches the lip or actually starts to
        // descend.  Apply that invariant to both same-face continuations and
        // newly contacted downstream faces before closing the prior sample.
        if (TryDeferWallPressForResidualRise(
                state,
                contactFeetY,
                wallPhysics,
                "ConfirmedWallContact"))
        {
            return true;
        }

        if (isGround3MandatoryFaceRoute &&
            wallMandatoryFaceInterceptCommitted &&
            contactFeetY < wallRecoveryPhysicalLipY - 0.01f)
        {
            BonusRunnerLog.Warning(
                $"MandatoryFaceInterceptFailed Reason=" +
                $"ApexOrDescentStillBelowOldLip, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY={contactFeetY:F3}, " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), PhysicalLipY=" +
                $"{wallRecoveryPhysicalLipY:F3}, ObjectiveY=" +
                $"{wallRecoveryRequiredReleaseY:F3}, PlannedContact=" +
                $"[Y={wallMandatoryFacePredictedContactFeetY:F3}," +
                $"VY={wallMandatoryFacePredictedContactVelocityY:F3}," +
                $"T={wallMandatoryFacePredictedContactSeconds:F3}]. " +
                "FailureDomain=ActionDelivery; a third blind DOWN is forbidden.");
            automaticTrajectoryCompatible = false;
            FinishLearningSample(state, "MandatoryFaceInterceptBelowLip");
            ResetAutomaticControlState();
            nextAutomaticAttemptTime = Time.unscaledTime + 0.20f;
            return true;
        }

        float predictedMaximumRise = 0f;
        float strongestSinglePhaseRise = 0f;
        jumpPlanner.ChooseWallRecoveryHold(
            float.MaxValue,
            wallPhysics,
            out strongestSinglePhaseRise,
            MinimumWallRecoveryHoldSeconds,
            MaximumWallRecoveryHoldSeconds);
        // Phase count is deliberately not precomputed. After every released
        // press, observed wall attachment decides whether another bounce is
        // legal. The old height-derived requirement planned two phases but
        // then silently kept the first commitment alive after contact was
        // already lost.
        float phasePlannedRise = plannedWallRise;
        float wallHold = hasKnownWallTarget
            ? jumpPlanner.ChooseWallRecoveryHold(
                phasePlannedRise,
                wallPhysics,
                out predictedMaximumRise,
                MinimumWallRecoveryHoldSeconds,
                MaximumWallRecoveryHoldSeconds)
            : FallbackWallRecoveryHoldSeconds;
        if (!hasKnownWallTarget)
        {
            jumpPlanner.ChooseWallRecoveryHold(
                0f, wallPhysics, out predictedMaximumRise);
        }

        bool wallExitTransferSelected = false;
        bool wallTopLandingSelected = false;
        bool mandatoryFaceSetupSelected = false;
        bool mandatoryFaceContactPulseSelected = false;
        bool section3CollectionFacePulseSelected = false;
        bool committedExitFaceInterceptSelected = false;
        bool committedExitFaceInterceptModelProven = false;
        bool completionDynamicFaceCandidatePromoted = false;
        bool wallLandingPredictionSelected = false;
        float wallLandingPredictedX = 0f;
        float wallLandingPredictedTravel = 0f;
        float wallLandingPredictedFlightSeconds = 0f;
        string wallTopPlanSummary = string.Empty;
        wallExitPredictedLandingX = 0f;
        wallExitPredictedTravel = 0f;
        wallExitPredictedFlightSeconds = 0f;
        wallExitTransferAcceptedRawBodyFit = false;
        wallExitTransferSafeTolerance = 0f;
        wallExitPlanSummary = string.Empty;
        wallExitTransferCommitted = false;
        wallLandingFlightCommitted = false;
        if (HasCommittedExitFaceFlight())
            wallExitContactWatchActive = false;
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        float mandatoryFaceLipSeconds = 0f;
        float mandatoryFaceTopClearSeconds = 0f;
        float mandatoryFaceContactSeconds = 0f;
        float mandatoryFaceTopClearFeetY = float.NaN;
        float mandatoryFaceContactFeetY = float.NaN;
        float mandatoryFaceContactVelocityY = float.NaN;
        float collectionFaceContactSeconds = 0f;
        float collectionFaceContactFeetY = float.NaN;
        float collectionFaceContactVelocityY = float.NaN;
        float committedExitFaceContactSeconds = 0f;
        float committedExitFaceContactFeetY = float.NaN;
        float committedExitFaceContactVelocityY = float.NaN;
        string committedExitFaceSummary = string.Empty;
        int mandatorySetupMinimumSteps = 0;
        int mandatorySetupMaximumSteps = 0;
        float mandatorySetupMinimumApexFeetY = float.NaN;
        float mandatorySetupMaximumApexFeetY = float.NaN;
        string mandatoryFacePlanSummary = string.Empty;
        float mandatoryFaceBottomY = wallExitTargetActive
            ? wallExitTarget.Top - Ground3ObjectiveFaceHeight
            : float.NaN;
        float mandatoryFaceMinimumFeetY = mandatoryFaceBottomY +
            MandatoryWallFaceMinimumContactOffset;
        float mandatoryFaceMaximumFeetY = wallExitTargetActive
            ? wallExitTarget.Top -
                MandatoryWallFaceMaximumContactTopClearance
            : float.NaN;
        float mandatoryFacePreferredFeetY = mandatoryFaceBottomY +
            MandatoryWallFacePreferredContactOffset;

        if (isGround3MandatoryFaceRoute)
        {
            BonusBoardSegment currentWall = mandatoryCurrentWall;
            bool exactMandatoryCurrentWallContact =
                wall.IsDetected &&
                wall.IsTouching &&
                wall.Normal.x <= -0.50f &&
                Mathf.Abs(wall.FaceX - currentWall.Left) <= 0.35f &&
                state.PlayerPosition.x <= wall.FaceX + 0.05f &&
                (currentWall.ColliderInstanceId == 0 ||
                 wall.ColliderInstanceId == currentWall.ColliderInstanceId);
            mandatoryFaceContactPulseSelected =
                jumpPlanner.TryChooseWallFaceInterceptHold(
                    state.PlayerPosition.x,
                    contactFeetY,
                    currentWall,
                    wallRecoveryPhysicalLipY,
                    wallRecoveryRequiredReleaseY,
                    wallExitTarget,
                    inferredPlayerHalfWidth,
                    GetWallRouteSpeed(),
                    mandatoryFaceMinimumFeetY,
                    mandatoryFaceMaximumFeetY,
                    mandatoryFacePreferredFeetY,
                    mandatoryFacePhysics,
                    MinimumWallLandingHoldSeconds,
                    MaximumWallLandingHoldSeconds,
                    true,
                    1,
                    out float faceInterceptHold,
                    out mandatoryFaceLipSeconds,
                    out mandatoryFaceTopClearSeconds,
                    out mandatoryFaceContactSeconds,
                    out mandatoryFaceTopClearFeetY,
                    out mandatoryFaceContactFeetY,
                    out mandatoryFaceContactVelocityY,
                    out mandatoryFacePlanSummary);
            if (!mandatoryFaceContactPulseSelected &&
                exactMandatoryCurrentWallContact)
            {
                string robustFacePlanSummary = mandatoryFacePlanSummary;
                mandatoryFaceContactPulseSelected =
                    jumpPlanner.TryChooseWallFaceInterceptHold(
                        state.PlayerPosition.x,
                        contactFeetY,
                        currentWall,
                        wallRecoveryPhysicalLipY,
                        wallRecoveryRequiredReleaseY,
                        wallExitTarget,
                        inferredPlayerHalfWidth,
                        GetWallRouteSpeed(),
                        mandatoryFaceMinimumFeetY,
                        mandatoryFaceMaximumFeetY,
                        mandatoryFacePreferredFeetY,
                        mandatoryFacePhysics,
                        MinimumWallLandingHoldSeconds,
                        MaximumWallLandingHoldSeconds,
                        true,
                        0,
                        out faceInterceptHold,
                        out mandatoryFaceLipSeconds,
                        out mandatoryFaceTopClearSeconds,
                        out mandatoryFaceContactSeconds,
                        out mandatoryFaceTopClearFeetY,
                        out mandatoryFaceContactFeetY,
                        out mandatoryFaceContactVelocityY,
                        out string exactStepFacePlanSummary);
                mandatoryFacePlanSummary =
                    $"RobustTolerance[Rejected:{robustFacePlanSummary}] " +
                    $"ExactContactStep[{exactStepFacePlanSummary}]";
                if (mandatoryFaceContactPulseSelected)
                {
                    BonusRunnerLog.Warning(
                        $"MandatoryFaceExactStepFallbackSelected Position=" +
                        $"({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), FeetY=" +
                        $"{contactFeetY:F3}, Velocity=" +
                        $"({state.PlayerVelocity.x:F3}," +
                        $"{state.PlayerVelocity.y:F3}), Hold=" +
                        $"{faceInterceptHold:F3}s, PredictedContact=" +
                        $"[Y={mandatoryFaceContactFeetY:F3},VY=" +
                        $"{mandatoryFaceContactVelocityY:F3},T=" +
                        $"{mandatoryFaceContactSeconds:F3}], FaceWindow=" +
                        $"[{mandatoryFaceMinimumFeetY:F3}," +
                        $"{mandatoryFaceMaximumFeetY:F3}]. The robust +/-1 " +
                        "horizontal-step envelope rejected the route, but " +
                        "the exact mapped contact and fixed-step actuator " +
                        "prove the zero-offset intercept. Physical target " +
                        "contact remains mandatory; this is not landing " +
                        "success.");
                }
            }
            if (mandatoryFaceContactPulseSelected)
            {
                wallHold = faceInterceptHold;
                wallTopLandingSequenceCommitted = false;
                wallLandingPredictionSelected = false;
                predictedMaximumRise = 0f;
                jumpPlanner.ChooseWallRecoveryHold(
                    plannedWallRise,
                    mandatoryFacePhysics,
                    out predictedMaximumRise,
                    faceInterceptHold,
                    faceInterceptHold);
            }
            else if (wallMandatoryFaceSetupActive)
            {
                if (mandatoryFaceSetupPhysicsExpired)
                {
                    return FailMandatoryFacePlan(
                        state,
                        "SetupPhysicsBudgetExpired",
                        $"Position=({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), FeetY=" +
                        $"{contactFeetY:F3}, Velocity=" +
                        $"({state.PlayerVelocity.x:F3}," +
                        $"{state.PlayerVelocity.y:F3}), FixedStep=" +
                        $"{JumpPhysicsFeedback.FixedStepSequence}, " +
                        $"DeadlineStep=" +
                        $"{wallMandatoryFaceSetupDeadlineFixedStep}, " +
                        $"Candidates[{mandatoryFacePlanSummary}]");
                }
                if (BonusRunnerLog.IsDebugMode &&
                    Time.unscaledTime >= nextMandatoryFaceWindowLogTime)
                {
                    nextMandatoryFaceWindowLogTime =
                        Time.unscaledTime + 0.08f;
                    BonusRunnerLog.Debug(
                        $"MandatoryFaceInterceptWindowWait Position=" +
                        $"({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), FeetY=" +
                        $"{contactFeetY:F3}, Velocity=" +
                        $"({state.PlayerVelocity.x:F3}," +
                        $"{state.PlayerVelocity.y:F3}), OldWall=" +
                        $"[{currentWall.Left:F3}," +
                        $"{currentWall.Right:F3}]@" +
                        $"{currentWall.Top:F3}, TargetFace=" +
                        $"X={wallExitTarget.Left - inferredPlayerHalfWidth:F3}," +
                        $"Y=[{mandatoryFaceBottomY:F3}," +
                        $"{wallExitTarget.Top:F3}], SafeFeet=" +
                        $"[{mandatoryFaceMinimumFeetY:F3}," +
                        $"{mandatoryFaceMaximumFeetY:F3}], Preferred=" +
                        $"{mandatoryFacePreferredFeetY:F3}, PlayerHeight=" +
                        $"{playerHeight:F3}, RemainingFixedSteps=" +
                        $"{Math.Max(0L, wallMandatoryFaceSetupDeadlineFixedStep - JumpPhysicsFeedback.FixedStepSequence)}, " +
                        $"Candidates[{mandatoryFacePlanSummary}]. " +
                        "Action=UP; wait on the confirmed old face and solve " +
                        "again from the next observed descent frame.",
                        "Recovery");
                }
                wallStallStartedAt =
                    Time.unscaledTime - WallStallConfirmationSeconds;
                jumpController.Release();
                return true;
            }
            else
            {
                mandatoryFaceSetupSelected =
                    jumpPlanner.TryChooseWallFaceSetupHold(
                        contactFeetY,
                        wallRecoveryPhysicalLipY,
                        mandatoryFacePhysics,
                        MinimumMandatoryFaceSetupHoldSeconds,
                        MaximumAttachedWallPulseHoldSeconds,
                        MandatoryWallFaceSetupSafetyMargin,
                        out float setupHold,
                        out mandatorySetupMinimumSteps,
                        out mandatorySetupMaximumSteps,
                        out mandatorySetupMinimumApexFeetY,
                        out mandatorySetupMaximumApexFeetY,
                        out string setupSummary);
                mandatoryFacePlanSummary =
                    $"Direct[{mandatoryFacePlanSummary}] Setup[{setupSummary}]";
                if (!mandatoryFaceSetupSelected)
                {
                    BonusRunnerLog.Warning(
                        $"MandatoryFacePlanUnavailable Position=" +
                        $"({state.PlayerPosition.x:F3}," +
                        $"{state.PlayerPosition.y:F3}), FeetY=" +
                        $"{contactFeetY:F3}, Velocity=" +
                        $"({state.PlayerVelocity.x:F3}," +
                        $"{state.PlayerVelocity.y:F3}), ReleaseY=" +
                        $"{wallRecoveryRequiredReleaseY:F3}, FaceWindow=" +
                        $"[{mandatoryFaceMinimumFeetY:F3}," +
                        $"{mandatoryFaceMaximumFeetY:F3}], " +
                        $"Candidates[{mandatoryFacePlanSummary}]. " +
                        "FailureDomain=Planner; no blind fallback pulse is sent.");
                    automaticTrajectoryCompatible = false;
                    FinishLearningSample(
                        state,
                        "MandatoryFacePlanUnavailable");
                    ResetAutomaticControlState();
                    nextAutomaticAttemptTime = Time.unscaledTime + 0.20f;
                    return true;
                }

                wallHold = setupHold;
                wallTopLandingSequenceCommitted = false;
                wallLandingPredictionSelected = false;
                predictedMaximumRise =
                    mandatorySetupMaximumApexFeetY - contactFeetY;
            }
        }
        float holdUntilWallLip = Mathf.Clamp(
            wallPhysics.InputDelaySeconds +
            Mathf.Max(0f, remainingRise - 0.20f) /
                Mathf.Max(1f, wallPhysics.JumpVelocity),
            MinimumWallLandingHoldSeconds,
            MaximumWallLandingHoldSeconds);
        // Any attached wall press can be the exit press. In the captured
        // failure the first press already crossed a four-unit lip; waiting for
        // a nominal "phase two" meant the runner had already flown beyond the
        // wall. Prefer a direct transfer to the authored downstream support.
        if (wallExitTargetActive &&
            !wallExitFaceContactRequired &&
            !wallTopLandingSequenceCommitted &&
            (!isGround3ObjectiveFace || remainingRise <= 2.25f))
        {
            BonusBoardSegment currentWall = mandatoryCurrentWall;
            bool completionTerrainTraversal =
                completionWallExitTraversal;
            bool terrainRouteActive =
                state.IsActiveGameplay || completionTerrainTraversal;
            bool mappedGround7NarrowWall =
                string.Equals(
                    currentWall.MapPieceName,
                    "Ground 7",
                    StringComparison.OrdinalIgnoreCase) &&
                currentWall.StaticSurfaceIndex == 2;
            if (mappedGround7NarrowWall)
            {
                // Ground 7's retained trace measured the held wall velocity
                // after gravity (18.627), not the raw configured force (20),
                // and its target-height crossing was shorter than the raw
                // analytical flight.  Use those observed quantities for this
                // failing transfer only.  Other wall routes keep their proven
                // V0.33 model. A normal Ground 7 undershoot is no longer
                // mislabeled as a landing; it remains a face-contact route.
                wallExitPhysics = BuildWallExitPlanningPhysics(
                    mandatoryFacePhysics,
                    wallExitPlanningSpeed,
                    sectionCruiseHorizontalSpeed,
                    terrainRouteActive &&
                        (!state.SpiritBoostEnabled ||
                         state.IsActiveGameplay)) with
                {
                    FlightTimeScale = Mathf.Clamp(
                        observedWallPhysics.FlightTimeScale,
                        0.75f,
                        1.15f)
                };
            }
            bool speedAboveEstablishedCruise =
                sectionCruiseHorizontalSpeed > 1f &&
                postLipHorizontalSpeed >
                    sectionCruiseHorizontalSpeed +
                    Mathf.Max(1.0f, sectionCruiseHorizontalSpeed * 0.10f);
            bool strictBoostLanding =
                state.SpiritBoostEnabled || speedAboveEstablishedCruise;
            bool normalSection3CollectionExit =
                state.IsActiveGameplay &&
                state.SectionIndex == 3 &&
                mappedGround7NarrowWall &&
                !state.SpiritBoostEnabled &&
                !speedAboveEstablishedCruise;
            float transferMaximumHold = normalSection3CollectionExit
                ? 0.135f
                : MaximumWallLandingHoldSeconds;

            // In normal section 4, a top-landing transfer skips the low soul
            // lane and the measured landing ran 5.82 units farther than the
            // model. Prefer the section-3 style route requested by the user:
            // a smaller pulse that clears the current lip, descends into the
            // next vertical face, then lets physical wall contact start the
            // normal bounded climb controller. Boost mode retains strict top
            // landing because its larger horizontal speed can make a low-face
            // intercept unsafe.
            float collectionFaceBottomY = wallExitTarget.Top - 4.50f;
            float collectionFaceMinimumFeetY =
                collectionFaceBottomY + 0.35f;
            float collectionFaceMaximumFeetY =
                wallExitTarget.Top - 0.45f;
            float collectionFacePreferredFeetY =
                wallExitTarget.Top - 2.00f;
            string collectionFaceSummary = string.Empty;
            if (normalSection3CollectionExit &&
                state.HasSphereProgress &&
                state.RemainingRequiredSpheres > 0 &&
                wallExitTarget.Left > currentWall.Right + 0.10f)
            {
                section3CollectionFacePulseSelected =
                    jumpPlanner.TryChooseWallFaceInterceptHold(
                        state.PlayerPosition.x,
                        contactFeetY,
                        currentWall,
                        wallRecoveryPhysicalLipY,
                        wallRecoveryRequiredReleaseY,
                        wallExitTarget,
                        inferredPlayerHalfWidth,
                        wallExitPlanningSpeed,
                        collectionFaceMinimumFeetY,
                        collectionFaceMaximumFeetY,
                        collectionFacePreferredFeetY,
                        wallExitPhysics,
                        MinimumWallLandingHoldSeconds,
                        transferMaximumHold,
                        true,
                        1,
                        out float collectionFaceHold,
                        out _,
                        out _,
                        out collectionFaceContactSeconds,
                        out _,
                        out collectionFaceContactFeetY,
                        out collectionFaceContactVelocityY,
                        out collectionFaceSummary);
                if (section3CollectionFacePulseSelected)
                {
                    wallExitTransferSelected = true;
                    wallHold = collectionFaceHold;
                    wallExitPredictedLandingX =
                        wallExitTarget.Left - inferredPlayerHalfWidth;
                    wallExitPredictedTravel = Mathf.Max(
                        0f,
                        wallExitPredictedLandingX - state.PlayerPosition.x);
                    wallExitPredictedFlightSeconds =
                        collectionFaceContactSeconds;
                    wallExitPlanSummary =
                        $"Section3CollectionFace[Hold={collectionFaceHold:F3}," +
                        $"ContactY={collectionFaceContactFeetY:F3}," +
                        $"ContactVY={collectionFaceContactVelocityY:F3}," +
                        $"Window=[{collectionFaceMinimumFeetY:F3}," +
                        $"{collectionFaceMaximumFeetY:F3}]] " +
                        collectionFaceSummary;
                }
            }
            float wallExitSafeTolerance = strictBoostLanding
                ? 0.02f
                : 0.20f;
            bool preparedTargetMatches =
                wallExitPreparedPlanActive &&
                Mathf.Abs(
                    wallExitPreparedTarget.Left -
                    wallExitTarget.Left) <= 0.12f &&
                Mathf.Abs(
                    wallExitPreparedTarget.Right -
                    wallExitTarget.Right) <= 0.12f &&
                Mathf.Abs(
                    wallExitPreparedTarget.Top -
                    wallExitTarget.Top) <= 0.12f;
            bool preparedSpeedMatches =
                preparedTargetMatches &&
                Mathf.Abs(
                    wallExitPreparedSpeed -
                    postLipHorizontalSpeed) <=
                Mathf.Max(1.50f, postLipHorizontalSpeed * 0.15f);
            bool applyPreparedMinimumHold =
                !strictBoostLanding && preparedSpeedMatches;
            float transferMinimumHold = applyPreparedMinimumHold
                ? Mathf.Clamp(
                    wallExitPreparedPlan.HoldSeconds,
                    MinimumWallLandingHoldSeconds,
                    transferMaximumHold)
                : MinimumWallLandingHoldSeconds;
            bool mappedGround5HighestPillarExit =
                state.SectionIndex == 1 &&
                string.Equals(
                    currentWall.MapPieceName,
                    "Ground 5",
                    StringComparison.OrdinalIgnoreCase) &&
                currentWall.StaticSurfaceIndex == 4 &&
                string.Equals(
                    wallExitTarget.MapPieceName,
                    "Ground 5",
                    StringComparison.OrdinalIgnoreCase) &&
                wallExitTarget.StaticSurfaceIndex == 1 &&
                (currentWall.MapPieceInstanceId != 0 &&
                 currentWall.MapPieceInstanceId ==
                    wallExitTarget.MapPieceInstanceId ||
                 !float.IsNaN(currentWall.MapPieceOriginX) &&
                 !float.IsNaN(wallExitTarget.MapPieceOriginX) &&
                 Mathf.Abs(
                     currentWall.MapPieceOriginX -
                     wallExitTarget.MapPieceOriginX) <= 0.05f) &&
                wallExitTarget.Top <= currentWall.Top - 5.00f;
            bool allowRawBodyFitLanding =
                mappedGround5HighestPillarExit ||
                (!strictBoostLanding &&
                 (preparedSpeedMatches || mappedGround7NarrowWall));
            float transferHold = wallHold;
            string directLandingSummary = string.Empty;
            bool directLandingSelected =
                !section3CollectionFacePulseSelected &&
                TryChooseWallExitTransferHoldWithinSpeedEnvelope(
                    wallExitSpeedEnvelopeRequired,
                    state.PlayerPosition.x,
                    contactFeetY,
                    wallRecoveryPhysicalLipY,
                    wallRecoveryRequiredReleaseY,
                    wallExitTarget,
                    wallExitMinimumPlanningSpeed,
                    wallExitPlanningSpeed,
                    wallExitPhysics,
                    wallReleaseTravelBias,
                    transferMinimumHold,
                    transferMaximumHold,
                    wallExitSafeTolerance,
                    allowRawBodyFitLanding,
                    out transferHold,
                    out wallExitPredictedFlightSeconds,
                    out wallExitPredictedTravel,
                    out wallExitPredictedLandingX,
                    out wallExitTransferAcceptedRawBodyFit,
                    out directLandingSummary);
            wallExitTransferSelected =
                section3CollectionFacePulseSelected ||
                directLandingSelected;
            if (section3CollectionFacePulseSelected)
            {
                transferHold = wallHold;
            }
            else if (wallExitTransferSelected)
                wallExitTransferSafeTolerance = wallExitSafeTolerance;

            wallExitPlanSummary =
                $"Policy[StrictBoost={strictBoostLanding}," +
                $"SpeedAboveCruise={speedAboveEstablishedCruise}," +
                $"SafeTolerance={wallExitSafeTolerance:F3}," +
                $"RawBodyFit={allowRawBodyFitLanding}," +
                $"Ground5HighestPillarExit=" +
                $"{mappedGround5HighestPillarExit}," +
                $"NormalSection3CollectionExit=" +
                $"{normalSection3CollectionExit},MaximumHold=" +
                $"{transferMaximumHold:F3}," +
                $"CollectionFacePulse=" +
                $"{section3CollectionFacePulseSelected}," +
                $"PreparedActive={wallExitPreparedPlanActive}," +
                $"PreparedTargetMatch={preparedTargetMatches}," +
                $"PreparedSpeedMatch={preparedSpeedMatches}," +
                $"PreparedSpeed={wallExitPreparedSpeed:F3}," +
                $"MinimumHold={transferMinimumHold:F3}] " +
                $"CollectionFaceCandidates[{collectionFaceSummary}] " +
                $"DirectLandingCandidates[{directLandingSummary}]";
            bool searchSpeedReachableAlternate =
                (mappedGround7NarrowWall &&
                 (!state.HasSphereProgress ||
                  state.RemainingRequiredSpheres <= 0)) ||
                wallExitAccelerationEnvelopeApplied ||
                completionTerrainTraversal;
            if (!wallExitTransferSelected && searchSpeedReachableAlternate)
            {
                string nearestTargetSummary = wallExitPlanSummary;
                BonusBoardSegment[] speedCandidates =
                    platformScanner.GetWallExitLandingCandidates(
                        currentWall,
                        inferredPlayerHalfWidth,
                        wallExitPlanningSpeed);
                foreach (BonusBoardSegment candidate in speedCandidates)
                {
                    bool sameAsCurrentExit =
                        Mathf.Abs(candidate.Left - wallExitTarget.Left) <= 0.12f &&
                        Mathf.Abs(candidate.Right - wallExitTarget.Right) <= 0.12f &&
                        Mathf.Abs(candidate.Top - wallExitTarget.Top) <= 0.12f;
                    if (sameAsCurrentExit)
                        continue;

                    bool candidateSelected =
                        TryChooseWallExitTransferHoldWithinSpeedEnvelope(
                            wallExitSpeedEnvelopeRequired,
                            state.PlayerPosition.x,
                            contactFeetY,
                            wallRecoveryPhysicalLipY,
                            wallRecoveryRequiredReleaseY,
                            candidate,
                            wallExitMinimumPlanningSpeed,
                            wallExitPlanningSpeed,
                            wallExitPhysics,
                            wallReleaseTravelBias,
                            MinimumWallLandingHoldSeconds,
                            transferMaximumHold,
                            strictBoostLanding ? 0.02f : 0.20f,
                            false,
                            out float candidateHold,
                            out float candidateFlight,
                            out float candidateTravel,
                            out float candidateLandingX,
                            out _,
                            out string candidateSummary);
                    nearestTargetSummary +=
                        $" | AlternateTarget=[{candidate.Left:F3}," +
                        $"{candidate.Right:F3}]@{candidate.Top:F3}" +
                        $"[{candidateSummary}]";
                    if (!candidateSelected)
                        continue;

                    string alternateSource = completionTerrainTraversal
                        ? "CompletionDynamicAlternate"
                        : wallExitAccelerationEnvelopeApplied
                            ? "AcceleratingCompletionAlternate"
                            : "Ground7SpeedReachableAlternate";
                    if (!ConfigureWallExitRouteContract(
                            currentWall,
                            candidate,
                            alternateSource))
                    {
                        continue;
                    }

                    BonusBoardSegment previousExit = wallExitTarget;
                    wallExitTarget = candidate;
                    wallExitTargetActive = true;
                    ClearWallExitPreparedContract();
                    transferHold = candidateHold;
                    wallExitPredictedFlightSeconds = candidateFlight;
                    wallExitPredictedTravel = candidateTravel;
                    wallExitPredictedLandingX = candidateLandingX;
                    wallExitTransferAcceptedRawBodyFit = false;
                    wallExitTransferSafeTolerance =
                        strictBoostLanding ? 0.02f : 0.20f;
                    wallExitTransferSelected = true;
                    BonusRunnerLog.Debug(
                        $"WallExitTargetSpeedPromoted Section={state.SectionIndex}, " +
                        $"Source={alternateSource}, " +
                        $"ObservedPostLipVX={postLipHorizontalSpeed:F3}, " +
                        $"PlanningVX={wallExitPlanningSpeed:F3}, " +
                        $"AccelerationEnvelopeApplied=" +
                        $"{wallExitAccelerationEnvelopeApplied}, From=" +
                        $"[{previousExit.Left:F3},{previousExit.Right:F3}]" +
                        $"@{previousExit.Top:F3}, To=[{candidate.Left:F3}," +
                        $"{candidate.Right:F3}]@{candidate.Top:F3}, Hold=" +
                        $"{candidateHold:F3}s, PredictedLanding=" +
                        $"{candidateLandingX:F3}. The nearest platform has no " +
                        "safe hold at the current speed; the first statically " +
                        "reachable downstream support becomes authoritative.",
                        "Routing");
                    break;
                }
                wallExitPlanSummary = nearestTargetSummary;
            }

            // Completion traversal is intentionally allowed to use geometry
            // that the authored collection route did not need.  A strict top
            // landing can fail even though the player's body can still meet a
            // later platform's finite left face while descending.  The V0.46
            // Section-1 trace is the canonical example: [699,701]@7 cannot
            // retain a 20.6-unit/s runner on its own top and no hold lands on
            // [703,710]@-2, but [714,718]@4 is a reachable wall contact.  Test
            // every bounded downstream candidate with the same fixed-step
            // face solver used by established wall routes.  This remains a
            // separately typed FaceOrTop outcome; it never weakens a landing
            // margin and it is unavailable before the sphere quota is met.
            if (!wallExitTransferSelected && completionTerrainTraversal)
            {
                BonusBoardSegment[] completionFaceCandidates =
                    platformScanner.GetWallExitLandingCandidates(
                        currentWall,
                        inferredPlayerHalfWidth,
                        wallExitPlanningSpeed);
                string completionFaceSearchSummary = string.Empty;
                foreach (BonusBoardSegment candidate in completionFaceCandidates)
                {
                    bool sameAsCurrentExit =
                        Mathf.Abs(candidate.Left - wallExitTarget.Left) <= 0.12f &&
                        Mathf.Abs(candidate.Right - wallExitTarget.Right) <= 0.12f &&
                        Mathf.Abs(candidate.Top - wallExitTarget.Top) <= 0.12f;
                    float candidateFaceGap = candidate.Left - currentWall.Right;
                    bool boundedFaceGeometry =
                        !sameAsCurrentExit &&
                        candidateFaceGap > 5.25f &&
                        candidateFaceGap <= CommittedExitFaceMaximumGap &&
                        candidate.Top <= currentWall.Top + 0.35f &&
                        candidate.Top >= currentWall.Top - 3.25f;
                    if (!boundedFaceGeometry)
                    {
                        completionFaceSearchSummary +=
                            $"Target=[{candidate.Left:F3},{candidate.Right:F3}]" +
                            $"@{candidate.Top:F3}:GeometryReject[SameImmediate=" +
                            $"{sameAsCurrentExit},Gap={candidateFaceGap:F3}," +
                            $"HeightDelta={candidate.Top - currentWall.Top:F3}] | ";
                        continue;
                    }

                    float candidateMinimumFaceFeetY =
                        candidate.Top - CommittedExitFaceDepth +
                        CommittedExitFaceBottomClearance;
                    float candidateMaximumFaceFeetY =
                        candidate.Top - CommittedExitFaceTopClearance;
                    float candidatePreferredFaceFeetY =
                        candidate.Top - CommittedExitFacePreferredDepth;
                    bool candidateFaceSelected =
                        TryChooseWallFaceInterceptHoldWithinSpeedEnvelope(
                            true,
                            state.PlayerPosition.x,
                            contactFeetY,
                            currentWall,
                            wallRecoveryPhysicalLipY,
                            wallRecoveryRequiredReleaseY,
                            candidate,
                            inferredPlayerHalfWidth,
                            wallExitMinimumPlanningSpeed,
                            wallExitPlanningSpeed,
                            candidateMinimumFaceFeetY,
                            candidateMaximumFaceFeetY,
                            candidatePreferredFaceFeetY,
                            wallExitPhysics,
                            transferMinimumHold,
                            transferMaximumHold,
                            true,
                            1,
                            out float candidateFaceHold,
                            out _,
                            out _,
                            out float candidateFaceContactSeconds,
                            out _,
                            out float candidateFaceContactFeetY,
                            out float candidateFaceContactVelocityY,
                            out string candidateFaceSummary);
                    completionFaceSearchSummary +=
                        $"Target=[{candidate.Left:F3},{candidate.Right:F3}]" +
                        $"@{candidate.Top:F3},Gap={candidateFaceGap:F3}," +
                        $"FaceWindow=[{candidateMinimumFaceFeetY:F3}," +
                        $"{candidateMaximumFaceFeetY:F3}],Selected=" +
                        $"{candidateFaceSelected},Candidates[" +
                        $"{candidateFaceSummary}] | ";
                    if (!candidateFaceSelected ||
                        !ConfigureWallExitRouteContract(
                            currentWall,
                            candidate,
                            "CompletionDynamicFaceIntercept"))
                    {
                        continue;
                    }

                    BonusBoardSegment previousExit = wallExitTarget;
                    wallExitTarget = candidate;
                    wallExitTargetActive = true;
                    ClearWallExitPreparedContract();
                    transferHold = candidateFaceHold;
                    committedExitFaceContactSeconds =
                        candidateFaceContactSeconds;
                    committedExitFaceContactFeetY = candidateFaceContactFeetY;
                    committedExitFaceContactVelocityY =
                        candidateFaceContactVelocityY;
                    committedExitFaceSummary = candidateFaceSummary;
                    wallExitPredictedLandingX =
                        candidate.Left - inferredPlayerHalfWidth;
                    wallExitPredictedTravel = Mathf.Max(
                        0f,
                        wallExitPredictedLandingX - state.PlayerPosition.x);
                    wallExitPredictedFlightSeconds =
                        candidateFaceContactSeconds;
                    wallExitTransferAcceptedRawBodyFit = false;
                    wallExitTransferSafeTolerance = 0f;
                    wallExitTransferSelected = true;
                    wallTopLandingSelected = false;
                    wallTopLandingSequenceCommitted = false;
                    committedExitFaceInterceptSelected = true;
                    committedExitFaceInterceptModelProven = true;
                    completionDynamicFaceCandidatePromoted = true;
                    wallExitPlanSummary +=
                        $" | CompletionDynamicFaceSearch[" +
                        $"{completionFaceSearchSummary}]";
                    BonusRunnerLog.Debug(
                        $"CompletionWallExitFacePromoted Section=" +
                        $"{state.SectionIndex}, Current=[{currentWall.Left:F3}," +
                        $"{currentWall.Right:F3}]@{currentWall.Top:F3}, From=" +
                        $"[{previousExit.Left:F3},{previousExit.Right:F3}]" +
                        $"@{previousExit.Top:F3}, ToFace=" +
                        $"[{candidate.Left:F3},{candidate.Right:F3}]" +
                        $"@{candidate.Top:F3}, Gap={candidateFaceGap:F3}, " +
                        $"ObservedPostLipVX={postLipHorizontalSpeed:F3}, " +
                        $"PlanningVXEnvelope=[{wallExitMinimumPlanningSpeed:F3}," +
                        $"{wallExitPlanningSpeed:F3}], Hold=" +
                        $"{candidateFaceHold:F3}s, PredictedContact=" +
                        $"({wallExitPredictedLandingX:F3}," +
                        $"{candidateFaceContactFeetY:F3})/" +
                        $"{candidateFaceContactSeconds:F3}s, ContactVY=" +
                        $"{candidateFaceContactVelocityY:F3}, FaceWindow=" +
                        $"[{candidateMinimumFaceFeetY:F3}," +
                        $"{candidateMaximumFaceFeetY:F3}]. " +
                        "Outcome=FixedStepProvenFaceOrTop; the watched face " +
                        "owns flight until physical contact or verified top " +
                        "support hands control back to the planner.",
                        "Routing");
                    break;
                }

                if (!completionDynamicFaceCandidatePromoted)
                {
                    wallExitPlanSummary +=
                        $" | CompletionDynamicFaceSearch[" +
                        $"{completionFaceSearchSummary}]";
                }
            }

            // V0.98 proved that the former completed-section Spirit
            // best-effort branch was not a safe route: it selected a 0.060s
            // direct landing after explicitly finding no hold safe at both
            // speed endpoints, then overshot the target by about nine units.
            // Terrain routing must remain proof-based until a typed reward
            // object is latched.  Keep the diagnostic search below available
            // for debug comparison, but it is no longer allowed to own input
            // when the full envelope has already rejected the transfer.
            bool sectionOneSpiritResumeRecovery =
                false;
            if (sectionOneSpiritResumeRecovery)
            {
                BonusBoardSegment[] spiritResumeCandidates =
                    platformScanner.GetWallExitLandingCandidates(
                        currentWall,
                        inferredPlayerHalfWidth,
                        wallExitPlanningSpeed);
                string spiritResumeSummary = string.Empty;
                foreach (BonusBoardSegment candidate in spiritResumeCandidates)
                {
                    bool candidateSelected =
                        jumpPlanner.TryChooseWallExitTransferHold(
                            state.PlayerPosition.x,
                            contactFeetY,
                            wallRecoveryPhysicalLipY,
                            wallRecoveryRequiredReleaseY,
                            candidate,
                            wallExitPlanningSpeed,
                            wallExitPhysics,
                            wallReleaseTravelBias,
                            MinimumWallLandingHoldSeconds,
                            transferMaximumHold,
                            0.02f,
                            false,
                            out float candidateHold,
                            out float candidateFlight,
                            out float candidateTravel,
                            out float candidateLandingX,
                            out _,
                            out string candidateSummary);
                    spiritResumeSummary +=
                        $"Target=[{candidate.Left:F3}," +
                        $"{candidate.Right:F3}]@{candidate.Top:F3}," +
                        $"Selected={candidateSelected},Candidates[" +
                        $"{candidateSummary}] | ";
                    if (!candidateSelected)
                        continue;

                    bool sameAsCurrentExit =
                        Mathf.Abs(candidate.Left - wallExitTarget.Left) <= 0.12f &&
                        Mathf.Abs(candidate.Right - wallExitTarget.Right) <= 0.12f &&
                        Mathf.Abs(candidate.Top - wallExitTarget.Top) <= 0.12f;
                    if (!sameAsCurrentExit &&
                        !ConfigureWallExitRouteContract(
                            currentWall,
                            candidate,
                            "SectionOneSpiritResumeUpperBound"))
                    {
                        continue;
                    }

                    BonusBoardSegment previousExit = wallExitTarget;
                    wallExitTarget = candidate;
                    wallExitTargetActive = true;
                    ClearWallExitPreparedContract();
                    transferHold = candidateHold;
                    wallExitPredictedFlightSeconds = candidateFlight;
                    wallExitPredictedTravel = candidateTravel;
                    wallExitPredictedLandingX = candidateLandingX;
                    wallExitTransferAcceptedRawBodyFit = false;
                    wallExitTransferSafeTolerance = 0.02f;
                    wallExitTransferSelected = true;
                    wallExitPlanSummary +=
                        $" | SectionOneSpiritResumeRecovery[" +
                        $"{spiritResumeSummary}]";
                    BonusRunnerLog.Warning(
                        $"SectionOneSpiritResumeRecoverySelected Current=" +
                        $"[{currentWall.Left:F3}," +
                        $"{currentWall.Right:F3}]@{currentWall.Top:F3}, From=" +
                        $"[{previousExit.Left:F3}," +
                        $"{previousExit.Right:F3}]@{previousExit.Top:F3}, " +
                        $"To=[{candidate.Left:F3}," +
                        $"{candidate.Right:F3}]@{candidate.Top:F3}, " +
                        $"ObservedWallVX={postLipHorizontalSpeed:F3}, " +
                        $"SpeedEnvelope=[{wallExitMinimumPlanningSpeed:F3}," +
                        $"{wallExitPlanningSpeed:F3}], Hold=" +
                        $"{candidateHold:F3}s, PredictedLanding=" +
                        $"{candidateLandingX:F3}. No single press was safe " +
                        "at both speed extremes; the measured Spirit lip " +
                        "resume tier owns this section-scoped recovery.");
                    break;
                }

                if (!wallExitTransferSelected)
                {
                    wallExitPlanSummary +=
                        $" | SectionOneSpiritResumeRecoveryRejected[" +
                        $"{spiritResumeSummary}]";
                }
            }

            // Strict landing remains authoritative. When it and every legal
            // alternate fail on the exact mapped Ground 7 S2 route, preserve a
            // separately typed result: reach the lower downstream vertical
            // face and let physical contact resume bounded climbing. The
            // fixed-step face solver is preferred and now evaluates the exact
            // powered-step count delivered by the actuator. The exact normal
            // S2/S3 routes also retain their demonstrated native-cap holds as
            // a liveness fallback when the remaining model still rejects a
            // contact that the trace proved. Completion speed may use only
            // the exact current-speed solver, never that normal-speed
            // empirical fallback. This result is never reported as a landing
            // prediction.
            float committedFaceGap =
                wallExitTarget.Left - currentWall.Right;
            bool mappedGround7FaceRoute =
                !wallExitTransferSelected &&
                mappedGround7NarrowWall &&
                terrainRouteActive &&
                (completionTerrainTraversal ||
                 !state.SpiritBoostEnabled &&
                 !speedAboveEstablishedCruise) &&
                (completionTerrainTraversal ||
                 !state.HasSphereProgress ||
                 state.RemainingRequiredSpheres > 0) &&
                committedFaceGap > 5.25f &&
                committedFaceGap <= CommittedExitFaceMaximumGap &&
                wallExitTarget.Top <= currentWall.Top + 0.35f &&
                wallExitTarget.Top >= currentWall.Top - 3.25f;
            if (mappedGround7FaceRoute)
            {
                float minimumFaceFeetY =
                    wallExitTarget.Top - CommittedExitFaceDepth +
                    CommittedExitFaceBottomClearance;
                float maximumFaceFeetY =
                    wallExitTarget.Top - CommittedExitFaceTopClearance;
                float preferredFaceFeetY =
                    wallExitTarget.Top - CommittedExitFacePreferredDepth;
                committedExitFaceInterceptModelProven =
                    TryChooseWallFaceInterceptHoldWithinSpeedEnvelope(
                        wallExitSpeedEnvelopeRequired,
                        state.PlayerPosition.x,
                        contactFeetY,
                        currentWall,
                        wallRecoveryPhysicalLipY,
                        wallRecoveryRequiredReleaseY,
                        wallExitTarget,
                        inferredPlayerHalfWidth,
                        wallExitMinimumPlanningSpeed,
                        wallExitPlanningSpeed,
                        minimumFaceFeetY,
                        maximumFaceFeetY,
                        preferredFaceFeetY,
                        wallExitPhysics,
                        transferMinimumHold,
                        transferMaximumHold,
                        true,
                        1,
                        out float faceHold,
                        out _,
                        out _,
                        out committedExitFaceContactSeconds,
                        out _,
                        out committedExitFaceContactFeetY,
                        out committedExitFaceContactVelocityY,
                        out committedExitFaceSummary);

                float empiricalFaceHold = Mathf.Min(
                    transferMaximumHold,
                    wallExitPhysics.EffectiveHoldCapSeconds);
                bool retainedNormalFaceEvidence =
                    !committedExitFaceInterceptModelProven &&
                    state.IsActiveGameplay &&
                    !completionTerrainTraversal &&
                    (state.SectionIndex == 2 || state.SectionIndex == 3) &&
                    empiricalFaceHold + 0.001f >= transferMinimumHold;
                committedExitFaceInterceptSelected =
                    committedExitFaceInterceptModelProven ||
                    retainedNormalFaceEvidence;
                if (committedExitFaceInterceptSelected)
                {
                    transferHold = committedExitFaceInterceptModelProven
                        ? faceHold
                        : empiricalFaceHold;
                    wallExitTransferSelected = true;
                    wallTopLandingSelected = false;
                    wallTopLandingSequenceCommitted = false;
                    wallExitTransferAcceptedRawBodyFit = false;
                    wallExitTransferSafeTolerance = 0f;
                    wallExitPredictedLandingX =
                        wallExitTarget.Left - inferredPlayerHalfWidth;
                    wallExitPredictedTravel = Mathf.Max(
                        0f,
                        wallExitPredictedLandingX -
                            state.PlayerPosition.x);
                    if (!committedExitFaceInterceptModelProven)
                    {
                        committedExitFaceContactSeconds = Mathf.Clamp(
                            transferHold +
                                wallExitPredictedTravel /
                                Mathf.Max(1f, wallExitPlanningSpeed),
                            0.10f,
                            1.20f);
                    }
                    wallExitPredictedFlightSeconds =
                        committedExitFaceContactSeconds;
                    wallExitPlanSummary +=
                        $" | CommittedFaceIntercept[Mode=" +
                        $"{(committedExitFaceInterceptModelProven ? "FixedStepProven" : "RetainedGround7Evidence")}," +
                        $"Hold={transferHold:F3},Gap={committedFaceGap:F3}," +
                        $"ContactX={wallExitPredictedLandingX:F3}," +
                        $"ContactY={committedExitFaceContactFeetY:F3}," +
                        $"ContactVY={committedExitFaceContactVelocityY:F3}," +
                        $"FaceWindow=[{minimumFaceFeetY:F3}," +
                        $"{maximumFaceFeetY:F3}],Candidates[" +
                        $"{committedExitFaceSummary}]]";
                    BonusRunnerLog.Debug(
                        $"CommittedExitFaceInterceptSelected Section=" +
                        $"{state.SectionIndex}, Mode=" +
                        $"{(committedExitFaceInterceptModelProven ? "FixedStepProven" : "RetainedGround7Evidence")}, " +
                        $"CompletionTraversal=" +
                        $"{completionTerrainTraversal}, " +
                        $"Current=[{currentWall.Left:F3}," +
                        $"{currentWall.Right:F3}]@{currentWall.Top:F3}/" +
                        $"{currentWall.MapPieceName}:S" +
                        $"{currentWall.StaticSurfaceIndex}, TargetFace=" +
                        $"X={wallExitTarget.Left:F3},Y=[" +
                        $"{minimumFaceFeetY:F3}," +
                        $"{maximumFaceFeetY:F3}], Gap={committedFaceGap:F3}, " +
                        $"ObservedVX={postLipHorizontalSpeed:F3}, " +
                        $"PlanningVXEnvelope=[{wallExitMinimumPlanningSpeed:F3}," +
                        $"{wallExitPlanningSpeed:F3}], Hold=" +
                        $"{transferHold:F3}s, PredictedContact=" +
                        $"({wallExitPredictedLandingX:F3}," +
                        $"{committedExitFaceContactFeetY:F3})/" +
                        $"{committedExitFaceContactSeconds:F3}s. " +
                        "ExpectedOutcome=FaceContactOrVerifiedTargetTop; " +
                        "strict landing was not weakened.",
                        "Routing");
                }
            }
            if (wallExitTransferSelected)
            {
                wallHold = transferHold;
                wallLandingPredictionSelected =
                    !section3CollectionFacePulseSelected &&
                    !committedExitFaceInterceptSelected;
                wallLandingPredictedX = wallExitPredictedLandingX;
                wallLandingPredictedTravel = wallExitPredictedTravel;
                wallLandingPredictedFlightSeconds =
                    wallExitPredictedFlightSeconds;
                predictedMaximumRise = 0f;
                jumpPlanner.ChooseWallRecoveryHold(
                    plannedWallRise,
                    wallPhysics,
                    out predictedMaximumRise,
                    transferHold,
                    transferHold);
            }
        }

        // The next platform is not always reachable directly. Ground 5's
        // first four-unit box is followed by a real gap: a 0.12s wall press
        // landed just beyond the box at x=502.318, while a shorter press can
        // settle safely on the box and let the normal planner handle the next
        // jump. Solve that wall-top landing explicitly before falling back to
        // a blind maximum-rise bounce.
        if (!wallExitTransferSelected &&
            hasKnownWallTarget &&
            !isGround3ObjectiveFace &&
            !wallExitFaceContactRequired)
        {
            BonusBoardSegment wallTopTarget = mandatoryCurrentWall;
            wallTopLandingSelected =
                TryChooseWallExitTransferHoldWithinSpeedEnvelope(
                    wallExitSpeedEnvelopeRequired,
                    state.PlayerPosition.x,
                    contactFeetY,
                    wallRecoveryPhysicalLipY,
                    wallRecoveryRequiredReleaseY,
                    wallTopTarget,
                    wallExitMinimumPlanningSpeed,
                    wallExitPlanningSpeed,
                    wallExitPhysics,
                    wallReleaseTravelBias,
                    MinimumWallLandingHoldSeconds,
                    MaximumWallLandingHoldSeconds,
                    0.20f,
                    false,
                    out float wallTopHold,
                    out wallLandingPredictedFlightSeconds,
                    out wallLandingPredictedTravel,
                    out wallLandingPredictedX,
                    out _,
                    out wallTopPlanSummary);
            if (wallTopLandingSelected)
            {
                wallHold = wallTopHold;
                wallTopLandingSequenceCommitted = true;
                wallLandingPredictionSelected = true;
                predictedMaximumRise = 0f;
                jumpPlanner.ChooseWallRecoveryHold(
                    plannedWallRise,
                    wallPhysics,
                    out predictedMaximumRise,
                    wallTopHold,
                    wallTopHold);
            }
        }

        float wallTopWidth = physicalWallRight - physicalWallLeft;
        bool reactiveWallLipEscapeSelected =
            !wallExitTransferSelected &&
            !wallTopLandingSelected &&
            hasKnownWallTarget &&
            !isGround3ObjectiveFace &&
            !wallExitFaceContactRequired &&
            !wallExitTargetActive &&
            wallTopWidth > StagedWallTopMaximumWidth &&
            remainingRise <= 1.25f &&
            wall.IsDetected &&
            wall.IsTouching;
        if (reactiveWallLipEscapeSelected)
        {
            // When a real face is already attached just below a wide top, a
            // failed landing model must not fall back to the 0.115s generic
            // climb floor. The normal Section-3 trace showed that this turns a
            // 0.50-unit lip correction into a full second arc across the whole
            // platform. Use the shortest pulse that the vertical model proves
            // can clear the observed lip, then let the real landing replan.
            wallHold = jumpPlanner.ChooseWallRecoveryHold(
                remainingRise,
                wallPhysics,
                out predictedMaximumRise,
                MinimumWallLandingHoldSeconds,
                0.075f);
            wallTopLandingSequenceCommitted = false;
            wallLandingPredictionSelected = false;
        }
        bool stagedAttachedBounceSelected =
            hasKnownWallTarget &&
            !wallExitFaceContactRequired &&
            (wallTopWidth <= StagedWallTopMaximumWidth ||
             isGround3ObjectiveFace) &&
            !wallExitTransferSelected &&
            !mandatoryFaceContactPulseSelected &&
            (!wallTopLandingSelected || remainingRise > 2.25f);
        if (stagedAttachedBounceSelected)
        {
            // This is not a landing attempt. Its job is to make a controlled
            // attached climb segment
            // toward/along the narrow wall edge, collect the edge spheres,
            // and preserve another wall press. Do this for every intermediate
            // contact, not only the first one. The failed section-two route
            // used 0.060s and released below the lip. Direct observation
            // confirmed that releasing that early loses wall attachment, so
            // each pulse is solved from the remaining rise. The captured
            // manual sequence used two approximately 0.132s pulses followed
            // by a 0.090s pulse, proving that neither pulse count nor weight
            // is fixed. The Ground 3 S2 -> S3 objective route is handled by
            // the separate fixed-step setup/face-intercept solver above.
            wallTopLandingSelected = false;
            wallLandingPredictionSelected = false;
            wallHold = jumpPlanner.ChooseWallRecoveryHold(
                plannedWallRise,
                wallPhysics,
                out predictedMaximumRise,
                MinimumAttachedWallPulseHoldSeconds,
                MaximumAttachedWallPulseHoldSeconds);
            wallTopLandingSequenceCommitted = true;
        }

        // The generic hold-to-lip floor is only a fallback for a blind attached
        // climb. Target-aware landing prediction already includes the airborne
        // continuation after release, and staged bounces deliberately preserve
        // a second wall press. Overriding either hold caused the observed
        // 0.090s safe wall-top plan to become 0.180s and overshoot the target.
        if (hasKnownWallTarget &&
            !wallLandingPredictionSelected &&
            !stagedAttachedBounceSelected &&
            !reactiveWallLipEscapeSelected &&
            !mandatoryFaceSetupSelected &&
            !mandatoryFaceContactPulseSelected &&
            !section3CollectionFacePulseSelected &&
            !committedExitFaceInterceptSelected)
            wallHold = Mathf.Max(wallHold, holdUntilWallLip);

        BonusRunnerLog.Debug(
            $"WallRecoveryPlan CurrentY={state.PlayerPosition.y:F3}, " +
            $"ContactFeetY={contactFeetY:F3}, " +
            $"TargetKnown={hasKnownWallTarget}, TargetTop={automaticTargetTop:F3}, " +
            $"RemainingRise={remainingRise:F3}, ChosenHold={wallHold:F3}s, " +
            $"HoldUntilLip={holdUntilWallLip:F3}s, " +
            $"SphereObjective[Found={hasWallSpheres},Count={wallSphereCount}," +
            $"MinY={(hasWallSpheres ? wallSphereMinimumY : float.NaN):F3}," +
            $"MaxY={(hasWallSpheres ? wallSphereMaximumY : float.NaN):F3}," +
            $"ScanSucceeded={wallSphereScanSucceeded},RetainedAfterFailure=" +
            $"{retainedWallObjectiveAfterScanFailure},PickupAboveFeet=" +
            $"{WallSpherePickupAboveFeet:F3},RequiredRise=" +
            $"{sphereRequiredRise:F3}], " +
            $"PlannedRise={plannedWallRise:F3}, " +
            $"PhasePlannedRise={phasePlannedRise:F3}, " +
            $"PhysicalLipY={wallRecoveryPhysicalLipY:F3}, " +
            $"ObjectiveReleaseY={wallRecoveryRequiredReleaseY:F3}, " +
            $"PhasePolicy=DynamicPostBounce, " +
            $"PriorBounces={wallRecoveryAttempts}, " +
            $"StrongestSinglePhaseRise={strongestSinglePhaseRise:F3}, " +
            $"PredictedMaxRise={predictedMaximumRise:F3}, " +
            $"ExitTarget={(wallExitTargetActive ? $"[{wallExitTarget.Left:F3},{wallExitTarget.Right:F3}]@{wallExitTarget.Top:F3}" : "None")}, " +
            $"ExitTransferSelected={wallExitTransferSelected}, " +
            $"WallTopLandingSelected={wallTopLandingSelected}, " +
            $"MandatoryFaceContact={wallExitFaceContactRequired}, " +
            $"MandatoryFaceSetup={mandatoryFaceSetupSelected}, " +
            $"MandatoryFaceContactPulse={mandatoryFaceContactPulseSelected}, " +
            $"Section3CollectionFacePulse=" +
            $"{section3CollectionFacePulseSelected}, " +
            $"CommittedExitFaceIntercept=" +
            $"{committedExitFaceInterceptSelected}, " +
            $"CommittedExitFaceMode=" +
            $"{(completionDynamicFaceCandidatePromoted ? "CompletionDynamicFixedStep" : committedExitFaceInterceptModelProven ? "FixedStepProven" : committedExitFaceInterceptSelected ? "RetainedGround7Evidence" : "NotSelected")}, " +
            $"MandatoryFaceState[SetupActive=" +
            $"{wallMandatoryFaceSetupActive},InterceptCommitted=" +
            $"{wallMandatoryFaceInterceptCommitted}], " +
            $"MandatoryFaceGeometry[Bottom={mandatoryFaceBottomY:F3}," +
            $"SafeFeet=[{mandatoryFaceMinimumFeetY:F3}," +
            $"{mandatoryFaceMaximumFeetY:F3}],Preferred=" +
            $"{mandatoryFacePreferredFeetY:F3},PlayerHeight=" +
            $"{playerHeight:F3}], " +
            $"MandatorySetupPrediction[HeldSteps=" +
            $"[{mandatorySetupMinimumSteps},{mandatorySetupMaximumSteps}]," +
            $"Apex=[{mandatorySetupMinimumApexFeetY:F3}," +
            $"{mandatorySetupMaximumApexFeetY:F3}]], " +
            $"MandatoryInterceptPrediction[LipT=" +
            $"{mandatoryFaceLipSeconds:F3},ClearT=" +
            $"{mandatoryFaceTopClearSeconds:F3},ClearY=" +
            $"{mandatoryFaceTopClearFeetY:F3},ContactT=" +
            $"{mandatoryFaceContactSeconds:F3},ContactY=" +
            $"{mandatoryFaceContactFeetY:F3},ContactVY=" +
            $"{mandatoryFaceContactVelocityY:F3}], " +
            $"Ground3ObjectiveFace={isGround3ObjectiveFace}, " +
            $"WallTopSequenceCommitted={wallTopLandingSequenceCommitted}, " +
            $"ReactiveWallLipEscape={reactiveWallLipEscapeSelected}, " +
            $"StagedAttachedBounce={stagedAttachedBounceSelected}, " +
            $"StagedHoldRange=" +
            $"{(stagedAttachedBounceSelected ? "0.075-0.135" : "NotApplicable")}, " +
            $"WallExitKinematics[AttachedVX=" +
            $"{Mathf.Abs(state.PlayerVelocity.x):F3},PostLipVX=" +
            $"{postLipHorizontalSpeed:F3},PlanningVXEnvelope=[" +
            $"{wallExitMinimumPlanningSpeed:F3}," +
            $"{wallExitPlanningSpeed:F3}],AccelerationEnvelope=" +
            $"{positiveHorizontalAccelerationEnvelope:F3},Applied=" +
            $"{wallExitAccelerationEnvelopeApplied},CompletionSpeedCeiling=" +
            $"{GetCompletionTraversalSpeedCeiling(state):F3}," +
            $"ReleaseTravelBias=" +
            $"{wallReleaseTravelBias:F3},BaseVX=" +
            $"{wallExitPhysics.BaseHorizontalSpeed:F3},SectionCruiseVX=" +
            $"{(sectionCruiseHorizontalSpeed > 1f ? sectionCruiseHorizontalSpeed.ToString("F3") : "Unresolved")},SpeedMode=" +
            $"{(state.IsActiveGameplay ? "ActiveLiveSpeedFloor" : "TransientDecay")},FlightScale=" +
            $"{wallExitPhysics.FlightTimeScale:F3}], " +
            $"ExitPrediction={(wallExitTransferSelected ? $"X={wallExitPredictedLandingX:F3},D={wallExitPredictedTravel:F3},T={wallExitPredictedFlightSeconds:F3}" : "Unavailable")}, " +
            $"ExitCandidates[{wallExitPlanSummary}], " +
            $"MandatoryFaceCandidates[{mandatoryFacePlanSummary}], " +
            $"WallTopCandidates[{wallTopPlanSummary}]; " +
            $"Physics[{wallPhysics.Summary}], ExitPhysics=" +
            $"[{wallExitPhysics.Summary}], FacePhysics=" +
            $"[{mandatoryFacePhysics.Summary}]",
            "Recovery");

        float phaseSeparationSeconds =
            wallRecoveryAttempts > 0 &&
            jumpController.LastReleaseAt >= 0f
                ? Mathf.Max(
                    0f,
                    Time.unscaledTime - jumpController.LastReleaseAt)
                : float.NaN;
        if (learningSampleActive)
        {
            FinishLearningSample(
                state,
                wallRecoveryImpulseConfirmed
                    ? "WallRecoveryPhaseComplete"
                    : "WallRecoveryRetry");
        }
        bool fixedStepBoundedFacePulse =
            mandatoryFaceSetupSelected ||
            mandatoryFaceContactPulseSelected ||
            section3CollectionFacePulseSelected ||
            committedExitFaceInterceptSelected;
        float facePulseFixedDelta =
            section3CollectionFacePulseSelected ||
            committedExitFaceInterceptSelected
                ? wallExitPhysics.FixedDeltaTime
                : mandatoryFacePhysics.FixedDeltaTime;
        int fixedStepHoldLimit =
            fixedStepBoundedFacePulse
                ? Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        wallHold /
                        Mathf.Clamp(
                            facePulseFixedDelta,
                            0.005f,
                            0.05f) -
                        0.0001f))
                : 0;
        ResetWallResidualRiseWait();
        jumpController.Press(
            player,
            wallHold,
            $"WallRecovery: Stall={stalledFor:F2}s, " +
            $"Contact={(geometricPlannedWallContact
                ? "TwoFixedStepGeometricStall"
                : inferredContinuousWallContact
                    ? "Kinematic"
                    : "Raycast")}, " +
            $"Collider={wall.ColliderInstanceId}:{wall.ColliderName}",
            fixedStepHoldLimit);
        if (!jumpController.IsHoldingJump)
        {
            nextWallRecoveryTime =
                Time.unscaledTime + WallRecoveryCooldownSeconds;
            return false;
        }

        wallExitTransferCommitted = wallExitTransferSelected;
        wallLandingFlightCommitted = wallLandingPredictionSelected;
        wallExitFaceInterceptCommitted =
            committedExitFaceInterceptSelected;
        wallExitCollectionFaceInterceptCommitted =
            section3CollectionFacePulseSelected;
        if (HasCommittedExitFaceFlight())
        {
            // Own the complete action from DOWN, not merely from a later lip
            // observation. Post-bounce classification can otherwise clear a
            // ten-unit target because the generic chain watcher only accepts
            // nearby faces.
            wallExitContactWatchActive = true;
            wallRecoveryCommitmentUntil = Mathf.Max(
                wallRecoveryCommitmentUntil,
                Time.unscaledTime + 1.25f);
        }
        if (mandatoryFaceSetupSelected)
        {
            wallMandatoryFaceSetupActive = true;
            wallMandatoryFaceSetupDeadline =
                Time.unscaledTime +
                MandatoryWallFaceSetupMaximumWaitSeconds;
            wallMandatoryFaceSetupDeadlineFixedStep =
                JumpPhysicsFeedback.FixedStepSequence +
                GetPhysicsStepBudget(
                    MandatoryWallFaceSetupMaximumWaitSeconds);
            wallMandatoryFaceInterceptCommitted = false;
            wallMandatoryFaceInterceptStartedAt = 0f;
            wallMandatoryFaceInterceptStartedFixedStep = -1;
            wallMandatoryFaceContactWatchDeadlineFixedStep = -1;
            wallMandatoryFaceTargetContactX = float.NaN;
            wallMandatoryFacePredictedTopClearFeetY = float.NaN;
            wallMandatoryFacePredictedContactFeetY = float.NaN;
            wallMandatoryFacePredictedContactVelocityY = float.NaN;
            wallMandatoryFacePredictedContactSeconds = 0f;
            wallMandatoryFaceReleasePredictedContactFeetY = float.NaN;
            wallMandatoryFaceReleasePredictedContactVelocityY = float.NaN;
            wallMandatoryFaceReleaseFeetY = float.NaN;
            wallMandatoryFaceReleaseVelocityY = float.NaN;
            wallMandatoryFaceContactObservedFixedStep = -1;
        }
        else if (mandatoryFaceContactPulseSelected)
        {
            wallMandatoryFaceSetupActive = false;
            wallMandatoryFaceSetupDeadline = 0f;
            wallMandatoryFaceSetupDeadlineFixedStep = -1;
            wallMandatoryFaceInterceptCommitted = true;
            wallMandatoryFaceInterceptStartedAt = Time.unscaledTime;
            wallMandatoryFaceInterceptStartedFixedStep =
                JumpPhysicsFeedback.FixedStepSequence;
            wallMandatoryFaceContactWatchDeadlineFixedStep =
                wallMandatoryFaceInterceptStartedFixedStep +
                GetPhysicsStepBudget(
                    MandatoryWallFaceContactWatchSeconds);
            wallMandatoryFaceTargetContactX =
                wallExitTarget.Left - inferredPlayerHalfWidth;
            wallMandatoryFacePredictedTopClearFeetY =
                mandatoryFaceTopClearFeetY;
            wallMandatoryFacePredictedContactFeetY =
                mandatoryFaceContactFeetY;
            wallMandatoryFacePredictedContactVelocityY =
                mandatoryFaceContactVelocityY;
            wallMandatoryFacePredictedContactSeconds =
                mandatoryFaceContactSeconds;
            wallMandatoryFaceReleasePredictedContactFeetY = float.NaN;
            wallMandatoryFaceReleasePredictedContactVelocityY = float.NaN;
            wallMandatoryFaceReleaseFeetY = float.NaN;
            wallMandatoryFaceReleaseVelocityY = float.NaN;
            wallMandatoryFaceContactObservedFixedStep = -1;
        }
        wallReleaseObservedFixedStep = -1;
        wallDetachedLastFixedStep = -1;
        wallDetachedConfirmationSteps = 0;
        passiveWallApproachActive = false;
        wallRecoveryAttempts++;
        wallActionPhase = wallRecoveryAttempts == 1
            ? WallActionPhase.WallJumpPhaseOne
            : WallActionPhase.WallJumpPhaseTwo;
        wallRecoveryContactLatched = true;
        wallRecoverySawUpwardMotion = false;
        wallRecoveryContactX = state.PlayerPosition.x;
        wallRecoveryContactY = state.PlayerPosition.y;
        wallStallStartedAt = -1f;
        nextWallRecoveryTime =
            Time.unscaledTime + WallRecoveryCooldownSeconds;
        automaticJumpArmed = false;
        airborneAfterAutomaticJump = true;
        automaticJumpRequestedAt = Time.unscaledTime;
        automaticJumpVelocityConfirmed = false;
        automaticPredictionActive = true;
        automaticPlanReason =
            $"WallRecoveryPhase{wallRecoveryAttempts}" +
            (wallExitTransferSelected
                ? committedExitFaceInterceptSelected
                    ? ":ExitFaceIntercept"
                    : section3CollectionFacePulseSelected
                    ? ":CollectionFaceIntercept"
                    : ":ExitTransfer"
                : wallTopLandingSelected
                    ? ":WallTopLanding"
                    : mandatoryFaceSetupSelected
                        ? ":MandatoryFaceSetup"
                    : mandatoryFaceContactPulseSelected
                        ? ":MandatoryFaceIntercept"
                    : stagedAttachedBounceSelected
                        ? ":StagedAttachedBounce"
                    : reactiveWallLipEscapeSelected
                        ? ":ReactiveLipEscape"
                    : ":AttachedClimb");
        // The approach press and the attached climb press have different
        // release rules. Leaving this as ApproachJumpThenWallJump caused
        // MonitorCommittedJump to see the same wall and release the actual
        // climb press on its very first render frame (0.009s in the captured
        // run instead of the planned 0.120s).
        automaticManeuver = BonusManeuverKind.WallJumpClimb;
        StartLearningSample(
            state,
            "Automatic",
            learnGroundKinematics: false);
        automaticAttemptId = learningSampleId;
        wallRecoveryImpulseStartY = state.PlayerPosition.y;
        wallRecoveryImpulseStartVelocityY = state.PlayerVelocity.y;
        wallRecoveryLastObservedY = state.PlayerPosition.y;
        nextWallClimbFrameLogTime = 0f;
        wallRecoveryUpwardPhysicsSteps = 0;
        wallRecoveryImpulseConfirmed = false;
        wallRecoveryImpulseFailureLogged = false;
        wallRecoveryImpulseReleaseObservedFixedStep = -1;
        wallRecoveryPrematureReleaseLogged = false;
        wallRecoveryLipCrossed = false;
        learningInputUpTime =
            learningInputDownTime + wallHold;
        learningInputReleased = true;
        automaticPlannedHold = wallHold;
        automaticPlanPhysicsSnapshot =
            mandatoryFaceSetupSelected ||
            mandatoryFaceContactPulseSelected
                ? mandatoryFacePhysics
                : wallExitTransferSelected
                    ? wallExitPhysics
                : wallPhysics;
        automaticPhysicsRevision = automaticPlanPhysicsSnapshot.ModelRevision;
        automaticPlanTriggerPosition = state.PlayerPosition;
        automaticPlannedLaunchX = state.PlayerPosition.x;
        automaticLaunchWindowLeft = state.PlayerPosition.x;
        automaticLaunchWindowRight = state.PlayerPosition.x;
        bool anyFaceContactPulseSelected =
            mandatoryFaceContactPulseSelected ||
            section3CollectionFacePulseSelected ||
            committedExitFaceInterceptSelected;
        float faceContactCenterX =
            anyFaceContactPulseSelected
                ? wallExitTarget.Left - inferredPlayerHalfWidth
                : 0f;
        float faceContactTravel =
            anyFaceContactPulseSelected
                ? Mathf.Max(
                    0f,
                    faceContactCenterX -
                        state.PlayerPosition.x)
                : 0f;
        automaticTargetHeightDelta =
            mandatoryFaceContactPulseSelected
                ? mandatoryFaceContactFeetY - contactFeetY
                : section3CollectionFacePulseSelected
                    ? collectionFaceContactFeetY - contactFeetY
                : committedExitFaceInterceptSelected
                    ? (float.IsNaN(committedExitFaceContactFeetY)
                        ? wallExitTarget.Top -
                            CommittedExitFacePreferredDepth - contactFeetY
                        : committedExitFaceContactFeetY - contactFeetY)
                : wallExitTransferSelected
                    ? wallExitTarget.Top - contactFeetY
                    : remainingRise;
        automaticPredictedHorizontalTravel =
            anyFaceContactPulseSelected
                ? faceContactTravel
            : wallLandingPredictionSelected
            ? wallLandingPredictedTravel
            : Mathf.Max(
                0f,
                physicalWallSafeLeft - state.PlayerPosition.x);
        automaticPredictedLandingX =
            anyFaceContactPulseSelected
                ? faceContactCenterX
            : wallLandingPredictionSelected
            ? wallLandingPredictedX
            : physicalWallSafeLeft;
        float wallSpeedRatio = wallExitPlanningSpeed > 0.01f
            ? Mathf.Clamp01(
                wallExitMinimumPlanningSpeed /
                wallExitPlanningSpeed)
            : 1f;
        automaticMinimumPredictedHorizontalTravel =
            anyFaceContactPulseSelected
                ? faceContactTravel
                : automaticPredictedHorizontalTravel * wallSpeedRatio;
        automaticMaximumPredictedHorizontalTravel =
            automaticPredictedHorizontalTravel;
        automaticFutureSpeedTransitionExpected =
            wallExitSpeedEnvelopeRequired;
        automaticSpiritBoostRouteEvidence =
            $"WallActionEnvelope[MinVX=" +
            $"{wallExitMinimumPlanningSpeed:F3},MaxVX=" +
            $"{wallExitPlanningSpeed:F3},Required=" +
            $"{wallExitSpeedEnvelopeRequired},Spirit=" +
            $"{state.SpiritBoostEnabled}]";
        automaticPredictionLaunchFeetY = contactFeetY;
        automaticPlannedTravelScale =
            automaticPlanPhysicsSnapshot.HorizontalTravelScale;
        automaticPlannedLandingBias = 0f;
        automaticTriggerSpeed = wallExitTransferSelected ||
            wallTopLandingSelected || anyFaceContactPulseSelected
                ? wallExitPlanningSpeed
                : Mathf.Max(1f, GetWallRouteSpeed());
        automaticPredictedFlightSeconds =
            mandatoryFaceContactPulseSelected
                ? mandatoryFaceContactSeconds
                : section3CollectionFacePulseSelected
                    ? collectionFaceContactSeconds
                : committedExitFaceInterceptSelected
                    ? committedExitFaceContactSeconds
                : wallLandingPredictionSelected
            ? wallLandingPredictedFlightSeconds
            : jumpPlanner.PredictRawInputToLandingSeconds(
                wallHold,
                remainingRise,
                wallPhysics);
        if (automaticPredictedFlightSeconds <= 0.05f)
            automaticPredictedFlightSeconds = 1.0f;
        automaticTrajectoryCompatible = true;
        float wallExitWatchSeconds = Mathf.Clamp(
            automaticPredictedFlightSeconds + 0.40f,
            0.80f,
            1.50f);
        wallRecoveryCommitmentUntil =
            Time.unscaledTime + wallExitWatchSeconds;
        wallExitContactWatchDeadlineFixedStep =
            HasCommittedExitFaceFlight()
                ? JumpPhysicsFeedback.FixedStepSequence +
                  GetPhysicsStepBudget(wallExitWatchSeconds)
                : -1;
        nextTrajectoryMonitorLogTime = 0f;
        ClearSecondStagePreview();
        ClearRoutePlanLock();

        BonusRunnerLog.Debug(
            $"WallRecoveryDecision Attempt={wallRecoveryAttempts}/" +
            $"{MaximumWallRecoveriesPerAirborneSequence}, " +
            $"ActionPhase={wallActionPhase}, " +
            $"TriggerMode={(authoritativeWatchedExitContact ? "ObservedWatchedExitFaceContact" : authoritativePlannedContact ? "ObservedPlannedFaceContact" : plannedBodyContactReady ? "BodyContact" : "ConfirmedStall")}, " +
            $"ContactEvidence={(inferredContinuousWallContact ? "Kinematic" : "Raycast")}, " +
            $"PhaseSeparation={(float.IsNaN(phaseSeparationSeconds) ? "FirstPhase" : $"{phaseSeparationSeconds:F3}s")}, " +
            $"Hold={wallHold:F3}s, X={state.PlayerPosition.x:F3}, " +
            $"Y={state.PlayerPosition.y:F3}, FeetY={contactFeetY:F3}, " +
            $"RemainingRise={remainingRise:F3}, PredictedMaxRise={predictedMaximumRise:F3}, " +
            $"PredictedLandingTime={automaticPredictedFlightSeconds:F3}s, " +
            $"PredictedLandingX={automaticPredictedLandingX:F3}, " +
            $"ExitTransfer={wallExitTransferSelected}, " +
            $"WallTopLanding={wallTopLandingSelected}, " +
            $"MandatoryFaceSetup={mandatoryFaceSetupSelected}, " +
            $"MandatoryFaceIntercept={mandatoryFaceContactPulseSelected}, " +
            $"CommittedExitFaceIntercept=" +
            $"{wallExitFaceInterceptCommitted}, " +
            $"CollectionFaceIntercept=" +
            $"{wallExitCollectionFaceInterceptCommitted}, " +
            $"PredictedOutcome=" +
            $"{(section3CollectionFacePulseSelected ? "CollectionFace" : anyFaceContactPulseSelected ? "FaceOrTop" : wallLandingPredictionSelected ? "Landing" : "Climb")}, " +
            $"MandatoryFacePrediction[ClearY=" +
            $"{mandatoryFaceTopClearFeetY:F3},ContactY=" +
            $"{mandatoryFaceContactFeetY:F3},ContactVY=" +
            $"{mandatoryFaceContactVelocityY:F3},ContactT=" +
            $"{mandatoryFaceContactSeconds:F3}], " +
            $"CommitUntil={wallRecoveryCommitmentUntil:F3}, " +
            $"CommitDeadlineFixedStep=" +
            $"{wallExitContactWatchDeadlineFixedStep}, " +
            $"Collider={wall.ColliderInstanceId}:{wall.ColliderName}.",
            "Recovery");
        return true;
    }

    /// <summary>
    /// The highest Ground 5 pillar has one pickup below the contact height at
    /// which the normal wall solver immediately starts the next pulse.  Keep
    /// input released through at least one fixed update while descending
    /// through that pickup.  Resume as soon as the pickup/band is observed and
    /// cap the wait with a bounded live-velocity/gravity prediction, then
    /// solve the next wall pulse from live state. This is deliberately scoped
    /// to the authored pillar
    /// identity so it cannot change the stable routes on other walls.
    /// </summary>
    [HideFromIl2Cpp]
    private bool TryManageGround5HighestPillarSink(
        BonusStageState state,
        BonusWallContact wall,
        float contactFeetY,
        bool hasWallSpheres,
        int wallSphereCount,
        float wallSphereMinimumY,
        float wallSphereMaximumY)
    {
        bool isHighestGround5Pillar =
            state.SectionIndex == 1 &&
            string.Equals(
                automaticTargetMapPieceName,
                "Ground 5",
                StringComparison.OrdinalIgnoreCase) &&
            automaticTargetStaticSurfaceIndex == 4;
        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        double fixedTime = Time.fixedTimeAsDouble;

        if (ground5HighestPillarSinkActive)
        {
            if (!isHighestGround5Pillar)
            {
                BonusRunnerLog.Warning(
                    $"Ground5HighestPillarSinkAborted Reason=" +
                    $"RouteIdentityChanged, Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY=" +
                    $"{contactFeetY:F3}, MapPiece=" +
                    $"{automaticTargetMapPieceName}#" +
                    $"{automaticTargetMapPieceInstanceId}/S" +
                    $"{automaticTargetStaticSurfaceIndex}.");
                ResetGround5HighestPillarSink();
                return false;
            }

            bool stillHasSpheres =
                BonusStageInspector.TryGetActiveSphereVerticalBounds(
                    automaticTargetLeft - 1.55f,
                    automaticTargetRight + 0.8f,
                    out int remainingSphereCount,
                    out float remainingMinimumY,
                    out float remainingMaximumY,
                    out bool sphereScanSucceeded);
            long rawSequenceDelta = Math.Max(
                0L,
                fixedStep - ground5HighestPillarSinkArmedFixedStep);
            float activeSinkFixedDelta = Mathf.Clamp(
                ground5HighestPillarSinkFixedDelta,
                0.005f,
                0.05f);
            double elapsedFixedTime = Math.Max(
                0d,
                fixedTime - ground5HighestPillarSinkArmedFixedTime);
            int elapsedPhysicsSteps = Math.Max(
                0,
                (int)Math.Floor(
                    (elapsedFixedTime + activeSinkFixedDelta * 0.25f) /
                    activeSinkFixedDelta));
            double observationGap = Math.Max(
                0d,
                fixedTime -
                    ground5HighestPillarSinkLastObservedFixedTime);
            int physicsStepsSinceLastObservation = Math.Max(
                0,
                (int)Math.Floor(
                    (observationGap +
                        activeSinkFixedDelta * 0.25f) /
                    activeSinkFixedDelta));
            if (physicsStepsSinceLastObservation > 1)
            {
                ground5HighestPillarSinkBackgroundCatchUpObserved = true;
            }
            ground5HighestPillarSinkLastObservedFixedTime = fixedTime;
            float armedSphereMinimumY =
                ground5HighestPillarSinkTargetFeetY - 0.42f;
            bool pickupCollected = sphereScanSucceeded &&
                (!stillHasSpheres ||
                 remainingSphereCount <
                    ground5HighestPillarSinkSphereCount ||
                 remainingMinimumY > armedSphereMinimumY + 0.20f);
            bool reachedPickupBand =
                contactFeetY <=
                    ground5HighestPillarSinkTargetFeetY + 0.08f;
            bool sinkDeadlineReached =
                ground5HighestPillarSinkDeadlineFixedTime >= 0d &&
                fixedTime + activeSinkFixedDelta * 0.25f >=
                    ground5HighestPillarSinkDeadlineFixedTime;
            bool keepReleased =
                elapsedPhysicsSteps < 1 ||
                !pickupCollected &&
                !reachedPickupBand &&
                !sinkDeadlineReached;
            if (keepReleased)
            {
                jumpController.Release();
                passiveWallApproachActive = true;
                wallActionPhase = WallActionPhase.AwaitingNextWallPress;
                wallStallStartedAt =
                    Time.unscaledTime - WallStallConfirmationSeconds;
                nextWallRecoveryTime = 0f;
                return true;
            }

            string completionReason = pickupCollected
                ? "PickupCollected"
                : reachedPickupBand
                    ? "PickupBandReached"
                    : "DynamicFixedStepSafetyLimit";
            BonusRunnerLog.Debug(
                $"Ground5HighestPillarSinkComplete Reason=" +
                $"{completionReason}, StartFeetY=" +
                $"{ground5HighestPillarSinkStartFeetY:F3}, " +
                $"ActualFeetY={contactFeetY:F3}, TargetFeetY=" +
                $"{ground5HighestPillarSinkTargetFeetY:F3}, " +
                $"StartFixedStep=" +
                $"{ground5HighestPillarSinkArmedFixedStep}, " +
                $"CurrentFixedStep={fixedStep}, RawSequenceDelta=" +
                $"{rawSequenceDelta}, StartFixedTime=" +
                $"{ground5HighestPillarSinkArmedFixedTime:F4}, " +
                $"CurrentFixedTime={fixedTime:F4}, " +
                $"ElapsedPhysicsSteps={elapsedPhysicsSteps}, " +
                $"PhysicsStepsSinceLastObservation=" +
                $"{physicsStepsSinceLastObservation}, " +
                $"BackgroundCatchUpObserved=" +
                $"{ground5HighestPillarSinkBackgroundCatchUpObserved}, " +
                $"StartSpheres=" +
                $"{ground5HighestPillarSinkSphereCount}, " +
                $"SphereScanSucceeded={sphereScanSucceeded}, " +
                $"RemainingSpheres=" +
                $"{(stillHasSpheres ? remainingSphereCount : 0)}, " +
                $"RemainingSphereY=" +
                $"{(stillHasSpheres ? $"[{remainingMinimumY:F3}," +
                    $"{remainingMaximumY:F3}]" : "None")}, " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), WallFaceX=" +
                $"{wall.FaceX:F3}. NextBehavior=solve the next wall pulse " +
                $"from live post-sink state.",
                "Routing");
            ResetGround5HighestPillarSink();
            wallStallStartedAt =
                Time.unscaledTime - WallStallConfirmationSeconds;
            nextWallRecoveryTime = 0f;
            return false;
        }

        const float pickupFeetOffset = 0.42f;
        // Spirit Boost reaches the authored S4 face at FeetY ~= 4.454 while
        // the pickup target is 3.420.  The former 0.85 window ended at 4.270,
        // so boosted runs skipped this state entirely and immediately pulsed
        // upward.  The wider bound still applies only to Ground 5/S4 and the
        // live downward phase; the fixed-step prediction remains responsible
        // for stopping at the pickup band.
        const float maximumArmFeetOffset = 1.25f;
        if (!isHighestGround5Pillar ||
            !hasWallSpheres ||
            wallSphereCount <= 0 ||
            state.PlayerVelocity.y >= -2.0f)
        {
            return false;
        }

        float targetFeetY = wallSphereMinimumY + pickupFeetOffset;
        if (contactFeetY <= targetFeetY ||
            contactFeetY > targetFeetY + maximumArmFeetOffset)
        {
            return false;
        }

        ground5HighestPillarSinkActive = true;
        ground5HighestPillarSinkArmedFixedStep = fixedStep;
        float sinkFixedDelta = Mathf.Clamp(
            automaticPlanPhysicsSnapshot.FixedDeltaTime,
            0.005f,
            0.05f);
        float sinkGravity = Mathf.Clamp(
            automaticPlanPhysicsSnapshot.GravityMagnitude,
            5f,
            100f);
        float predictedFeetY = contactFeetY;
        float predictedVelocityY = state.PlayerVelocity.y;
        int sinkStepBudget = 8;
        for (int step = 1; step <= 8; step++)
        {
            predictedVelocityY -= sinkGravity * sinkFixedDelta;
            predictedFeetY += predictedVelocityY * sinkFixedDelta;
            if (predictedFeetY <= targetFeetY + 0.08f)
            {
                sinkStepBudget = step;
                break;
            }
        }
        sinkStepBudget = Mathf.Clamp(sinkStepBudget, 2, 8);
        ground5HighestPillarSinkArmedFixedTime = fixedTime;
        ground5HighestPillarSinkFixedDelta = sinkFixedDelta;
        ground5HighestPillarSinkDeadlineFixedTime =
            fixedTime + sinkStepBudget * sinkFixedDelta;
        ground5HighestPillarSinkLastObservedFixedTime = fixedTime;
        ground5HighestPillarSinkBackgroundCatchUpObserved = false;
        ground5HighestPillarSinkStartFeetY = contactFeetY;
        ground5HighestPillarSinkTargetFeetY = targetFeetY;
        ground5HighestPillarSinkSphereCount = wallSphereCount;
        wallExitTransferCommitted = false;
        wallLandingFlightCommitted = false;
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        wallTopLandingSequenceCommitted = false;
        passiveWallApproachActive = true;
        wallActionPhase = WallActionPhase.AwaitingNextWallPress;
        wallStallStartedAt =
            Time.unscaledTime - WallStallConfirmationSeconds;
        nextWallRecoveryTime = 0f;
        jumpController.Release();
        BonusRunnerLog.Debug(
            $"Ground5HighestPillarSinkArmed Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"FeetY={contactFeetY:F3}, Velocity=" +
            $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"TargetFeetY={targetFeetY:F3}, ObjectiveSpheres=" +
            $"{wallSphereCount}, SphereY=[{wallSphereMinimumY:F3}," +
            $"{wallSphereMaximumY:F3}], ArmWindowFeet=" +
            $"({targetFeetY:F3},{targetFeetY + maximumArmFeetOffset:F3}], " +
            $"SinkModel[FixedDt={sinkFixedDelta:F4},Gravity=" +
            $"{sinkGravity:F3},Steps={sinkStepBudget},PredictedFeetY=" +
            $"{predictedFeetY:F3}], FixedStep={fixedStep}, " +
            $"FixedTime={fixedTime:F4}, DeadlineFixedTime=" +
            $"{ground5HighestPillarSinkDeadlineFixedTime:F4}, " +
            $"MapPiece={automaticTargetMapPieceName}#" +
            $"{automaticTargetMapPieceInstanceId}/G" +
            $"{automaticTargetRegistryGeneration}/S" +
            $"{automaticTargetStaticSurfaceIndex}. Action=release with a " +
            $"planned {sinkStepBudget}-physics-step budget; the next runtime " +
            $"observation stops at/after the deadline, or earlier when the " +
            $"pickup or target feet band is confirmed.",
            "Routing");
        return true;
    }

    [HideFromIl2Cpp]
    private bool TryManageAttachedObjectiveDescent(
        BonusStageState state,
        BonusWallContact wall,
        float contactFeetY,
        int capturedObjectiveCount = 0,
        float capturedObjectiveMinimumY = float.NaN,
        float capturedObjectiveMaximumY = float.NaN)
    {
        bool isGround3 = string.Equals(
            automaticTargetMapPieceName,
            "Ground 3",
            StringComparison.OrdinalIgnoreCase);
        bool isSecondRaisedFace =
            automaticTargetStaticSurfaceIndex == 3 ||
            automaticTargetStaticSurfaceIndex < 0 &&
            !float.IsNaN(automaticTargetMapPieceOriginX) &&
            Mathf.Abs(
                automaticTargetLeft -
                (automaticTargetMapPieceOriginX + 1f)) <= 0.30f;
        if (state.SectionIndex != 1 || !isGround3 || !isSecondRaisedFace)
        {
            return false;
        }

        float mappedFaceBottomY =
            automaticTargetTop - Ground3ObjectiveFaceHeight;
        bool raycastContact =
            wall.IsDetected &&
            wall.IsTouching &&
            Mathf.Abs(wall.FaceX - automaticTargetLeft) <=
                MandatoryWallFaceRayXMatchTolerance &&
            wall.Normal.x <= -0.25f &&
            contactFeetY >= mappedFaceBottomY - 0.25f &&
            contactFeetY <= automaticTargetTop + 0.45f;
        bool kinematicContact =
            attachedObjectiveDescentActive &&
            Mathf.Abs(state.PlayerVelocity.x) <=
                WallAttachmentVelocityTolerance + 0.75f &&
            state.PlayerPosition.x >= automaticTargetLeft - 1.05f &&
            state.PlayerPosition.x <= automaticTargetLeft + 0.30f &&
            contactFeetY <= automaticTargetTop + 0.45f;
        if (!raycastContact && !kinematicContact)
        {
            if (!attachedObjectiveDescentActive)
                return false;

            // The kinematic fallback above already covers a one-frame raycast
            // miss while the body remains slow and inside the mapped face
            // corridor. If both tests fail, this is real detachment evidence,
            // not permission to keep releasing input for the rest of the
            // 0.90-second descent window. Relinquish exclusive descent
            // ownership immediately so a newly observed wall contact can
            // authorize a fresh pulse and the pit lifecycle guard can act if
            // no recoverable face remains.
            BonusRunnerLog.Warning(
                $"AttachedObjectiveDescentAborted Reason=ContactLost, " +
                $"Position=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY={contactFeetY:F3}, " +
                $"TargetFeetY={attachedObjectiveDescentTargetFeetY:F3}, " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), TargetFaceX=" +
                $"{automaticTargetLeft:F3}, RayDetected={wall.IsDetected}, " +
                $"RayTouching={wall.IsTouching}. " +
                "ExpectedResult=remain attached during released descent; " +
                "ActualResult=neither raycast nor near-face kinematics " +
                "confirm attachment. FailureDomain=Execution; " +
                "NextBehavior=await a new physical contact without " +
                "suppressing lifecycle or airborne control.");
            attachedObjectiveDescentActive = false;
            attachedObjectiveDescentDeadline = 0f;
            attachedObjectiveDescentDeadlineFixedStep = -1;
            attachedObjectiveDescentSphereCount = 0;
            nextAttachedObjectiveDescentLogTime = 0f;
            wallActionPhase = WallActionPhase.AwaitingWallContact;
            wallStallStartedAt = -1f;
            nextWallRecoveryTime = 0f;
            passiveWallApproachActive = true;
            jumpController.Release();
            return false;
        }

        if (!attachedObjectiveDescentActive)
        {
            bool hasLiveLaneObjectives =
                BonusStageInspector.TryGetActiveSphereVerticalBounds(
                    automaticTargetLeft - 1.55f,
                    automaticTargetLeft + 0.70f,
                    out int sphereCount,
                    out float minimumSphereY,
                    out float maximumSphereY,
                    out bool sphereScanSucceeded);
            bool hasCapturedLaneObjectives =
                !sphereScanSucceeded &&
                capturedObjectiveCount > 0 &&
                !float.IsNaN(capturedObjectiveMinimumY) &&
                !float.IsNaN(capturedObjectiveMaximumY);
            if (!hasLiveLaneObjectives && hasCapturedLaneObjectives)
            {
                sphereCount = capturedObjectiveCount;
                minimumSphereY = capturedObjectiveMinimumY;
                maximumSphereY = capturedObjectiveMaximumY;
                BonusRunnerLog.Debug(
                    $"AttachedObjectiveDescentUsingCapturedObjectives " +
                    $"Count={sphereCount}, Y=[{minimumSphereY:F3}," +
                    $"{maximumSphereY:F3}]. The live sphere scan was " +
                    "temporarily unavailable on the confirmed-face " +
                    "handoff frame.",
                    "Routing");
            }

            bool hasLaneObjectives =
                hasLiveLaneObjectives || hasCapturedLaneObjectives;
            if (!hasLaneObjectives ||
                minimumSphereY >= contactFeetY - 0.45f)
            {
                return false;
            }

            float knownFaceBottom = automaticTargetTop - 5.0f;
            attachedObjectiveDescentActive = true;
            attachedObjectiveDescentDeadline = Time.unscaledTime + 0.90f;
            attachedObjectiveDescentDeadlineFixedStep =
                JumpPhysicsFeedback.FixedStepSequence +
                GetPhysicsStepBudget(0.90f);
            attachedObjectiveDescentTargetFeetY = Mathf.Max(
                knownFaceBottom + 0.45f,
                minimumSphereY - 0.35f);
            attachedObjectiveDescentSphereCount = sphereCount;
            wallExitObjectiveCountAtCapture = 0;
            wallExitObjectiveMinimumY = float.NaN;
            wallExitObjectiveMaximumY = float.NaN;
            nextAttachedObjectiveDescentLogTime = 0f;
            wallExitTransferCommitted = false;
            wallLandingFlightCommitted = false;
            wallExitFaceInterceptCommitted = false;
            wallExitCollectionFaceInterceptCommitted = false;
            wallExitContactWatchDeadlineFixedStep = -1;
            wallTopLandingSequenceCommitted = false;
            wallRecoveryContactLatched = false;
            wallRecoverySawUpwardMotion = false;
            wallRecoveryLipCrossed = false;
            wallReleaseObservedFixedStep = -1;
            wallDetachedLastFixedStep = -1;
            wallDetachedConfirmationSteps = 0;
            passiveWallApproachActive = true;
            automaticJumpRequestedAt = Time.unscaledTime;
            wallActionPhase = WallActionPhase.AttachedObjectiveDescent;
            wallRecoveryCommitmentUntil =
                attachedObjectiveDescentDeadline + 0.40f;
            automaticPlanReason = "Ground3AttachedObjectiveDescent";
            automaticManeuver =
                BonusManeuverKind.ApproachJumpThenWallJump;
            jumpController.Release();
            if (learningSampleActive && learningSource == "Automatic")
                FinishLearningSample(state, "AttachedObjectiveDescentStarted");

            BonusRunnerLog.Debug(
                $"AttachedObjectiveDescentArmed MapPiece=" +
                $"{automaticTargetMapPieceName}#" +
                $"{automaticTargetMapPieceInstanceId}/S" +
                $"{automaticTargetStaticSurfaceIndex}, FaceX=" +
                $"{automaticTargetLeft:F3}, FaceY=[{knownFaceBottom:F3}," +
                $"{automaticTargetTop:F3}], ContactFeetY=" +
                $"{contactFeetY:F3}, ObjectiveSpheres={sphereCount}, " +
                $"SphereY=[{minimumSphereY:F3},{maximumSphereY:F3}], " +
                $"TargetFeetY={attachedObjectiveDescentTargetFeetY:F3}, " +
                $"Deadline={attachedObjectiveDescentDeadline:F3}, " +
                $"DeadlineFixedStep=" +
                $"{attachedObjectiveDescentDeadlineFixedStep}. " +
                "ExpectedBehavior=remain released and slide down the same " +
                "face through the trench pickups before issuing a new " +
                "wall-jump press.",
                "Routing");
        }

        bool reachedObjectiveBand =
            contactFeetY <= attachedObjectiveDescentTargetFeetY + 0.12f;
        bool deadlineExpired =
            attachedObjectiveDescentDeadlineFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence >
                attachedObjectiveDescentDeadlineFixedStep;
        if (!reachedObjectiveBand && !deadlineExpired)
        {
            jumpController.Release();
            passiveWallApproachActive = true;
            wallActionPhase = WallActionPhase.AttachedObjectiveDescent;
            wallStallStartedAt = -1f;
            if (BonusRunnerLog.IsDebugMode &&
                Time.unscaledTime >= nextAttachedObjectiveDescentLogTime)
            {
                nextAttachedObjectiveDescentLogTime =
                    Time.unscaledTime + 0.08f;
                BonusRunnerLog.Debug(
                    $"AttachedObjectiveDescentFrame Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY=" +
                    $"{contactFeetY:F3}, TargetFeetY=" +
                    $"{attachedObjectiveDescentTargetFeetY:F3}, " +
                    $"Velocity=({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), WallFaceX=" +
                    $"{wall.FaceX:F3}, RemainingFixedSteps=" +
                    $"{Math.Max(0L, attachedObjectiveDescentDeadlineFixedStep - JumpPhysicsFeedback.FixedStepSequence)}, " +
                    $"ObjectiveSpheresAtArm=" +
                    $"{attachedObjectiveDescentSphereCount}. Action=ReleaseAndSlide.",
                    "Routing");
            }
            return true;
        }

        string completionReason = reachedObjectiveBand
            ? "ObjectiveBandReached"
            : "BoundedDeadlineReached";
        BonusRunnerLog.Debug(
            $"AttachedObjectiveDescentComplete Reason={completionReason}, " +
            $"Position=({state.PlayerPosition.x:F3}," +
            $"{state.PlayerPosition.y:F3}), FeetY={contactFeetY:F3}, " +
            $"TargetFeetY={attachedObjectiveDescentTargetFeetY:F3}, " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), FaceX={wall.FaceX:F3}. " +
            "NextBehavior=solve a fresh bounded wall pulse from the actual " +
            "low contact state.",
            "Routing");
        attachedObjectiveDescentActive = false;
        attachedObjectiveDescentDeadline = 0f;
        attachedObjectiveDescentDeadlineFixedStep = -1;
        attachedObjectiveDescentSphereCount = 0;
        nextAttachedObjectiveDescentLogTime = 0f;
        wallActionPhase = WallActionPhase.AwaitingNextWallPress;
        wallStallStartedAt =
            Time.unscaledTime - WallStallConfirmationSeconds;
        nextWallRecoveryTime = 0f;
        return false;
    }

    [HideFromIl2Cpp]
    private bool TryDeferWallPressForResidualRise(
        BonusStageState state,
        float contactFeetY,
        JumpPhysicsSnapshot wallPhysics,
        string context)
    {
        float remainingToRelease =
            wallRecoveryRequiredReleaseY - contactFeetY;
        float upwardVelocity = Mathf.Max(0f, state.PlayerVelocity.y);
        float gravity = Mathf.Max(1f, wallPhysics.GravityMagnitude);
        float passiveRise = upwardVelocity * upwardVelocity / (2f * gravity);
        float predictedApexFeetY = contactFeetY + passiveRise;
        bool passiveRiseClearsRelease =
            predictedApexFeetY >=
                wallRecoveryRequiredReleaseY +
                WallResidualRiseSafetyMargin;
        bool sameTargetWait =
            wallResidualRiseWaitActive &&
            Mathf.Abs(
                wallResidualRiseWaitTargetLeft -
                automaticTargetLeft) <= 0.20f;
        bool shouldWait =
            remainingToRelease > 0.01f &&
            state.PlayerVelocity.y >
                WallResidualRiseVelocityThreshold;

        if (!shouldWait)
        {
            if (wallResidualRiseWaitActive)
            {
                string result = remainingToRelease <= 0.01f
                    ? "ReleaseHeightReached"
                    : "ApexOrDescentObserved";
                BonusRunnerLog.Debug(
                    $"WallResidualRiseWaitComplete Result={result}, " +
                    $"Context={context}, Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), FeetY=" +
                    $"{contactFeetY:F3}, Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), ReleaseY=" +
                    $"{wallRecoveryRequiredReleaseY:F3}, Remaining=" +
                    $"{remainingToRelease:F3}, Waited=" +
                    $"{Time.unscaledTime - wallResidualRiseWaitStartedAt:F3}s, " +
                    $"FixedSteps=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}. " +
                    "A new DOWN is legal only if contact remains confirmed " +
                    "and the lip has not been reached.",
                    "Recovery");
                if (remainingToRelease > 0.01f)
                {
                    // The prior staged pulse has now completed without
                    // crossing the lip. Re-solving from this observed
                    // apex/descent may legitimately choose the mapped exit;
                    // retaining the old sequence commitment would force the
                    // minimum staged pulse forever and ignore that target.
                    wallTopLandingSequenceCommitted = false;
                }
            }
            ResetWallResidualRiseWait();
            return false;
        }

        if (!sameTargetWait)
        {
            ResetWallResidualRiseWait();
            wallResidualRiseWaitActive = true;
            wallResidualRiseWaitStartedAt = Time.unscaledTime;
            wallResidualRiseWaitStartFeetY = contactFeetY;
            wallResidualRiseWaitTargetLeft = automaticTargetLeft;
            nextWallResidualRiseLogTime = Time.unscaledTime;
            BonusRunnerLog.Debug(
                $"WallResidualRiseWaitStarted Context={context}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY=" +
                $"{contactFeetY:F3}, Velocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), Target=" +
                $"[{automaticTargetLeft:F3}," +
                $"{automaticTargetRight:F3}]@" +
                $"{automaticTargetTop:F3}, ReleaseY=" +
                $"{wallRecoveryRequiredReleaseY:F3}, Remaining=" +
                $"{remainingToRelease:F3}, Gravity={gravity:F3}, " +
                $"PassiveRise={passiveRise:F3}, PredictedApexFeetY=" +
                $"{predictedApexFeetY:F3}, " +
                $"CanReachRelease={passiveRiseClearsRelease}, " +
                $"Attempt={wallRecoveryAttempts}/" +
                $"{MaximumWallRecoveriesPerAirborneSequence}. " +
                "Action=keep UP released; do not reset a live upward " +
                "impulse with another wall DOWN.",
                "Recovery");
        }

        float waited = Time.unscaledTime - wallResidualRiseWaitStartedAt;
        if (waited > WallResidualRiseMaximumWaitSeconds)
        {
            BonusRunnerLog.Warning(
                $"WallResidualRiseWaitExpired Context={context}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY=" +
                $"{contactFeetY:F3}, Velocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), ReleaseY=" +
                $"{wallRecoveryRequiredReleaseY:F3}, Remaining=" +
                $"{remainingToRelease:F3}, Waited={waited:F3}s. " +
                "The bounded wait expired; contact-gated wall planning may " +
                "resume instead of hanging indefinitely.");
            ResetWallResidualRiseWait();
            return false;
        }

        if (BonusRunnerLog.IsDebugMode &&
            Time.unscaledTime >= nextWallResidualRiseLogTime)
        {
            nextWallResidualRiseLogTime = Time.unscaledTime + 0.08f;
            BonusRunnerLog.Debug(
                $"WallResidualRiseWaitFrame Context={context}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), FeetY=" +
                $"{contactFeetY:F3}, VelocityY=" +
                $"{state.PlayerVelocity.y:F3}, RiseSinceWait=" +
                $"{contactFeetY - wallResidualRiseWaitStartFeetY:F3}, " +
                $"Remaining={remainingToRelease:F3}, PassiveRise=" +
                $"{passiveRise:F3}, PredictedApexFeetY=" +
                $"{predictedApexFeetY:F3}, " +
                $"CanReachRelease={passiveRiseClearsRelease}, " +
                $"Elapsed={waited:F3}s. Action=NoDown.",
                "Recovery");
        }

        // Horizontal contact was already established by the caller. Preserve
        // that evidence while the independent vertical-state gate waits for
        // either the lip or a real apex/descending sample.
        wallStallStartedAt =
            Time.unscaledTime - WallStallConfirmationSeconds;
        jumpController.Release();
        return true;
    }

    private void ResetWallResidualRiseWait()
    {
        wallResidualRiseWaitActive = false;
        wallResidualRiseWaitStartedAt = 0f;
        wallResidualRiseWaitStartFeetY = 0f;
        wallResidualRiseWaitTargetLeft = 0f;
        nextWallResidualRiseLogTime = 0f;
    }

    private void ResetMandatoryFacePlanState()
    {
        wallMandatoryFaceSetupActive = false;
        wallMandatoryFaceSetupDeadline = 0f;
        wallMandatoryFaceSetupDeadlineFixedStep = -1;
        wallMandatoryFaceInterceptCommitted = false;
        wallMandatoryFaceInterceptStartedAt = 0f;
        wallMandatoryFaceInterceptStartedFixedStep = -1;
        wallMandatoryFaceContactWatchDeadlineFixedStep = -1;
        wallMandatoryFaceTargetContactX = float.NaN;
        wallMandatoryFacePredictedTopClearFeetY = float.NaN;
        wallMandatoryFacePredictedContactFeetY = float.NaN;
        wallMandatoryFacePredictedContactVelocityY = float.NaN;
        wallMandatoryFacePredictedContactSeconds = 0f;
        wallMandatoryFaceReleasePredictedContactFeetY = float.NaN;
        wallMandatoryFaceReleasePredictedContactVelocityY = float.NaN;
        wallMandatoryFaceReleaseFeetY = float.NaN;
        wallMandatoryFaceReleaseVelocityY = float.NaN;
        wallMandatoryFaceContactObservedFixedStep = -1;
        nextMandatoryFaceWindowLogTime = 0f;
    }

    private bool IsCommittedWallClimbActive()
    {
        return learningSampleActive &&
            learningSource == "Automatic" &&
            !wallRecoveryLipCrossed &&
            automaticPlanReason.StartsWith(
                "WallRecovery",
                System.StringComparison.Ordinal) &&
            Time.unscaledTime < wallRecoveryCommitmentUntil;
    }

    // This helper returns managed route records and diagnostic strings via
    // out parameters. AutoBonusRunnerRuntime is injected as an IL2CPP type;
    // ClassInjector cannot convert this managed-only signature.
    [HideFromIl2Cpp]
    private bool HasRecoverableWallAhead(
        BonusStageState state,
        PlayerMovement player,
        bool requirePlannedTarget,
        out BonusWallContact wall,
        out string evidence)
    {
        wall = wallDetector.Detect(player, GetWallRouteSpeed());
        float committedFaceFeetY =
            player != null && player.playerCollider != null
                ? player.playerCollider.bounds.min.y
                : state.PlayerPosition.y - 0.27f;
        bool committedFaceFlightPending =
            requirePlannedTarget &&
            HasCommittedExitFaceFlight() &&
            wallExitContactWatchActive &&
            wallExitTargetActive &&
            automaticPredictionActive &&
            learningSampleActive &&
            string.Equals(
                learningSource,
                "Automatic",
                StringComparison.Ordinal) &&
            wallExitContactWatchDeadlineFixedStep >= 0 &&
            JumpPhysicsFeedback.FixedStepSequence <=
                wallExitContactWatchDeadlineFixedStep &&
            state.PlayerPosition.x <= wallExitTarget.Right + 1.0f &&
            wallExitTarget.Width > 0.05f &&
            wallExitTarget.Left - state.PlayerPosition.x <=
                CommittedExitFaceMaximumGap + 1.50f &&
            committedFaceFeetY >=
                wallExitTarget.Top - CommittedExitFaceDepth - 0.25f;
        if (committedFaceFlightPending)
        {
            evidence =
                $"CommittedExitFaceFlightPending(Mode=" +
                $"{(wallExitCollectionFaceInterceptCommitted ? "CollectionFace" : "FaceOrTop")}," +
                $"Target=[{wallExitTarget.Left:F3}," +
                $"{wallExitTarget.Right:F3}]@{wallExitTarget.Top:F3}," +
                $"FeetY={committedFaceFeetY:F3}," +
                $"FixedStep={JumpPhysicsFeedback.FixedStepSequence}/" +
                $"{wallExitContactWatchDeadlineFixedStep}," +
                $"RayDetected={wall.IsDetected}," +
                $"RayDistance={wall.Distance:F3})";
            return true;
        }
        if (!wall.IsDetected)
        {
            evidence = wall.Reason ?? "WallNotDetected";
            return false;
        }
        if (wall.Distance > RecoverableWallProbeDistance)
        {
            evidence =
                $"WallTooFar(Distance={wall.Distance:F3}," +
                $"Limit={RecoverableWallProbeDistance:F3})";
            return false;
        }
        if (!requirePlannedTarget)
        {
            evidence = wall.IsTouching
                ? "ObservedTouchingWall"
                : "ObservedReachableWall";
            return true;
        }

        bool plannedWallRoute =
            HasCommittedExitFaceFlight() &&
                wallExitTargetActive ||
            passiveWallApproachActive ||
            automaticPredictionActive &&
                (automaticManeuver ==
                    BonusManeuverKind.ApproachJumpThenWallJump ||
                 automaticManeuver ==
                    BonusManeuverKind.EnterTrenchThenWallJump ||
                 automaticManeuver == BonusManeuverKind.WallJumpClimb);
        if (!plannedWallRoute)
        {
            evidence = "DetectedWallHasNoPlannedWallRoute";
            return false;
        }

        bool committedExitTargetOwnsProbe =
            HasCommittedExitFaceFlight() &&
            wallExitTargetActive;
        float plannedTargetLeft = committedExitTargetOwnsProbe
            ? wallExitTarget.Left
            : automaticTargetLeft;
        float plannedTargetRight = committedExitTargetOwnsProbe
            ? wallExitTarget.Right
            : automaticTargetRight;
        float plannedTargetTop = committedExitTargetOwnsProbe
            ? wallExitTarget.Top
            : automaticTargetTop;
        bool targetGeometryAvailable =
            plannedTargetRight > plannedTargetLeft + 0.05f;
        bool targetFaceMatches =
            targetGeometryAvailable &&
            wall.FaceX >= plannedTargetLeft - 0.80f &&
            wall.FaceX <= plannedTargetRight + 0.80f &&
            state.PlayerPosition.x <= plannedTargetRight + 0.80f &&
            state.PlayerPosition.y < plannedTargetTop + 0.50f;
        evidence = targetFaceMatches
            ? committedExitTargetOwnsProbe
                ? "CommittedExitFaceTargetMatch"
                : "PlannedTargetWallMatch"
            : $"DetectedWallTargetMismatch(Target=[{plannedTargetLeft:F3}," +
              $"{plannedTargetRight:F3}]@{plannedTargetTop:F3}," +
              $"CommittedFace={committedExitTargetOwnsProbe})";
        return targetFaceMatches;
    }

    private bool UpdateAutomaticPitConfirmation(bool isCandidate)
    {
        if (!isCandidate)
        {
            ResetAutomaticPitConfirmation();
            return false;
        }

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (fixedStep != pitDescentCandidateLastFixedStep)
        {
            pitDescentCandidateLastFixedStep = fixedStep;
            pitDescentCandidateFixedSteps++;
        }
        return pitDescentCandidateFixedSteps >=
            PitDescentConfirmationFixedSteps;
    }

    private void ResetAutomaticPitConfirmation()
    {
        pitDescentCandidateLastFixedStep = -1;
        pitDescentCandidateFixedSteps = 0;
    }

    private bool UpdateManualPitConfirmation(bool isCandidate)
    {
        if (!isCandidate)
        {
            ResetManualPitConfirmation();
            return false;
        }

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (fixedStep != manualPitCandidateLastFixedStep)
        {
            manualPitCandidateLastFixedStep = fixedStep;
            manualPitCandidateFixedSteps++;
        }
        return manualPitCandidateFixedSteps >=
            PitDescentConfirmationFixedSteps;
    }

    private void ResetManualPitConfirmation()
    {
        manualPitCandidateLastFixedStep = -1;
        manualPitCandidateFixedSteps = 0;
    }

    private void ResetWallRecoveryAfterLanding()
    {
        ResetPassiveWallTargetSupportEvidence();
        wallStallStartedAt = -1f;
        wallRecoveryAttempts = 0;
        wallRecoveryContactLatched = false;
        wallRecoverySawUpwardMotion = false;
        wallRecoveryContactX = 0f;
        wallRecoveryContactY = 0f;
        wallRecoveryCommitmentUntil = 0f;
        wallRecoveryImpulseStartY = 0f;
        wallRecoveryImpulseStartVelocityY = 0f;
        wallRecoveryLastObservedY = 0f;
        nextWallClimbFrameLogTime = 0f;
        wallRecoveryUpwardPhysicsSteps = 0;
        wallRecoveryImpulseConfirmed = false;
        wallRecoveryImpulseFailureLogged = false;
        wallRecoveryImpulseReleaseObservedFixedStep = -1;
        wallRecoveryPrematureReleaseLogged = false;
        wallRecoveryLipCrossed = false;
        wallRecoveryPhysicalLipY = 0f;
        wallRecoveryPhysicalLeft = 0f;
        wallRecoveryPhysicalRight = 0f;
        wallRecoveryPhysicalSafeLeft = 0f;
        wallRecoveryPhysicalSafeRight = 0f;
        wallRecoveryPhysicalLipFrozen = false;
        wallRecoveryRequiredReleaseY = 0f;
        ResetWallObjectiveCache();
        wallExitTargetActive = false;
        wallExitTarget = default;
        ClearWallExitPreparedContract();
        wallExitPredictedLandingX = 0f;
        wallExitPredictedTravel = 0f;
        wallExitPredictedFlightSeconds = 0f;
        wallExitTransferAcceptedRawBodyFit = false;
        wallExitTransferSafeTolerance = 0f;
        wallExitPlanSummary = string.Empty;
        wallExitTransferCommitted = false;
        wallLandingFlightCommitted = false;
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        ClearWatchedExitSupportFixedStepLatch();
        wallTopLandingSequenceCommitted = false;
        wallExitContactWatchActive = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        ResetWallResidualRiseWait();
        wallExitFaceContactRequired = false;
        wallExitObjectiveCountAtCapture = 0;
        wallExitObjectiveMinimumY = float.NaN;
        wallExitObjectiveMaximumY = float.NaN;
        ResetMandatoryFacePlanState();
        attachedObjectiveDescentActive = false;
        attachedObjectiveDescentDeadline = 0f;
        attachedObjectiveDescentDeadlineFixedStep = -1;
        attachedObjectiveDescentTargetFeetY = 0f;
        attachedObjectiveDescentSphereCount = 0;
        nextAttachedObjectiveDescentLogTime = 0f;
        ResetGround5HighestPillarSink();
        wallReleaseObservedFixedStep = -1;
        wallDetachedLastFixedStep = -1;
        wallDetachedConfirmationSteps = 0;
        wallActionPhase = WallActionPhase.None;
        wallRouteSpeedLatched = false;
        wallRouteHorizontalSpeed = 0f;
        wallInputSeparationReleaseFixedStep = -1;
        sharedWallOwnedFaceX = float.NaN;
    }

    private void ResetPassiveWallTargetSupportEvidence()
    {
        passiveWallTargetSupportLastFixedStep = -1;
        passiveWallTargetSupportFixedSteps = 0;
        passiveWallTargetSupportPlayerInstanceId = 0;
        passiveWallTargetSupportTarget = default;
        passiveWallTargetSupportSurface = default;
    }

    private void ClearWallExitPreparedContract()
    {
        wallExitPreparedPlanActive = false;
        wallExitPreparedPlan = default;
        wallExitPreparedSpeed = 0f;
        wallExitPreparedTarget = default;
    }

    private void ResetWallObjectiveCache()
    {
        wallObjectiveCacheActive = false;
        wallObjectiveCacheTargetLeft = 0f;
        wallObjectiveCacheTargetRight = 0f;
        wallObjectiveCacheTargetTop = 0f;
        wallObjectiveCacheCount = 0;
        wallObjectiveCacheMinimumY = float.NaN;
        wallObjectiveCacheMaximumY = float.NaN;
    }

    private void ResetGround5HighestPillarSink()
    {
        ground5HighestPillarSinkActive = false;
        ground5HighestPillarSinkArmedFixedStep = -1;
        ground5HighestPillarSinkArmedFixedTime = -1d;
        ground5HighestPillarSinkDeadlineFixedTime = -1d;
        ground5HighestPillarSinkFixedDelta = 0.02f;
        ground5HighestPillarSinkLastObservedFixedTime = -1d;
        ground5HighestPillarSinkBackgroundCatchUpObserved = false;
        ground5HighestPillarSinkStartFeetY = 0f;
        ground5HighestPillarSinkTargetFeetY = 0f;
        ground5HighestPillarSinkSphereCount = 0;
    }

    private void ResetWallRecoveryState()
    {
        ClearRecentAutomaticFlightContact();
        ResetWallRecoveryAfterLanding();
        passiveWallApproachActive = false;
        nextWallRecoveryTime = 0f;
        nextWallProbeLogTime = 0f;
    }

    private void ResetStage2UnmappedWallTraverse()
    {
        stage2UnmappedWallTraverseActive = false;
        stage2UnmappedWallTraverseTarget = default;
        stage2UnmappedWallTraversePulses = 0;
        stage2UnmappedWallStallLastFixedStep = -1;
        stage2UnmappedWallStallFixedSteps = 0;
        stage2UnmappedWallLastPulsePosition = default;
        nextStage2UnmappedWallLogTime = 0f;
    }

    private void ClearRecentAutomaticFlightContact()
    {
        recentAutomaticFlightContactActive = false;
        recentAutomaticFlightAttemptId = 0;
        recentAutomaticFlightRouteId = 0;
        recentAutomaticFlightEndedFixedStep = -1;
        recentAutomaticFlightEndedAt = -1f;
        recentAutomaticFlightPlayerInstanceId = 0;
        recentAutomaticFlightMap = string.Empty;
        recentAutomaticFlightSection = -1;
        recentAutomaticFlightOutcome = string.Empty;
        recentAutomaticFlightPlan = string.Empty;
        recentAutomaticFlightManeuver = BonusManeuverKind.None;
        recentAutomaticFlightSource = default;
        recentAutomaticFlightTarget = default;
        recentAutomaticFlightPredictedLandingX = 0f;
        recentAutomaticFlightTriggerVelocity = default;
    }

    private void CaptureRecentAutomaticFlightContact(
        BonusStageState state,
        string outcome)
    {
        bool lifecycleClosure =
            outcome.IndexOf("Stage", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("Section", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("PositionDiscontinuity", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("Pit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("Retry", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("Manual", StringComparison.OrdinalIgnoreCase) >= 0 ||
            outcome.IndexOf("Automation", StringComparison.OrdinalIgnoreCase) >= 0;
        bool nonWallAutomaticFlight =
            learningSource == "Automatic" &&
            learningTookOff &&
            automaticPredictionActive &&
            automaticManeuver != BonusManeuverKind.None &&
            automaticManeuver != BonusManeuverKind.EnterTrenchThenWallJump &&
            automaticManeuver != BonusManeuverKind.ApproachJumpThenWallJump &&
            automaticManeuver != BonusManeuverKind.WallJumpClimb;
        BonusBoardSegment target = BuildAutomaticTargetSegment();
        bool eligible =
            !lifecycleClosure &&
            state.UsesStage3AuthoredRouting &&
            state.SectionIndex >= 0 &&
            state.SectionIndex <= 2 &&
            state.PlayerInstanceId != 0 &&
            nonWallAutomaticFlight &&
            target.Width > 0.05f;
        if (!eligible)
        {
            if (lifecycleClosure)
                ClearRecentAutomaticFlightContact();
            return;
        }

        recentAutomaticFlightContactActive = true;
        recentAutomaticFlightAttemptId = automaticAttemptId;
        recentAutomaticFlightRouteId = activeRouteDecisionId;
        recentAutomaticFlightEndedFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
        recentAutomaticFlightEndedAt = Time.unscaledTime;
        recentAutomaticFlightPlayerInstanceId = state.PlayerInstanceId;
        recentAutomaticFlightMap = state.MapName ?? string.Empty;
        recentAutomaticFlightSection = state.SectionIndex;
        recentAutomaticFlightOutcome = outcome ?? string.Empty;
        recentAutomaticFlightPlan = automaticPlanReason ?? string.Empty;
        recentAutomaticFlightManeuver = automaticManeuver;
        recentAutomaticFlightSource = automaticSourceSegment;
        recentAutomaticFlightTarget = target;
        recentAutomaticFlightPredictedLandingX =
            automaticPredictedLandingX;
        recentAutomaticFlightTriggerVelocity =
            learningTriggerVelocity;
    }

    private void ResetAutomaticControlState()
    {
        automaticJumpArmed = true;
        airborneAfterAutomaticJump = false;
        automaticJumpVelocityConfirmed = false;
        ResetPartialAutomaticJumpImpulse();
        stage2LowRouteOverheadPreservedAttemptId = 0;
        automaticPredictionActive = false;
        automaticMinimumPredictedHorizontalTravel = 0f;
        automaticMaximumPredictedHorizontalTravel = 0f;
        automaticFutureSpeedTransitionExpected = false;
        automaticSpiritBoostRouteEvidence = "Unavailable";
        automaticPlannedLandingBias = 0f;
        activeRouteDecisionId = 0;
        automaticPlanReason = string.Empty;
        automaticManeuver = BonusManeuverKind.None;
        automaticLandingAllowsRawBodyFit = false;
        automaticLandingSafeTolerance = 0f;
        automaticTargetColliderId = 0;
        automaticTargetColliderName = string.Empty;
        automaticTargetMapPieceName = string.Empty;
        automaticTargetMapPieceOriginX = float.NaN;
        automaticTargetMapPieceInstanceId = 0;
        automaticTargetRegistryGeneration = 0;
        automaticTargetStaticSurfaceIndex = -1;
        automaticTargetTop = 0f;
        automaticSphereCountAtPlan = -1;
        automaticRemainingSpheresAtPlan = -1;
        automaticRawExpectedSphereHits = 0;
        automaticExpectedSphereHits = 0;
        automaticExpectedSpeedBoostHits = 0;
        automaticSpheresAtPlan = "Unavailable";
        automaticTrajectoryCompatible = false;
        nextDynamicPlanLogTime = 0f;
        noSupportStallStartedAt = -1f;
        nextNoSupportStallLogTime = 0f;
        lastRouteSignature = string.Empty;
        lastIntentionalDropSignature = string.Empty;
        nextIntentionalDropLogTime = 0f;
        lastBoostRouteSelection = string.Empty;
        lastLiveRouteSelection = string.Empty;
        ResetStage2GroundedWallContactEvidence();
        ResetSharedGroundedWallStallEvidence();
        ResetStage2UnmappedWallTraverse();
        ResetWallRecoveryState();
        ClearRoutePlanLock();
        ClearSecondStagePreview();
    }

    private void ResetPartialAutomaticJumpImpulse()
    {
        partialAutomaticJumpImpulseOwnedFlight = false;
        partialAutomaticJumpImpulseAttemptId = 0;
        partialAutomaticJumpObservedVelocityY = 0f;
        partialAutomaticJumpExpectedVelocityY = 0f;
    }

    private void ClearAutomaticAttemptIdentityAfterLifecycleHandoff()
    {
        automaticAttemptId = 0;
        automaticTargetLeft = 0f;
        automaticTargetRight = 0f;
        automaticTargetSafeLeft = 0f;
        automaticTargetSafeRight = 0f;
        automaticTargetTop = 0f;
        automaticPredictedLandingX = 0f;
        automaticPredictedHorizontalTravel = 0f;
        automaticPlannedLaunchX = 0f;
        automaticLaunchWindowLeft = 0f;
        automaticLaunchWindowRight = 0f;
        automaticSourceSegment = default;
    }

    [HideFromIl2Cpp]
    private bool TryUseSpiritWaitPlanCache(
        BonusStageState state,
        BonusBoardScanResult liveScan,
        Vector3 livePosition,
        float liveSpeed,
        JumpPhysicsSnapshot physics,
        BonusHazard hazard,
        IReadOnlyList<Vector2> objectives,
        SpiritBoostRouteContext spiritBoost,
        out BonusBoardScanResult cachedScan,
        out BonusJumpPlan cachedPlan,
        out string evidence)
    {
        cachedScan = default;
        cachedPlan = default;
        if (!spiritWaitPlanCacheActive)
        {
            evidence = "SpiritWaitPlanCacheMiss[Inactive]";
            return false;
        }

        int objectiveSignature =
            ComputeSpiritObjectiveSignature(objectives);
        int triggerSignature =
            ComputeSpiritTriggerSignature(spiritBoost);
        int hazardSignature =
            ComputeSpiritHazardSignature(hazard);
        bool sphereProgressChanged =
            spiritWaitPlanCacheSphereProgress != state.CollectedSpheres;
        bool objectivesChanged =
            spiritWaitPlanCacheObjectiveSignature != objectiveSignature;
        int removedPassedObjectives = 0;
        bool passedObjectiveRemovalsAccepted =
            (sphereProgressChanged || objectivesChanged) &&
            state.UsesStage2LiveRouting &&
            !state.SpiritBoostEnabled &&
            state.SectionIndex == 3 &&
            spiritWaitPlanCachePlan.IsValid &&
            !spiritWaitPlanCachePlan.ShouldJumpNow &&
            livePosition.x < spiritWaitPlanCacheReplanX &&
            state.CollectedSpheres >= spiritWaitPlanCacheSphereProgress &&
            TryAcceptOnlyPassedObjectiveRemovals(
                spiritWaitPlanCacheObjectives,
                objectives,
                livePosition.x + 0.80f,
                out removedPassedObjectives);
        string invalidReason = null;
        if (!IsStableGroundWaitCacheKinematics(
                state,
                liveSpeed,
                spiritBoost))
            invalidReason = "KinematicsChanged";
        else if (!string.Equals(
                     spiritWaitPlanCacheMap,
                     state.MapName,
                     StringComparison.Ordinal) ||
                 spiritWaitPlanCacheSection != state.SectionIndex)
            invalidReason = "MapOrSectionChanged";
        else if (spiritWaitPlanCachePhysicsRevision != physics.ModelRevision)
            invalidReason = "PhysicsRevisionChanged";
        else if (Mathf.Abs(spiritWaitPlanCacheSpeed - liveSpeed) > 0.15f &&
                 !CanReuseStage2DeceleratingSpiritWait(
                     state,
                     spiritWaitPlanCacheSpeed,
                     liveSpeed,
                     spiritBoost))
            invalidReason = "SpeedChanged";
        else if (sphereProgressChanged &&
                 !passedObjectiveRemovalsAccepted)
            invalidReason = "SphereProgressChanged";
        else if (objectivesChanged &&
                 !passedObjectiveRemovalsAccepted)
            invalidReason = "ObjectivesChanged";
        else if (spiritWaitPlanCacheTriggerSignature != triggerSignature)
            invalidReason = "TriggerGeometryChanged";
        else if (spiritWaitPlanCacheHazardSignature != hazardSignature)
            invalidReason = "HazardChanged";
        else if (!SpiritCacheSourceMatches(
                     liveScan.Current,
                     spiritWaitPlanCacheScan.Current,
                     state.UsesStage2LiveRouting))
            invalidReason = "SourceSurfaceChanged";
        else if (!spiritWaitPlanCacheScan.HasNext &&
                 liveScan.HasNext)
            invalidReason = "NewTargetEnteredVerifiedHorizon";
        else if (spiritWaitPlanCacheScan.HasNext &&
                 liveScan.HasNext &&
                 !SpiritCacheScanContainsTarget(
                     liveScan,
                     spiritWaitPlanCacheScan.Next) &&
                 liveScan.Next.Left <
                     spiritWaitPlanCacheScan.Next.Left - 0.35f)
            invalidReason = "EarlierTargetEnteredVerifiedHorizon";

        if (invalidReason != null)
        {
            evidence =
                $"SpiritWaitPlanCacheMiss[{invalidReason},X=" +
                $"{livePosition.x:F3},CachedVX=" +
                $"{spiritWaitPlanCacheSpeed:F3},LiveVX={liveSpeed:F3}," +
                $"CachedRev={spiritWaitPlanCachePhysicsRevision}," +
                $"LiveRev={physics.ModelRevision},CachedSpheres=" +
                $"{spiritWaitPlanCacheSphereProgress},LiveSpheres=" +
                $"{state.CollectedSpheres}]";
            spiritWaitPlanCacheActive = false;
            spiritWaitPlanCacheObjectives = Array.Empty<Vector2>();
            return false;
        }

        if (livePosition.x >= spiritWaitPlanCacheReplanX)
        {
            // The cache is only a cheap coast decision. The final native step
            // always rebuilds both speed endpoints at the actual X.
            spiritWaitPlanCacheActive = false;
            spiritWaitPlanCacheObjectives = Array.Empty<Vector2>();
            evidence =
                $"SpiritWaitPlanCacheFinalProofRequired[X=" +
                $"{livePosition.x:F3},Launch=" +
                $"{spiritWaitPlanCachePlan.PlannedLaunchX:F3}," +
                $"ReplanX={spiritWaitPlanCacheReplanX:F3}]";
            return false;
        }

        if (passedObjectiveRemovalsAccepted)
        {
            spiritWaitPlanCacheSphereProgress = state.CollectedSpheres;
            spiritWaitPlanCacheObjectiveSignature = objectiveSignature;
            spiritWaitPlanCacheObjectives =
                SnapshotSpiritObjectives(objectives);
        }
        cachedScan = spiritWaitPlanCacheScan;
        cachedPlan = spiritWaitPlanCachePlan;
        evidence =
            $"SpiritWaitPlanCacheHit[ArmedStep=" +
            $"{spiritWaitPlanCacheArmedFixedStep},X={livePosition.x:F3}," +
            $"Launch={cachedPlan.PlannedLaunchX:F3}," +
            $"Ahead={cachedPlan.PlannedLaunchX - livePosition.x:F3}," +
            $"PhysicsRev={physics.ModelRevision}," +
            $"Objectives={objectives?.Count ?? 0}," +
            $"PassedObjectivesRemoved=" +
            $"{(passedObjectiveRemovalsAccepted
                ? removedPassedObjectives
                : 0)}]";
        return true;
    }

    [HideFromIl2Cpp]
    private void TryStoreSpiritWaitPlanCache(
        BonusStageState state,
        BonusBoardScanResult scan,
        BonusJumpPlan plan,
        Vector3 livePosition,
        float liveSpeed,
        JumpPhysicsSnapshot physics,
        BonusHazard hazard,
        IReadOnlyList<Vector2> objectives,
        SpiritBoostRouteContext spiritBoost,
        long fixedStep)
    {
        float replanDistance = GetSpiritWaitCacheReplanDistance(
            liveSpeed,
            physics);
        bool stableKinematics = IsStableGroundWaitCacheKinematics(
            state,
            liveSpeed,
            spiritBoost);
        bool approachingExecutablePlan =
            plan.IsValid &&
            !plan.ShouldJumpNow &&
            (string.Equals(
                 plan.Reason,
                 "ApproachingLaunchWindow",
                 StringComparison.Ordinal) ||
             string.Equals(
                 plan.Reason,
                 "ApproachingSameSurfaceSphereCollection",
                 StringComparison.Ordinal) ||
             plan.Maneuver ==
                 BonusManeuverKind.ApproachJumpThenWallJump &&
             (string.Equals(
                  plan.Reason,
                  "ApproachingWallContact",
                  StringComparison.Ordinal) ||
              string.Equals(
                  plan.Reason,
                  "ApproachingTrenchEntry",
                  StringComparison.Ordinal) ||
              string.Equals(
                 plan.Reason,
                  "ApproachingDeepTrenchEntry",
                  StringComparison.Ordinal) ||
              state.UsesStage2LiveRouting &&
              state.SpiritBoostEnabled &&
              string.Equals(
                  plan.Reason,
                  "ApproachingStage2SpiritRisingStair",
                  StringComparison.Ordinal))) &&
            (plan.Maneuver == BonusManeuverKind.GroundJumpToLanding ||
             plan.Maneuver == BonusManeuverKind.SphereCollectionJump ||
             plan.Maneuver == BonusManeuverKind.SphereSweepToLowerLanding ||
             plan.Maneuver ==
                 BonusManeuverKind.ApproachJumpThenWallJump);
        bool provedNoCommandAcrossVerifiedSurface =
            !scan.HasNext &&
            !plan.IsValid &&
            (string.Equals(
                 plan.Reason,
                 "NextBoardUnavailable",
                 StringComparison.Ordinal) ||
             string.Equals(
                 plan.Reason,
                 "ContinuousSurface",
                 StringComparison.Ordinal));
        bool cacheable =
            stableKinematics &&
            scan.IsValid &&
            (approachingExecutablePlan &&
             plan.PlannedLaunchX - livePosition.x > replanDistance ||
             provedNoCommandAcrossVerifiedSurface &&
             scan.Current.SafeRight - livePosition.x >
                 replanDistance + 0.50f);
        if (!cacheable)
        {
            spiritWaitPlanCacheActive = false;
            spiritWaitPlanCacheObjectives = Array.Empty<Vector2>();
            return;
        }

        spiritWaitPlanCacheActive = true;
        spiritWaitPlanCacheMap = state.MapName ?? string.Empty;
        spiritWaitPlanCacheSection = state.SectionIndex;
        spiritWaitPlanCacheScan = scan;
        spiritWaitPlanCachePlan = plan;
        spiritWaitPlanCachePhysicsRevision = physics.ModelRevision;
        spiritWaitPlanCacheSpeed = liveSpeed;
        spiritWaitPlanCacheSphereProgress = state.CollectedSpheres;
        spiritWaitPlanCacheObjectiveSignature =
            ComputeSpiritObjectiveSignature(objectives);
        spiritWaitPlanCacheObjectives =
            SnapshotSpiritObjectives(objectives);
        spiritWaitPlanCacheTriggerSignature =
            ComputeSpiritTriggerSignature(spiritBoost);
        spiritWaitPlanCacheHazardSignature =
            ComputeSpiritHazardSignature(hazard);
        spiritWaitPlanCacheArmedFixedStep = fixedStep;
        float stage2SpiritReplanDistance =
            state.UsesStage2LiveRouting &&
            state.SpiritBoostEnabled &&
            spiritBoost.CurrentBoostComponent > 0.15f
                ? Mathf.Max(
                    replanDistance,
                    Mathf.Abs(liveSpeed) *
                        Mathf.Clamp(
                            physics.FixedDeltaTime,
                            0.005f,
                            0.05f) *
                        2.25f +
                    0.20f)
                : replanDistance;
        spiritWaitPlanCacheReplanX = approachingExecutablePlan
            ? plan.PlannedLaunchX - stage2SpiritReplanDistance
            : scan.Current.SafeRight -
              Mathf.Clamp(liveSpeed * 0.12f, 1.00f, 3.00f);
        spiritWaitPlanCacheHitLogged = false;
        BonusRunnerLog.Debug(
            $"GroundWaitPlanCacheArmed FixedStep={fixedStep}, Mode=" +
            $"{(state.SpiritBoostEnabled ? "Spirit" : "Ordinary")}, Section=" +
            $"{state.SectionIndex}, X={livePosition.x:F3}, VX=" +
            $"{liveSpeed:F3}, Launch={plan.PlannedLaunchX:F3}, Ahead=" +
            $"{plan.PlannedLaunchX - livePosition.x:F3}, Hold=" +
            $"{plan.HoldSeconds:F3}, ExpectedSoulHits=" +
            $"{plan.ExpectedSphereHits}, ExpectedSpeedBoostHits=" +
            $"{plan.ExpectedSpeedBoostHits}, PhysicsRev=" +
            $"{physics.ModelRevision}, ReplanX=" +
            $"{spiritWaitPlanCacheReplanX:F3}, Objectives=" +
            $"{objectives?.Count ?? 0}. The cached command is WAIT only; " +
            "DOWN still requires a fresh fixed-step slow/fast proof near " +
            "the launch window.",
            "Performance");
    }

    [HideFromIl2Cpp]
    private void LogGroundPlanningPhaseCost(
        BonusStageState state,
        Vector3 position,
        float speed,
        BonusJumpPlan plan,
        bool cacheHit,
        long startedTimestamp,
        long platformScanCompleted,
        long hazardScanCompleted,
        long physicsModelCompleted,
        long objectiveScanCompleted,
        long spiritContextCompleted,
        long routePlanningCompleted,
        long fixedStep)
    {
        double ticksToMilliseconds =
            1000d / System.Diagnostics.Stopwatch.Frequency;
        double totalMilliseconds =
            (routePlanningCompleted - startedTimestamp) *
            ticksToMilliseconds;
        bool expensiveFullPlan =
            !cacheHit &&
            totalMilliseconds >= 8d;
        bool periodicSlowCacheHit =
            cacheHit &&
            totalMilliseconds >= 5d &&
            Time.unscaledTime >= nextGroundPlanningPhaseCostLogTime;
        if (!expensiveFullPlan && !periodicSlowCacheHit)
        {
            return;
        }

        nextGroundPlanningPhaseCostLogTime =
            Time.unscaledTime + 0.75f;
        BonusRunnerLog.Debug(
            $"GroundPlanningPhaseCost FixedStep={fixedStep}, Mode=" +
            $"{(state.SpiritBoostEnabled ? "Spirit" : "Ordinary")}, " +
            $"Cache={(cacheHit ? "Hit" : "Miss")}, TotalMs=" +
            $"{totalMilliseconds:F3}, PlatformMs=" +
            $"{(platformScanCompleted - startedTimestamp) *
               ticksToMilliseconds:F3}, HazardMs=" +
            $"{(hazardScanCompleted - platformScanCompleted) *
               ticksToMilliseconds:F3}, PhysicsMs=" +
            $"{(physicsModelCompleted - hazardScanCompleted) *
               ticksToMilliseconds:F3}, ObjectivesMs=" +
            $"{(objectiveScanCompleted - physicsModelCompleted) *
               ticksToMilliseconds:F3}, SpiritContextMs=" +
            $"{(spiritContextCompleted - objectiveScanCompleted) *
               ticksToMilliseconds:F3}, RouteMs=" +
            $"{(routePlanningCompleted - spiritContextCompleted) *
               ticksToMilliseconds:F3}, Section={state.SectionIndex}, " +
            $"X={position.x:F3}, VX={speed:F3}, Plan=" +
            $"{plan.Reason}/{plan.Maneuver}.",
            "Performance");
    }

    [HideFromIl2Cpp]
    private void LogSpiritGroundPlanningCost(
        BonusStageState state,
        Vector3 position,
        float speed,
        BonusJumpPlan plan,
        bool cacheHit,
        string cacheEvidence,
        long startedTimestamp,
        long fixedStep)
    {
        if (!state.SpiritBoostEnabled)
            return;

        double elapsedMilliseconds =
            (System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp) *
            1000d / System.Diagnostics.Stopwatch.Frequency;
        bool firstCacheHit = cacheHit && !spiritWaitPlanCacheHitLogged;
        bool expensiveFullPlan = !cacheHit && elapsedMilliseconds >= 15d;
        bool periodicSlowCacheHit =
            cacheHit &&
            elapsedMilliseconds >= 5d &&
            Time.unscaledTime >= nextSpiritPlanningCostLogTime;
        if (!firstCacheHit && !expensiveFullPlan && !periodicSlowCacheHit)
            return;

        spiritWaitPlanCacheHitLogged |= cacheHit;
        nextSpiritPlanningCostLogTime = Time.unscaledTime + 0.75f;
        BonusRunnerLog.Debug(
            $"SpiritGroundPlanningCost FixedStep={fixedStep}, Mode=" +
            $"{(cacheHit ? "CachedWait" : "FullProof")}, ElapsedMs=" +
            $"{elapsedMilliseconds:F3}, Section={state.SectionIndex}, " +
            $"Position=({position.x:F3},{position.y:F3}), VX={speed:F3}, " +
            $"Plan={plan.Reason}/{plan.Maneuver}, ShouldJump=" +
            $"{plan.ShouldJumpNow}, PlannedLaunch={plan.PlannedLaunchX:F3}, " +
            $"ExpectedSoulHits={plan.ExpectedSphereHits}, " +
            $"ExpectedSpeedBoostHits={plan.ExpectedSpeedBoostHits}, Cache=" +
            $"{cacheEvidence}.",
            "Performance");
    }

    [HideFromIl2Cpp]
    private static bool IsStableGroundWaitCacheKinematics(
        BonusStageState state,
        float liveSpeed,
        SpiritBoostRouteContext spiritBoost)
    {
        if (!state.SpiritBoostEnabled)
        {
            return !spiritBoost.Enabled &&
                liveSpeed > 1f &&
                liveSpeed < 80f;
        }

        if (!spiritBoost.Enabled ||
            !spiritBoost.KinematicsAvailable ||
            spiritBoost.BaseHorizontalSpeed <= 1f)
        {
            return false;
        }

        if (state.UsesStage2LiveRouting)
        {
            float expectedMaximumSpeed =
                spiritBoost.BaseHorizontalSpeed +
                Mathf.Max(
                    0.15f,
                    spiritBoost.CurrentBoostComponent) +
                0.50f;
            return liveSpeed >=
                       spiritBoost.BaseHorizontalSpeed - 0.25f &&
                   liveSpeed <= expectedMaximumSpeed;
        }

        return spiritBoost.CurrentBoostComponent <= 0.15f &&
               Mathf.Abs(
                   liveSpeed -
                   spiritBoost.BaseHorizontalSpeed) <= 0.25f;
    }

    [HideFromIl2Cpp]
    private static bool CanReuseStage2DeceleratingSpiritWait(
        BonusStageState state,
        float cachedSpeed,
        float liveSpeed,
        SpiritBoostRouteContext spiritBoost)
    {
        if (!state.UsesStage2LiveRouting ||
            !state.SpiritBoostEnabled ||
            !spiritBoost.Enabled ||
            !spiritBoost.KinematicsAvailable)
        {
            return false;
        }

        // A native Spirit component decays monotonically between pickups.
        // Reusing WAIT while it falls is safe because the final DOWN remains
        // forbidden until a fresh proof before the cached launch. Any speed
        // increase (a real boost pickup/reset) still invalidates immediately.
        return liveSpeed <= cachedSpeed + 0.15f &&
               cachedSpeed - liveSpeed <= 2.0f &&
               liveSpeed >= spiritBoost.BaseHorizontalSpeed - 0.25f;
    }

    [HideFromIl2Cpp]
    private static float GetSpiritWaitCacheReplanDistance(
        float speed,
        JumpPhysicsSnapshot physics) =>
        Mathf.Max(
            0.30f,
            Mathf.Abs(speed) * Mathf.Clamp(
                physics.FixedDeltaTime,
                0.005f,
                0.05f) * 1.10f);

    [HideFromIl2Cpp]
    private static bool SpiritCacheSourceMatches(
        BonusBoardSegment live,
        BonusBoardSegment cached,
        bool allowStage2CompositeExtension = false)
    {
        bool colliderMatches =
            live.ColliderInstanceId == 0 ||
            cached.ColliderInstanceId == 0 ||
            live.ColliderInstanceId == cached.ColliderInstanceId;
        bool rightBoundaryMatches =
            Mathf.Abs(live.Right - cached.Right) <= 0.30f;
        bool authoritativeColliderMatch =
            live.ColliderInstanceId != 0 &&
            cached.ColliderInstanceId != 0 &&
            live.ColliderInstanceId == cached.ColliderInstanceId;
        if (allowStage2CompositeExtension &&
            authoritativeColliderMatch &&
            live.Right >= cached.Right - 0.30f)
        {
            // Stage 2's one pooled CompositeCollider2D exposes more of the
            // same runway as the forward scan advances. A monotonically
            // extending right boundary is not a topology change; HasNext and
            // target-identity checks below still invalidate as soon as an
            // earlier support enters the verified horizon.
            rightBoundaryMatches = true;
        }
        return colliderMatches &&
            rightBoundaryMatches &&
            Mathf.Abs(live.Top - cached.Top) <= 0.15f;
    }

    [HideFromIl2Cpp]
    private static bool SpiritCacheSurfaceMatches(
        BonusBoardSegment live,
        BonusBoardSegment cached)
    {
        bool colliderMatches =
            live.ColliderInstanceId == 0 ||
            cached.ColliderInstanceId == 0 ||
            live.ColliderInstanceId == cached.ColliderInstanceId;
        return colliderMatches &&
            Mathf.Abs(live.Left - cached.Left) <= 0.35f &&
            Mathf.Abs(live.Right - cached.Right) <= 0.60f &&
            Mathf.Abs(live.Top - cached.Top) <= 0.15f;
    }

    [HideFromIl2Cpp]
    private static bool SpiritCacheScanContainsTarget(
        BonusBoardScanResult liveScan,
        BonusBoardSegment cachedTarget)
    {
        if (liveScan.HasNext &&
            SpiritCacheSurfaceMatches(liveScan.Next, cachedTarget))
        {
            return true;
        }
        if (liveScan.HasIntermediate &&
            SpiritCacheSurfaceMatches(liveScan.Intermediate, cachedTarget))
        {
            return true;
        }
        return liveScan.Alternatives != null &&
            liveScan.Alternatives.Any(surface =>
                SpiritCacheSurfaceMatches(surface, cachedTarget));
    }

    [HideFromIl2Cpp]
    private static int ComputeSpiritObjectiveSignature(
        IReadOnlyList<Vector2> objectives)
    {
        unchecked
        {
            int hash = 17;
            int count = objectives?.Count ?? 0;
            hash = hash * 31 + count;
            for (int index = 0; index < count; index++)
            {
                Vector2 objective = objectives[index];
                hash = hash * 31 + Mathf.RoundToInt(objective.x * 100f);
                hash = hash * 31 + Mathf.RoundToInt(objective.y * 100f);
            }
            return hash;
        }
    }

    [HideFromIl2Cpp]
    private static Vector2[] SnapshotSpiritObjectives(
        IReadOnlyList<Vector2> objectives)
    {
        int count = objectives?.Count ?? 0;
        if (count == 0)
            return Array.Empty<Vector2>();
        if (objectives is Vector2[] array)
            return array;

        Vector2[] snapshot = new Vector2[count];
        for (int index = 0; index < count; index++)
            snapshot[index] = objectives[index];
        return snapshot;
    }

    [HideFromIl2Cpp]
    private static bool TryAcceptOnlyPassedObjectiveRemovals(
        IReadOnlyList<Vector2> cachedObjectives,
        IReadOnlyList<Vector2> liveObjectives,
        float passedRight,
        out int removedObjectives)
    {
        // GetActiveSpherePositions orders both snapshots by X and then Y.
        // This allocation-free merge accepts a strict deletion-only change;
        // it must never be used with an unsorted objective source.
        removedObjectives = 0;
        int cachedCount = cachedObjectives?.Count ?? 0;
        int liveCount = liveObjectives?.Count ?? 0;
        if (cachedCount <= liveCount)
            return false;

        int cachedIndex = 0;
        int liveIndex = 0;
        while (cachedIndex < cachedCount &&
               liveIndex < liveCount)
        {
            Vector2 cached = cachedObjectives[cachedIndex];
            Vector2 live = liveObjectives[liveIndex];
            if (Mathf.Abs(cached.x - live.x) <= 0.04f &&
                Mathf.Abs(cached.y - live.y) <= 0.04f)
            {
                cachedIndex++;
                liveIndex++;
                continue;
            }

            bool cachedSortsBeforeLive =
                cached.x < live.x - 0.04f ||
                Mathf.Abs(cached.x - live.x) <= 0.04f &&
                cached.y < live.y - 0.04f;
            if (!cachedSortsBeforeLive ||
                cached.x > passedRight + 0.001f)
            {
                // A live-only or forward objective means the route evidence
                // changed. The WAIT proof must be rebuilt immediately.
                return false;
            }

            removedObjectives++;
            cachedIndex++;
        }

        if (liveIndex < liveCount)
            return false;
        while (cachedIndex < cachedCount)
        {
            Vector2 cached = cachedObjectives[cachedIndex++];
            if (cached.x > passedRight + 0.001f)
                return false;
            removedObjectives++;
        }

        return removedObjectives > 0 &&
            cachedCount - removedObjectives == liveCount;
    }

    [HideFromIl2Cpp]
    private static int ComputeSpiritTriggerSignature(
        SpiritBoostRouteContext spiritBoost)
    {
        unchecked
        {
            int hash = spiritBoost.TriggerScanSucceeded ? 23 : 29;
            BonusSpeedBoostTrigger[] triggers =
                spiritBoost.ActiveTriggers ??
                Array.Empty<BonusSpeedBoostTrigger>();
            hash = hash * 31 + triggers.Length;
            foreach (BonusSpeedBoostTrigger trigger in triggers)
            {
                hash = hash * 31 + trigger.InstanceId;
                hash = hash * 31 + Mathf.RoundToInt(trigger.Left * 100f);
                hash = hash * 31 + Mathf.RoundToInt(trigger.Right * 100f);
                hash = hash * 31 + Mathf.RoundToInt(trigger.Bottom * 100f);
                hash = hash * 31 + Mathf.RoundToInt(trigger.Top * 100f);
            }
            return hash;
        }
    }

    [HideFromIl2Cpp]
    private static int ComputeSpiritHazardSignature(BonusHazard hazard)
    {
        if (!hazard.IsValid)
            return 0;
        unchecked
        {
            int hash = hazard.InstanceId;
            hash = hash * 31 + Mathf.RoundToInt(hazard.Left * 100f);
            hash = hash * 31 + Mathf.RoundToInt(hazard.Right * 100f);
            hash = hash * 31 + Mathf.RoundToInt(hazard.Top * 100f);
            return hash;
        }
    }

    private void ClearRoutePlanLock()
    {
        // The retained ground WAIT cache can never authorize input and must
        // be discarded at every ownership/lifecycle transition.
        spiritWaitPlanCacheActive = false;
        spiritWaitPlanCacheObjectives = Array.Empty<Vector2>();
        spiritWaitPlanCacheReplanX = 0f;
        spiritWaitPlanCacheHitLogged = false;
    }

    private BonusBoardSegment BuildAutomaticTargetSegment() => new(
        automaticTargetLeft,
        automaticTargetRight,
        automaticTargetTop,
        automaticTargetSafeLeft,
        automaticTargetSafeRight,
        automaticTargetColliderId,
        automaticTargetColliderName,
        automaticTargetMapPieceName,
        automaticTargetMapPieceOriginX,
        automaticTargetMapPieceInstanceId,
        automaticTargetRegistryGeneration,
        automaticTargetStaticSurfaceIndex);

    private static bool StaticOrColliderIdentityMatches(
        BonusBoardSegment live,
        BonusBoardSegment expected)
    {
        // The bonus stage uses one CompositeCollider2D across many prefab
        // seams. A continuous live segment can legitimately change its
        // single static annotation as the probe crosses that seam. Bounds and
        // top are checked independently by every caller, so matching live
        // collider geometry is authoritative and static identity is only an
        // additional signal when both sides can actually provide it.
        if (live.ColliderInstanceId != 0 &&
            expected.ColliderInstanceId != 0 &&
            live.ColliderInstanceId == expected.ColliderInstanceId)
        {
            return true;
        }

        // A static preview and the live composite collider can annotate the
        // exact same physical runway with adjacent pooled prefab identities.
        // Geometry is authoritative at that seam; rejecting coincident
        // bounds/top polluted a verified Ground 5 exit as WrongSupport and
        // discarded useful landing feedback.
        bool coincidentGeometry =
            Mathf.Abs(live.Left - expected.Left) <= 0.12f &&
            Mathf.Abs(live.Right - expected.Right) <= 0.12f &&
            Mathf.Abs(live.Top - expected.Top) <= 0.35f;
        if (coincidentGeometry)
            return true;

        bool liveHasStaticIdentity =
            live.MapPieceInstanceId != 0 &&
            live.StaticSurfaceIndex >= 0;
        bool expectedHasStaticIdentity =
            expected.MapPieceInstanceId != 0 &&
            expected.StaticSurfaceIndex >= 0;
        if (liveHasStaticIdentity && expectedHasStaticIdentity)
        {
            return live.MapPieceInstanceId == expected.MapPieceInstanceId &&
                   live.StaticSurfaceIndex == expected.StaticSurfaceIndex;
        }

        // Identity is unavailable on one side (for example a static preview
        // versus the same live composite surface). Do not turn a verified
        // bounds/top match into a false WrongSupport result.
        return true;
    }

    private void BeginPassiveWallApproach(
        BonusStageState state,
        BonusJumpPlan plan,
        BonusBoardScanResult scan,
        BonusBoardSegment target,
        BonusHazard hazard,
        JumpPhysicsSnapshot planningPhysics)
    {
        ResetWallRecoveryAfterLanding();
        ResetPartialAutomaticJumpImpulse();
        LatchWallRouteSpeed(state, "PassiveWallApproach");
        passiveWallApproachActive = true;
        activeRouteDecisionId = ++nextRouteDecisionId;
        wallActionPhase = WallActionPhase.EnteringTrench;
        automaticJumpArmed = false;
        automaticAttemptId = 0;
        automaticJumpRequestedAt = Time.unscaledTime;
        airborneAfterAutomaticJump = false;
        automaticJumpVelocityConfirmed = false;
        automaticPredictionActive = true;
        automaticPlanReason = plan.Reason;
        automaticManeuver = BonusManeuverKind.EnterTrenchThenWallJump;
        automaticTargetSafeLeft = target.SafeLeft;
        automaticTargetSafeRight = target.SafeRight;
        automaticLandingAllowsRawBodyFit = false;
        automaticLandingSafeTolerance = 0f;
        automaticTargetLeft = target.Left;
        automaticTargetRight = target.Right;
        automaticTargetTop = target.Top;
        sharedWallOwnedFaceX = target.Left;
        automaticTargetColliderId = target.ColliderInstanceId;
        automaticTargetColliderName = target.ColliderName;
        automaticTargetMapPieceName = target.MapPieceName;
        automaticTargetMapPieceOriginX = target.MapPieceOriginX;
        automaticTargetMapPieceInstanceId = target.MapPieceInstanceId;
        automaticTargetRegistryGeneration = target.RegistryGeneration;
        automaticTargetStaticSurfaceIndex = target.StaticSurfaceIndex;
        automaticSourceSegment = scan.Current;
        automaticPredictionLaunchFeetY = scan.Current.Top;
        automaticTargetHeightDelta = target.Top - scan.Current.Top;
        automaticPlanTriggerPosition = state.PlayerPosition;
        automaticPlannedLaunchX = plan.PlannedLaunchX;
        automaticLaunchWindowLeft = plan.LaunchWindowLeft;
        automaticLaunchWindowRight = plan.LaunchWindowRight;
        automaticPredictedFlightSeconds = plan.PredictedFlightSeconds;
        automaticPredictedHorizontalTravel = plan.HorizontalTravel;
        automaticMinimumPredictedHorizontalTravel = plan.HorizontalTravel;
        automaticMaximumPredictedHorizontalTravel = plan.HorizontalTravel;
        automaticFutureSpeedTransitionExpected = false;
        automaticSpiritBoostRouteEvidence =
            "WallContactObservedAuthority";
        automaticPredictedLandingX = plan.PredictedLandingX;
        automaticPlannedHold = 0f;
        automaticPlanPhysicsSnapshot = planningPhysics;
        automaticPhysicsRevision = planningPhysics.ModelRevision;
        automaticTriggerSpeed = Mathf.Max(
            1f,
            Mathf.Abs(state.PlayerVelocity.x));
        automaticPlannedTravelScale = planningPhysics.HorizontalTravelScale;
        automaticPlannedLandingBias = 0f;
        automaticTrajectoryCompatible = true;
        automaticHazardAtPlan = hazard.IsValid
            ? $"[{hazard.Left:F3},{hazard.Right:F3}]@{hazard.Top:F3}," +
              $"Id={hazard.InstanceId},Path={hazard.ComponentPath}"
            : "None";
        automaticSphereCountAtPlan =
            BonusStageInspector.TryGetBonusSphereCount(out int sphereCount)
                ? sphereCount
                : -1;
        automaticRemainingSpheresAtPlan = state.HasSphereProgress
            ? state.RemainingRequiredSpheres
            : -1;
        automaticRawExpectedSphereHits = 0;
        automaticExpectedSphereHits = 0;
        automaticExpectedSpeedBoostHits = 0;
        automaticSpheresAtPlan = BonusStageInspector.DescribeActiveSpheres(
            state.PlayerPosition.x - 1.0f,
            target.Right + 2.0f);
        ClearRoutePlanLock();
        ClearSecondStagePreview();
        PrepareSecondStagePreview(
            state,
            target,
            Mathf.Clamp(
                plan.PredictedLandingX,
                target.SafeLeft,
                target.SafeRight),
            planningPhysics,
            "WallDropApproach");

        BonusRunnerLog.Debug(
            $"WallDropRouteArmed RouteId={activeRouteDecisionId}, Frame={Time.frameCount}, " +
            $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"Source=[{scan.Current.Left:F3},{scan.Current.Right:F3}]" +
            $"@{scan.Current.Top:F3}, Target=[{target.Left:F3}," +
            $"{target.Right:F3}]@{target.Top:F3}, " +
            $"ArmX={plan.PlannedLaunchX:F3}, " +
            $"ExpectedWallContactX={plan.PredictedLandingX:F3}, " +
            $"ExpectedContactTime={plan.PredictedFlightSeconds:F3}s, " +
            $"Hazard={automaticHazardAtPlan}, " +
            $"RouteSpheres[{automaticSpheresAtPlan}]. No input has been sent; " +
            "waiting for physical wall contact before wall-jump phase one.",
            "Recovery");
    }

    private void MarkAutomaticJumpRequested(
        BonusStageState state,
        BonusJumpPlan plan,
        BonusBoardSegment target,
        BonusBoardScanResult scan,
        BonusHazard hazard,
        JumpPhysicsSnapshot planningPhysics,
        float triggerSpeedOverride = 0f,
        SpiritBoostRouteContext spiritBoost = default)
    {
        if (!jumpController.IsHoldingJump) return;
        ResetPartialAutomaticJumpImpulse();
        stage2LowRouteOverheadPreservedAttemptId = 0;
        ClearRecentAutomaticFlightContact();
        // These names now describe route evidence only. A Stage-2-specific
        // input owner must never survive the command; actual wall contact is
        // rebased onto the shared physical wall executor.
        ResetStage2UnmappedWallTraverse();
        if (plan.Maneuver == BonusManeuverKind.ApproachJumpThenWallJump)
            LatchWallRouteSpeed(state, "ActiveWallApproach");
        activeRouteDecisionId = ++nextRouteDecisionId;
        automaticJumpArmed = false;
        automaticJumpRequestedAt = Time.unscaledTime;
        automaticJumpVelocityConfirmed = false;
        StartLearningSample(
            state,
            "Automatic",
            IsGroundLearningManeuver(plan.Maneuver));
        automaticAttemptId = learningSampleId;
        learningInputUpTime = learningInputDownTime + plan.HoldSeconds;
        learningInputReleased = true;
        automaticPredictionActive = true;
        automaticPredictedLandingX = plan.PredictedLandingX;
        automaticPredictedFlightSeconds = plan.PredictedFlightSeconds;
        automaticPredictedHorizontalTravel = plan.HorizontalTravel;
        automaticMinimumPredictedHorizontalTravel =
            plan.MinimumHorizontalTravel > 0f
                ? plan.MinimumHorizontalTravel
                : plan.HorizontalTravel;
        automaticMaximumPredictedHorizontalTravel =
            plan.MaximumHorizontalTravel > 0f
                ? plan.MaximumHorizontalTravel
                : plan.HorizontalTravel;
        automaticFutureSpeedTransitionExpected =
            plan.FutureSpeedTransitionExpected;
        automaticExpectedSpeedBoostHits =
            plan.ExpectedSpeedBoostHits;
        automaticSpiritBoostRouteEvidence = spiritBoost.Summary;
        automaticPredictionLaunchFeetY = scan.Current.Top;
        automaticPlannedTravelScale = planningPhysics.HorizontalTravelScale;
        automaticPlannedHold = plan.HoldSeconds;
        automaticTriggerSpeed = triggerSpeedOverride > 1f
            ? triggerSpeedOverride
            : Mathf.Max(1f, Mathf.Abs(state.PlayerVelocity.x));
        automaticTargetHeightDelta = target.Top - scan.Current.Top;
        automaticPlannedLandingBias =
            planningPhysics.LandingErrorProfile.GetAppliedBias(
                automaticTargetHeightDelta,
                automaticPlannedHold,
                out _,
                out _);
        automaticPhysicsRevision = planningPhysics.ModelRevision;
        automaticTargetSafeLeft = target.SafeLeft;
        automaticTargetSafeRight = target.SafeRight;
        automaticLandingAllowsRawBodyFit = false;
        automaticLandingSafeTolerance = 0f;
        automaticTargetLeft = target.Left;
        automaticTargetRight = target.Right;
        automaticTargetTop = target.Top;
        automaticTargetColliderId = target.ColliderInstanceId;
        automaticTargetColliderName = target.ColliderName;
        automaticTargetMapPieceName = target.MapPieceName;
        automaticTargetMapPieceOriginX = target.MapPieceOriginX;
        automaticTargetMapPieceInstanceId = target.MapPieceInstanceId;
        automaticTargetRegistryGeneration = target.RegistryGeneration;
        automaticTargetStaticSurfaceIndex = target.StaticSurfaceIndex;
        automaticPlanReason = plan.Reason;
        automaticManeuver = plan.Maneuver;
        if (plan.Maneuver == BonusManeuverKind.ApproachJumpThenWallJump)
            wallActionPhase = WallActionPhase.ApproachJumpInFlight;
        automaticPlanPhysicsSnapshot = planningPhysics;
        automaticTrajectoryCompatible = true;
        nextTrajectoryMonitorLogTime = 0f;
        automaticPlanTriggerPosition = state.PlayerPosition;
        automaticPlannedLaunchX = plan.PlannedLaunchX;
        automaticLaunchWindowLeft = plan.LaunchWindowLeft;
        automaticLaunchWindowRight = plan.LaunchWindowRight;
        automaticSourceSegment = scan.Current;
        automaticHazardAtPlan = hazard.IsValid
            ? $"[{hazard.Left:F3},{hazard.Right:F3}]@{hazard.Top:F3}," +
              $"Id={hazard.InstanceId},Path={hazard.ComponentPath}"
            : "None";
        automaticSphereCountAtPlan =
            BonusStageInspector.TryGetBonusSphereCount(out int sphereCount)
                ? sphereCount
                : -1;
        automaticRemainingSpheresAtPlan = state.HasSphereProgress
            ? state.RemainingRequiredSpheres
            : -1;
        automaticRawExpectedSphereHits = plan.ExpectedSphereHits;
        automaticExpectedSphereHits =
            automaticRemainingSpheresAtPlan >= 0
                ? Math.Min(
                    automaticRawExpectedSphereHits,
                    automaticRemainingSpheresAtPlan)
                : automaticRawExpectedSphereHits;
        automaticSpheresAtPlan = BonusStageInspector.DescribeActiveSpheres(
            state.PlayerPosition.x - 1.0f,
            Mathf.Max(target.Right, plan.PredictedLandingX) + 2.0f);

        BonusRunnerLog.Debug(
            $"JumpAttemptPlan RouteId={activeRouteDecisionId}, AttemptId={automaticAttemptId}, Frame={Time.frameCount}, " +
            $"Map={state.MapName}, Section={state.SectionIndex}, " +
            $"Maneuver={plan.Maneuver}, Plan={plan.Reason}, " +
            $"Trigger=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"TriggerVelocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"SourceRaw=[{scan.Current.Left:F3},{scan.Current.Right:F3}] " +
            $"SourceSafe=[{scan.Current.SafeLeft:F3},{scan.Current.SafeRight:F3}] " +
            $"SourceTop={scan.Current.Top:F3} SourceCollider=" +
            $"{scan.Current.ColliderInstanceId}:{scan.Current.ColliderName}, " +
            $"TargetRaw=[{target.Left:F3},{target.Right:F3}] " +
            $"TargetSafe=[{target.SafeLeft:F3},{target.SafeRight:F3}] " +
            $"TargetCenter={(target.SafeLeft + target.SafeRight) * 0.5f:F3} " +
            $"TargetSafeWidth={target.SafeRight - target.SafeLeft:F3} " +
            $"TargetTop={target.Top:F3} TargetCollider={target.ColliderInstanceId}:{target.ColliderName}, " +
            $"Gap={scan.Gap:F3}, DeltaY={target.Top - scan.Current.Top:F3}, " +
            $"ScanReason={scan.Reason}, " +
            (scan.HasIntermediate
                ? $"IntermediateRaw=[{scan.Intermediate.Left:F3}," +
                  $"{scan.Intermediate.Right:F3}]@{scan.Intermediate.Top:F3}, "
                : string.Empty) +
            $"Hold={plan.HoldSeconds:F3}s, PredictedFlight={plan.PredictedFlightSeconds:F3}s, " +
            $"PredictedTravel={plan.HorizontalTravel:F3}, TravelEnvelope=" +
            $"[{automaticMinimumPredictedHorizontalTravel:F3}," +
            $"{automaticMaximumPredictedHorizontalTravel:F3}], " +
            $"FutureSpeedTransition=" +
            $"{automaticFutureSpeedTransitionExpected}, PlannedLaunch=" +
            $"{plan.PlannedLaunchX:F3}, " +
            $"LaunchWindow=[{plan.LaunchWindowLeft:F3},{plan.LaunchWindowRight:F3}], " +
            $"PredictedLanding=({plan.PredictedLandingX:F3},{target.Top:F3}), " +
            $"Hazard={automaticHazardAtPlan}, " +
            $"SphereCountAtPlan={automaticSphereCountAtPlan}, " +
            $"ExpectedSphereHits={automaticExpectedSphereHits}, " +
            $"RawExpectedSphereHits={automaticRawExpectedSphereHits}, " +
            $"ExpectedSpeedBoostHits=" +
            $"{automaticExpectedSpeedBoostHits}, " +
            $"RemainingAtPlan={automaticRemainingSpheresAtPlan}, " +
            $"SpiritBoostRoute[{automaticSpiritBoostRouteEvidence}], " +
            $"RouteSpheres[{automaticSpheresAtPlan}], " +
            $"PhysicsRev={automaticPhysicsRevision}",
            "Attempt");

        secondStageObservedAirborne = false;
        if (automaticFutureSpeedTransitionExpected)
        {
            // Preserve the target support identity even though its projected
            // continuation is non-authoritative. The physical landing may be
            // only one FixedUpdate wide; clearing this entire contract made
            // the fixed-step controller unable to recognize and recover an
            // otherwise valid edge contact after a Spirit reset.
            PrepareSecondStagePreview(
                state,
                target,
                plan.PredictedLandingX,
                planningPhysics,
                $"FutureSpeedSupport:{automaticAttemptId}");
            BonusRunnerLog.Debug(
                $"SecondStageSupportIdentityPreserved AttemptId=" +
                $"{automaticAttemptId}, Reason=FutureSpiritBoostTransition," +
                $" TravelEnvelope=[" +
                $"{automaticMinimumPredictedHorizontalTravel:F3}," +
                $"{automaticMaximumPredictedHorizontalTravel:F3}]. " +
                "The projected action is diagnostic only; the target support " +
                "identity is retained and the next action is rebuilt from " +
                "live position, velocity and typed boost state on the " +
                "physical landing FixedUpdate.",
                "Lookahead");
            return;
        }
        PrepareSecondStagePreview(
            state,
            target,
            plan.PredictedLandingX,
            planningPhysics,
            $"JumpAttempt:{automaticAttemptId}");
    }

    private void PrepareSecondStagePreview(
        BonusStageState state,
        BonusBoardSegment expectedSupport,
        float expectedLandingX,
        JumpPhysicsSnapshot physics,
        string source)
    {
        PlayerMovement player = PlayerMovement.instance;
        if (player == null || expectedSupport.Width <= 0.05f)
        {
            ClearSecondStagePreview();
            return;
        }

        BonusBoardScanResult projectedScan =
            platformScanner.ScanProjectedSupport(
                expectedSupport,
                expectedLandingX,
                player,
                GetWallRouteSpeed());
        BonusHazard projectedHazard = hazardScanner.FindNearest(
            new Vector3(expectedLandingX, expectedSupport.Top, 0f));
        float projectedSpeed = GetWallRouteSpeed();
        string projectedReachableSelection = "NotEvaluated";
        Vector2[] projectedSphereObjectives =
            state.HasSphereProgress &&
            state.RemainingRequiredSpheres > 0
                ? BonusStageInspector.GetActiveSpherePositions(
                    expectedLandingX - 1.0f,
                    expectedLandingX + SectionObjectiveHorizon)
                : Array.Empty<Vector2>();
        SpiritBoostRouteContext spiritBoostRouteContext =
            CaptureRouteSpeedContext(
                state,
                player,
                expectedLandingX - 2.0f,
                expectedLandingX + 30f,
                physics.BaseHorizontalSpeed > 1f &&
                physics.BaseHorizontalSpeed < 80f
                    ? physics.BaseHorizontalSpeed
                    : sectionCruiseHorizontalSpeed > 1f
                        ? sectionCruiseHorizontalSpeed
                        : 0f);
        bool expectedSupportOwnsPendingBoost =
            spiritBoostRouteContext.ActiveTriggers != null &&
            spiritBoostRouteContext.ActiveTriggers.Any(trigger =>
                trigger.IsValid &&
                trigger.Left <= expectedSupport.Right + 0.75f &&
                trigger.Right >= expectedSupport.Left - 0.75f);
        if (expectedSupportOwnsPendingBoost)
        {
            // The preview represents the fixed step immediately after the
            // first jump touches this verified boost platform. Its
            // continuation must be solved with the deterministic reset
            // speed, not the pre-pickup velocity latched at takeoff.
            projectedSpeed = Mathf.Max(
                projectedSpeed,
                spiritBoostRouteContext.BaseHorizontalSpeed +
                spiritBoostRouteContext.MaximumBoostComponent);
        }
        BonusJumpPlan projectedSelectorPlan = default;
        bool projectedSelectorPlanAvailable = false;
        projectedScan = jumpPlanner.SelectReachableRoute(
            projectedScan,
            new Vector3(
                expectedLandingX,
                expectedSupport.Top,
                0f),
            new Vector2(projectedSpeed, 0f),
            physics,
            projectedHazard,
            projectedSphereObjectives,
            sectionIndex: state.SectionIndex,
            preferSphereCoverage:
                projectedSphereObjectives.Length > 0,
            allowRecoverableLowerFaceCatch:
                !state.UsesStage3AuthoredRouting &&
                state.SectionIndex >= 2,
            useFixedStepAlignedHolds:
                !state.UsesStage3AuthoredRouting &&
                state.SectionIndex >= 2,
            spiritBoost: spiritBoostRouteContext,
            selectionContext: "Preview",
            selection: out projectedReachableSelection,
            selectedPlan: out projectedSelectorPlan,
            selectedPlanAvailable:
                out projectedSelectorPlanAvailable,
            useStage2LiveTopologyProfile:
                state.UsesStage2LiveRouting);
        BonusJumpPlan projectedPlan = projectedSelectorPlanAvailable
            ? projectedSelectorPlan
            : jumpPlanner.Plan(
                projectedScan,
                new Vector3(expectedLandingX, expectedSupport.Top, 0f),
                new Vector2(projectedSpeed, 0f),
                physics,
                projectedHazard,
                projectedSphereObjectives,
                sectionIndex: state.SectionIndex,
                preferSphereCoverage:
                projectedSphereObjectives.Length > 0,
                allowRecoverableLowerFaceCatch:
                    !state.UsesStage3AuthoredRouting &&
                    state.SectionIndex >= 2,
                useFixedStepAlignedHolds:
                    !state.UsesStage3AuthoredRouting &&
                    state.SectionIndex >= 2,
                spiritBoost: spiritBoostRouteContext,
                useStage2LiveTopologyProfile:
                    state.UsesStage2LiveRouting);

        string signature = projectedScan.IsValid
            ? projectedScan.HasNext
                ? $"{projectedScan.Current.Left:F2}:{projectedScan.Current.Right:F2}:" +
                  $"{projectedScan.Current.Top:F2}>{projectedScan.Next.Left:F2}:" +
                  $"{projectedScan.Next.Right:F2}:{projectedScan.Next.Top:F2}:" +
                  $"{projectedPlan.HoldSeconds:F2}:{projectedPlan.Reason}"
                : $"{projectedScan.Current.Left:F2}:{projectedScan.Current.Right:F2}:NoNext"
            : $"Invalid:{projectedScan.Reason}";
        bool changed = signature != secondStageSignature;

        secondStagePreviewActive = true;
        secondStageExpectedSupport = expectedSupport;
        secondStageExpectedLandingX = expectedLandingX;
        secondStageProjectedScan = projectedScan;
        secondStageProjectedPlan = projectedPlan;
        secondStageSource = source;
        secondStageSignature = signature;
        nextSecondStageRefreshTime = Time.unscaledTime + 0.10f;

        if (changed)
        {
            string next = projectedScan.IsValid && projectedScan.HasNext
                ? $"[{projectedScan.Next.Left:F3},{projectedScan.Next.Right:F3}] " +
                  $"Safe=[{projectedScan.Next.SafeLeft:F3}," +
                  $"{projectedScan.Next.SafeRight:F3}]@{projectedScan.Next.Top:F3}"
                : "Unavailable";
            BonusRunnerLog.Debug(
                $"SecondStagePreview Source={source}, " +
                $"ExpectedLandingX={expectedLandingX:F3}, " +
                $"ExpectedSupport=[{expectedSupport.Left:F3}," +
                $"{expectedSupport.Right:F3}]@{expectedSupport.Top:F3}, " +
                $"ProjectedVX={projectedSpeed:F3}, " +
                $"PendingBoostOnSupport=" +
                $"{expectedSupportOwnsPendingBoost}, " +
                "CompletionImplicitBoostReset=False, " +
                $"ProjectedCurrent=" +
                $"[{projectedScan.Current.Left:F3},{projectedScan.Current.Right:F3}]" +
                $"@{projectedScan.Current.Top:F3}, Next={next}, " +
                $"PreparedAction={(projectedPlan.ShouldJumpNow ? "Jump" : "Wait")}, " +
                $"PreparedHold={projectedPlan.HoldSeconds:F3}s, " +
                $"PreparedWindow=[{projectedPlan.LaunchWindowLeft:F3}," +
                $"{projectedPlan.LaunchWindowRight:F3}], " +
                $"PreparedLanding={projectedPlan.PredictedLandingX:F3}, " +
                $"Reason={projectedPlan.Reason}, ReachableSelection=" +
                $"{projectedReachableSelection}.",
                "Lookahead");
        }
    }

    private void RefreshSecondStagePreview(
        BonusStageState state,
        PlayerMovement player)
    {
        if (!secondStagePreviewActive || player == null)
            return;

        secondStageObservedAirborne = true;
        if (!automaticTrajectoryCompatible ||
            Time.unscaledTime < nextSecondStageRefreshTime)
            return;

        float previewHorizontalSpeed = Mathf.Abs(state.PlayerVelocity.x);
        if (previewHorizontalSpeed <= 1f || previewHorizontalSpeed >= 80f)
            previewHorizontalSpeed = Mathf.Max(1f, lastReliableHorizontalSpeed);
        JumpPhysicsSnapshot physics = BuildPlanningPhysics(
            state.SectionIndex,
            jumpPhysicsFeedback.CaptureSnapshot(player),
            previewHorizontalSpeed,
            state.IsActiveGameplay ||
                IsSuccessfulCompletionTraversal(state),
            state.SpiritBoostEnabled
                ? sectionCruiseHorizontalSpeed
                : 0f);
        PrepareSecondStagePreview(
            state,
            secondStageExpectedSupport,
            secondStageExpectedLandingX,
            physics,
            secondStageSource);
    }

    private void MonitorAutomaticTrajectory(BonusStageState state)
    {
        if (!automaticPredictionActive || !learningTookOff ||
            !automaticTrajectoryCompatible)
            return;

        // A wall action intentionally has zero horizontal motion while
        // attached and resumes forward speed only at the lip. Linear
        // trigger-to-landing interpolation therefore reports a false
        // deviation and drowns the useful WallClimbFrame/landing diagnostics.
        if (automaticManeuver == BonusManeuverKind.WallJumpClimb)
            return;

        float elapsed = Mathf.Max(
            0f,
            Time.unscaledTime - automaticJumpRequestedAt);
        float progress = Mathf.Clamp01(
            elapsed /
            Mathf.Max(0.05f, automaticPredictedFlightSeconds));
        float expectedX = Mathf.Lerp(
            automaticPlanTriggerPosition.x,
            automaticPredictedLandingX,
            progress);
        float expectedMinimumX =
            automaticPlanTriggerPosition.x +
            automaticMinimumPredictedHorizontalTravel * progress;
        float expectedMaximumX =
            automaticPlanTriggerPosition.x +
            automaticMaximumPredictedHorizontalTravel * progress;
        float expectedY = jumpPlanner.PredictVerticalYAtTime(
            automaticPredictionLaunchFeetY,
            automaticPlannedHold,
            elapsed,
            automaticPlanPhysicsSnapshot);
        float errorX = state.PlayerPosition.x - expectedX;
        float errorY = state.PlayerPosition.y - expectedY;
        bool horizontalCompatible =
            automaticFutureSpeedTransitionExpected ||
            Mathf.Abs(errorX) <= 0.65f;
        bool compatible =
            horizontalCompatible &&
            Mathf.Abs(errorY) <= 0.90f;

        if (Time.unscaledTime >= nextTrajectoryMonitorLogTime || !compatible)
        {
            nextTrajectoryMonitorLogTime = Time.unscaledTime + 0.12f;
            BonusRunnerLog.Debug(
                $"TrajectoryMonitor AttemptId={automaticAttemptId}, " +
                $"Elapsed={elapsed:F3}s, Progress={progress:F3}, " +
                $"Expected=({expectedX:F3},{expectedY:F3}), " +
                $"ExpectedXEnvelope=[{expectedMinimumX:F3}," +
                $"{expectedMaximumX:F3}], FutureSpeedTransition=" +
                $"{automaticFutureSpeedTransitionExpected}, " +
                $"Actual=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Error=({errorX:F3},{errorY:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), Compatible={compatible}.",
                "Trajectory");
        }

        if (!compatible)
        {
            automaticTrajectoryCompatible = false;
            BonusRunnerLog.Debug(
                $"SecondStageInvalidated AttemptId={automaticAttemptId}, " +
                $"Reason=TrajectoryDeviation, ErrorX={errorX:F3}, " +
                $"ErrorY={errorY:F3}, HorizontalCompatible=" +
                $"{horizontalCompatible}. Landing geometry will be rescanned.",
                "Lookahead");
        }
    }

    private void ConfirmOrRejectSecondStagePreview(
        BonusStageState state,
        BonusBoardScanResult liveScan)
    {
        if (!secondStagePreviewActive || !secondStageObservedAirborne)
            return;

        bool supportMatch = liveScan.IsValid &&
            Mathf.Abs(liveScan.Current.Top -
                secondStageExpectedSupport.Top) <= 0.35f &&
            state.PlayerPosition.x >= secondStageExpectedSupport.Left - 0.15f &&
            state.PlayerPosition.x <= secondStageExpectedSupport.Right + 0.15f;
        float landingError =
            state.PlayerPosition.x - secondStageExpectedLandingX;
        bool landingMatch = Mathf.Abs(landingError) <= 0.55f;
        bool geometryMatch = supportMatch &&
            liveScan.HasNext == secondStageProjectedScan.HasNext;
        if (geometryMatch && liveScan.HasNext)
        {
            geometryMatch =
                Mathf.Abs(liveScan.Next.Left -
                    secondStageProjectedScan.Next.Left) <= 0.25f &&
                Mathf.Abs(liveScan.Next.Right -
                    secondStageProjectedScan.Next.Right) <= 0.25f &&
                Mathf.Abs(liveScan.Next.Top -
                    secondStageProjectedScan.Next.Top) <= 0.35f;
        }

        bool confirmed =
            automaticTrajectoryCompatible &&
            supportMatch && landingMatch && geometryMatch;
        BonusRunnerLog.Debug(
            $"SecondStage{(confirmed ? "Confirmed" : "Rejected")} " +
            $"Source={secondStageSource}, ActualLanding=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"ExpectedLandingX={secondStageExpectedLandingX:F3}, " +
            $"LandingErrorX={landingError:F3}, SupportMatch={supportMatch}, " +
            $"GeometryMatch={geometryMatch}, " +
            $"TrajectoryCompatible={automaticTrajectoryCompatible}, " +
            $"PreparedPlan={secondStageProjectedPlan.Reason}. " +
            "The live landing scan is authoritative.",
            "Lookahead");
        ClearSecondStagePreview();
    }

    private void ClearSecondStagePreview()
    {
        secondStagePreviewActive = false;
        secondStageObservedAirborne = false;
        secondStageExpectedSupport = default;
        secondStageProjectedScan = default;
        secondStageProjectedPlan = default;
        secondStageExpectedLandingX = 0f;
        secondStageSource = string.Empty;
        secondStageSignature = string.Empty;
        nextSecondStageRefreshTime = 0f;
    }

    private void CaptureWallExitTargetFromPreview(BonusStageState state)
    {
        if (wallExitTargetActive ||
            !secondStagePreviewActive ||
            !secondStageProjectedScan.IsValid ||
            !secondStageProjectedScan.HasNext)
        {
            return;
        }

        bool previewDescribesPlannedWall =
            Mathf.Abs(secondStageExpectedSupport.Left -
                automaticTargetLeft) <= 0.25f &&
            Mathf.Abs(secondStageExpectedSupport.Right -
                automaticTargetRight) <= 0.25f &&
            Mathf.Abs(secondStageExpectedSupport.Top -
                automaticTargetTop) <= 0.35f;
        BonusBoardSegment candidate = secondStageProjectedScan.Next;
        if (!previewDescribesPlannedWall ||
            candidate.Right <= automaticTargetRight + 0.10f)
        {
            return;
        }

        BonusBoardSegment completedWall = BuildAutomaticTargetSegment();
        bool ordinaryStage2Ground4PreviewReturnsToUnderlyingRoad =
            state.UsesStage2LiveRouting &&
            (state.SectionIndex == 0 || state.SectionIndex == 1) &&
            !state.SpiritBoostEnabled &&
            string.Equals(
                completedWall.MapPieceName,
                "Stage2 Ground 4",
                StringComparison.OrdinalIgnoreCase) &&
            (completedWall.StaticSurfaceIndex == 1 ||
             completedWall.StaticSurfaceIndex == 3) &&
            completedWall.MapPieceInstanceId != 0 &&
            string.Equals(
                candidate.MapPieceName,
                "Stage2 Ground 4",
                StringComparison.OrdinalIgnoreCase) &&
            candidate.StaticSurfaceIndex == 0 &&
            candidate.MapPieceInstanceId ==
                completedWall.MapPieceInstanceId &&
            candidate.Top <= completedWall.Top - 3.50f &&
            candidate.Left <= completedWall.Left + 0.15f &&
            candidate.Right >= completedWall.Right - 0.15f;
        if (ordinaryStage2Ground4PreviewReturnsToUnderlyingRoad)
        {
            BonusRunnerLog.Debug(
                $"WallExitPreviewRejected Reason=" +
                $"Stage2Ground4UnderlyingRoadIsNotStairSuccessor, Wall=" +
                $"[{completedWall.Left:F3},{completedWall.Right:F3}]@" +
                $"{completedWall.Top:F3}/S" +
                $"{completedWall.StaticSurfaceIndex}, Candidate=" +
                $"[{candidate.Left:F3},{candidate.Right:F3}]@" +
                $"{candidate.Top:F3}/S{candidate.StaticSurfaceIndex}, " +
                $"MapPiece={completedWall.MapPieceName}#" +
                $"{completedWall.MapPieceInstanceId}. The broad lower road " +
                "contains the stair but is not its forward exit; static " +
                "rising-chain capture remains eligible in this frame.",
                "Recovery");
            return;
        }

        if (!ConfigureWallExitRouteContract(
                completedWall,
                candidate,
                "SecondStagePreview"))
        {
            // Do not publish a rejected preview.  In particular, an
            // unverified Ground 3 S2 successor must not block the static-map
            // capture below and then fall through to the generic top-landing
            // executor.
            wallExitTargetActive = false;
            wallExitTarget = default;
            ClearWallExitPreparedContract();
            return;
        }

        wallExitTargetActive = true;
        wallExitTarget = candidate;
        bool preparedPlanTargetsCandidate =
            secondStageProjectedPlan.IsValid &&
            secondStageProjectedPlan.HoldSeconds >= 0.02f &&
            secondStageProjectedPlan.PredictedLandingX >=
                candidate.SafeLeft - 0.25f &&
            secondStageProjectedPlan.PredictedLandingX <=
                candidate.SafeRight + 0.25f;
        if (preparedPlanTargetsCandidate)
        {
            wallExitPreparedPlanActive = true;
            wallExitPreparedPlan = secondStageProjectedPlan;
            wallExitPreparedSpeed = GetWallRouteSpeed();
            wallExitPreparedTarget = candidate;
            BonusRunnerLog.Debug(
                $"WallExitPreparedContractCaptured Target=" +
                $"[{candidate.Left:F3},{candidate.Right:F3}] Safe=" +
                $"[{candidate.SafeLeft:F3},{candidate.SafeRight:F3}]@" +
                $"{candidate.Top:F3}, PreparedAction=" +
                $"{(secondStageProjectedPlan.ShouldJumpNow ? "Jump" : "Wait")}, " +
                $"PreparedHold={secondStageProjectedPlan.HoldSeconds:F3}s, " +
                $"PreparedLanding=" +
                $"{secondStageProjectedPlan.PredictedLandingX:F3}, " +
                $"PreparedSpeed={wallExitPreparedSpeed:F3}, Maneuver=" +
                $"{secondStageProjectedPlan.Maneuver}, Reason=" +
                $"{secondStageProjectedPlan.Reason}. The downstream action " +
                $"survives the wall-contact handoff and may set a normal-speed " +
                $"minimum hold; live wall kinematics remain authoritative.",
                "Lookahead");
        }
        else
        {
            ClearWallExitPreparedContract();
        }
        BonusRunnerLog.Debug(
            $"WallExitTargetCaptured Wall=[{automaticTargetLeft:F3}," +
            $"{automaticTargetRight:F3}]@{automaticTargetTop:F3}, " +
            $"Exit=[{candidate.Left:F3},{candidate.Right:F3}] " +
            $"Safe=[{candidate.SafeLeft:F3},{candidate.SafeRight:F3}]" +
            $"@{candidate.Top:F3}, Source={secondStageSource}, " +
            $"MapPiece={candidate.MapPieceName}#" +
            $"{candidate.MapPieceInstanceId}/G{candidate.RegistryGeneration}/" +
            $"S{candidate.StaticSurfaceIndex}.",
            "Recovery");
    }

    private void CaptureChainedWallTargetFromStaticMap(float playerHalfWidth)
    {
        if (wallExitTargetActive)
            return;

        BonusBoardSegment currentWall = BuildAutomaticTargetSegment();
        bool risingChain = platformScanner.TryFindChainedWallStep(
            currentWall,
            playerHalfWidth,
            out BonusBoardSegment candidate);
        if (!risingChain &&
            !platformScanner.TryFindWallRouteContinuation(
                currentWall,
                playerHalfWidth,
                out candidate))
        {
            return;
        }

        bool sameAuthoredPiece =
            currentWall.MapPieceInstanceId != 0 &&
            currentWall.MapPieceInstanceId ==
                candidate.MapPieceInstanceId;
        bool ground3LevelObjectiveFace =
            sameAuthoredPiece &&
            string.Equals(
                currentWall.MapPieceName,
                "Ground 3",
                StringComparison.OrdinalIgnoreCase) &&
            currentWall.StaticSurfaceIndex == 2 &&
            candidate.StaticSurfaceIndex == 3 &&
            Mathf.Abs(candidate.Top - currentWall.Top) <= 0.35f;
        bool ground3DownstreamExit =
            sameAuthoredPiece &&
            string.Equals(
                currentWall.MapPieceName,
                "Ground 3",
                StringComparison.OrdinalIgnoreCase) &&
            currentWall.StaticSurfaceIndex == 3 &&
            candidate.StaticSurfaceIndex == 1 &&
            candidate.Top < currentWall.Top - 0.35f;
        bool ground5DownstreamExit =
            sameAuthoredPiece &&
            string.Equals(
                currentWall.MapPieceName,
                "Ground 5",
                StringComparison.OrdinalIgnoreCase) &&
            currentWall.StaticSurfaceIndex == 4 &&
            candidate.StaticSurfaceIndex == 1 &&
            candidate.Top < currentWall.Top - 0.35f;
        bool knownNonRisingContinuation =
            ground3LevelObjectiveFace ||
            ground3DownstreamExit ||
            ground5DownstreamExit;
        if (!risingChain && !knownNonRisingContinuation)
        {
            BonusRunnerLog.Debug(
                $"WallRouteContinuationRejected Reason=" +
                $"UnknownNonRisingRouteRole, Current=" +
                $"[{currentWall.Left:F3},{currentWall.Right:F3}]@" +
                $"{currentWall.Top:F3}/{currentWall.MapPieceName}#" +
                $"{currentWall.MapPieceInstanceId}/S" +
                $"{currentWall.StaticSurfaceIndex}, Candidate=" +
                $"[{candidate.Left:F3},{candidate.Right:F3}]@" +
                $"{candidate.Top:F3}/{candidate.MapPieceName}#" +
                $"{candidate.MapPieceInstanceId}/S" +
                $"{candidate.StaticSurfaceIndex}. " +
                "Only mapped Ground3 S2->S3, Ground3 S3->S1, and " +
                "Ground5 S4->S1 non-rising wall continuations are legal.",
                "Recovery");
            return;
        }

        if (!ConfigureWallExitRouteContract(
                currentWall,
                candidate,
                risingChain
                    ? "StaticRisingChain"
                    : "StaticForwardContinuation"))
        {
            wallExitTargetActive = false;
            wallExitTarget = default;
            ClearWallExitPreparedContract();
            return;
        }

        ClearWallExitPreparedContract();
        wallExitTargetActive = true;
        wallExitTarget = candidate;
        BonusRunnerLog.Debug(
            $"WallChainTargetCaptured Source=StaticMap, Role=" +
            $"{(risingChain ? "RisingChain" : ground3LevelObjectiveFace ? "MandatoryLevelContact" : "MappedDownstreamExit")}, Wall=" +
            $"[{currentWall.Left:F3},{currentWall.Right:F3}]" +
            $"@{currentWall.Top:F3}, Next=" +
            $"[{candidate.Left:F3},{candidate.Right:F3}]" +
            $"@{candidate.Top:F3}, Gap=" +
            $"{candidate.Left - currentWall.Right:F3}, Rise=" +
            $"{candidate.Top - currentWall.Top:F3}, MapPiece=" +
            $"{candidate.MapPieceName}#{candidate.MapPieceInstanceId}/" +
            $"G{candidate.RegistryGeneration}/S" +
            $"{candidate.StaticSurfaceIndex}.",
            "Recovery");
    }

    private void CaptureGround7WallExitTargetFromStaticMap(
        float playerHalfWidth)
    {
        if (wallExitTargetActive)
            return;

        BonusBoardSegment currentWall = BuildAutomaticTargetSegment();
        bool mappedGround7NarrowWall =
            string.Equals(
                currentWall.MapPieceName,
                "Ground 7",
                StringComparison.OrdinalIgnoreCase) &&
            currentWall.StaticSurfaceIndex == 2;
        if (!mappedGround7NarrowWall)
            return;

        BonusBoardSegment candidate =
            platformScanner.GetWallExitLandingCandidates(
                    currentWall,
                    playerHalfWidth,
                    GetWallRouteSpeed())
                .FirstOrDefault();
        if (candidate.Width < 0.75f ||
            !ConfigureWallExitRouteContract(
                currentWall,
                candidate,
                "Ground7StaticExitFallback"))
        {
            return;
        }

        ClearWallExitPreparedContract();
        wallExitTargetActive = true;
        wallExitTarget = candidate;
        BonusRunnerLog.Debug(
            $"WallExitTargetCaptured Wall=[{currentWall.Left:F3}," +
            $"{currentWall.Right:F3}]@{currentWall.Top:F3}, Exit=" +
            $"[{candidate.Left:F3},{candidate.Right:F3}] Safe=" +
            $"[{candidate.SafeLeft:F3},{candidate.SafeRight:F3}]" +
            $"@{candidate.Top:F3}, Source=Ground7StaticExitFallback, " +
            $"PostLipVX={GetWallRouteSpeed():F3}, MapPiece=" +
            $"{candidate.MapPieceName}#{candidate.MapPieceInstanceId}/G" +
            $"{candidate.RegistryGeneration}/S" +
            $"{candidate.StaticSurfaceIndex}.",
            "Recovery");
    }

    [HideFromIl2Cpp]
    private bool ConfigureWallExitRouteContract(
        BonusBoardSegment completedWall,
        BonusBoardSegment candidate,
        string source)
    {
        ClearWatchedExitSupportFixedStepLatch();
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        wallExitFaceContactRequired = false;
        wallExitObjectiveCountAtCapture = 0;
        wallExitObjectiveMinimumY = float.NaN;
        wallExitObjectiveMaximumY = float.NaN;
        ResetMandatoryFacePlanState();

        bool verifiedMandatoryGeometry =
            IsVerifiedGround3MandatoryFaceGeometry(
                latestState,
                completedWall,
                candidate,
                out string geometryReason);
        int objectiveCount = 0;
        float minimumObjectiveY = float.NaN;
        float maximumObjectiveY = float.NaN;
        bool hasLowerLaneObjectives =
            verifiedMandatoryGeometry &&
            BonusStageInspector.TryGetActiveSphereVerticalBounds(
                candidate.Left - 1.55f,
                candidate.Left + 0.70f,
                out objectiveCount,
                out minimumObjectiveY,
                out maximumObjectiveY) &&
            minimumObjectiveY < candidate.Top - 0.45f;
        if (!verifiedMandatoryGeometry &&
            string.Equals(
                completedWall.MapPieceName,
                "Ground 3",
                StringComparison.OrdinalIgnoreCase) &&
            completedWall.StaticSurfaceIndex == 2)
        {
            BonusRunnerLog.Debug(
                $"WallExitRouteContractRejected Source={source}, " +
                $"Reason={geometryReason}, Completed=" +
                $"{completedWall.MapPieceName}#" +
                $"{completedWall.MapPieceInstanceId}/G" +
                $"{completedWall.RegistryGeneration}/S" +
                $"{completedWall.StaticSurfaceIndex}@" +
                $"[{completedWall.Left:F3}," +
                $"{completedWall.Right:F3}]/Y{completedWall.Top:F3}, " +
                $"Candidate={candidate.MapPieceName}#" +
                $"{candidate.MapPieceInstanceId}/G" +
                $"{candidate.RegistryGeneration}/S" +
                $"{candidate.StaticSurfaceIndex}@" +
                $"[{candidate.Left:F3},{candidate.Right:F3}]/" +
                $"Y{candidate.Top:F3}. Candidate is not published; the " +
                "static-map resolver may replace it in the same frame.",
                "Routing");
            return false;
        }

        // A verified S2 -> S3 route with no remaining lower-lane objective is
        // still a valid optional/top route.  Other mapped wall continuations
        // also remain valid without the mandatory face-contact contract.
        if (!hasLowerLaneObjectives)
            return true;

        wallExitFaceContactRequired = true;
        wallExitObjectiveCountAtCapture = objectiveCount;
        wallExitObjectiveMinimumY = minimumObjectiveY;
        wallExitObjectiveMaximumY = maximumObjectiveY;
        BonusRunnerLog.Debug(
            $"WallExitRouteContract Source={source}, Contract=" +
            $"MandatoryFaceContact, Completed=" +
            $"[{completedWall.Left:F3},{completedWall.Right:F3}]@" +
            $"{completedWall.Top:F3}/S{completedWall.StaticSurfaceIndex}, " +
            $"Target=[{candidate.Left:F3},{candidate.Right:F3}]@" +
            $"{candidate.Top:F3}/S{candidate.StaticSurfaceIndex}, " +
            $"ObjectiveCount={objectiveCount}, ObjectiveY=" +
            $"[{minimumObjectiveY:F3},{maximumObjectiveY:F3}]. " +
            "A top landing is not route success while the lower objective " +
            "lane remains active; physical face contact must precede the " +
            "attached descent.",
            "Routing");
        return true;
    }

    private bool TryArmWallExitContactWatch(
        BonusStageState state,
        string source)
    {
        if (!wallExitTargetActive)
            return false;

        BonusBoardSegment completedWall = BuildAutomaticTargetSegment();
        float gap = wallExitTarget.Left - completedWall.Right;
        float rise = wallExitTarget.Top - completedWall.Top;
        bool canBecomeContactTarget =
            gap >= 0.10f && gap <= 5.25f &&
            rise >= -0.35f && rise <= 7.50f &&
            state.PlayerPosition.x < wallExitTarget.Left + 0.20f;
        if (!canBecomeContactTarget)
            return false;

        wallExitContactWatchActive = true;
        wallRecoveryCommitmentUntil = Time.unscaledTime + 1.25f;
        if (HasCommittedExitFaceFlight())
        {
            wallExitContactWatchDeadlineFixedStep =
                Math.Max(
                    wallExitContactWatchDeadlineFixedStep,
                    JumpPhysicsFeedback.FixedStepSequence +
                    GetPhysicsStepBudget(1.25f));
        }
        if (wallMandatoryFaceInterceptCommitted &&
            wallExitFaceContactRequired)
        {
            wallMandatoryFaceContactWatchDeadlineFixedStep =
                JumpPhysicsFeedback.FixedStepSequence +
                GetPhysicsStepBudget(
                    MandatoryWallFaceContactWatchSeconds);
        }
        BonusRunnerLog.Debug(
            $"WallExitContactWatchArmed Source={source}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Completed=[{completedWall.Left:F3}," +
            $"{completedWall.Right:F3}]@{completedWall.Top:F3}, " +
            $"Exit=[{wallExitTarget.Left:F3}," +
            $"{wallExitTarget.Right:F3}]@{wallExitTarget.Top:F3}, " +
            $"Gap={gap:F3}, Rise={rise:F3}, Deadline=" +
            $"{wallRecoveryCommitmentUntil:F3}, ContactRequired=" +
            $"{wallExitFaceContactRequired}, DeadlineFixedStep=" +
            $"{wallMandatoryFaceContactWatchDeadlineFixedStep}, " +
            $"CommittedDeadlineFixedStep=" +
            $"{wallExitContactWatchDeadlineFixedStep}, " +
            $"ObjectiveCount=" +
            $"{wallExitObjectiveCountAtCapture}. " +
            (wallExitFaceContactRequired
                ? "A top landing would skip the active lower objective lane; " +
                  "physical face contact is mandatory."
                : "Safe landing is allowed; physical contact with this face " +
                  "will re-enter wall climbing."),
            "Recovery");
        return true;
    }

    private bool TryPromoteChainedWallTarget(
        BonusStageState state,
        bool allowLevelFace = false,
        float observedFaceX = float.NaN,
        float observedFeetY = float.NaN)
    {
        if (!wallExitTargetActive)
            return false;

        BonusBoardSegment completedWall = BuildAutomaticTargetSegment();
        BonusBoardSegment nextWall = wallExitTarget;
        bool satisfiedMandatoryContact = wallExitFaceContactRequired;
        float gap = nextWall.Left - completedWall.Right;
        float rise = nextWall.Top - completedWall.Top;
        float minimumRise = allowLevelFace ? -0.35f : 0.30f;
        bool isForwardRisingStep =
            gap >= 0.10f && gap <= 5.25f &&
            rise >= minimumRise && rise <= 7.50f &&
            state.PlayerPosition.x < nextWall.Left + 0.20f;
        if (!isForwardRisingStep)
            return false;

        bool observedContactSupplied =
            !float.IsNaN(observedFaceX) &&
            !float.IsNaN(observedFeetY);
        bool observedFiniteFaceContact =
            !observedContactSupplied ||
            Mathf.Abs(observedFaceX - nextWall.Left) <= 0.45f &&
            observedFeetY <= nextWall.Top - 0.10f;
        if (!observedFiniteFaceContact)
        {
            BonusRunnerLog.Warning(
                $"WallChainTargetPromotionRejected Reason=" +
                $"ObservedContactOutsideFiniteFace, Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"ObservedFaceX={observedFaceX:F3}, FeetY=" +
                $"{observedFeetY:F3}, ExpectedFaceX={nextWall.Left:F3}, " +
                $"ExpectedTop={nextWall.Top:F3}. The geometric successor " +
                "cannot replace physical face evidence.");
            return false;
        }

        if (learningSampleActive)
            FinishLearningSample(state, "WallPulseChainTransfer");

        wallExitTargetActive = false;
        wallExitTarget = default;
        ClearWallExitPreparedContract();
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        wallExitFaceContactRequired = false;
        wallExitObjectiveCountAtCapture = 0;
        wallExitObjectiveMinimumY = float.NaN;
        wallExitObjectiveMaximumY = float.NaN;
        ResetMandatoryFacePlanState();
        wallExitContactWatchActive = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        automaticTargetSafeLeft = nextWall.SafeLeft;
        automaticTargetSafeRight = nextWall.SafeRight;
        automaticLandingAllowsRawBodyFit = false;
        automaticLandingSafeTolerance = 0f;
        automaticTargetLeft = nextWall.Left;
        automaticTargetRight = nextWall.Right;
        automaticTargetTop = nextWall.Top;
        sharedWallOwnedFaceX = observedContactSupplied
            ? observedFaceX
            : nextWall.Left;
        automaticTargetColliderId = nextWall.ColliderInstanceId;
        automaticTargetColliderName = nextWall.ColliderName;
        automaticTargetMapPieceName = nextWall.MapPieceName;
        automaticTargetMapPieceOriginX = nextWall.MapPieceOriginX;
        automaticTargetMapPieceInstanceId = nextWall.MapPieceInstanceId;
        automaticTargetRegistryGeneration = nextWall.RegistryGeneration;
        automaticTargetStaticSurfaceIndex = nextWall.StaticSurfaceIndex;
        automaticTargetHeightDelta =
            nextWall.Top - state.PlayerPosition.y;

        // Keep the route-wide pulse count. Promoting a new face changes the
        // contact target, not the bounded climb action.
        wallRecoveryContactLatched = false;
        wallRecoverySawUpwardMotion = false;
        wallRecoveryLipCrossed = false;
        wallRecoveryPhysicalLipY = nextWall.Top - 0.20f;
        wallRecoveryPhysicalLeft = nextWall.Left;
        wallRecoveryPhysicalRight = nextWall.Right;
        wallRecoveryPhysicalSafeLeft = nextWall.SafeLeft;
        wallRecoveryPhysicalSafeRight = nextWall.SafeRight;
        wallRecoveryPhysicalLipFrozen = true;
        wallRecoveryRequiredReleaseY = nextWall.Top - 0.20f;
        wallRecoveryCommitmentUntil = Time.unscaledTime + 1.50f;
        wallReleaseObservedFixedStep = -1;
        wallDetachedLastFixedStep = -1;
        wallDetachedConfirmationSteps = 0;
        wallExitTransferCommitted = false;
        wallLandingFlightCommitted = false;
        wallExitFaceInterceptCommitted = false;
        wallExitCollectionFaceInterceptCommitted = false;
        wallExitContactWatchDeadlineFixedStep = -1;
        wallTopLandingSequenceCommitted = false;
        wallStallStartedAt = -1f;
        nextWallRecoveryTime = 0f;
        passiveWallApproachActive = true;
        wallActionPhase = WallActionPhase.AwaitingWallContact;

        automaticJumpArmed = false;
        airborneAfterAutomaticJump = true;
        automaticJumpRequestedAt = Time.unscaledTime;
        automaticJumpVelocityConfirmed = true;
        automaticPredictionActive = true;
        automaticTrajectoryCompatible = true;
        automaticPlanReason = "ChainedWallApproach";
        automaticManeuver = BonusManeuverKind.ApproachJumpThenWallJump;
        automaticPlanTriggerPosition = state.PlayerPosition;
        automaticPlannedLaunchX = state.PlayerPosition.x;
        automaticLaunchWindowLeft = state.PlayerPosition.x;
        automaticLaunchWindowRight = nextWall.Left;
        automaticPredictedLandingX = nextWall.SafeLeft;
        automaticPredictedHorizontalTravel = Mathf.Max(
            0f,
            nextWall.SafeLeft - state.PlayerPosition.x);
        automaticPredictedFlightSeconds = 1.0f;

        BonusRunnerLog.Debug(
            $"WallChainTargetPromoted Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), Completed=" +
            $"[{completedWall.Left:F3},{completedWall.Right:F3}]" +
            $"@{completedWall.Top:F3}, Next=" +
            $"[{nextWall.Left:F3},{nextWall.Right:F3}]" +
            $"@{nextWall.Top:F3}, Gap={gap:F3}, Rise={rise:F3}, " +
            $"ObservedContact=" +
            $"{(observedContactSupplied ? $"FaceX={observedFaceX:F3}/FeetY={observedFeetY:F3}" : "NotRequiredAtOldLip")}, " +
            $"PromotionMode={(allowLevelFace ? "PhysicalExitFaceContact" : "RisingStaticChain")}. " +
            $"MandatoryFaceContact=" +
            $"{(satisfiedMandatoryContact ? "Satisfied" : "NotRequired")}. " +
            "NextState=AwaitingWallContact; leaving the old face does not " +
            "end the multi-pillar climb.",
            "Recovery");
        return true;
    }

    private void AdoptWallExitTargetAfterLip(BonusStageState state)
    {
        if (!wallExitTargetActive)
            return;

        BonusBoardSegment exit = wallExitTarget;
        automaticTargetSafeLeft = exit.SafeLeft;
        automaticTargetSafeRight = exit.SafeRight;
        automaticLandingAllowsRawBodyFit =
            wallExitTransferAcceptedRawBodyFit;
        automaticLandingSafeTolerance =
            wallExitTransferSafeTolerance;
        automaticTargetLeft = exit.Left;
        automaticTargetRight = exit.Right;
        automaticTargetTop = exit.Top;
        automaticTargetColliderId = exit.ColliderInstanceId;
        automaticTargetColliderName = exit.ColliderName;
        automaticTargetMapPieceName = exit.MapPieceName;
        automaticTargetMapPieceOriginX = exit.MapPieceOriginX;
        automaticTargetMapPieceInstanceId = exit.MapPieceInstanceId;
        automaticTargetRegistryGeneration = exit.RegistryGeneration;
        automaticTargetStaticSurfaceIndex = exit.StaticSurfaceIndex;
        if (wallExitPredictedFlightSeconds > 0.05f)
        {
            automaticPredictedFlightSeconds =
                wallExitPredictedFlightSeconds;
            automaticPredictedHorizontalTravel = wallExitPredictedTravel;
            automaticPredictedLandingX = wallExitPredictedLandingX;
        }
        else
        {
            automaticPredictedLandingX = Mathf.Clamp(
                state.PlayerPosition.x +
                    Mathf.Max(0f, exit.SafeLeft - state.PlayerPosition.x),
                exit.SafeLeft,
                exit.SafeRight);
            automaticPredictedHorizontalTravel = Mathf.Max(
                0f,
                automaticPredictedLandingX - automaticPlanTriggerPosition.x);
        }

        // A solved transfer is still only a prediction. Keep the same mapped
        // face armed until a stable landing is verified: if the character
        // collides below the lip, that physical contact is authoritative and
        // TryWallRecoveryJump reopens bounded wall control for this target.
        wallExitContactWatchActive = true;
        wallRecoveryCommitmentUntil = Mathf.Max(
            wallRecoveryCommitmentUntil,
            Time.unscaledTime + 1.25f);
        if (HasCommittedExitFaceFlight())
        {
            wallExitContactWatchDeadlineFixedStep =
                Math.Max(
                    wallExitContactWatchDeadlineFixedStep,
                    JumpPhysicsFeedback.FixedStepSequence +
                    GetPhysicsStepBudget(1.25f));
        }

        BonusRunnerLog.Debug(
            $"WallExitTransferActivated AttemptId={automaticAttemptId}, " +
            $"TransferMode=" +
            $"{(wallExitCollectionFaceInterceptCommitted ? "CollectionFacePending" : wallExitFaceInterceptCommitted ? "FaceOrTopPending" : "SafeLandingWithFaceFallback")}, " +
            $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"Target=[{exit.Left:F3},{exit.Right:F3}] " +
            $"Safe=[{exit.SafeLeft:F3},{exit.SafeRight:F3}]@{exit.Top:F3}, " +
            $"PredictedOutcomeX={automaticPredictedLandingX:F3}, " +
            $"PredictedTravel={automaticPredictedHorizontalTravel:F3}, " +
            $"PredictedFlight={automaticPredictedFlightSeconds:F3}s, " +
            $"LandingAcceptance[SafeTolerance=" +
            $"{automaticLandingSafeTolerance:F3},RawBodyFit=" +
            $"{automaticLandingAllowsRawBodyFit}], " +
            $"ContactFallbackDeadline={wallRecoveryCommitmentUntil:F3}. " +
            $"ContactFallbackDeadlineFixedStep=" +
            $"{wallExitContactWatchDeadlineFixedStep}. " +
            (wallExitCollectionFaceInterceptCommitted
                ? "The low-objective route requires the mapped left face; " +
                  "a top landing is recoverable terrain but is recorded as " +
                  "a missed collection route."
                : wallExitFaceInterceptCommitted
                ? "The strict landing solver rejected every hold; the mapped " +
                  "left face is the expected outcome and a verified target-top " +
                  "landing remains an equally safe completion."
                : "The downstream support is the expected landing objective, " +
                  "but its physical face remains armed as an authoritative " +
                  "wall-contact fallback until stable landing."),
            "Recovery");
    }

    private void ValidateAutomaticJumpResponse(BonusStageState state)
    {
        bool passiveNoInputIntent =
            passiveWallApproachActive &&
            automaticManeuver == BonusManeuverKind.EnterTrenchThenWallJump &&
            automaticAttemptId == 0 &&
            automaticPlannedHold <= 0f &&
            !jumpController.IsHoldingJump;
        if (automaticJumpArmed ||
            automaticJumpVelocityConfirmed ||
            passiveNoInputIntent ||
            !state.HasPlayer)
            return;

        bool nonWallAutomaticFlight =
            automaticManeuver != BonusManeuverKind.None &&
            automaticManeuver !=
                BonusManeuverKind.EnterTrenchThenWallJump &&
            automaticManeuver !=
                BonusManeuverKind.ApproachJumpThenWallJump &&
            automaticManeuver != BonusManeuverKind.WallJumpClimb;
        float expectedJumpVelocity =
            automaticPlanPhysicsSnapshot.JumpVelocity;
        float observedJumpVelocity =
            learningTookOff
                ? learningTakeoffVelocity.y
                : state.PlayerVelocity.y;
        float impulseRatio = expectedJumpVelocity > 0.01f
            ? observedJumpVelocity / expectedJumpVelocity
            : 1f;
        bool severePartialImpulse =
            nonWallAutomaticFlight &&
            learningSampleActive &&
            learningSource == "Automatic" &&
            learningTookOff &&
            !state.IsGrounded &&
            expectedJumpVelocity >= 10f &&
            observedJumpVelocity > 5f &&
            observedJumpVelocity <= expectedJumpVelocity * 0.60f &&
            expectedJumpVelocity - observedJumpVelocity >= 4f &&
            learningTakeoffTime - learningInputDownTime <= 0.30f;
        if (severePartialImpulse)
        {
            automaticJumpVelocityConfirmed = true;
            automaticTrajectoryCompatible = false;
            partialAutomaticJumpImpulseOwnedFlight = true;
            partialAutomaticJumpImpulseAttemptId = automaticAttemptId;
            partialAutomaticJumpObservedVelocityY =
                observedJumpVelocity;
            partialAutomaticJumpExpectedVelocityY =
                expectedJumpVelocity;
            BonusRunnerLog.Warning(
                $"AutomaticJumpPartialImpulse AttemptId=" +
                $"{automaticAttemptId}, Plan={automaticPlanReason}/" +
                $"{automaticManeuver}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), CurrentVelocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), TakeoffVY=" +
                $"{observedJumpVelocity:F3}, ExpectedVY=" +
                $"{expectedJumpVelocity:F3}, Ratio={impulseRatio:F3}, " +
                $"InputToTakeoff=" +
                $"{learningTakeoffTime - learningInputDownTime:F3}s. " +
                "The game accepted only a severe partial impulse. The " +
                "nominal landing trajectory is invalidated, but the active " +
                "flight retains recovery ownership so an exact mapped face " +
                "can be promoted before pit confirmation.");
            return;
        }

        if (state.PlayerVelocity.y > 5f)
        {
            automaticJumpVelocityConfirmed = true;
            BonusRunnerLog.Debug(
                $"Automatic jump accepted by game: VY={state.PlayerVelocity.y:F3}, X={state.PlayerPosition.x:F3}.",
                "Control");
            return;
        }

        if (Time.unscaledTime - automaticJumpRequestedAt >= 0.20f && state.IsGrounded)
        {
            BonusRunnerLog.Warning(
                $"Automatic jump was not accepted within 0.20s. " +
                $"JumpPanel: Down={JumpPanel.jumpDown}, Pressed={JumpPanel.jumpPressed}, Up={JumpPanel.jumpUp}, " +
                $"HeldPointers={JumpPanel.instance?.heldPointerCount ?? -1}, VY={state.PlayerVelocity.y:F3}.");
            jumpController.Release();
            FinishLearningSample(state, "InputNotAcceptedByGame");
            ResetAutomaticControlState();
            nextAutomaticAttemptTime = Time.unscaledTime + 0.35f;
        }
    }

    private void ResetLandingConfirmation()
    {
        landingCandidateLastFixedStep = -1;
        landingCandidateStableFixedSteps = 0;
        landingCandidateTop = 0f;
        landingCandidateColliderId = 0;
        landingCandidateFirstObservedAt = -1f;
    }

    private void ClearWatchedExitSupportFixedStepLatch()
    {
        wallExitSupportFixedStepLatched = false;
        wallExitSupportLatchedAttemptId = 0;
        wallExitSupportLatchedFixedStep = -1;
        wallExitSupportLatchedPlayerInstanceId = 0;
        wallExitSupportLatchedTarget = default;
        wallExitSupportLatchedSurface = default;
        wallExitSupportLatchedPosition = default;
        wallExitSupportLatchedVelocity = default;
        wallExitSupportLatchedPlayerHalfWidth = 0f;
        wallExitSupportLatchedAt = 0f;
        wallExitSupportLatchedFaceOrTop = false;
        wallExitSupportLatchedCollectionFace = false;
        wallExitSupportLatchedCompletionNarrow = false;
        wallExitSupportLatchedAutomaticTarget = false;
    }

    private static bool IsGroundLearningManeuver(
        BonusManeuverKind maneuver) =>
        maneuver == BonusManeuverKind.GroundJumpToLanding ||
        maneuver == BonusManeuverKind.ApproachJumpThenWallJump ||
        maneuver == BonusManeuverKind.SphereCollectionJump ||
        maneuver == BonusManeuverKind.SphereSweepToLowerLanding ||
        maneuver == BonusManeuverKind.HazardClearanceJump;

    private bool IsManualGroundLearningEligible(BonusStageState state)
    {
        PlayerMovement player = PlayerMovement.instance;
        if (player == null || !state.IsGrounded)
            return false;

        BonusWallContact wall = wallDetector.Detect(
            player,
            Mathf.Max(1f, lastReliableHorizontalSpeed));
        bool physicallyAttached =
            wall.IsDetected &&
            wall.IsTouching &&
            Mathf.Abs(state.PlayerVelocity.x) <=
                WallAttachmentVelocityTolerance;
        return !physicallyAttached;
    }

    private static bool TryReadNativeSpiritBoostComponent(
        PlayerMovement player,
        out float component)
    {
        return BonusStageInspector.TryReadSpiritBoostComponentWorldUnits(
            player,
            out component);
    }

    private void StartLearningSample(
        BonusStageState state,
        string source,
        bool learnGroundKinematics)
    {
        if (learningSampleActive)
            FinishLearningSample(state, "InterruptedByNewInput");

        learningSampleActive = true;
        learningMayLearnGroundKinematics = learnGroundKinematics;
        learningSampleId = ++nextLearningSampleId;
        learningSource = source;
        learningMap = state.MapName;
        learningSection = state.SectionIndex;
        learningInputDownTime = Time.unscaledTime;
        learningInputUpTime = 0f;
        learningTakeoffTime = 0f;
        learningTriggerPosition = state.PlayerPosition;
        learningTriggerVelocity = state.PlayerVelocity;
        learningTakeoffPosition = default;
        learningTakeoffVelocity = state.PlayerVelocity;
        learningApexPosition = state.PlayerPosition;
        learningMaximumY = state.PlayerPosition.y;
        learningSpiritBoostModeEnabled = state.SpiritBoostEnabled;
        learningSpiritBoostReadAvailable =
            TryReadNativeSpiritBoostComponent(
                PlayerMovement.instance,
                out learningStartingSpiritBoostComponent);
        learningMaximumSpiritBoostComponent =
            learningStartingSpiritBoostComponent;
        learningStartingHorizontalSpeed =
            Mathf.Abs(state.PlayerVelocity.x);
        learningMaximumHorizontalSpeed =
            learningStartingHorizontalSpeed;
        learningPreviousVelocityY = state.PlayerVelocity.y;
        learningLastObservedPosition = state.PlayerPosition;
        learningLastObservedAt = Time.unscaledTime;
        learningLastObservedFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
        learningTookOff = false;
        learningFirstApexCaptured = false;
        learningInputReleased = false;
        nextLandingDeferredLogTime = 0f;
        ResetLandingConfirmation();
        if (source == "Manual" && learnGroundKinematics)
            ResetManualWallSequence();

        PlayerMovement player = PlayerMovement.instance;
        if (player != null)
            jumpPhysicsFeedback.BeginInput(
                source,
                player,
                learnGroundKinematics);

        BonusRunnerLog.Debug(
            $"JumpSampleStart AttemptId={learningSampleId}, Source={source}, Map={learningMap}, Section={learningSection}, " +
            $"Frame={Time.frameCount}, X={state.PlayerPosition.x:F3}, Y={state.PlayerPosition.y:F3}, " +
            $"VX={state.PlayerVelocity.x:F3}, VY={state.PlayerVelocity.y:F3}, " +
            $"LearnGroundKinematics={learnGroundKinematics}, " +
            $"SpiritBoostMode={learningSpiritBoostModeEnabled}, " +
            $"NativeBoostRead={learningSpiritBoostReadAvailable}, " +
            $"StartBoostComponent=" +
            $"{learningStartingSpiritBoostComponent:F3}",
            "Learning");
    }

    private bool TryContinueStage2UnmappedWallTraverse(
        BonusStageState state,
        BonusBoardScanResult landingScan,
        bool landingSupportConfirmed)
    {
        if (!stage2UnmappedWallTraverseActive ||
            !state.UsesStage2LiveRouting ||
            !state.IsActiveGameplay)
        {
            return false;
        }
        if (learningSampleActive &&
            learningSource != "Automatic")
        {
            ResetStage2UnmappedWallTraverse();
            return false;
        }

        BonusBoardSegment target =
            stage2UnmappedWallTraverseTarget;
        if (target.Width <= 0.05f)
        {
            ResetStage2UnmappedWallTraverse();
            return false;
        }

        // The approach input and the first wall input must remain separate
        // native edges.  Retain Stage-2 ownership while the approach DOWN is
        // still held; the scheduled UP is delivered by JumpController, then
        // this chain observes stationary contact before pressing again.
        if (jumpController.IsHoldingJump)
        {
            stage2UnmappedWallStallLastFixedStep = -1;
            stage2UnmappedWallStallFixedSteps = 0;
            return true;
        }

        bool landedOnDownstreamTarget =
            landingSupportConfirmed &&
            Mathf.Abs(landingScan.Current.Top - target.Top) <= 0.35f &&
            state.PlayerPosition.x >= target.Left - 0.20f &&
            state.PlayerPosition.x <= target.Right + 0.20f;
        if (landedOnDownstreamTarget)
        {
            BonusRunnerLog.Debug(
                $"Stage2UnmappedWallTraverseResolved Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Target=[{target.Left:F3},{target.Right:F3}]@" +
                $"{target.Top:F3}, Pulses=" +
                $"{stage2UnmappedWallTraversePulses}. The real downstream " +
                "support closes the temporary physical-contact route.",
                "Recovery");
            ResetStage2UnmappedWallTraverse();
            return false;
        }

        // A different verified top is a legitimate re-planning point. The
        // special chain is needed only while the composite staircase cannot
        // be represented as a support. A support under the body on the near
        // side of the captured face is still the wall foot, not a completed
        // landing, and must not relinquish the dedicated chain.
        bool landedBeyondCapturedFace =
            landingSupportConfirmed &&
            state.PlayerPosition.x >= target.Left + 0.15f;
        if (landedBeyondCapturedFace)
        {
            ResetStage2UnmappedWallTraverse();
            return false;
        }

        bool stationaryGroundContact =
            state.IsGrounded &&
            Mathf.Abs(state.PlayerVelocity.x) <= 0.50f &&
            Mathf.Abs(state.PlayerVelocity.y) <= 2.50f &&
            state.PlayerPosition.x <
                target.Right + 0.20f;
        if (!stationaryGroundContact)
        {
            stage2UnmappedWallStallLastFixedStep = -1;
            stage2UnmappedWallStallFixedSteps = 0;
            return false;
        }

        PlayerMovement player = PlayerMovement.instance;
        if (player == null)
            return false;

        float routeSpeed = Mathf.Max(
            1f,
            Mathf.Max(
                lastReliableHorizontalSpeed,
                sectionCruiseHorizontalSpeed));
        BonusWallContact wall =
            wallDetector.Detect(player, routeSpeed);
        bool exactFirstWallContact =
            wall.IsDetected && wall.IsTouching;
        bool establishedSteppedClimb =
            stage2UnmappedWallTraversePulses > 0 &&
            state.PlayerPosition.x >=
                stage2UnmappedWallLastPulsePosition.x + 0.35f;
        if (!exactFirstWallContact &&
            !establishedSteppedClimb)
        {
            if (Time.unscaledTime >= nextStage2UnmappedWallLogTime)
            {
                nextStage2UnmappedWallLogTime =
                    Time.unscaledTime + 0.25f;
                BonusRunnerLog.Debug(
                    $"Stage2UnmappedWallContactPending Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Target=" +
                    $"[{target.Left:F3},{target.Right:F3}]@" +
                    $"{target.Top:F3}, Pulses=" +
                    $"{stage2UnmappedWallTraversePulses}, Wall=" +
                    $"{wall.IsDetected}/{wall.IsTouching}/" +
                    $"{wall.Reason}. The first corrective DOWN still " +
                    "requires exact wall contact.",
                    "Recovery");
            }
            return false;
        }

        long fixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (fixedStep != stage2UnmappedWallStallLastFixedStep)
        {
            stage2UnmappedWallStallLastFixedStep = fixedStep;
            stage2UnmappedWallStallFixedSteps++;
        }
        if (stage2UnmappedWallStallFixedSteps < 2)
            return false;

        const int maximumUnmappedWallPulses = 6;
        if (stage2UnmappedWallTraversePulses >=
            maximumUnmappedWallPulses)
        {
            if (Time.unscaledTime >= nextStage2UnmappedWallLogTime)
            {
                nextStage2UnmappedWallLogTime =
                    Time.unscaledTime + 0.50f;
                BonusRunnerLog.Warning(
                    $"Stage2UnmappedWallTraversePulseLimit Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Target=" +
                    $"[{target.Left:F3},{target.Right:F3}]@" +
                    $"{target.Top:F3}, Pulses=" +
                    $"{stage2UnmappedWallTraversePulses}/" +
                    $"{maximumUnmappedWallPulses}. No additional DOWN " +
                    "is issued without new forward progress.");
            }
            return false;
        }

        int pulseIndex = stage2UnmappedWallTraversePulses + 1;
        float requestedHold =
            stage2UnmappedWallTraversePulses == 0
                ? 0.08f
                : 0.12f;
        JumpPhysicsSnapshot planningPhysics = BuildPlanningPhysics(
            state.SectionIndex,
            jumpPhysicsFeedback.CaptureSnapshot(player),
            routeSpeed,
            false);
        float fixedDelta = Mathf.Clamp(
            planningPhysics.FixedDeltaTime,
            0.005f,
            0.05f);
        int fixedStepHoldLimit = Mathf.Clamp(
            Mathf.RoundToInt(requestedHold / fixedDelta),
            1,
            64);
        float hold = Mathf.Min(
            planningPhysics.EffectiveHoldCapSeconds,
            fixedStepHoldLimit * fixedDelta);
        fixedStepHoldLimit = Mathf.Max(
            1,
            Mathf.RoundToInt(hold / fixedDelta));

        float playerHalfWidth = player.playerCollider != null
            ? Mathf.Max(
                0.15f,
                player.playerCollider.bounds.extents.x)
            : 0.60f;
        float feetY = player.playerCollider != null
            ? player.playerCollider.bounds.min.y
            : state.PlayerPosition.y - 0.27f;
        BonusBoardSegment contactSupport = new(
            state.PlayerPosition.x - playerHalfWidth,
            state.PlayerPosition.x + playerHalfWidth,
            feetY,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            wall.ColliderInstanceId,
            wall.ColliderName,
            "Stage2UnmappedWallContact");
        BonusBoardScanResult contactScan = new(
            true,
            contactSupport,
            true,
            target,
            0f,
            Mathf.Max(
                0f,
                target.Left - contactSupport.Right),
            target.Top - feetY,
            "Stage2UnmappedWallPhysicalContact");
        float predictedFlight =
            jumpPlanner.PredictRawInputToLandingSeconds(
                hold,
                target.Top - feetY,
                planningPhysics);
        if (predictedFlight <= 0f)
            predictedFlight = 0.70f;
        float predictedLandingX = Mathf.Clamp(
            (target.SafeLeft + target.SafeRight) * 0.50f,
            target.Left,
            target.Right);
        BonusJumpPlan pulsePlan = new(
            true,
            true,
            hold,
            predictedFlight,
            Mathf.Max(
                0f,
                predictedLandingX - state.PlayerPosition.x),
            state.PlayerPosition.x,
            predictedLandingX,
            state.PlayerPosition.x,
            state.PlayerPosition.x,
            $"Stage2UnmappedWallClimbPulse{pulseIndex}",
            $"PhysicalContact={exactFirstWallContact}," +
            $"EstablishedStep={establishedSteppedClimb}," +
            $"WallFace={(wall.IsDetected ? wall.FaceX.ToString("F3") : "Unavailable")}," +
            $"Target=[{target.Left:F3},{target.Right:F3}]@" +
            $"{target.Top:F3}",
            BonusManeuverKind.GroundJumpToLanding);

        long priorAttemptId = automaticAttemptId;
        string priorPlan = automaticPlanReason;
        if (learningSampleActive &&
            learningSource == "Automatic")
        {
            FinishLearningSample(
                state,
                exactFirstWallContact
                    ? "Stage2UnmappedWallContactHandoff"
                    : "Stage2UnmappedWallStepHandoff");
        }
        jumpController.Release();
        jumpController.Press(
            player,
            hold,
            $"Stage2 unmapped wall pulse {pulseIndex}: " +
            $"target={target.Left:F2}..{target.Right:F2}",
            fixedStepHoldLimit);
        MarkAutomaticJumpRequested(
            state,
            pulsePlan,
            target,
            contactScan,
            default,
            planningPhysics,
            routeSpeed);
        if (!automaticPredictionActive)
            return false;

        stage2UnmappedWallTraversePulses = pulseIndex;
        stage2UnmappedWallLastPulsePosition =
            state.PlayerPosition;
        stage2UnmappedWallStallLastFixedStep = -1;
        stage2UnmappedWallStallFixedSteps = 0;
        nextStage2UnmappedWallLogTime = 0f;
        BonusRunnerLog.Warning(
            $"Stage2UnmappedWallClimbPulseIssued PriorAttempt=" +
            $"{priorAttemptId}, PriorPlan={priorPlan}, NewAttempt=" +
            $"{automaticAttemptId}, Pulse={pulseIndex}/" +
            $"{maximumUnmappedWallPulses}, Position=" +
            $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3}," +
            $"{state.PlayerVelocity.y:F3}), Hold={hold:F3}s/" +
            $"{fixedStepHoldLimit}Steps, ExactWall=" +
            $"{exactFirstWallContact}, EstablishedStep=" +
            $"{establishedSteppedClimb}, WallFace=" +
            $"{(wall.IsDetected ? wall.FaceX.ToString("F3") : "Unavailable")}, " +
            $"Target=[{target.Left:F3},{target.Right:F3}]@" +
            $"{target.Top:F3}. FailureDomain=Perception; the old " +
            "unverifiable landing is closed before the separated climb " +
            "press receives input ownership.");
        return true;
    }

    private void UpdateLearningSample(BonusStageState state)
    {
        if (!learningSampleActive || !state.HasPlayer)
            return;

        learningMaximumHorizontalSpeed = Mathf.Max(
            learningMaximumHorizontalSpeed,
            Mathf.Abs(state.PlayerVelocity.x));
        bool boostRead = TryReadNativeSpiritBoostComponent(
            PlayerMovement.instance,
            out float currentBoostComponent);
        learningSpiritBoostReadAvailable &= boostRead;
        if (boostRead)
        {
            learningMaximumSpiritBoostComponent = Mathf.Max(
                learningMaximumSpiritBoostComponent,
                currentBoostComponent);
        }

        float observationDelta = Mathf.Max(
            0.001f,
            Time.unscaledTime - learningLastObservedAt);
        Vector3 frameDelta =
            state.PlayerPosition - learningLastObservedPosition;
        long currentObservationFixedStep =
            JumpPhysicsFeedback.FixedStepSequence;
        long elapsedObservationFixedSteps =
            learningLastObservedFixedStep >= 0
                ? Math.Max(
                    0L,
                    currentObservationFixedStep -
                    learningLastObservedFixedStep)
                : 0L;
        float observationFixedDelta = Mathf.Clamp(
            latestPhysicsSnapshot.FixedDeltaTime > 0f
                ? latestPhysicsSnapshot.FixedDeltaTime
                : Time.fixedDeltaTime,
            0.005f,
            0.05f);
        float physicsObservationDelta =
            elapsedObservationFixedSteps > 0
                ? elapsedObservationFixedSteps * observationFixedDelta
                : Mathf.Min(observationDelta, observationFixedDelta);
        float maximumExpectedHorizontalDelta =
            Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.x),
                Mathf.Abs(lastReliableHorizontalSpeed)) *
            physicsObservationDelta + 1.25f;
        float verticalVelocityEnvelope = Mathf.Max(
            Mathf.Max(
                Mathf.Abs(state.PlayerVelocity.y),
                Mathf.Abs(learningPreviousVelocityY)),
            Mathf.Max(
                5f,
                Mathf.Abs(latestPhysicsSnapshot.JumpVelocity)));
        float gravityEnvelope = Mathf.Clamp(
            Mathf.Abs(latestPhysicsSnapshot.GravityMagnitude),
            1f,
            120f);
        float maximumExpectedVerticalDelta =
            verticalVelocityEnvelope * physicsObservationDelta +
            0.5f * gravityEnvelope *
                physicsObservationDelta * physicsObservationDelta +
            1.25f;
        bool positionDiscontinuity =
            Mathf.Abs(frameDelta.y) > maximumExpectedVerticalDelta ||
            Mathf.Abs(frameDelta.x) > maximumExpectedHorizontalDelta + 1.25f;
        learningLastObservedPosition = state.PlayerPosition;
        learningLastObservedAt = Time.unscaledTime;
        learningLastObservedFixedStep = currentObservationFixedStep;
        if (positionDiscontinuity)
        {
            bool automaticLifecycleTransition =
                learningSource == "Automatic";
            BonusRunnerLog.Warning(
                $"ActionPositionDiscontinuity AttemptId={learningSampleId}, " +
                $"Source={learningSource}, Delta=({frameDelta.x:F3},{frameDelta.y:F3}), " +
                $"WallDt={observationDelta:F3}s, PhysicsDt=" +
                $"{physicsObservationDelta:F3}s, FixedSteps=" +
                $"{elapsedObservationFixedSteps}, Limits=" +
                $"[X={maximumExpectedHorizontalDelta + 1.25f:F3}," +
                $"Y={maximumExpectedVerticalDelta:F3}], Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}). " +
                "FailureDomain=Lifecycle, FailureReason=TeleportOrRespawn; " +
                "this frame is excluded from jump and wall-flight learning.");
            jumpController.Release();
            FinishLearningSample(state, "PositionDiscontinuity");
            ResetAutomaticControlState();
            if (automaticLifecycleTransition)
            {
                ResetCompletionTraversalSpeedEvidence(
                    "ActionPositionDiscontinuity");
                if (!pitDescentGuardActive)
                {
                    pitRespawnImmediateTakeoverEligible = false;
                    pitRespawnTakeoverEvidence =
                        "ActionPositionDiscontinuity";
                }
                pitDescentGuardActive = true;
                pitRespawnLastFixedStep = -1;
                pitRespawnStableFixedSteps = 0;
                nextAutomaticAttemptTime = Time.unscaledTime + 0.45f;
            }
            else
            {
                nextAutomaticAttemptTime = Time.unscaledTime + 0.15f;
            }
            return;
        }

        float timeSinceInput = Time.unscaledTime - learningInputDownTime;
        MonitorWallRecoveryImpulse(state, timeSinceInput);
        bool compressedFixedStepWallTakeoff =
            learningSource == "Automatic" &&
            automaticManeuver == BonusManeuverKind.WallJumpClimb &&
            jumpController.LastReleaseHeldFixedSteps >= 2 &&
            Mathf.Abs(
                jumpController.LastPressStartedAt -
                learningInputDownTime) <= 0.10f &&
            !state.IsGrounded &&
            state.PlayerPosition.y >= learningTriggerPosition.y + 0.30f;
        bool validUpwardTakeoff =
            compressedFixedStepWallTakeoff ||
            !state.IsGrounded &&
                state.PlayerVelocity.y > 5f &&
                timeSinceInput <= 0.30f &&
                state.PlayerPosition.y >=
                    learningTriggerPosition.y - 0.75f;
        if (!learningTookOff && validUpwardTakeoff)
        {
            learningTookOff = true;
            learningTakeoffTime = Time.unscaledTime;
            learningTakeoffPosition = state.PlayerPosition;
            learningTakeoffVelocity = state.PlayerVelocity;
            learningPreviousVelocityY = state.PlayerVelocity.y;
            if (state.PlayerPosition.y > learningMaximumY)
            {
                learningMaximumY = state.PlayerPosition.y;
                learningApexPosition = state.PlayerPosition;
            }
            if (!compressedFixedStepWallTakeoff)
            {
                jumpPhysicsFeedback.ObserveTakeoff(
                    learningInputDownTime,
                    learningTakeoffVelocity);
            }
            BonusRunnerLog.Debug(
                $"JumpAttemptTakeoff AttemptId={learningSampleId}, Source={learningSource}, " +
                $"ObservationMode=" +
                $"{(compressedFixedStepWallTakeoff ? "CompressedFixedStepCatchUp" : "LiveUpwardFrame")}, " +
                $"InputToTakeoff={learningTakeoffTime - learningInputDownTime:F3}s, " +
                $"Position=({learningTakeoffPosition.x:F3},{learningTakeoffPosition.y:F3}), " +
                $"Velocity=({learningTakeoffVelocity.x:F3},{learningTakeoffVelocity.y:F3}), " +
                $"TriggerDelta=({learningTakeoffPosition.x - learningTriggerPosition.x:F3}," +
                $"{learningTakeoffPosition.y - learningTriggerPosition.y:F3})",
                "Attempt");
        }

        if (learningTookOff && !learningFirstApexCaptured)
        {
            if (state.PlayerPosition.y > learningMaximumY)
            {
                learningMaximumY = state.PlayerPosition.y;
                learningApexPosition = state.PlayerPosition;
            }

            if (learningPreviousVelocityY > 0f &&
                state.PlayerVelocity.y <= 0f &&
                !state.IsGrounded)
            {
                learningFirstApexCaptured = true;
                BonusRunnerLog.Debug(
                    $"JumpAttemptApex AttemptId={learningSampleId}, Source={learningSource}, " +
                    $"X={learningApexPosition.x:F3}, Y={learningApexPosition.y:F3}, " +
                    $"RiseFromTrigger={learningApexPosition.y - learningTriggerPosition.y:F3}, " +
                    $"RiseFromTakeoff={learningApexPosition.y - learningTakeoffPosition.y:F3}, " +
                    $"HorizontalFromTakeoff={learningApexPosition.x - learningTakeoffPosition.x:F3}, " +
                    $"CrossingVY={state.PlayerVelocity.y:F3}. Later pit bounces will be ignored.",
                    "Attempt");
            }

            learningPreviousVelocityY = state.PlayerVelocity.y;
        }

        bool committedWallClimbActive = IsCommittedWallClimbActive();
        float wallPlayerHalfWidth = Mathf.Max(
            0.15f,
            automaticTargetSafeLeft - automaticTargetLeft - 0.15f);
        float wallPhysicalHoldElapsed = Mathf.Max(
            0f,
            Time.unscaledTime - jumpController.LastPressStartedAt);
        bool requiredWallPhasesComplete = wallRecoveryAttempts >= 1;
        PlayerMovement wallLearningPlayer = PlayerMovement.instance;
        bool hasWallColliderFeet =
            wallLearningPlayer != null &&
            wallLearningPlayer.playerCollider != null;
        float wallColliderFeetY = hasWallColliderFeet
            ? wallLearningPlayer.playerCollider.bounds.min.y
            : float.NaN;
        bool solvedWallFlight = HasSolvedWallFlightCommitment();
        bool wallLipCleared =
            committedWallClimbActive &&
            !wallRecoveryLipCrossed &&
            requiredWallPhasesComplete &&
            wallRecoveryPhysicalLipFrozen &&
            hasWallColliderFeet &&
            state.PlayerPosition.x >=
                wallRecoveryPhysicalLeft + wallPlayerHalfWidth - 0.08f &&
            state.PlayerPosition.x <=
                wallRecoveryPhysicalRight + wallPlayerHalfWidth + 0.15f &&
            wallColliderFeetY >= wallRecoveryPhysicalLipY &&
            Mathf.Abs(state.PlayerVelocity.x) > 1.25f;
        if (wallLipCleared)
        {
            wallRecoveryLipCrossed = true;
            wallActionPhase = WallActionPhase.ExitFlight;
            BonusRunnerLog.Debug(
                $"WallLipCleared AttemptId={automaticAttemptId}, " +
                $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                $"ColliderFeetY={wallColliderFeetY:F3}, " +
                $"PhysicalHold={wallPhysicalHoldElapsed:F3}s, " +
                $"PhysicalLipY={wallRecoveryPhysicalLipY:F3}, " +
                $"ObjectiveReleaseY={wallRecoveryRequiredReleaseY:F3}, " +
                $"RequiredCenterX={wallRecoveryPhysicalLeft + wallPlayerHalfWidth - 0.08f:F3}, " +
                $"PhysicalWallRaw=[{wallRecoveryPhysicalLeft:F3}," +
                $"{wallRecoveryPhysicalRight:F3}]@" +
                $"{wallRecoveryPhysicalLipY + 0.20f:F3}, " +
                $"CurrentTargetRaw=[{automaticTargetLeft:F3}," +
                $"{automaticTargetRight:F3}]@{automaticTargetTop:F3}, " +
                $"SolvedWallFlight=" +
                $"{solvedWallFlight}, ExitTransferCommitted=" +
                $"{wallExitTransferCommitted}, WallTopLandingCommitted=" +
                $"{wallLandingFlightCommitted}, MandatoryFaceCommitted=" +
                $"{wallMandatoryFaceInterceptCommitted}, ExitFaceCommitted=" +
                $"{HasCommittedExitFaceFlight()}. The body, not merely its edge, " +
                "has crossed the lip. A solved wall-top, face, or transfer " +
                "flight keeps its planned hold through the controller " +
                "deadline; an unsolved " +
                "climb releases at this observed boundary.",
                "Recovery");
            if (jumpController.IsHoldingJump && !solvedWallFlight)
            {
                jumpController.Release();
                learningInputUpTime = Time.unscaledTime;
                learningInputReleased = true;
            }
            else if (jumpController.IsHoldingJump)
            {
                BonusRunnerLog.Debug(
                    $"WallExitHoldPreserved AttemptId={automaticAttemptId}, " +
                    $"PhysicalHold={wallPhysicalHoldElapsed:F3}s, " +
                    $"PlannedHold={automaticPlannedHold:F3}s, " +
                    $"RemainingHold=" +
                    $"{Mathf.Max(0f, automaticPlannedHold - wallPhysicalHoldElapsed):F3}s. " +
                    (HasCommittedExitFaceFlight() ||
                     wallMandatoryFaceInterceptCommitted
                        ? "The committed downstream face/top outcome, not lip " +
                          "clearance alone, owns the remaining press duration."
                        : "The solved downstream landing, not lip clearance " +
                          "alone, owns the remaining press duration."),
                    "Recovery");
            }
            wallRecoveryContactLatched = false;
            wallRecoverySawUpwardMotion = false;
            if (wallExitTransferCommitted)
            {
                AdoptWallExitTargetAfterLip(state);
            }
            else if (solvedWallFlight)
            {
                // A solved face/top action retains its frozen contract until
                // the planned deadline and downstream contact/landing. Do not
                // let generic lip chaining replace it in this same frame.
                TryArmWallExitContactWatch(
                    state,
                    "SolvedWallFlightLipCleared");
            }
            else if (TryPromoteChainedWallTarget(state))
            {
                // The old lip is only an intermediate edge. The newly
                // promoted higher face owns subsequent contact and pulse
                // decisions, so the old learning sample must not continue
                // through this frame under stale target geometry.
                return;
            }
            else
            {
                TryArmWallExitContactWatch(
                    state,
                    "WallLipCleared");
            }
            committedWallClimbActive = false;
        }
        bool fixedStepWatchedExitSupport = false;
        long fixedStepSupportStep = -1;
        BonusBoardSegment fixedStepSupportSurface = default;
        Vector3 fixedStepSupportPosition = default;
        Vector2 fixedStepSupportVelocity = default;
        float fixedStepSupportPlayerHalfWidth = 0f;
        float fixedStepSupportAt = 0f;
        bool fixedStepSupportFaceOrTop = false;
        bool fixedStepSupportCollectionFace = false;
        bool fixedStepSupportCompletionNarrow = false;
        bool fixedStepSupportAutomaticTarget = false;
        BonusBoardSegment fixedStepSupportTarget = default;
        if (wallExitSupportFixedStepLatched)
        {
            bool samePlayer =
                state.PlayerInstanceId != 0 &&
                state.PlayerInstanceId ==
                    wallExitSupportLatchedPlayerInstanceId;
            bool sameAttempt =
                automaticAttemptId == wallExitSupportLatchedAttemptId;
            bool capturedAutomaticTarget =
                wallExitSupportLatchedAutomaticTarget;
            BonusBoardSegment currentExpectedTarget = capturedAutomaticTarget
                ? BuildAutomaticTargetSegment()
                : wallExitTarget;
            bool currentExpectedTargetActive = capturedAutomaticTarget
                ? automaticTargetRight > automaticTargetLeft + 0.05f
                : wallExitTargetActive;
            bool sameTarget =
                currentExpectedTargetActive &&
                Mathf.Abs(
                    currentExpectedTarget.Left -
                    wallExitSupportLatchedTarget.Left) <= 0.12f &&
                Mathf.Abs(
                    currentExpectedTarget.Right -
                    wallExitSupportLatchedTarget.Right) <= 0.12f &&
                Mathf.Abs(
                    currentExpectedTarget.Top -
                    wallExitSupportLatchedTarget.Top) <= 0.35f &&
                StaticOrColliderIdentityMatches(
                    currentExpectedTarget,
                    wallExitSupportLatchedTarget);
            bool currentCompletionNarrowMode =
                !wallExitFaceInterceptCommitted &&
                !wallExitCollectionFaceInterceptCommitted &&
                IsSuccessfulCompletionTraversal(GetRoutingState(state)) &&
                wallExitTargetActive &&
                wallExitTarget.Width <= 2.25f;
            bool currentAutomaticTargetMode =
                !wallExitFaceInterceptCommitted &&
                !wallExitCollectionFaceInterceptCommitted &&
                !currentCompletionNarrowMode &&
                !wallExitTargetActive &&
                !wallExitContactWatchActive &&
                !HasCommittedExitFaceFlight() &&
                automaticManeuver == BonusManeuverKind.WallJumpClimb &&
                automaticTargetRight > automaticTargetLeft + 0.05f;
            bool sameMode =
                wallExitFaceInterceptCommitted ==
                    wallExitSupportLatchedFaceOrTop &&
                wallExitCollectionFaceInterceptCommitted ==
                    wallExitSupportLatchedCollectionFace &&
                currentCompletionNarrowMode ==
                    wallExitSupportLatchedCompletionNarrow &&
                currentAutomaticTargetMode ==
                    wallExitSupportLatchedAutomaticTarget;
            bool sameOwnership =
                automaticPredictionActive &&
                learningSampleActive &&
                string.Equals(
                    learningSource,
                    "Automatic",
                    StringComparison.Ordinal) &&
                !wallExitFaceContactRequired &&
                (capturedAutomaticTarget
                    ? currentAutomaticTargetMode
                    : wallExitContactWatchActive ||
                      HasCommittedExitFaceFlight());
            fixedStepWatchedExitSupport =
                samePlayer &&
                sameAttempt &&
                sameTarget &&
                sameMode &&
                sameOwnership;
            if (fixedStepWatchedExitSupport)
            {
                fixedStepSupportStep =
                    wallExitSupportLatchedFixedStep;
                fixedStepSupportSurface =
                    wallExitSupportLatchedSurface;
                fixedStepSupportPosition =
                    wallExitSupportLatchedPosition;
                fixedStepSupportVelocity =
                    wallExitSupportLatchedVelocity;
                fixedStepSupportPlayerHalfWidth =
                    wallExitSupportLatchedPlayerHalfWidth;
                fixedStepSupportAt = wallExitSupportLatchedAt;
                fixedStepSupportFaceOrTop =
                    wallExitSupportLatchedFaceOrTop;
                fixedStepSupportCollectionFace =
                    wallExitSupportLatchedCollectionFace;
                fixedStepSupportCompletionNarrow =
                    wallExitSupportLatchedCompletionNarrow;
                fixedStepSupportAutomaticTarget =
                    wallExitSupportLatchedAutomaticTarget;
                fixedStepSupportTarget =
                    wallExitSupportLatchedTarget;
                BonusRunnerLog.Debug(
                    $"WatchedExitSupportFixedStepConsumed AttemptId=" +
                    $"{automaticAttemptId}, CapturedFixedStep=" +
                    $"{fixedStepSupportStep}, CurrentFixedStep=" +
                    $"{JumpPhysicsFeedback.FixedStepSequence}, " +
                    $"PhysicsStepAge=" +
                    $"{Math.Max(0L, JumpPhysicsFeedback.FixedStepSequence - fixedStepSupportStep)}, " +
                    $"CapturedPosition=" +
                    $"({fixedStepSupportPosition.x:F3}," +
                    $"{fixedStepSupportPosition.y:F3}), CurrentPosition=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Mode=" +
                    $"{(fixedStepSupportCollectionFace ? "CollectionFaceTopMiss" : fixedStepSupportFaceOrTop ? "FaceOrTop" : fixedStepSupportCompletionNarrow ? "CompletionNarrow" : "AutomaticWallTarget")}. " +
                    "The captured physics support, rather than the later " +
                    "render position, resolves this watched landing.",
                    "Recovery");
            }
            else
            {
                BonusRunnerLog.Debug(
                    $"WatchedExitSupportFixedStepDiscarded " +
                    $"CapturedAttempt=" +
                    $"{wallExitSupportLatchedAttemptId}, CurrentAttempt=" +
                    $"{automaticAttemptId}, SamePlayer={samePlayer}, " +
                    $"SameTarget={sameTarget}, SameMode={sameMode}, " +
                    $"SameOwnership={sameOwnership}. Route/life identity " +
                    "changed before render consumption.",
                    "Recovery");
            }
            ClearWatchedExitSupportFixedStepLatch();
        }

        Vector3 landingEvidencePosition = fixedStepWatchedExitSupport
            ? fixedStepSupportPosition
            : state.PlayerPosition;
        Vector2 landingEvidenceVelocity = fixedStepWatchedExitSupport
            ? fixedStepSupportVelocity
            : state.PlayerVelocity;
        bool landingEvidenceGrounded =
            fixedStepWatchedExitSupport || state.IsGrounded;
        bool landingEvidenceHoldingJump =
            !fixedStepWatchedExitSupport && jumpController.IsHoldingJump;
        long landingEvidenceFixedStep = fixedStepWatchedExitSupport
            ? fixedStepSupportStep
            : JumpPhysicsFeedback.FixedStepSequence;
        float fixedStepLiveBodyOverlap = fixedStepWatchedExitSupport
            ? Mathf.Max(
                0f,
                Mathf.Min(
                    state.PlayerPosition.x +
                        fixedStepSupportPlayerHalfWidth,
                    fixedStepSupportSurface.Right) -
                Mathf.Max(
                    state.PlayerPosition.x -
                        fixedStepSupportPlayerHalfWidth,
                    fixedStepSupportSurface.Left))
            : 0f;
        bool liveStillOnFixedStepSupport =
            fixedStepWatchedExitSupport &&
            state.IsGrounded &&
            Mathf.Abs(
                state.PlayerPosition.y -
                fixedStepSupportSurface.Top) <= 0.60f &&
            fixedStepLiveBodyOverlap >= 0.15f;

        BonusBoardScanResult landingScan = default;
        bool landingSupportConfirmed = false;
        if (fixedStepWatchedExitSupport)
        {
            landingScan = new BonusBoardScanResult(
                true,
                fixedStepSupportSurface,
                false,
                default,
                fixedStepSupportSurface.Right -
                    fixedStepSupportPosition.x,
                0f,
                0f,
                "FixedStepWatchedExitSupportLatch");
            landingSupportConfirmed = true;
        }
        else if (state.IsGrounded)
        {
            try
            {
                PlayerMovement landingPlayer = PlayerMovement.instance;
                if (landingPlayer != null)
                {
                    landingScan = platformScanner.Scan(
                        state.PlayerPosition,
                        landingPlayer,
                        Mathf.Max(
                            1f,
                            Mathf.Abs(state.PlayerVelocity.x)));
                    landingSupportConfirmed =
                        landingScan.IsValid &&
                        state.PlayerPosition.x >=
                            landingScan.Current.Left - 0.20f &&
                        state.PlayerPosition.x <=
                            landingScan.Current.Right + 0.20f &&
                        Mathf.Abs(
                            state.PlayerPosition.y -
                            landingScan.Current.Top) <= 0.60f;
                }
            }
            catch
            {
                landingSupportConfirmed = false;
            }
        }
        bool risingGroundPulse =
            landingEvidenceVelocity.y > 2.50f ||
            landingEvidenceHoldingJump;
        bool stableLandingFrame =
            landingEvidenceGrounded &&
            landingSupportConfirmed &&
            !risingGroundPulse &&
            Mathf.Abs(landingEvidenceVelocity.y) <= 2.50f;
        BonusBoardSegment landingOutcomeTarget =
            fixedStepWatchedExitSupport
                ? fixedStepSupportTarget
                : wallExitTarget;
        bool landingOutcomeTargetActive =
            fixedStepWatchedExitSupport || wallExitTargetActive;
        bool landingMatchesWatchedExit =
            landingOutcomeTargetActive &&
            landingSupportConfirmed &&
            Mathf.Abs(
                landingScan.Current.Top -
                landingOutcomeTarget.Top) <= 0.35f &&
            landingEvidencePosition.x >=
                landingOutcomeTarget.Left - 0.20f &&
            landingEvidencePosition.x <=
                landingOutcomeTarget.Right + 0.20f;
        bool completionNarrowWatchedExit =
            fixedStepWatchedExitSupport
                ? fixedStepSupportCompletionNarrow
                : IsSuccessfulCompletionTraversal(state) &&
                  wallExitTargetActive &&
                  wallExitTarget.Width <= 2.25f;
        bool watchedExitOwnershipActive =
            fixedStepWatchedExitSupport ||
            wallExitContactWatchActive ||
            HasCommittedExitFaceFlight();
        bool faceOrTopOutcomeActive = fixedStepWatchedExitSupport
            ? fixedStepSupportFaceOrTop
            : wallExitFaceInterceptCommitted;
        bool collectionFaceOutcomeActive = fixedStepWatchedExitSupport
            ? fixedStepSupportCollectionFace
            : wallExitCollectionFaceInterceptCommitted;
        bool automaticTargetOutcomeActive =
            fixedStepWatchedExitSupport &&
            fixedStepSupportAutomaticTarget;
        bool authoritativeWatchedExitSupportFrame =
            watchedExitOwnershipActive &&
            !wallExitFaceContactRequired &&
            landingEvidenceGrounded &&
            landingSupportConfirmed &&
            !landingEvidenceHoldingJump &&
            landingEvidenceVelocity.y <= 2.50f &&
            landingMatchesWatchedExit &&
            (faceOrTopOutcomeActive ||
             collectionFaceOutcomeActive ||
             completionNarrowWatchedExit ||
             automaticTargetOutcomeActive);
        bool authoritativeOptionalWatchedExitLanding =
            authoritativeWatchedExitSupportFrame &&
            !collectionFaceOutcomeActive &&
            (faceOrTopOutcomeActive ||
             completionNarrowWatchedExit ||
             automaticTargetOutcomeActive);
        bool authoritativeCollectionWatchedExitLanding =
            authoritativeWatchedExitSupportFrame &&
            collectionFaceOutcomeActive;
        // The transition-road [256,258] support reports one authoritative
        // Grounded/contact frame while VY is still about -4.7. Requiring a
        // near-zero vertical sample rejects the only physical landing and the
        // runner leaves the two-unit top before the next fixed step. This
        // relaxed contact is legal only for an already prepared narrow chain;
        // ordinary and wall landings retain the two-step stable proof.
        bool preparedPassiveDrop =
            string.Equals(
                secondStageProjectedPlan.Reason,
                "IntentionalDrop",
                StringComparison.Ordinal) &&
            secondStageProjectedPlan.Maneuver ==
                BonusManeuverKind.CoastToLowerLanding;
        float remainingSecondStageResidence =
            (secondStageExpectedSupport.Right + 0.15f -
             landingEvidencePosition.x) /
            Mathf.Max(1f, Mathf.Abs(landingEvidenceVelocity.x));
        float criticalResidenceThreshold = Mathf.Max(
            0.070f,
            Mathf.Clamp(
                automaticPlanPhysicsSnapshot.FixedDeltaTime,
                0.005f,
                0.05f) * 3.75f);
        bool criticalResidenceHandoffAuthorized =
            state.SectionIndex == 2 ||
            (state.SpiritBoostEnabled && state.SectionIndex == 3);
        bool criticalResidenceHandoff =
            criticalResidenceHandoffAuthorized &&
            secondStageExpectedSupport.Width <= 4.25f &&
            remainingSecondStageResidence <= criticalResidenceThreshold &&
            (secondStageProjectedPlan.IsValid || preparedPassiveDrop);
        bool urgentNarrowChainLanding =
            landingEvidenceGrounded &&
            landingSupportConfirmed &&
            !risingGroundPulse &&
            landingEvidenceVelocity.y <= 2.50f &&
            learningSource == "Automatic" &&
            secondStagePreviewActive &&
            secondStageObservedAirborne &&
            secondStageProjectedScan.IsValid &&
            secondStageProjectedScan.HasNext &&
            ((secondStageProjectedPlan.IsValid &&
              secondStageExpectedSupport.Width <= 2.25f) ||
             criticalResidenceHandoff) &&
            Mathf.Abs(
                landingScan.Current.Top -
                secondStageExpectedSupport.Top) <= 0.35f &&
            landingEvidencePosition.x >=
                secondStageExpectedSupport.Left - 0.15f &&
            landingEvidencePosition.x <=
                secondStageExpectedSupport.Right + 0.15f;
        bool landingConfirmationFrame =
            stableLandingFrame ||
            urgentNarrowChainLanding ||
            authoritativeOptionalWatchedExitLanding ||
            authoritativeCollectionWatchedExitLanding;
        if (landingConfirmationFrame)
        {
            int supportColliderId =
                landingScan.Current.ColliderInstanceId;
            bool sameCandidate = landingCandidateStableFixedSteps > 0 &&
                Mathf.Abs(
                    landingScan.Current.Top - landingCandidateTop) <= 0.35f &&
                (landingCandidateColliderId == 0 ||
                 supportColliderId == 0 ||
                 landingCandidateColliderId == supportColliderId);
            if (!sameCandidate)
            {
                landingCandidateLastFixedStep = -1;
                landingCandidateStableFixedSteps = 0;
                landingCandidateTop = landingScan.Current.Top;
                landingCandidateColliderId = supportColliderId;
                landingCandidateFirstObservedAt = Time.unscaledTime;
            }

            long fixedStep = landingEvidenceFixedStep;
            if (fixedStep != landingCandidateLastFixedStep)
            {
                landingCandidateLastFixedStep = fixedStep;
                landingCandidateStableFixedSteps++;
            }
        }
        else
        {
            ResetLandingConfirmation();
        }
        int requiredStableLandingSteps =
            urgentNarrowChainLanding ||
            authoritativeOptionalWatchedExitLanding ||
            authoritativeCollectionWatchedExitLanding
                ? 1
                : 2;
        bool verifiedLanding =
            landingConfirmationFrame &&
            landingCandidateStableFixedSteps >=
                requiredStableLandingSteps;
        if (urgentNarrowChainLanding && verifiedLanding)
        {
            BonusRunnerLog.Debug(
                $"UrgentNarrowLandingConfirmed AttemptId=" +
                $"{automaticAttemptId}, Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), Support=" +
                $"[{landingScan.Current.Left:F3}," +
                $"{landingScan.Current.Right:F3}]@" +
                $"{landingScan.Current.Top:F3}, PreparedNext=" +
                $"{secondStageProjectedPlan.Reason}/" +
                $"{secondStageProjectedPlan.Maneuver}, PreparedWindow=" +
                $"[{secondStageProjectedPlan.LaunchWindowLeft:F3}," +
                $"{secondStageProjectedPlan.LaunchWindowRight:F3}], " +
                $"RemainingResidence={remainingSecondStageResidence:F3}s, " +
                $"CriticalThreshold={criticalResidenceThreshold:F3}s, " +
                $"PassiveDrop={preparedPassiveDrop}. " +
                "One authoritative fixed-step contact is sufficient because " +
                "the platform residence time is shorter than the normal " +
                "two-step wall-pulse rejection window.",
                "Lookahead");
        }
        if (authoritativeOptionalWatchedExitLanding && verifiedLanding)
        {
            float remainingResidenceSeconds =
                (landingOutcomeTarget.Right + 0.20f -
                 landingEvidencePosition.x) /
                Mathf.Max(1f, Mathf.Abs(landingEvidenceVelocity.x));
            BonusRunnerLog.Debug(
                $"AuthoritativeWatchedExitLandingConfirmed AttemptId=" +
                $"{automaticAttemptId}, FixedStep=" +
                $"{landingEvidenceFixedStep}, Mode=" +
                $"{(faceOrTopOutcomeActive ? "FaceOrTopPending" : automaticTargetOutcomeActive ? "AutomaticWallTarget" : "CompletionNarrowWatch")}, " +
                $"EvidenceSource=" +
                $"{(fixedStepWatchedExitSupport ? "FixedStepLatch" : "RenderObservation")}, " +
                $"Position=({landingEvidencePosition.x:F3}," +
                $"{landingEvidencePosition.y:F3}), Velocity=" +
                $"({landingEvidenceVelocity.x:F3}," +
                $"{landingEvidenceVelocity.y:F3}), Support=" +
                $"[{landingScan.Current.Left:F3}," +
                $"{landingScan.Current.Right:F3}]@" +
                $"{landingScan.Current.Top:F3}, WatchedExit=" +
                $"[{landingOutcomeTarget.Left:F3}," +
                $"{landingOutcomeTarget.Right:F3}]@" +
                $"{landingOutcomeTarget.Top:F3}, StableFixedSteps=" +
                $"{landingCandidateStableFixedSteps}/1, Holding=" +
                $"{landingEvidenceHoldingJump}, " +
                $"RemainingResidence={remainingResidenceSeconds:F3}s. " +
                "One exact, non-rising static-support step resolves this " +
                "optional watch before high horizontal speed leaves the top.",
                "Recovery");
        }
        if (authoritativeCollectionWatchedExitLanding && verifiedLanding)
        {
            BonusRunnerLog.Debug(
                $"AuthoritativeCollectionTopSupportObserved AttemptId=" +
                $"{automaticAttemptId}, FixedStep=" +
                $"{landingEvidenceFixedStep}, EvidenceSource=" +
                $"{(fixedStepWatchedExitSupport ? "FixedStepLatch" : "RenderObservation")}, " +
                $"Position=({landingEvidencePosition.x:F3}," +
                $"{landingEvidencePosition.y:F3}), Velocity=" +
                $"({landingEvidenceVelocity.x:F3}," +
                $"{landingEvidenceVelocity.y:F3}), Support=" +
                $"[{landingScan.Current.Left:F3}," +
                $"{landingScan.Current.Right:F3}]@" +
                $"{landingScan.Current.Top:F3}. One real non-rising " +
                "support step is authoritative terrain recovery, but the " +
                "later collection branch records it as a missed face lane.",
                "Recovery");
        }
        bool landingSupportMatchesTarget =
            landingSupportConfirmed &&
            Mathf.Abs(landingScan.Current.Top - automaticTargetTop) <= 0.35f &&
            landingEvidencePosition.x >=
                automaticTargetLeft + wallPlayerHalfWidth - 0.08f &&
            landingEvidencePosition.x <= automaticTargetRight + 0.25f;
        if ((wallExitContactWatchActive ||
             HasCommittedExitFaceFlight()) &&
            verifiedLanding &&
            landingMatchesWatchedExit)
        {
            if (wallExitFaceContactRequired)
            {
                BonusRunnerLog.Warning(
                    $"MandatoryWallFaceContactMissed Position=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), LandedSupport=" +
                    $"[{landingScan.Current.Left:F3}," +
                    $"{landingScan.Current.Right:F3}]@" +
                    $"{landingScan.Current.Top:F3}, RequiredFace=" +
                    $"[{wallExitTarget.Left:F3}," +
                    $"{wallExitTarget.Right:F3}]@" +
                    $"{wallExitTarget.Top:F3}, ObjectiveCountAtCapture=" +
                    $"{wallExitObjectiveCountAtCapture}. " +
                    "ExpectedResult=physical face contact followed by " +
                    "attached objective descent; ActualResult=top landing. " +
                    "FailureDomain=RouteExecution; this landing is not " +
                    "reported as successful while the lower lane was skipped.");
                automaticTrajectoryCompatible = false;
                FinishLearningSample(
                    state,
                    "MandatoryWallFaceContactMissed");
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime + 0.10f;
                return;
            }

            if (collectionFaceOutcomeActive)
            {
                long missedCollectionAttemptId = automaticAttemptId;
                string missedCollectionPlan = automaticPlanReason;
                BonusBoardSegment missedCollectionTarget = wallExitTarget;
                BonusRunnerLog.Warning(
                    $"CollectionFaceTopLandingMissed AttemptId=" +
                    $"{missedCollectionAttemptId}, Plan=" +
                    $"{missedCollectionPlan}, Position=" +
                    $"({landingEvidencePosition.x:F3}," +
                    $"{landingEvidencePosition.y:F3}), Velocity=" +
                    $"({landingEvidenceVelocity.x:F3}," +
                    $"{landingEvidenceVelocity.y:F3}), Support=" +
                    $"[{landingScan.Current.Left:F3}," +
                    $"{landingScan.Current.Right:F3}]@" +
                    $"{landingScan.Current.Top:F3}, RequiredFace=" +
                    $"X={missedCollectionTarget.Left:F3}, FaceDepth=" +
                    $"{CommittedExitFaceDepth:F3}. " +
                    "ExpectedResult=physical face contact for the lower " +
                    "objective lane; ActualResult=top landing. " +
                    "FailureDomain=RouteExecution; the real support is " +
                    "preserved as outcome evidence; live support is checked " +
                    "separately before grounded planning is re-armed.");
                automaticTrajectoryCompatible = false;
                if (learningSource == "Automatic")
                    jumpController.Release();
                if (fixedStepWatchedExitSupport)
                {
                    authoritativeLandingEvidenceActive = true;
                    authoritativeLandingEvidenceHistorical = true;
                    authoritativeLandingEvidenceSurface =
                        fixedStepSupportSurface;
                    authoritativeLandingEvidencePlayerHalfWidth =
                        fixedStepSupportPlayerHalfWidth;
                    authoritativeLandingEvidenceAt = fixedStepSupportAt;
                    authoritativeLandingEvidenceFixedStep =
                        fixedStepSupportStep;
                }
                FinishLearningSample(
                    state with
                    {
                        PlayerPosition = landingEvidencePosition,
                        PlayerVelocity = landingEvidenceVelocity,
                        IsGrounded = true
                    },
                    "CollectionFaceTopLandingMissed");
                ResetWallRecoveryState();
                bool collectionCanResumeGroundPlanner =
                    !fixedStepWatchedExitSupport ||
                    liveStillOnFixedStepSupport;
                automaticJumpArmed = collectionCanResumeGroundPlanner;
                airborneAfterAutomaticJump =
                    !collectionCanResumeGroundPlanner;
                nextAutomaticAttemptTime = 0f;
                ClearRoutePlanLock();
                BonusRunnerLog.Debug(
                    $"CollectionFaceTopLandingHandoff AttemptId=" +
                    $"{missedCollectionAttemptId}, EvidenceSource=" +
                    $"{(fixedStepWatchedExitSupport ? "FixedStepLatch" : "RenderObservation")}, " +
                    $"LiveStillSupported={liveStillOnFixedStepSupport}, " +
                    $"LiveBodyOverlap={fixedStepLiveBodyOverlap:F3}, " +
                    $"NextState=" +
                    $"{(collectionCanResumeGroundPlanner ? "GroundPlanner" : "HistoricalSupportOnly")}. " +
                    "A historical top contact resolves the route outcome but " +
                    "does not pretend the player is still grounded.",
                    "Recovery");
                return;
            }

            BonusBoardSegment resolvedExitTarget = wallExitTarget;
            bool resolvedCommittedFaceIntercept =
                faceOrTopOutcomeActive;
            string landingConfirmationMode =
                authoritativeOptionalWatchedExitLanding
                    ? resolvedCommittedFaceIntercept
                        ? "OneStepAuthoritativeFaceOrTop"
                        : "OneStepAuthoritativeCompletionNarrow"
                    : "TwoStepStable";

            // The formal lip threshold is not guaranteed to run before a
            // narrow/high-speed landing. Atomically make the actual watched
            // support the learning target before closing the sample; otherwise
            // a correct exit landing is scored against the old wall top.
            automaticTargetSafeLeft = resolvedExitTarget.SafeLeft;
            automaticTargetSafeRight = resolvedExitTarget.SafeRight;
            automaticLandingAllowsRawBodyFit =
                authoritativeOptionalWatchedExitLanding ||
                wallExitTransferAcceptedRawBodyFit;
            automaticLandingSafeTolerance =
                authoritativeOptionalWatchedExitLanding
                    ? Mathf.Max(
                        wallExitTransferSafeTolerance,
                        0.20f)
                    : wallExitTransferSafeTolerance;
            automaticTargetLeft = resolvedExitTarget.Left;
            automaticTargetRight = resolvedExitTarget.Right;
            automaticTargetTop = resolvedExitTarget.Top;
            automaticTargetColliderId =
                resolvedExitTarget.ColliderInstanceId;
            automaticTargetColliderName =
                resolvedExitTarget.ColliderName;
            automaticTargetMapPieceName =
                resolvedExitTarget.MapPieceName;
            automaticTargetMapPieceOriginX =
                resolvedExitTarget.MapPieceOriginX;
            automaticTargetMapPieceInstanceId =
                resolvedExitTarget.MapPieceInstanceId;
            automaticTargetRegistryGeneration =
                resolvedExitTarget.RegistryGeneration;
            automaticTargetStaticSurfaceIndex =
                resolvedExitTarget.StaticSurfaceIndex;
            automaticTargetHeightDelta =
                resolvedExitTarget.Top - automaticPlanTriggerPosition.y;
            automaticPredictedLandingX = landingEvidencePosition.x;
            automaticPredictedHorizontalTravel = Mathf.Max(
                0f,
                landingEvidencePosition.x - automaticPlanTriggerPosition.x);

            wallExitContactWatchActive = false;
            wallExitContactWatchDeadlineFixedStep = -1;
            wallExitTargetActive = false;
            wallExitTarget = default;
            ClearWallExitPreparedContract();
            wallExitFaceInterceptCommitted = false;
            wallExitCollectionFaceInterceptCommitted = false;
            wallExitFaceContactRequired = false;
            wallExitObjectiveCountAtCapture = 0;
            wallExitObjectiveMinimumY = float.NaN;
            wallExitObjectiveMaximumY = float.NaN;
            ResetMandatoryFacePlanState();
            BonusRunnerLog.Debug(
                $"WallExitContactWatchResolved Result=VerifiedTargetLanding, " +
                $"ConfirmationMode={landingConfirmationMode}, " +
                $"CommittedFaceIntercept=" +
                $"{resolvedCommittedFaceIntercept}, " +
                $"ObservedSupportAuthority=" +
                $"{authoritativeOptionalWatchedExitLanding}, " +
                $"Position=({landingEvidencePosition.x:F3}," +
                $"{landingEvidencePosition.y:F3}), Support=" +
                $"[{landingScan.Current.Left:F3}," +
                $"{landingScan.Current.Right:F3}]@" +
                $"{landingScan.Current.Top:F3}. No face fallback is needed; " +
                "normal landing reset may release wall-route ownership.",
                "Recovery");
            if (authoritativeOptionalWatchedExitLanding)
            {
                long resolvedAttemptId = automaticAttemptId;
                string resolvedPlan = automaticPlanReason;
                if (learningSource == "Automatic")
                    jumpController.Release();
                if (fixedStepWatchedExitSupport)
                {
                    authoritativeLandingEvidenceActive = true;
                    authoritativeLandingEvidenceHistorical = true;
                    authoritativeLandingEvidenceSurface =
                        fixedStepSupportSurface;
                    authoritativeLandingEvidencePlayerHalfWidth =
                        fixedStepSupportPlayerHalfWidth;
                    authoritativeLandingEvidenceAt = fixedStepSupportAt;
                    authoritativeLandingEvidenceFixedStep =
                        fixedStepSupportStep;
                }
                FinishLearningSample(
                    state with
                    {
                        PlayerPosition = landingEvidencePosition,
                        PlayerVelocity = landingEvidenceVelocity,
                        IsGrounded = true
                    },
                    "Landed");
                ResetWallRecoveryState();
                bool canResumeGroundPlanner =
                    !fixedStepWatchedExitSupport ||
                    liveStillOnFixedStepSupport;
                automaticJumpArmed = canResumeGroundPlanner;
                airborneAfterAutomaticJump = !canResumeGroundPlanner;
                nextAutomaticAttemptTime = 0f;
                ClearRoutePlanLock();
                BonusRunnerLog.Debug(
                    $"WallExitLandingOwnershipHandedOff AttemptId=" +
                    $"{resolvedAttemptId}, Plan={resolvedPlan}, Mode=" +
                    $"{landingConfirmationMode}, LiveStillSupported=" +
                    $"{liveStillOnFixedStepSupport}, LiveBodyOverlap=" +
                    $"{fixedStepLiveBodyOverlap:F3}, NextState=" +
                    $"{(canResumeGroundPlanner ? "GroundPlanner" : "HistoricalSupportOnly")}, " +
                    $"LandingPosition=({landingEvidencePosition.x:F3}," +
                    $"{landingEvidencePosition.y:F3}), CurrentPosition=" +
                    $"({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}). The stale wall route is " +
                    "closed, while only a still-live support can authorize an " +
                    "immediate grounded decision.",
                    "Recovery");
                return;
            }
        }
        if ((wallExitContactWatchActive ||
             HasCommittedExitFaceFlight()) &&
            verifiedLanding &&
            landingSupportConfirmed &&
            !landingMatchesWatchedExit)
        {
            BonusBoardSegment abandonedExitTarget = wallExitTarget;
            bool abandonedMandatoryFace = wallExitFaceContactRequired;
            bool abandonedCollectionFace =
                wallExitCollectionFaceInterceptCommitted;
            long completedAttemptId = automaticAttemptId;
            string completedPlan = automaticPlanReason;
            FinishLearningSample(state, "Landed");
            ResetWallRecoveryState();
            automaticJumpArmed = true;
            airborneAfterAutomaticJump = false;
            nextAutomaticAttemptTime = 0f;
            ClearRoutePlanLock();
            BonusRunnerLog.Warning(
                $"WallExitIntermediateLandingReplan AttemptId=" +
                $"{completedAttemptId}, Plan={completedPlan}, Position=" +
                $"({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}), ActualSupport=" +
                $"[{landingScan.Current.Left:F3}," +
                $"{landingScan.Current.Right:F3}]@" +
                $"{landingScan.Current.Top:F3}, AbandonedExit=" +
                $"[{abandonedExitTarget.Left:F3}," +
                $"{abandonedExitTarget.Right:F3}]@" +
                $"{abandonedExitTarget.Top:F3}, MandatoryFace=" +
                $"{abandonedMandatoryFace}, CollectionFace=" +
                $"{abandonedCollectionFace}. A stable real support overrides " +
                "the missed wall-exit prediction; all old contact watches " +
                "are cleared and grounded planning resumes immediately.");
            return;
        }
        bool confirmedTargetTopLanding =
            (committedWallClimbActive || automaticTargetOutcomeActive) &&
            requiredWallPhasesComplete &&
            verifiedLanding &&
            landingSupportMatchesTarget;
        if ((learningTookOff || automaticTargetOutcomeActive) &&
            confirmedTargetTopLanding)
        {
            BonusRunnerLog.Debug(
                $"WallClimbStableLanding AttemptId={automaticAttemptId}, " +
                $"Position=({landingEvidencePosition.x:F3}," +
                $"{landingEvidencePosition.y:F3}), Velocity=" +
                $"({landingEvidenceVelocity.x:F3}," +
                $"{landingEvidenceVelocity.y:F3}), EvidenceSource=" +
                $"{(automaticTargetOutcomeActive ? "FixedStepLatch" : "RenderObservation")}, " +
                $"ImpulseConfirmed={wallRecoveryImpulseConfirmed}, " +
                $"LipCrossed={wallRecoveryLipCrossed}, " +
                $"TargetRaw=[{automaticTargetLeft:F3},{automaticTargetRight:F3}]" +
                $"@{automaticTargetTop:F3}. Releasing wall hold at the action boundary.",
                "Recovery");
            wallRecoveryCommitmentUntil = 0f;
            jumpController.Release();
            if (automaticTargetOutcomeActive)
            {
                authoritativeLandingEvidenceActive = true;
                authoritativeLandingEvidenceHistorical = true;
                authoritativeLandingEvidenceSurface =
                    fixedStepSupportSurface;
                authoritativeLandingEvidencePlayerHalfWidth =
                    fixedStepSupportPlayerHalfWidth;
                authoritativeLandingEvidenceAt = fixedStepSupportAt;
                authoritativeLandingEvidenceFixedStep =
                    fixedStepSupportStep;
            }
            FinishLearningSample(
                state with
                {
                    PlayerPosition = landingEvidencePosition,
                    PlayerVelocity = landingEvidenceVelocity,
                    IsGrounded = true
                },
                "Landed");
            if (automaticTargetOutcomeActive)
            {
                ResetWallRecoveryState();
                automaticJumpArmed = liveStillOnFixedStepSupport;
                airborneAfterAutomaticJump =
                    !liveStillOnFixedStepSupport;
                ClearRoutePlanLock();
                BonusRunnerLog.Debug(
                    $"WallTargetLandingFixedStepHandoff AttemptId=" +
                    $"{automaticAttemptId}, CapturedFixedStep=" +
                    $"{fixedStepSupportStep}, LiveStillSupported=" +
                    $"{liveStillOnFixedStepSupport}, LiveBodyOverlap=" +
                    $"{fixedStepLiveBodyOverlap:F3}, NextState=" +
                    $"{(liveStillOnFixedStepSupport ? "GroundPlanner" : "HistoricalSupportOnly")}.",
                    "Recovery");
            }
            nextAutomaticAttemptTime = liveStillOnFixedStepSupport ||
                !automaticTargetOutcomeActive
                    ? Time.unscaledTime + 0.10f
                    : 0f;
            return;
        }
        bool possiblePlannedWallContact =
            committedWallClimbActive &&
            learningSource == "Automatic" &&
            automaticPredictionActive &&
            state.IsGrounded &&
            (risingGroundPulse || !landingSupportConfirmed);
        if (learningTookOff && verifiedLanding && !possiblePlannedWallContact)
        {
            if (automaticPlanReason.StartsWith(
                    "WallRecovery",
                    System.StringComparison.Ordinal))
            {
                BonusRunnerLog.Debug(
                    $"WallClimbLandingObserved AttemptId={automaticAttemptId}, " +
                    $"Position=({state.PlayerPosition.x:F3}," +
                    $"{state.PlayerPosition.y:F3}), Velocity=" +
                    $"({state.PlayerVelocity.x:F3}," +
                    $"{state.PlayerVelocity.y:F3}), " +
                    $"ImpulseConfirmed={wallRecoveryImpulseConfirmed}, " +
                    $"LipCrossed={wallRecoveryLipCrossed}, " +
                    $"Target=[{automaticTargetLeft:F3}," +
                    $"{automaticTargetRight:F3}]@{automaticTargetTop:F3}.",
                    "Recovery");
            }
            if (learningSource == "Automatic")
                jumpController.Release();
            FinishLearningSample(state, "Landed");
            return;
        }

        if (learningTookOff &&
            state.IsGrounded &&
            !verifiedLanding &&
            Time.unscaledTime >= nextLandingDeferredLogTime)
        {
            nextLandingDeferredLogTime = Time.unscaledTime + 0.25f;
            BonusRunnerLog.Debug(
                $"LandingDeferredForWallCheck AttemptId={automaticAttemptId}, " +
                $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                $"RisingGroundPulse={risingGroundPulse}, " +
                $"SupportConfirmed={landingSupportConfirmed}, " +
                $"StableFixedSteps={landingCandidateStableFixedSteps}/" +
                $"{requiredStableLandingSteps}, " +
                $"FixedStep={JumpPhysicsFeedback.FixedStepSequence}, " +
                $"WatchActive={wallExitContactWatchActive}, " +
                $"WatchMatch={landingMatchesWatchedExit}, " +
                $"CommittedFaceIntercept=" +
                $"{wallExitFaceInterceptCommitted}, CollectionFaceIntercept=" +
                $"{wallExitCollectionFaceInterceptCommitted}, MandatoryFace=" +
                $"{wallExitFaceContactRequired}, " +
                $"SupportReason={(landingScan.Reason ?? "Unavailable")}, " +
                $"TargetRaw=[{automaticTargetLeft:F3},{automaticTargetRight:F3}]" +
                $"@{automaticTargetTop:F3}.",
                "Recovery");
        }

        if (!learningTookOff &&
            Time.unscaledTime - learningInputDownTime >= 0.40f)
        {
            bool automaticSample = learningSource == "Automatic";
            if (automaticSample)
                jumpController.Release();
            FinishLearningSample(state, "InputNotAccepted");
            if (automaticSample)
            {
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime + 0.35f;
            }
            return;
        }

        // A committed face flight has its own physics-step deadline and is
        // resolved later in the same frame by contact/landing/overflight
        // authority. A long background render pause must not let this generic
        // wall-clock watchdog erase that contract first.
        if (learningTookOff &&
            !HasCommittedExitFaceFlight() &&
            !IsAdoptedExitFaceAwaitingClimb() &&
            Time.unscaledTime - learningTakeoffTime >= 4f)
        {
            bool automaticSample = learningSource == "Automatic";
            FinishLearningSample(state, "NoLandingWithin4s");
            if (automaticSample)
            {
                jumpController.Release();
                bool likelyPitLifecycle =
                    state.PlayerPosition.y < -3.50f;
                // A sample timeout must never clear a stronger lifecycle
                // guard installed by player/map loss or a confirmed pit.
                // Only the stable-respawn branch may release that ownership.
                if (!pitDescentGuardActive && likelyPitLifecycle)
                {
                    pitRespawnImmediateTakeoverEligible = false;
                    pitRespawnTakeoverEvidence =
                        "UnconfirmedPitSampleTimeout";
                    pitDescentGuardActive = true;
                }
                if (pitDescentGuardActive)
                {
                    pitRespawnLastFixedStep = -1;
                    pitRespawnStableFixedSteps = 0;
                }
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime +
                    (pitDescentGuardActive ? 0.45f : 0.25f);
            }
        }
    }

    private void MonitorWallRecoveryImpulse(
        BonusStageState state,
        float timeSinceInput)
    {
        if (learningSource != "Automatic" ||
            !automaticPlanReason.StartsWith(
                "WallRecovery",
                System.StringComparison.Ordinal))
        {
            return;
        }

        float stepRise =
            state.PlayerPosition.y - wallRecoveryLastObservedY;
        if (stepRise >= 0.02f && state.PlayerVelocity.y > 2f)
            wallRecoveryUpwardPhysicsSteps++;
        if (Mathf.Abs(stepRise) >= 0.005f)
            wallRecoveryLastObservedY = state.PlayerPosition.y;

        float totalRise =
            state.PlayerPosition.y - wallRecoveryImpulseStartY;
        float deliveredFixedStepHold =
            jumpController.LastReleaseHeldFixedSteps > 0
                ? jumpController.LastReleaseHeldFixedSteps *
                  Mathf.Clamp(
                      automaticPlanPhysicsSnapshot.FixedDeltaTime,
                      0.005f,
                      0.05f)
                : 0f;
        bool currentPulseHasFixedStepEvidence =
            jumpController.LastReleaseHeldFixedSteps > 0 &&
            Mathf.Abs(
                jumpController.LastPressStartedAt -
                learningInputDownTime) <= 0.10f;
        float deliveredHoldEvidence =
            currentPulseHasFixedStepEvidence
                ? deliveredFixedStepHold
                : timeSinceInput;
        bool compressedFixedStepImpulse =
            currentPulseHasFixedStepEvidence &&
            jumpController.LastReleaseHeldFixedSteps >= 2 &&
            !state.IsGrounded &&
            totalRise >= 0.30f;
        bool stage2Section0NearLipRebound =
            state.UsesStage2LiveRouting &&
            state.SectionIndex == 0 &&
            currentPulseHasFixedStepEvidence &&
            wallRecoveryUpwardPhysicsSteps >= 1 &&
            totalRise >= 0.04f &&
            wallRecoveryImpulseStartVelocityY < -1f &&
            state.PlayerVelocity.y > 2f &&
            state.PlayerVelocity.y -
                wallRecoveryImpulseStartVelocityY >= 5f;
        if (!wallRecoveryPrematureReleaseLogged &&
            automaticManeuver == BonusManeuverKind.WallJumpClimb &&
            !wallRecoveryLipCrossed &&
            !jumpController.IsHoldingJump &&
            deliveredHoldEvidence + 0.015f < automaticPlannedHold)
        {
            wallRecoveryPrematureReleaseLogged = true;
            BonusRunnerLog.Warning(
                $"WallClimbHoldTruncated AttemptId={automaticAttemptId}, " +
                $"Elapsed={timeSinceInput:F3}s, FixedStepEquivalent=" +
                $"{deliveredFixedStepHold:F3}s/" +
                $"{jumpController.LastReleaseHeldFixedSteps}Steps, " +
                $"DeliveredEvidence={deliveredHoldEvidence:F3}s, " +
                $"EvidenceClock=" +
                $"{(currentPulseHasFixedStepEvidence ? "PhysicsSteps" : "UnscaledFallback")}, PlannedHold=" +
                $"{automaticPlannedHold:F3}s, LastPress=" +
                $"{jumpController.LastPressStartedAt:F3}, LastRelease=" +
                $"{jumpController.LastReleaseAt:F3}, Maneuver=" +
                $"{automaticManeuver}, Position=" +
                $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"Velocity=({state.PlayerVelocity.x:F3}," +
                $"{state.PlayerVelocity.y:F3}).");
        }
        if (!wallRecoveryImpulseConfirmed &&
            !wallRecoveryImpulseFailureLogged &&
            (wallRecoveryUpwardPhysicsSteps >= 2 ||
             compressedFixedStepImpulse ||
             stage2Section0NearLipRebound) &&
            (totalRise >= 0.30f ||
             stage2Section0NearLipRebound) &&
            (state.PlayerVelocity.y > 5f ||
             compressedFixedStepImpulse ||
             stage2Section0NearLipRebound))
        {
            wallRecoveryImpulseConfirmed = true;
            BonusRunnerLog.Debug(
                $"WallClimbImpulseConfirmed AttemptId={automaticAttemptId}, " +
                $"Elapsed={timeSinceInput:F3}s, PhysicsRiseSteps=" +
                $"{wallRecoveryUpwardPhysicsSteps}, CompressedFixedStep=" +
                $"{compressedFixedStepImpulse}, Stage2NearLipRebound=" +
                $"{stage2Section0NearLipRebound}, DeliveredFixedSteps=" +
                $"{jumpController.LastReleaseHeldFixedSteps}, " +
                $"StartY={wallRecoveryImpulseStartY:F3}, " +
                $"CurrentY={state.PlayerPosition.y:F3}, " +
                $"DeltaY={totalRise:F3}, StartVY=" +
                $"{wallRecoveryImpulseStartVelocityY:F3}, " +
                $"CurrentVY={state.PlayerVelocity.y:F3}. " +
                "The second press produced sustained upward movement.",
                "Recovery");
        }

        if (BonusRunnerLog.IsDebugMode &&
            Time.unscaledTime >= nextWallClimbFrameLogTime &&
            timeSinceInput <= 0.55f)
        {
            nextWallClimbFrameLogTime = Time.unscaledTime + 0.04f;
            BonusRunnerLog.Debug(
                $"WallClimbFrame AttemptId={automaticAttemptId}, " +
                $"Elapsed={timeSinceInput:F3}s, " +
                $"Phase={(wallRecoveryLipCrossed ? "LipCrossed" : wallRecoveryImpulseConfirmed ? "Rising" : "AwaitingImpulse")}, " +
                $"Position=({state.PlayerPosition.x:F3}," +
                $"{state.PlayerPosition.y:F3}), Velocity=" +
                $"({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
                $"DeltaY={totalRise:F3}, PhysicsRiseSteps=" +
                $"{wallRecoveryUpwardPhysicsSteps}, " +
                $"Grounded={state.IsGrounded}, " +
                $"Holding={jumpController.IsHoldingJump}, " +
                $"PlannedHold={automaticPlannedHold:F3}s, " +
                $"Maneuver={automaticManeuver}, " +
                $"Target=[{automaticTargetLeft:F3}," +
                $"{automaticTargetRight:F3}]@{automaticTargetTop:F3}",
                "Recovery");
        }

        bool fixedStepBoundedWallPulsePending =
            jumpController.IsHoldingJump &&
            (HasCommittedExitFaceFlight() ||
             wallMandatoryFaceSetupActive ||
             wallMandatoryFaceInterceptCommitted);
        long currentFixedStep = JumpPhysicsFeedback.FixedStepSequence;
        if (!wallRecoveryImpulseConfirmed &&
            !wallRecoveryImpulseFailureLogged &&
            currentPulseHasFixedStepEvidence &&
            !jumpController.IsHoldingJump &&
            wallRecoveryImpulseReleaseObservedFixedStep < 0)
        {
            wallRecoveryImpulseReleaseObservedFixedStep = currentFixedStep;
            BonusRunnerLog.Debug(
                $"WallClimbImpulseDecisionDeferred AttemptId=" +
                $"{automaticAttemptId}, ReleaseObservedFixedStep=" +
                $"{wallRecoveryImpulseReleaseObservedFixedStep}, " +
                $"UpwardSteps={wallRecoveryUpwardPhysicsSteps}, " +
                $"DeltaY={totalRise:F3}, VY=" +
                $"{state.PlayerVelocity.y:F3}. A released one-step pulse " +
                "gets one subsequent physics step before failure is " +
                "classified; render callbacks cannot reject it between " +
                "the first and second upward observations.",
                "Recovery");
        }
        bool twoPostReleasePhysicsStepsObserved =
            wallRecoveryImpulseReleaseObservedFixedStep >= 0 &&
            currentFixedStep >=
                wallRecoveryImpulseReleaseObservedFixedStep + 2;
        bool impulseFailureObservationReady =
            fixedStepBoundedWallPulsePending
                ? false
                : currentPulseHasFixedStepEvidence
                    ? !jumpController.IsHoldingJump &&
                      twoPostReleasePhysicsStepsObserved
                    : timeSinceInput >= 0.14f;
        if (!wallRecoveryImpulseConfirmed &&
            !wallRecoveryImpulseFailureLogged &&
            impulseFailureObservationReady)
        {
            wallRecoveryImpulseFailureLogged = true;
            BonusRunnerLog.Warning(
                $"WallClimbImpulseRejected AttemptId={automaticAttemptId}: " +
                $"the second press did not produce two upward physics steps " +
                $"after a post-release physics barrier within " +
                $"{timeSinceInput:F3}s. ReleaseObservedFixedStep=" +
                $"{wallRecoveryImpulseReleaseObservedFixedStep}, " +
                $"CurrentFixedStep={currentFixedStep}, StartY=" +
                $"{wallRecoveryImpulseStartY:F3}, CurrentY=" +
                $"{state.PlayerPosition.y:F3}, DeltaY={totalRise:F3}, " +
                $"StartVY={wallRecoveryImpulseStartVelocityY:F3}, " +
                $"CurrentVY={state.PlayerVelocity.y:F3}. " +
                "The hold is released and the contact may retry after a " +
                "real physics-frame barrier; this press consumes one retry " +
                "slot and is not a successful climb.");
            jumpController.Release();
            wallReleaseObservedFixedStep =
                JumpPhysicsFeedback.FixedStepSequence;
            if (wallRecoveryAttempts >=
                MaximumWallRecoveriesPerAirborneSequence)
            {
                wallActionPhase = WallActionPhase.Failed;
                FinishLearningSample(
                    state,
                    "WallImpulseRetryLimitReached");
                ResetAutomaticControlState();
                nextAutomaticAttemptTime = Time.unscaledTime + 0.25f;
                return;
            }
            wallRecoveryContactLatched = false;
            wallRecoverySawUpwardMotion = false;
            wallActionPhase = WallActionPhase.AwaitingWallContact;
            nextWallRecoveryTime = Time.unscaledTime + 0.01f;
        }
    }

    private void FinishLearningSample(BonusStageState state, string outcome)
    {
        bool useAuthoritativeLandingEvidence =
            authoritativeLandingEvidenceActive;
        bool landingEvidenceWasHistorical =
            authoritativeLandingEvidenceHistorical;
        BonusBoardSegment capturedLandingSurface =
            authoritativeLandingEvidenceSurface;
        float capturedLandingPlayerHalfWidth =
            authoritativeLandingEvidencePlayerHalfWidth;
        float capturedLandingAt = authoritativeLandingEvidenceAt;
        long capturedLandingFixedStep =
            authoritativeLandingEvidenceFixedStep;
        // The override belongs to exactly one completion call, including the
        // early-return case where lifecycle cleanup already closed the sample.
        authoritativeLandingEvidenceActive = false;
        authoritativeLandingEvidenceHistorical = false;
        authoritativeLandingEvidenceSurface = default;
        authoritativeLandingEvidencePlayerHalfWidth = 0f;
        authoritativeLandingEvidenceAt = 0f;
        authoritativeLandingEvidenceFixedStep = -1;
        if (!learningSampleActive)
            return;

        CaptureRecentAutomaticFlightContact(state, outcome);

        float sampleEndTime =
            useAuthoritativeLandingEvidence && capturedLandingAt > 0f
                ? capturedLandingAt
                : landingCandidateStableFixedSteps >= 2 &&
                  landingCandidateFirstObservedAt > 0f
                    ? landingCandidateFirstObservedAt
                : Time.unscaledTime;

        float holdSeconds = learningInputReleased
            ? Mathf.Max(0f, learningInputUpTime - learningInputDownTime)
            : Mathf.Max(0f, sampleEndTime - learningInputDownTime);
        float controllerWallClockHold = float.NaN;
        float controllerFixedStepHold = float.NaN;
        int controllerHeldFixedSteps = 0;
        string holdMeasurement = learningSource == "Automatic"
            ? "Planned"
            : "PhysicalInput";
        if (learningSource == "Automatic" &&
            Mathf.Abs(jumpController.LastPressStartedAt - learningInputDownTime) <= 0.10f)
        {
            float actualReleaseTime =
                jumpController.LastReleaseAt >= jumpController.LastPressStartedAt
                    ? jumpController.LastReleaseAt
                    : sampleEndTime;
            controllerWallClockHold = Mathf.Max(
                0f,
                actualReleaseTime - jumpController.LastPressStartedAt);
            holdSeconds = controllerWallClockHold;
            holdMeasurement = "ControllerActual";
            if (jumpController.LastReleaseHeldFixedSteps > 0)
            {
                float fixedStepEquivalent =
                    jumpController.LastReleaseHeldFixedSteps *
                    Mathf.Clamp(
                        automaticPlanPhysicsSnapshot.FixedDeltaTime,
                        0.005f,
                        0.05f);
                controllerFixedStepHold = fixedStepEquivalent;
                controllerHeldFixedSteps =
                    jumpController.LastReleaseHeldFixedSteps;
                // In a throttled/background frame, unscaled wall time can
                // advance while no physics step is delivered. The native
                // controller's counted fixed steps are the authoritative
                // physical hold; wall-clock elapsed is retained separately
                // for diagnosing focus stalls.
                holdSeconds = fixedStepEquivalent;
                holdMeasurement = "ControllerFixedStepEquivalent";
            }
        }
        float flightSeconds = learningTookOff
            ? Mathf.Max(0f, sampleEndTime - learningTakeoffTime)
            : 0f;
        float horizontalDistance = learningTookOff
            ? state.PlayerPosition.x - learningTakeoffPosition.x
            : 0f;
        float heightGain = learningMaximumY - learningTriggerPosition.y;
        float takeoffToApexGain = learningTookOff
            ? learningMaximumY - learningTakeoffPosition.y
            : 0f;
        string landingSurface = "NotLanded";
        bool landingSurfaceValid = false;
        float observedLandingTop = state.PlayerPosition.y;
        if (outcome == "Landed" && state.HasPlayer)
        {
            if (useAuthoritativeLandingEvidence)
            {
                landingSurfaceValid = true;
                observedLandingTop = capturedLandingSurface.Top;
                landingSurface =
                    $"Evidence=FixedStepLatch@{capturedLandingFixedStep} " +
                    $"Raw=[{capturedLandingSurface.Left:F3}," +
                    $"{capturedLandingSurface.Right:F3}] Safe=" +
                    $"[{capturedLandingSurface.SafeLeft:F3}," +
                    $"{capturedLandingSurface.SafeRight:F3}] Top=" +
                    $"{capturedLandingSurface.Top:F3} Collider=" +
                    $"{capturedLandingSurface.ColliderInstanceId}:" +
                    $"{capturedLandingSurface.ColliderName}";
            }
            else
            {
                try
                {
                    PlayerMovement landingPlayer = PlayerMovement.instance;
                    if (landingPlayer != null)
                    {
                        BonusBoardScanResult observedLanding = platformScanner.Scan(
                            state.PlayerPosition,
                            landingPlayer,
                            Mathf.Abs(state.PlayerVelocity.x));
                        landingSurfaceValid = observedLanding.IsValid;
                        if (landingSurfaceValid)
                            observedLandingTop = observedLanding.Current.Top;
                        landingSurface = observedLanding.IsValid
                            ? $"Raw=[{observedLanding.Current.Left:F3}," +
                              $"{observedLanding.Current.Right:F3}] " +
                              $"Safe=[{observedLanding.Current.SafeLeft:F3}," +
                              $"{observedLanding.Current.SafeRight:F3}] " +
                              $"Top={observedLanding.Current.Top:F3} Collider=" +
                              $"{observedLanding.Current.ColliderInstanceId}:" +
                              $"{observedLanding.Current.ColliderName}"
                            : $"ScanInvalid:{observedLanding.Reason}";
                    }
                }
                catch (System.Exception exception)
                {
                    landingSurface = $"ScanFailed:{exception.GetType().Name}";
                }
            }
        }
        string physicsFeedback = jumpPhysicsFeedback.CompleteSample(
            holdSeconds,
            learningTookOff,
            learningFirstApexCaptured,
            learningTakeoffPosition,
            learningApexPosition);

        float inputToLandingSeconds = Mathf.Max(
            0f,
            sampleEndTime - learningInputDownTime);
        float inputToTakeoffSeconds = learningTookOff
            ? Mathf.Max(0f, learningTakeoffTime - learningInputDownTime)
            : float.PositiveInfinity;
        float triggerToTakeoffX = learningTookOff
            ? Mathf.Abs(learningTakeoffPosition.x - learningTriggerPosition.x)
            : float.PositiveInfinity;
        bool timingLandedInSafeTarget =
            automaticTargetSafeRight > automaticTargetSafeLeft + 0.05f &&
            state.PlayerPosition.x >= automaticTargetSafeLeft &&
            state.PlayerPosition.x <= automaticTargetSafeRight &&
            Mathf.Abs(observedLandingTop - automaticTargetTop) <= 0.35f;
        bool timingStartedOnPlannedSource =
            Mathf.Abs(
                learningTriggerPosition.y -
                automaticPredictionLaunchFeetY) <= 0.15f &&
            Mathf.Abs(learningTriggerVelocity.y) <= 2.50f;
        bool timingHasStableTopContact =
            landingCandidateStableFixedSteps >= 2 &&
            Mathf.Abs(state.PlayerVelocity.y) <= 2.50f &&
            !useAuthoritativeLandingEvidence;
        bool timingHoldBucketMatchesPlan =
            JumpCalibrationBuckets.GetHoldBucket(holdSeconds) ==
            JumpCalibrationBuckets.GetHoldBucket(automaticPlannedHold);
        int sphereCountAtEnd =
            learningSource == "Automatic" &&
            BonusStageInspector.TryGetBonusSphereCount(
                out int observedSphereCount)
                ? observedSphereCount
                : -1;
        int actualSphereHits =
            automaticSphereCountAtPlan >= 0 && sphereCountAtEnd >= 0
                ? Mathf.Max(
                    0,
                    sphereCountAtEnd - automaticSphereCountAtPlan)
                : -1;
        float boostComponentIncrease =
            learningMaximumSpiritBoostComponent -
            learningStartingSpiritBoostComponent;
        float horizontalSpeedIncrease =
            learningMaximumHorizontalSpeed -
            learningStartingHorizontalSpeed;
        float horizontalDiscontinuityThreshold = Mathf.Max(
            4.0f,
            learningStartingHorizontalSpeed * 0.20f);
        bool nativeBoostResetObserved =
            boostComponentIncrease > 0.25f ||
            horizontalSpeedIncrease >= horizontalDiscontinuityThreshold;
        // BonusSphere progress and SpiritBoost are independent objects. The
        // retained V0.55 trace changes VX by +10 while the sphere counter is
        // unchanged, so sphere delta cannot be a kinematics gate. Isolate the
        // sample only when the typed additive component or a matching positive
        // VX discontinuity proves a speed reset during this flight.
        bool boostKinematicsClean =
            !learningSpiritBoostModeEnabled ||
            learningSpiritBoostReadAvailable &&
            !nativeBoostResetObserved;
        // Map identity chooses candidate geometry, never the physics-learning
        // contract. Every map isolates timing and endpoint evidence in the
        // same way so an authored-route sample cannot double-train the model.
        const bool exclusiveLiveCalibration = true;
        bool cleanFlightTimingSample =
            learningMayLearnGroundKinematics &&
            outcome == "Landed" &&
            landingSurfaceValid &&
            timingLandedInSafeTarget &&
            timingStartedOnPlannedSource &&
            timingHasStableTopContact &&
            timingHoldBucketMatchesPlan &&
            boostKinematicsClean &&
            !landingEvidenceWasHistorical &&
            learningTookOff &&
            learningFirstApexCaptured &&
            inputToTakeoffSeconds <= 0.08f &&
            triggerToTakeoffX <= Mathf.Max(
                0.80f,
                Mathf.Abs(learningTakeoffVelocity.x) * 0.08f);
        string liveFlightTimingCalibrationChannel = "Rejected";
        if (cleanFlightTimingSample)
        {
            JumpPhysicsSnapshot timingPhysics =
                jumpPhysicsFeedback.CaptureSnapshot(PlayerMovement.instance);
            // Calibration belongs to the planned support-to-support geometry,
            // not the character's instantaneous transform. Using player Y made
            // floating +2.995 edge contacts alias true +2 landings.
            float timingHeightDelta =
                observedLandingTop - automaticPredictionLaunchFeetY;
            float rawPredictedFlight =
                jumpPlanner.PredictRawInputToLandingSeconds(
                    holdSeconds,
                    timingHeightDelta,
                    timingPhysics);
            if (rawPredictedFlight > 0f)
            {
                liveFlightTimingCalibrationChannel =
                    jumpPhysicsFeedback.ObserveFlightTiming(
                    rawPredictedFlight,
                    inputToLandingSeconds,
                    learningSource,
                    holdSeconds,
                    timingHeightDelta,
                    useExclusiveLiveChannel:
                        exclusiveLiveCalibration);
            }
        }
        else
        {
            BonusRunnerLog.Debug(
                $"FlightTimingRejected AttemptId={learningSampleId}, " +
                $"Source={learningSource}, Outcome={outcome}, " +
                $"LandingSurfaceValid={landingSurfaceValid}, " +
                $"TookOff={learningTookOff}, ApexCaptured=" +
                $"{learningFirstApexCaptured}, InputToTakeoff=" +
                $"{inputToTakeoffSeconds:F3}s, TriggerToTakeoffX=" +
                $"{triggerToTakeoffX:F3}, LearnGroundKinematics=" +
                $"{learningMayLearnGroundKinematics}, " +
                $"SafeTargetTiming={timingLandedInSafeTarget}, " +
                $"StartedOnPlannedSource={timingStartedOnPlannedSource}, " +
                $"StableTopContact={timingHasStableTopContact}, " +
                $"StableFixedSteps={landingCandidateStableFixedSteps}, " +
                $"HoldBucketMatchesPlan={timingHoldBucketMatchesPlan}, " +
                $"BoostKinematicsClean={boostKinematicsClean}, " +
                $"NativeBoostRead={learningSpiritBoostReadAvailable}, " +
                $"BoostComponent=" +
                $"{learningStartingSpiritBoostComponent:F3}->" +
                $"{learningMaximumSpiritBoostComponent:F3}, " +
                $"HorizontalSpeed=" +
                $"{learningStartingHorizontalSpeed:F3}->" +
                $"{learningMaximumHorizontalSpeed:F3}, " +
                $"BoostResetObserved={nativeBoostResetObserved}, " +
                $"ActualSphereHits={actualSphereHits}, " +
                $"TriggerVY={learningTriggerVelocity.y:F3}, EndVY=" +
                $"{state.PlayerVelocity.y:F3}, " +
                $"HistoricalEvidence=" +
                $"{landingEvidenceWasHistorical}, EvidenceFixedStep=" +
                $"{capturedLandingFixedStep}.",
                "Physics");
        }

        BonusRunnerLog.Debug(
            $"JumpSample AttemptId={learningSampleId}, Source={learningSource}, Outcome={outcome}, Map={learningMap}, Section={learningSection}, " +
            $"Hold={holdSeconds:F3}s, HoldMeasurement={holdMeasurement}, " +
            $"ControllerWallClockHold=" +
            $"{(float.IsNaN(controllerWallClockHold) ? "Unavailable" : controllerWallClockHold.ToString("F3") + "s")}, " +
            $"ControllerFixedStepHold=" +
            $"{(float.IsNaN(controllerFixedStepHold) ? "Unavailable" : controllerFixedStepHold.ToString("F3") + "s")}/" +
            $"{controllerHeldFixedSteps}Steps, " +
            $"Trigger=({learningTriggerPosition.x:F3},{learningTriggerPosition.y:F3}), " +
            $"Takeoff=({learningTakeoffPosition.x:F3},{learningTakeoffPosition.y:F3}), " +
            $"TakeoffVelocity=({learningTakeoffVelocity.x:F3},{learningTakeoffVelocity.y:F3}), " +
            $"FirstApex=({learningApexPosition.x:F3},{learningApexPosition.y:F3}), " +
            $"FirstApexCaptured={learningFirstApexCaptured}, " +
            $"TriggerHeightGain={heightGain:F3}, TakeoffToApexGain={takeoffToApexGain:F3}, " +
            $"End=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"EndVelocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"SpiritBoostKinematics[Mode=" +
            $"{learningSpiritBoostModeEnabled},Read=" +
            $"{learningSpiritBoostReadAvailable},Component=" +
            $"{learningStartingSpiritBoostComponent:F3}->" +
            $"{learningMaximumSpiritBoostComponent:F3},VX=" +
            $"{learningStartingHorizontalSpeed:F3}->" +
            $"{learningMaximumHorizontalSpeed:F3},Reset=" +
            $"{nativeBoostResetObserved},Clean=" +
            $"{boostKinematicsClean}], " +
            $"HorizontalDistance={horizontalDistance:F3}, Flight={flightSeconds:F3}s; " +
            $"EndEvidence=" +
            $"{(useAuthoritativeLandingEvidence ? "FixedStepLatch" : "LiveRender")}, " +
            $"EvidenceFixedStep={capturedLandingFixedStep}, " +
            $"HistoricalEvidence={landingEvidenceWasHistorical}; " +
            $"LandingSurface[{landingSurface}]; " +
            $"{physicsFeedback}",
            "Learning");

        if (learningSource == "Manual" && manualWallSequenceActive)
        {
            BonusRunnerLog.Debug(
                $"ManualWallSequence AttemptId={learningSampleId}, Outcome={outcome}, " +
                $"WallJumps={manualWallJumpCount}, LastWallDown=" +
                $"({manualWallJumpPosition.x:F3},{manualWallJumpPosition.y:F3}), " +
                $"LastWallHold={manualWallJumpHold:F3}s, Collider=" +
                $"{manualWallColliderId}:{manualWallColliderName}, " +
                $"End=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                $"LandingSurface[{landingSurface}]",
                "Recovery");
            ResetManualWallSequence();
        }

        if (learningSource == "Automatic" && automaticPredictionActive)
        {
            string sphereProgress =
                automaticSphereCountAtPlan >= 0 && sphereCountAtEnd >= 0
                    ? $"{automaticSphereCountAtPlan}->{sphereCountAtEnd} " +
                      $"Delta={sphereCountAtEnd - automaticSphereCountAtPlan}"
                    : $"Unavailable(Plan={automaticSphereCountAtPlan}," +
                      $"End={sphereCountAtEnd})";
            string sphereOutcome = automaticExpectedSphereHits <= 0
                ? "NoPlannedPickup"
                : actualSphereHits < 0
                    ? "Unverified"
                    : actualSphereHits >= automaticExpectedSphereHits
                        ? "Met"
                        : "BelowPrediction";
            if (outcome == "Landed" && state.HasPlayer)
            {
                float landingError =
                    state.PlayerPosition.x - automaticPredictedLandingX;
                float acceptedSafeLeft =
                    automaticTargetSafeLeft -
                    automaticLandingSafeTolerance;
                float acceptedSafeRight =
                    automaticTargetSafeRight +
                    automaticLandingSafeTolerance;
                bool withinPreferredTargetX =
                    state.PlayerPosition.x >= acceptedSafeLeft &&
                    state.PlayerPosition.x <= acceptedSafeRight;
                bool withinAuthorizedRawTargetX = false;
                float actualRawBodyOverlap = 0f;
                float requiredRawBodyOverlap = Mathf.Max(
                    0.15f,
                    Mathf.Abs(automaticTriggerSpeed) * Mathf.Clamp(
                        automaticPlanPhysicsSnapshot.FixedDeltaTime,
                        0.005f,
                        0.05f));
                bool supportMatched = false;
                bool actualSupportAvailable = false;
                BonusBoardSegment actualSupport = default;
                string supportCheck = "Unavailable";
                BonusBoardSegment expectedSupport = new(
                    automaticTargetLeft,
                    automaticTargetRight,
                    automaticTargetTop,
                    automaticTargetSafeLeft,
                    automaticTargetSafeRight,
                    automaticTargetColliderId,
                    automaticTargetColliderName,
                    automaticTargetMapPieceName,
                    automaticTargetMapPieceOriginX,
                    automaticTargetMapPieceInstanceId,
                    automaticTargetRegistryGeneration,
                    automaticTargetStaticSurfaceIndex);
                try
                {
                    float bodyMinimumX = 0f;
                    float bodyMaximumX = 0f;
                    bool bodyBoundsAvailable = false;
                    string evidenceSource;
                    if (useAuthoritativeLandingEvidence)
                    {
                        actualSupportAvailable = true;
                        actualSupport = capturedLandingSurface;
                        float halfWidth = Mathf.Max(
                            0.15f,
                            capturedLandingPlayerHalfWidth);
                        bodyMinimumX = state.PlayerPosition.x - halfWidth;
                        bodyMaximumX = state.PlayerPosition.x + halfWidth;
                        bodyBoundsAvailable = true;
                        evidenceSource =
                            $"FixedStepLatch@{capturedLandingFixedStep}";
                    }
                    else
                    {
                        PlayerMovement player = PlayerMovement.instance;
                        if (player == null)
                        {
                            supportCheck = "PlayerUnavailable";
                            throw new InvalidOperationException(
                                "Player unavailable for live landing scan.");
                        }
                        BonusBoardScanResult landingScan = platformScanner.Scan(
                            state.PlayerPosition,
                            player,
                            Mathf.Abs(state.PlayerVelocity.x));
                        actualSupportAvailable = landingScan.IsValid;
                        if (actualSupportAvailable)
                            actualSupport = landingScan.Current;
                        if (player.playerCollider != null)
                        {
                            Bounds playerBounds = player.playerCollider.bounds;
                            bodyMinimumX = playerBounds.min.x;
                            bodyMaximumX = playerBounds.max.x;
                            bodyBoundsAvailable = true;
                        }
                        evidenceSource = "RenderColliderScan";
                        if (!actualSupportAvailable)
                            supportCheck = $"ScanInvalid:{landingScan.Reason}";
                    }

                    if (actualSupportAvailable)
                    {
                        if (automaticLandingAllowsRawBodyFit &&
                            bodyBoundsAvailable)
                        {
                            actualRawBodyOverlap = Mathf.Max(
                                0f,
                                Mathf.Min(
                                    bodyMaximumX,
                                    automaticTargetRight) -
                                Mathf.Max(
                                    bodyMinimumX,
                                    automaticTargetLeft));
                            withinAuthorizedRawTargetX =
                                actualRawBodyOverlap >=
                                    requiredRawBodyOverlap;
                        }
                        bool topMatched = Mathf.Abs(
                            actualSupport.Top - automaticTargetTop) <= 0.35f;
                        bool rawBoundsMatched =
                            (state.PlayerPosition.x >= automaticTargetLeft - 0.10f &&
                             state.PlayerPosition.x <= automaticTargetRight + 0.10f) ||
                            withinAuthorizedRawTargetX;
                        bool identityMatched =
                            StaticOrColliderIdentityMatches(
                                actualSupport,
                                expectedSupport);
                        supportMatched =
                            topMatched && rawBoundsMatched && identityMatched;
                        supportCheck =
                            $"Evidence={evidenceSource},TopMatch={topMatched}," +
                            $"BoundsMatch={rawBoundsMatched}," +
                            $"IdentityMatch={identityMatched},ActualSupport=" +
                            $"{actualSupport.ColliderInstanceId}:" +
                            $"{actualSupport.ColliderName},MapPiece=" +
                            $"{actualSupport.MapPieceName}#" +
                            $"{actualSupport.MapPieceInstanceId},Generation=" +
                            $"{actualSupport.RegistryGeneration},Surface=" +
                            $"{actualSupport.StaticSurfaceIndex}";
                    }
                }
                catch (System.Exception exception)
                {
                    supportCheck = $"CheckFailed:{exception.GetType().Name}";
                }
                bool withinTargetX =
                    withinPreferredTargetX ||
                    withinAuthorizedRawTargetX;
                bool landedOnTarget = withinTargetX && supportMatched;
                float actualTriggerTravel =
                    state.PlayerPosition.x - learningTriggerPosition.x;
                float actualTakeoffTravel = learningTookOff
                    ? state.PlayerPosition.x - learningTakeoffPosition.x
                    : 0f;
                float errorFromPredictedX =
                    state.PlayerPosition.x - automaticPredictedLandingX;
                float targetLeftMargin =
                    state.PlayerPosition.x - automaticTargetSafeLeft;
                float targetRightMargin =
                    automaticTargetSafeRight - state.PlayerPosition.x;
                float actualSupportTopError = actualSupportAvailable
                    ? actualSupport.Top - automaticTargetTop
                    : float.NaN;
                bool wallAction = automaticPlanReason.StartsWith(
                    "WallRecovery",
                    System.StringComparison.Ordinal);
                string resultClass =
                    wallAction && wallRecoveryLipCrossed
                        ? landedOnTarget
                            ? "WallActionSuccess"
                            : "WallLipClearedDifferentSupport"
                        : landedOnTarget
                            ? "Success"
                            : !withinTargetX
                                ? state.PlayerPosition.x < acceptedSafeLeft
                                    ? "ShortOfSafeRange"
                                    : "BeyondSafeRange"
                                : "WrongSupport";
                string executionStatus =
                    wallAction && wallRecoveryLipCrossed
                        ? landedOnTarget
                            ? "Success:LipClearedAndTargetReached"
                            : "Failure:WrongSupportAfterLip"
                        : learningTookOff
                            ? "InputAccepted"
                            : "InputNotConfirmed";
                string routeStatus = landedOnTarget
                    ? "TargetReached"
                    : wallAction && wallRecoveryLipCrossed
                        ? "TargetMissedAfterWallExit"
                        : "TargetMissed";
                string failureDomain = landedOnTarget
                        ? "None"
                        : wallAction && wallRecoveryLipCrossed
                            ? actualSupportAvailable
                                ? "ExecutionModel"
                                : "Execution:NoStableSupport"
                        : supportMatched
                            ? "ExecutionModel"
                            : "RouteOrExecution";
                string actualSupportDescription = actualSupportAvailable
                    ? $"Raw=[{actualSupport.Left:F3},{actualSupport.Right:F3}] " +
                      $"Safe=[{actualSupport.SafeLeft:F3},{actualSupport.SafeRight:F3}] " +
                      $"Top={actualSupport.Top:F3} Collider=" +
                      $"{actualSupport.ColliderInstanceId}:{actualSupport.ColliderName} " +
                      $"MapPiece={actualSupport.MapPieceName}#" +
                      $"{actualSupport.MapPieceInstanceId}/G" +
                      $"{actualSupport.RegistryGeneration}/S" +
                      $"{actualSupport.StaticSurfaceIndex}"
                    : "Unavailable";
                // Raw support is normally diagnostic-only. A route whose
                // wall-exit solver explicitly selected RawBodyFit is the one
                // exception; the accepted mode/tolerance must survive into
                // outcome scoring or a planned physical landing is falsely
                // labelled BeyondSafeRange.
                // Calibration has one map-independent evidence contract.
                // Edge/face contacts, transient Grounded pulses and Spirit
                // speed scenarios are useful execution evidence, but are not
                // stable top landings and must never train horizontal travel.
                // A wide level landing updates the global multiplicative
                // scale; every other stable safe-centre landing may update the
                // height/hold residual. One sample can enter only one channel.
                automaticPlanPhysicsSnapshot.LandingErrorProfile.GetBias(
                    automaticTargetHeightDelta,
                    automaticPlannedHold,
                    out int existingResidualSamples);
                bool stableSafeCentreLanding =
                    boostKinematicsClean &&
                    !learningSpiritBoostModeEnabled &&
                    landedOnTarget &&
                    timingHasStableTopContact &&
                    !landingEvidenceWasHistorical &&
                    learningTookOff &&
                    !automaticFutureSpeedTransitionExpected &&
                    !wallAction &&
                    automaticManeuver ==
                        BonusManeuverKind.GroundJumpToLanding;
                bool timingModelWasStableBeforeEndpointSample =
                    liveFlightTimingCalibrationChannel.StartsWith(
                        "Stable",
                        StringComparison.Ordinal);
                bool successfulLandingFeedbackEligible =
                    stableSafeCentreLanding &&
                    timingModelWasStableBeforeEndpointSample &&
                    Mathf.Abs(automaticTargetHeightDelta) <= 0.35f &&
                    automaticTargetSafeRight - automaticTargetSafeLeft >=
                        2.0f &&
                    existingResidualSamples == 0;
                bool landingErrorFeedbackEligible =
                    stableSafeCentreLanding &&
                    timingModelWasStableBeforeEndpointSample &&
                    !successfulLandingFeedbackEligible;
                string calibrationChannel =
                    !boostKinematicsClean
                        ? "Rejected:SpiritBoostTransition"
                        : !stableSafeCentreLanding
                            ? "Rejected:NoStableSafeCentreTopLanding"
                        : !timingModelWasStableBeforeEndpointSample
                            ? $"TimingOnly:" +
                              $"{liveFlightTimingCalibrationChannel}"
                        : landingErrorFeedbackEligible
                            ? "HeightHoldResidual"
                            : successfulLandingFeedbackEligible
                                ? "LevelTravelScale"
                                : "Rejected";
                if (landingErrorFeedbackEligible)
                {
                    jumpPhysicsFeedback.ObserveLandingError(
                        automaticPredictedHorizontalTravel,
                        actualTriggerTravel,
                        automaticPlannedLandingBias,
                        automaticTargetHeightDelta,
                        automaticPlannedHold,
                        automaticPlanReason);
                }
                if (successfulLandingFeedbackEligible &&
                    !automaticPlanReason.StartsWith(
                        "WallRecovery",
                        System.StringComparison.Ordinal))
                {
                    jumpPhysicsFeedback.ObserveSuccessfulLanding(
                        automaticPredictedHorizontalTravel,
                        actualTriggerTravel,
                        automaticPlannedTravelScale,
                        automaticPlannedHold,
                        automaticTriggerSpeed,
                        automaticTargetHeightDelta);
                }
                BonusRunnerLog.Debug(
                    $"JumpAttemptResult RouteId={activeRouteDecisionId}, AttemptId={automaticAttemptId}, Outcome={outcome}, " +
                    $"ResultClass={resultClass}, Plan={automaticPlanReason}, " +
                    $"ExecutionStatus={executionStatus}, RouteStatus={routeStatus}, " +
                    $"FailureDomain={failureDomain}, " +
                    $"PlanTrigger=({automaticPlanTriggerPosition.x:F3},{automaticPlanTriggerPosition.y:F3}), " +
                    $"ActualTakeoff=({learningTakeoffPosition.x:F3},{learningTakeoffPosition.y:F3}), " +
                    $"Apex=({learningApexPosition.x:F3},{learningApexPosition.y:F3}), " +
                    $"ActualLanding=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
                    $"SourceRaw=[{automaticSourceSegment.Left:F3},{automaticSourceSegment.Right:F3}] " +
                    $"SourceSafe=[{automaticSourceSegment.SafeLeft:F3},{automaticSourceSegment.SafeRight:F3}] " +
                    $"SourceTop={automaticSourceSegment.Top:F3} SourceCollider=" +
                    $"{automaticSourceSegment.ColliderInstanceId}:{automaticSourceSegment.ColliderName}, " +
                    $"TargetRaw=[{automaticTargetLeft:F3},{automaticTargetRight:F3}] " +
                    $"TargetSafe=[{automaticTargetSafeLeft:F3},{automaticTargetSafeRight:F3}] " +
                    $"TargetTop={automaticTargetTop:F3} TargetCollider=" +
                    $"{automaticTargetColliderId}:{automaticTargetColliderName}, " +
                    $"ActualSupport[{actualSupportDescription}], " +
                    $"PlannedLaunch={automaticPlannedLaunchX:F3} " +
                    $"LaunchWindow=[{automaticLaunchWindowLeft:F3},{automaticLaunchWindowRight:F3}], " +
                    $"PredictedLanding=({automaticPredictedLandingX:F3},{automaticTargetTop:F3}), " +
                    $"PredictedFlight={automaticPredictedFlightSeconds:F3}s " +
                    $"ActualFlight={flightSeconds:F3}s FlightError={flightSeconds - automaticPredictedFlightSeconds:F3}s, " +
                    $"PlannedHold={automaticPlannedHold:F3}s " +
                    $"PlannedEffectiveHold={Mathf.Min(automaticPlannedHold, automaticPlanPhysicsSnapshot.EffectiveHoldCapSeconds):F3}s " +
                    $"ActualHold={holdSeconds:F3}s " +
                    $"ActualEffectiveHold={Mathf.Min(holdSeconds, automaticPlanPhysicsSnapshot.EffectiveHoldCapSeconds):F3}s " +
                    $"ControllerHoldError={holdSeconds - automaticPlannedHold:F3}s, " +
                    $"PredictedTravel={automaticPredictedHorizontalTravel:F3} " +
                    $"TravelEnvelope=[" +
                    $"{automaticMinimumPredictedHorizontalTravel:F3}," +
                    $"{automaticMaximumPredictedHorizontalTravel:F3}] " +
                    $"FutureSpeedTransition=" +
                    $"{automaticFutureSpeedTransitionExpected} " +
                    $"ActualTriggerTravel={actualTriggerTravel:F3} " +
                    $"ActualTakeoffTravel={actualTakeoffTravel:F3}, " +
                    $"LandingErrorX={landingError:F3} PredictedToActualErrorX={errorFromPredictedX:F3}, " +
                    $"SafeMarginLeft={targetLeftMargin:F3} SafeMarginRight={targetRightMargin:F3}, " +
                    $"SupportTopErrorY={actualSupportTopError:F3}, " +
                    $"LandingAcceptance[SafeTolerance=" +
                    $"{automaticLandingSafeTolerance:F3}," +
                    $"RawBodyFitAuthorized=" +
                    $"{automaticLandingAllowsRawBodyFit}," +
                    $"RawOverlap={actualRawBodyOverlap:F3}/" +
                    $"{requiredRawBodyOverlap:F3}," +
                    $"WithinSafeTolerance={withinPreferredTargetX}," +
                    $"WithinAuthorizedRaw={withinAuthorizedRawTargetX}], " +
                    $"WithinTargetX={withinTargetX}, SupportMatched={supportMatched}, " +
                    $"LandedOnTarget={landedOnTarget}, HazardAtPlan={automaticHazardAtPlan}, " +
                    $"SphereProgress={sphereProgress}, " +
                    $"ExpectedSphereHits={automaticExpectedSphereHits}, " +
                    $"RawExpectedSphereHits=" +
                    $"{automaticRawExpectedSphereHits}, " +
                    $"RemainingAtPlan={automaticRemainingSpheresAtPlan}, " +
                    $"ActualSphereHits={actualSphereHits}, SphereOutcome={sphereOutcome}, " +
                    $"ExpectedSpeedBoostHits=" +
                    $"{automaticExpectedSpeedBoostHits}, " +
                    $"SpiritBoostRoute[" +
                    $"{automaticSpiritBoostRouteEvidence}], " +
                    $"BoostKinematicsClean={boostKinematicsClean}, " +
                    $"CalibrationChannel={calibrationChannel}, " +
                    $"SupportCheck={supportCheck}, PhysicsRevAtPlan={automaticPhysicsRevision}",
                    "Attempt");
            }
            else
            {
                bool wallExitFlight = string.Equals(
                    outcome,
                    "WallBounceExitFlight",
                    StringComparison.Ordinal);
                string resultFailureDomain = wallExitFlight
                    ? "None"
                    : string.Equals(
                        outcome,
                        "PositionDiscontinuity",
                        StringComparison.Ordinal)
                        ? "Lifecycle"
                        : "ExecutionOrTransition";
                BonusRunnerLog.Debug(
                    $"JumpAttemptResult RouteId={activeRouteDecisionId}, AttemptId={automaticAttemptId}, Outcome={outcome}, " +
                    $"ResultClass={outcome}, Plan={automaticPlanReason}, " +
                    $"ExecutionStatus={(wallExitFlight ? "Success:ExitFlight" : outcome)}, " +
                    $"RouteStatus={(wallExitFlight ? "ReplanFromObservedFlight" : "NotApplicable")}, " +
                    $"FailureDomain={resultFailureDomain}, " +
                    $"PlanTrigger=({automaticPlanTriggerPosition.x:F3},{automaticPlanTriggerPosition.y:F3}), " +
                    $"PlannedLaunch={automaticPlannedLaunchX:F3} " +
                    $"LaunchWindow=[{automaticLaunchWindowLeft:F3},{automaticLaunchWindowRight:F3}], " +
                    $"PredictedFlight={automaticPredictedFlightSeconds:F3}s, " +
                    $"TargetRaw=[{automaticTargetLeft:F3},{automaticTargetRight:F3}] " +
                    $"TargetSafe=[{automaticTargetSafeLeft:F3},{automaticTargetSafeRight:F3}] " +
                    $"TargetTop={automaticTargetTop:F3}, " +
                    $"ResultPosition={(state.HasPlayer ? $"({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3})" : "Unavailable")}, " +
                    $"HazardAtPlan={automaticHazardAtPlan}, " +
                    $"SphereProgress={sphereProgress}, " +
                    $"ExpectedSphereHits={automaticExpectedSphereHits}, " +
                    $"RawExpectedSphereHits=" +
                    $"{automaticRawExpectedSphereHits}, " +
                    $"RemainingAtPlan={automaticRemainingSpheresAtPlan}, " +
                    $"ActualSphereHits={actualSphereHits}, SphereOutcome={sphereOutcome}, " +
                    $"ExpectedSpeedBoostHits=" +
                    $"{automaticExpectedSpeedBoostHits}, " +
                    "LandingMetrics=NotApplicable",
                    "Attempt");
            }

            automaticPredictionActive = false;
        }

        learningSampleActive = false;
        learningMayLearnGroundKinematics = false;
        if (partialAutomaticJumpImpulseAttemptId == learningSampleId)
            ResetPartialAutomaticJumpImpulse();
        learningSpiritBoostReadAvailable = false;
        learningSpiritBoostModeEnabled = false;
        learningStartingSpiritBoostComponent = 0f;
        learningMaximumSpiritBoostComponent = 0f;
        learningStartingHorizontalSpeed = 0f;
        learningMaximumHorizontalSpeed = 0f;
        ResetLandingConfirmation();
    }

    private void ObserveManualWallJumpDown(BonusStageState state)
    {
        if (Mathf.Abs(state.PlayerVelocity.x) >
                WallAttachmentVelocityTolerance)
            return;

        PlayerMovement player = PlayerMovement.instance;
        if (player == null)
            return;

        BonusWallContact wall = wallDetector.Detect(
            player,
            lastReliableHorizontalSpeed);
        if (!wall.IsDetected || !wall.IsTouching)
            return;

        bool newSequence =
            !manualWallSequenceActive ||
            Time.unscaledTime - manualWallLastPulseTime > 1.25f ||
            manualWallJumpCount > 0 &&
                Mathf.Abs(wall.Point.x - manualWallPoint.x) > 1.00f;
        if (newSequence)
        {
            manualWallSequenceActive = true;
            manualWallSequenceId = ++nextManualWallSequenceId;
            manualWallJumpCount = 0;
        }

        manualWallSequenceActive = true;
        manualWallJumpCount++;
        manualWallJumpDownTime = Time.unscaledTime;
        manualWallJumpHold = 0f;
        manualWallJumpPosition = state.PlayerPosition;
        manualWallJumpStartVelocity = state.PlayerVelocity;
        manualWallPoint = wall.Point;
        manualWallLastPulseTime = Time.unscaledTime;
        manualWallColliderId = wall.ColliderInstanceId;
        manualWallColliderName = wall.ColliderName;
        BonusRunnerLog.Debug(
            $"ManualWallPulseDown SequenceId={manualWallSequenceId}, " +
            $"AttemptId={(learningSampleActive && learningSource == "Manual" ? learningSampleId : 0)}, " +
            $"Pulse={manualWallJumpCount}, " +
            $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"WallPoint=({wall.Point.x:F3},{wall.Point.y:F3}), " +
            $"Distance={wall.Distance:F3}, Collider=" +
            $"{wall.ColliderInstanceId}:{wall.ColliderName}",
            "Recovery");
    }

    private void ObserveManualWallJumpUp(BonusStageState state)
    {
        if (!manualWallSequenceActive || manualWallJumpDownTime <= 0f)
            return;

        manualWallJumpHold = Mathf.Max(
            0f,
            Time.unscaledTime - manualWallJumpDownTime);
        BonusWallContact wall = default;
        PlayerMovement player = PlayerMovement.instance;
        if (player != null)
        {
            wall = wallDetector.Detect(
                player,
                lastReliableHorizontalSpeed);
        }
        float rise = state.PlayerPosition.y - manualWallJumpPosition.y;
        float travelX = state.PlayerPosition.x - manualWallJumpPosition.x;
        bool sameWall = wall.IsDetected &&
            Mathf.Abs(wall.Point.x - manualWallPoint.x) <= 1.00f;
        BonusRunnerLog.Debug(
            $"ManualWallPulseUp SequenceId={manualWallSequenceId}, " +
            $"AttemptId={(learningSampleActive && learningSource == "Manual" ? learningSampleId : 0)}, " +
            $"Pulse={manualWallJumpCount}, Hold={manualWallJumpHold:F3}s, " +
            $"Delta=({travelX:F3},{rise:F3}), " +
            $"StartVelocity=({manualWallJumpStartVelocity.x:F3}," +
            $"{manualWallJumpStartVelocity.y:F3}), " +
            $"Position=({state.PlayerPosition.x:F3},{state.PlayerPosition.y:F3}), " +
            $"Velocity=({state.PlayerVelocity.x:F3},{state.PlayerVelocity.y:F3}), " +
            $"WallAfterRelease[Detected={wall.IsDetected}," +
            $"Touching={wall.IsTouching},SameWall={sameWall}," +
            $"Point=({wall.Point.x:F3},{wall.Point.y:F3})," +
            $"BodyGap={wall.BodyGap:F3}], Collider=" +
            $"{manualWallColliderId}:{manualWallColliderName}",
            "Recovery");
        manualWallJumpDownTime = 0f;
        manualWallLastPulseTime = Time.unscaledTime;
    }

    private void ResetManualWallSequence()
    {
        manualWallSequenceActive = false;
        manualWallJumpCount = 0;
        manualWallJumpDownTime = 0f;
        manualWallJumpHold = 0f;
        manualWallJumpPosition = default;
        manualWallJumpStartVelocity = default;
        manualWallPoint = default;
        manualWallLastPulseTime = -10f;
        manualWallSequenceId = 0;
        manualWallColliderId = 0;
        manualWallColliderName = string.Empty;
    }
}
