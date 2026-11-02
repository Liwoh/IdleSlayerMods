using System;
using AutoAdventurer.Diagnostics;
using Il2Cpp;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AutoAdventurer.Gameplay;

/// <summary>
/// Lightweight Runner/Rage companion for the L automation toggle. It sends
/// the shortest native jump-panel pulse possible and continuously requests an
/// arrow attack; the game remains authoritative for jump eligibility, bow
/// availability, and attack cooldown.
/// </summary>
internal sealed class AutomaticJumpAttackService
{
    private const float RetryJumpAfterRejectedInputSeconds = 0.08f;
    private const float BoxScanIntervalSeconds = 0.05f;
    // Begin the held jump only when the box is almost over the player.
    // Runtime evidence from both normal movement and an accelerated approach
    // showed that 0.05+ viewport widths launches before the box enters the
    // player's vertical path.
    private const float BoxJumpViewportLead = 0.018f;
    // A fixed viewport lead is not sufficient while Boost, Wind Dash, or
    // another speed modifier changes the map's horizontal flow. Estimate the
    // box closing speed and begin early enough to preserve roughly this much
    // ascent time before horizontal alignment. Keep this deliberately short:
    // the native held jump rises quickly, while Boost/Rage can otherwise make
    // a velocity-derived lead much too large.
    private const float BoxJumpLeadTimeSeconds = 0.055f;
    private const float MaximumBoxJumpViewportLead = 0.06f;
    private const float BoxJumpHoldSeconds = 0.25f;
    private const float BoxPassedViewportDistance = -0.03f;
    private const float BoxGroundStableSeconds = 0.04f;
    private const float BoxGroundVerticalTolerance = 0.025f;
    private const float LightAttackIntervalSeconds = 1f;
    private const float MediumAttackIntervalSeconds = 1f / 3f;
    private const float HighAttackIntervalSeconds = 0.125f;
    private const float ExtraHighAttackIntervalSeconds = 1f / 15f;

    private float nextJumpAttemptTime;
    private float nextAttackTime;
    private float nextBoxScanTime;
    private float releaseBoxJumpAt = -1f;
    private bool boxJumpHeld;
    private bool untrackedBoxApproaching;
    private bool untrackedBoxActive;
    private float untrackedBoxHorizontalDistance = float.NaN;
    private float currentBoxJumpLead = BoxJumpViewportLead;
    private float previousBoxHorizontalDistance = float.NaN;
    private float previousBoxSampleTime = -1f;
    private float estimatedBoxClosingSpeed;
    private int previousBoxTargetId;
    private bool boxJumpAttempted;
    private float lastBoxPlayerY;
    private float boxPlayerStationarySince = -1f;
    private bool hasBoxPlayerY;
    private bool boxTargetLogged;
    // Diagnostics: the box being aimed at, to log whether each attempt hits
    // and how high the player is when the box passes over it.
    private RandomBox boxTargetNormal;
    private SpecialRandomBox boxTargetSpecial;
    private float boxJumpStartedAt = -1f;
    private bool boxAlignmentLogged;

    /// <summary>
    /// Movement abilities must wait for the entire lifetime of any active
    /// Random Box that has no matching horizontal magnet. Waiting only until
    /// the box was close enough to jump allowed an activation to change the
    /// approach speed and invalidate the jump timing.
    /// </summary>
    internal bool IsCatchingUntrackedBox => untrackedBoxActive;

    internal void Tick(
        float now,
        bool enabled,
        bool autoJump,
        bool autoShootArrows,
        string arrowAttackFrequency)
    {
        if (!enabled || !IsCentralGameplayState())
        {
            Reset();
            return;
        }

        PlayerMovement player = PlayerMovement.instance;
        if (player == null)
        {
            Reset();
            return;
        }

        ReleaseHeldBoxJumpWhenDue(now);

        if (autoShootArrows && now >= nextAttackTime)
        {
            TryAttack(player);
            nextAttackTime = now + GetAttackInterval(arrowAttackFrequency);
        }

        if (!autoJump)
        {
            ReleaseHeldBoxJump();
            untrackedBoxApproaching = false;
            untrackedBoxActive = false;
            untrackedBoxHorizontalDistance = float.NaN;
            ResetBoxApproachObservation();
            boxJumpAttempted = false;
            ResetBoxGroundObservation();
            return;
        }

        // Before the horizontal box-magnet upgrade is bought, early-game
        // Random Boxes can pass over a character that is only making minimum
        // jump pulses. When an unhit box reaches the player, briefly hold the
        // native jump input so the character rises into its path. Once the
        // magnet exists, the game already handles horizontal alignment and
        // this helper remains completely dormant.
        if (now >= nextBoxScanTime)
        {
            nextBoxScanTime = now + BoxScanIntervalSeconds;
            ScanUntrackedRandomBoxes(
                player,
                out untrackedBoxActive,
                out untrackedBoxHorizontalDistance,
                out int boxTargetId,
                out RandomBox normalTarget,
                out SpecialRandomBox specialTarget);
            if (untrackedBoxActive)
            {
                if (normalTarget != null || specialTarget != null)
                {
                    boxTargetNormal = normalTarget;
                    boxTargetSpecial = specialTarget;
                }
                LogBoxAlignment(player, now);
                ObserveBoxApproach(now, boxTargetId,
                    untrackedBoxHorizontalDistance);
                untrackedBoxApproaching =
                    untrackedBoxHorizontalDistance <= currentBoxJumpLead;
            }
            else
            {
                untrackedBoxApproaching = false;
                ResetBoxApproachObservation();
            }
            if (untrackedBoxActive && !boxTargetLogged)
            {
                boxTargetLogged = true;
                AdventurerLog.MovementDebug(
                    "Untracked Random Box targeted; normal jumping and movement " +
                    "abilities are suspended until the box clears.");
            }
            else if (!untrackedBoxActive && boxTargetLogged)
            {
                boxTargetLogged = false;
                AdventurerLog.MovementDebug(
                    $"Untracked Random Box cleared; result={BoxOutcome()}; " +
                    $"jumped={boxJumpAttempted}; normal jumping and movement " +
                    "abilities resumed.");
                ClearBoxDiagnostics();
            }
        }

        // Enter an exclusive box-catching mode as soon as an untracked box is
        // active. Normal repeated jump pulses stop completely so the player
        // remains low and predictable. Perform one short held jump only when
        // the box reaches the narrow alignment window.
        if (untrackedBoxActive)
        {
            if (untrackedBoxApproaching &&
                !boxJumpAttempted &&
                !boxJumpHeld &&
                now >= nextJumpAttemptTime &&
                IsReadyForBoxJump(player, now) &&
                BeginBoxJump())
            {
                boxJumpAttempted = true;
                boxJumpHeld = true;
                // This is the one place where a taller-than-minimum jump is
                // intentional. Holding for a stable quarter second gives the
                // player enough vertical overlap after the later horizontal
                // trigger without changing ordinary automatic jump height.
                float holdSeconds = Math.Clamp(
                    Math.Max(player.jumpTime, BoxJumpHoldSeconds),
                    BoxJumpHoldSeconds,
                    0.3f);
                releaseBoxJumpAt = now + holdSeconds;
                nextJumpAttemptTime = releaseBoxJumpAt +
                                      RetryJumpAfterRejectedInputSeconds;
                boxJumpStartedAt = now;
                AdventurerLog.MovementDebug(
                    $"Untracked Random Box jump committed; " +
                    $"horizontalLead={untrackedBoxHorizontalDistance:0.###} viewport; " +
                    $"verticalGap={BoxVerticalGap(player):0.###} viewport; " +
                    $"speed={player.currentSpeed:0.##}/{player.speed:0.##}; " +
                    $"triggerLead={currentBoxJumpLead:0.###}; " +
                    $"closingSpeed={estimatedBoxClosingSpeed:0.###} viewport/s; " +
                    $"hold={holdSeconds:0.###}s.");
            }
            return;
        }

        boxJumpAttempted = false;
        ResetBoxGroundObservation();

        if (now < nextJumpAttemptTime)
            return;

        // DOWN+UP in one managed pass produces the minimum native jump. Pulse
        // continuously and let PlayerMovement accept it only when jumping is
        // legal. This deliberately avoids IsGrounded(), which can remain
        // false while the character is visibly standing on the floor.
        if (PulseJump())
            nextJumpAttemptTime = now + RetryJumpAfterRejectedInputSeconds;
    }

    private static float GetAttackInterval(string frequency)
    {
        if (string.Equals(frequency, "Ultra", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(frequency, "Extreme", StringComparison.OrdinalIgnoreCase))
            return 0f;
        if (string.Equals(frequency, "Extra High", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(frequency, "ExtraHigh", StringComparison.OrdinalIgnoreCase))
            return ExtraHighAttackIntervalSeconds;
        if (string.Equals(frequency, "High", StringComparison.OrdinalIgnoreCase))
            return HighAttackIntervalSeconds;
        if (string.Equals(frequency, "Light", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(frequency, "Low", StringComparison.OrdinalIgnoreCase))
            return LightAttackIntervalSeconds;
        return MediumAttackIntervalSeconds;
    }

    private static void TryAttack(PlayerMovement player)
    {
        if (player.bowDisabled)
            return;

        try
        {
            // Called every rendered frame so no ready attack window is missed.
            // ShootArrow performs the game's own unlock/cooldown validation.
            player.ShootArrow();
        }
        catch
        {
            // Missing/rebuilt player and bow objects are normal around
            // Ascension and scene changes; the outer runtime will retry.
        }
    }

    private static bool PulseJump()
    {
        JumpPanel panel = JumpPanel.instance;
        EventSystem eventSystem = EventSystem.current;
        if (panel == null || eventSystem == null)
            return false;

        PointerEventData data = new(eventSystem)
        {
            position = new Vector2(Screen.width * 0.8f, Screen.height)
        };

        bool downSent = false;
        try
        {
            panel.OnPointerDown(data);
            downSent = true;
            panel.OnPointerUp(data);
            return true;
        }
        catch
        {
            if (downSent)
            {
                try
                {
                    panel.OnPointerUp(data);
                }
                catch
                {
                    // Input cleanup must never break the main runtime.
                }
            }
            return false;
        }
    }

    private static bool BeginBoxJump()
    {
        JumpPanel panel = JumpPanel.instance;
        EventSystem eventSystem = EventSystem.current;
        if (panel == null || eventSystem == null)
            return false;

        try
        {
            panel.OnPointerDown(CreateJumpPointer(eventSystem));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReleaseHeldBoxJumpWhenDue(float now)
    {
        if (boxJumpHeld && now >= releaseBoxJumpAt)
            ReleaseHeldBoxJump();
    }

    private void ReleaseHeldBoxJump()
    {
        if (!boxJumpHeld)
            return;

        boxJumpHeld = false;
        releaseBoxJumpAt = -1f;
        JumpPanel panel = JumpPanel.instance;
        EventSystem eventSystem = EventSystem.current;
        if (panel == null || eventSystem == null)
            return;

        try
        {
            panel.OnPointerUp(CreateJumpPointer(eventSystem));
        }
        catch
        {
            // Scene changes can destroy the input target while held.
        }
    }

    private static PointerEventData CreateJumpPointer(EventSystem eventSystem) =>
        new(eventSystem)
        {
            position = new Vector2(Screen.width * 0.8f, Screen.height)
        };

    private bool IsReadyForBoxJump(PlayerMovement player, float now)
    {
        if (player.IsGrounded())
        {
            lastBoxPlayerY = player.transform.position.y;
            hasBoxPlayerY = true;
            boxPlayerStationarySince = now;
            return true;
        }

        float currentY = player.transform.position.y;
        if (!hasBoxPlayerY ||
            Math.Abs(currentY - lastBoxPlayerY) > BoxGroundVerticalTolerance)
        {
            lastBoxPlayerY = currentY;
            hasBoxPlayerY = true;
            boxPlayerStationarySince = now;
            return false;
        }

        return !player.isJumping &&
               boxPlayerStationarySince >= 0f &&
               now - boxPlayerStationarySince >= BoxGroundStableSeconds;
    }

    private void ResetBoxGroundObservation()
    {
        hasBoxPlayerY = false;
        boxPlayerStationarySince = -1f;
    }

    private static void ScanUntrackedRandomBoxes(
        PlayerMovement player,
        out bool active,
        out float horizontalDistance,
        out int targetId,
        out RandomBox normalTarget,
        out SpecialRandomBox specialTarget)
    {
        active = false;
        normalTarget = null;
        specialTarget = null;
        Transform boxTransform = null;
        float closestDistance = float.MaxValue;
        AscensionSkills skills = AscensionSkills.list;
        bool normalBoxTracked = skills?.RandomBoxMagnet?.unlocked == true;
        bool specialBoxTracked = skills?.SpecialRandomBoxMagnet?.unlocked == true;

        if (!normalBoxTracked)
        {
            foreach (RandomBox box in
                     UnityEngine.Object.FindObjectsByType<RandomBox>(FindObjectsSortMode.None))
            {
                if (box == null || box.isHitted || box.gameObject == null ||
                    !box.gameObject.activeInHierarchy)
                    continue;
                active = true;
                if (ConsiderBox(player.transform, box.transform,
                        ref boxTransform, ref closestDistance))
                    normalTarget = box;
            }
        }

        if (!specialBoxTracked)
        {
            foreach (SpecialRandomBox box in
                     UnityEngine.Object.FindObjectsByType<SpecialRandomBox>(FindObjectsSortMode.None))
            {
                if (box == null || box.isHitted || box.gameObject == null ||
                    !box.gameObject.activeInHierarchy)
                    continue;
                active = true;
                if (ConsiderBox(player.transform, box.transform,
                        ref boxTransform, ref closestDistance))
                {
                    normalTarget = null;
                    specialTarget = box;
                }
            }
        }

        horizontalDistance = boxTransform != null
            ? closestDistance
            : float.NaN;
        targetId = boxTransform != null ? boxTransform.GetInstanceID() : 0;
    }

    private void ObserveBoxApproach(
        float now, int targetId, float horizontalDistance)
    {
        if (targetId == 0 || float.IsNaN(horizontalDistance))
        {
            ResetBoxApproachObservation();
            return;
        }

        if (targetId != previousBoxTargetId ||
            previousBoxSampleTime < 0f ||
            float.IsNaN(previousBoxHorizontalDistance))
        {
            previousBoxTargetId = targetId;
            previousBoxHorizontalDistance = horizontalDistance;
            previousBoxSampleTime = now;
            estimatedBoxClosingSpeed = 0f;
            currentBoxJumpLead = BoxJumpViewportLead;
            return;
        }

        float elapsed = now - previousBoxSampleTime;
        if (elapsed > 0.001f)
        {
            float observedClosingSpeed =
                (previousBoxHorizontalDistance - horizontalDistance) / elapsed;
            if (observedClosingSpeed > 0f && observedClosingSpeed < 10f)
            {
                estimatedBoxClosingSpeed = estimatedBoxClosingSpeed <= 0f
                    ? observedClosingSpeed
                    : Mathf.Lerp(estimatedBoxClosingSpeed,
                        observedClosingSpeed, 0.5f);
            }

            currentBoxJumpLead = Mathf.Clamp(
                Math.Max(BoxJumpViewportLead,
                    estimatedBoxClosingSpeed * BoxJumpLeadTimeSeconds),
                BoxJumpViewportLead,
                MaximumBoxJumpViewportLead);
        }

        previousBoxHorizontalDistance = horizontalDistance;
        previousBoxSampleTime = now;
    }

    private void ResetBoxApproachObservation()
    {
        currentBoxJumpLead = BoxJumpViewportLead;
        previousBoxHorizontalDistance = float.NaN;
        previousBoxSampleTime = -1f;
        estimatedBoxClosingSpeed = 0f;
        previousBoxTargetId = 0;
    }

    private static bool ConsiderBox(
        Transform player,
        Transform candidate,
        ref Transform selected,
        ref float closestDistance)
    {
        if (player == null || candidate == null)
            return false;

        float horizontalDistance;
        Camera camera = Camera.main;
        if (camera != null)
        {
            float playerX = camera.WorldToViewportPoint(player.position).x;
            float boxX = camera.WorldToViewportPoint(candidate.position).x;
            horizontalDistance = boxX - playerX;
        }
        else
        {
            // This fallback is only used for the brief frames in which the
            // gameplay camera is being rebuilt.
            horizontalDistance = candidate.position.x - player.position.x;
        }

        if (horizontalDistance < BoxPassedViewportDistance ||
            horizontalDistance >= closestDistance)
            return false;

        closestDistance = horizontalDistance;
        selected = candidate;
        return true;
    }

    private Transform BoxTargetTransform() =>
        boxTargetNormal != null ? boxTargetNormal.transform
        : boxTargetSpecial != null ? boxTargetSpecial.transform
        : null;

    // Box height minus player height, in viewport units (positive = above).
    private float BoxVerticalGap(PlayerMovement player)
    {
        Transform box = BoxTargetTransform();
        Camera camera = Camera.main;
        if (box == null || camera == null) return float.NaN;
        return camera.WorldToViewportPoint(box.position).y -
               camera.WorldToViewportPoint(player.transform.position).y;
    }

    private void LogBoxAlignment(PlayerMovement player, float now)
    {
        if (boxJumpStartedAt < 0f || boxAlignmentLogged ||
            float.IsNaN(untrackedBoxHorizontalDistance) ||
            untrackedBoxHorizontalDistance > 0f)
            return;
        boxAlignmentLogged = true;
        AdventurerLog.MovementDebug(
            $"Untracked Random Box alignment: {now - boxJumpStartedAt:0.###}s after the jump; " +
            $"verticalGap={BoxVerticalGap(player):0.###} viewport; " +
            $"isJumping={player.isJumping}; jumpHeld={boxJumpHeld}; " +
            $"speed={player.currentSpeed:0.##}/{player.speed:0.##}.");
    }

    private string BoxOutcome()
    {
        try
        {
            if (boxTargetNormal != null) return boxTargetNormal.isHitted ? "hit" : "missed";
            if (boxTargetSpecial != null) return boxTargetSpecial.isHitted ? "hit" : "missed";
        }
        catch
        {
            // A box destroyed during the check reads as unknown below.
        }
        // Gone without ever being seen hit: it left the screen behind the
        // player, or was hit and destroyed between two scans (rare).
        return boxTargetNormal is null && boxTargetSpecial is null
            ? "unknown"
            : "missed (destroyed)";
    }

    private void ClearBoxDiagnostics()
    {
        boxTargetNormal = null;
        boxTargetSpecial = null;
        boxJumpStartedAt = -1f;
        boxAlignmentLogged = false;
    }

    private static bool IsCentralGameplayState()
    {
        GameStates state = GameState.current;
        return state == GameStates.RunnerMode ||
               state == GameStates.RageMode;
    }

    internal void Reset()
    {
        ReleaseHeldBoxJump();
        nextJumpAttemptTime = 0f;
        nextAttackTime = 0f;
        nextBoxScanTime = 0f;
        untrackedBoxApproaching = false;
        untrackedBoxActive = false;
        untrackedBoxHorizontalDistance = float.NaN;
        ResetBoxApproachObservation();
        boxJumpAttempted = false;
        ResetBoxGroundObservation();
        boxTargetLogged = false;
        ClearBoxDiagnostics();
    }
}
